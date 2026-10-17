<script setup>
/**
 * নিয়মাবলি (Rulebook) — HR's own rule pages (docs/HR_RULEBOOK_PROMPT.md §3.6).
 *
 * The list: pages grouped by category, each with its status chip (খসড়া Draft,
 * নিশ্চিত Confirmed, বাতিল Retired), its confirmed version and who confirmed it
 * when; search by title and text, filters by category and status; "Export all
 * confirmed". Opening a page shows it in HrRulePage, which holds the editor,
 * the actions and the history.
 *
 * What a person may see and do is the server's decision: a reader gets confirmed
 * and retired pages only, and `canEdit` decides which buttons show. Theme tokens
 * only; usable at 360 px.
 */
import { computed, onMounted, ref, watch } from 'vue'
import { BookMarked, Download, Loader2, Plus, Search } from '@lucide/vue'
import { exportRulebook, listRulebook } from '@/services/hr.service.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import HrRulePage from '@/components/hr/HrRulePage.vue'
import { toDate } from '@/lib/time'

/** The chips; which statuses exist is the server's list. */
const STATUS = {
  draft: { bn: 'খসড়া', en: 'Draft', variant: 'outline' },
  confirmed: { bn: 'নিশ্চিত', en: 'Confirmed', variant: 'default' },
  retired: { bn: 'বাতিল', en: 'Retired', variant: 'secondary' },
}

const pages = ref([])
const categories = ref([])
const statuses = ref([])
const canEdit = ref(false)
const loading = ref(true)
const error = ref('')
const exporting = ref(false)

const q = ref('')
const category = ref('all')
const status = ref('all')

/** The open page: null for the list, 'new' for a new page, or a page id. */
const open = ref(null)
const pageView = ref(null)

let searchTimer = null
let loadSeq = 0

async function load() {
  const seq = ++loadSeq
  error.value = ''
  try {
    const res = await listRulebook({
      q: q.value.trim(),
      category: category.value === 'all' ? '' : category.value,
      status: status.value === 'all' ? '' : status.value,
    })
    if (seq !== loadSeq) return   // a newer search answered first
    pages.value = res.pages ?? []
    categories.value = res.categories ?? []
    statuses.value = res.statuses ?? []
    canEdit.value = !!res.canEdit
  } catch (e) {
    if (seq === loadSeq) error.value = e.message || 'The Rulebook could not be loaded.'
  } finally {
    if (seq === loadSeq) loading.value = false
  }
}

onMounted(load)
watch([category, status], load)
watch(q, () => {
  clearTimeout(searchTimer)
  searchTimer = setTimeout(load, 250)
})

/** The list, grouped by category in the server's order; empty groups left out. */
const groups = computed(() => categories.value
  .map((c) => ({ ...c, pages: pages.value.filter((p) => p.category === c.key) }))
  .filter((g) => g.pages.length))

/** A reader is never shown drafts, so the filter offers only what they can see. */
const statusOptions = computed(() => statuses.value.filter((s) => canEdit.value || s !== 'draft'))

const categoryOf = (key) => categories.value.find((c) => c.key === key)

function fmtDate(value) {
  const d = toDate(value)
  return d ? d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }) : ''
}

/** Leaving a page with unsaved changes asks first. */
function leaveAllowed() {
  return !pageView.value?.dirty || window.confirm('সংরক্ষণ না করা পরিবর্তন আছে। ছেড়ে যাবেন? / You have unsaved changes. Leave this page?')
}

function openPage(id) {
  if (open.value !== null && !leaveAllowed()) return
  open.value = id
}

function backToList() {
  if (!leaveAllowed()) return
  open.value = null
  load()
}

async function doExport() {
  exporting.value = true
  error.value = ''
  try {
    await exportRulebook()
  } catch (e) {
    error.value = e.message || 'The export failed.'
  } finally {
    exporting.value = false
  }
}
</script>

<template>
  <HrRulePage
    v-if="open !== null"
    ref="pageView"
    :key="open"
    :page-id="open === 'new' ? null : open"
    :categories="categories"
    :can-edit="canEdit"
    @back="backToList"
    @created="(id) => { open = id; load() }"
    @changed="load"
  />

  <div v-else class="mx-auto w-full max-w-5xl p-4 sm:p-6" data-testid="rulebook-list">
    <div class="flex flex-wrap items-start justify-between gap-3">
      <div class="min-w-0">
        <h2 class="flex items-center gap-2 text-lg font-semibold">
          <BookMarked class="size-5 shrink-0 text-muted-foreground" aria-hidden="true" />
          নিয়মাবলি <span class="text-sm font-normal text-muted-foreground">Rulebook</span>
        </h2>
        <p class="mt-1 text-sm text-muted-foreground">
          HR-এর নিজের নিয়ম, এক বিষয়ে এক পাতা। নিশ্চিত পাতা লাইব্রেরিতে যায়, প্রশ্নের উত্তর আগে সেখান থেকে।
          <span class="text-xs">HR's own rule pages; confirmed pages go to the Library.</span>
        </p>
      </div>
      <div class="flex flex-wrap items-center gap-2">
        <Button size="sm" variant="outline" :disabled="exporting" @click="doExport">
          <Loader2 v-if="exporting" class="animate-spin" />
          <Download v-else />
          সব নিশ্চিত এক্সপোর্ট <span class="text-xs opacity-70">Export all confirmed</span>
        </Button>
        <Button v-if="canEdit" size="sm" data-testid="rulebook-new" @click="openPage('new')">
          <Plus />
          নতুন নিয়ম <span class="text-xs opacity-80">New</span>
        </Button>
      </div>
    </div>

    <!-- Search and filters -->
    <div class="mt-4 grid gap-2 sm:grid-cols-[1fr_auto_auto]">
      <div class="relative">
        <Search class="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
        <Input v-model="q" class="h-9 pl-8" placeholder="শিরোনাম বা লেখায় খুঁজুন · Search title and text" aria-label="Search the Rulebook" />
      </div>
      <Select v-model="category">
        <SelectTrigger class="h-9 w-full sm:w-52" aria-label="Category"><SelectValue /></SelectTrigger>
        <SelectContent>
          <SelectItem value="all">সব বিভাগ · All categories</SelectItem>
          <SelectItem v-for="c in categories" :key="c.key" :value="c.key">{{ c.bn }} · {{ c.en }}</SelectItem>
        </SelectContent>
      </Select>
      <Select v-model="status">
        <SelectTrigger class="h-9 w-full sm:w-44" aria-label="Status"><SelectValue /></SelectTrigger>
        <SelectContent>
          <SelectItem value="all">সব অবস্থা · All statuses</SelectItem>
          <SelectItem v-for="s in statusOptions" :key="s" :value="s">{{ STATUS[s]?.bn }} · {{ STATUS[s]?.en }}</SelectItem>
        </SelectContent>
      </Select>
    </div>

    <p v-if="error" class="mt-3 text-sm text-destructive" role="alert">{{ error }}</p>

    <div v-if="loading" class="mt-8 flex justify-center text-muted-foreground">
      <Loader2 class="size-5 animate-spin" aria-label="Loading" />
    </div>

    <div v-else-if="!groups.length" class="mt-10 rounded-lg border border-dashed p-8 text-center text-sm text-muted-foreground">
      <template v-if="q || category !== 'all' || status !== 'all'">
        কিছু মেলেনি। <span class="text-xs">Nothing matches.</span>
      </template>
      <template v-else-if="canEdit">
        এখনো কোনো পাতা নেই। “নতুন নিয়ম” দিয়ে প্রথম নিয়মটি লিখুন।
        <span class="block text-xs">No pages yet. Write the first rule with “New”.</span>
      </template>
      <template v-else>
        এখনো কোনো নিশ্চিত নিয়ম নেই। <span class="text-xs">No confirmed rules yet.</span>
      </template>
    </div>

    <!-- Grouped by category -->
    <template v-else>
    <section v-for="g in groups" :key="g.key" class="mt-6" :aria-label="g.en">
      <h3 class="mb-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
        {{ g.bn }} · {{ g.en }} <span class="font-normal normal-case">({{ g.pages.length }})</span>
      </h3>
      <ul class="grid gap-2">
        <li v-for="p in g.pages" :key="p.id">
          <button
            type="button"
            class="flex w-full flex-col gap-1.5 rounded-lg border bg-card px-4 py-3 text-left transition-colors hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            :data-rulebook-page="p.id"
            @click="openPage(p.id)"
          >
            <span class="flex min-w-0 items-start justify-between gap-2">
              <span :class="['min-w-0 wrap-break-word font-medium', p.status === 'retired' && 'text-muted-foreground line-through']">{{ p.title }}</span>
              <Badge :variant="STATUS[p.status]?.variant ?? 'outline'" class="shrink-0 font-normal" :data-status="p.status">
                {{ STATUS[p.status]?.bn }} <span class="ml-1 opacity-75">{{ STATUS[p.status]?.en }}</span>
              </Badge>
            </span>
            <span class="text-xs text-muted-foreground">
              <template v-if="p.confirmedVersion">
                নিশ্চিত v{{ p.confirmedVersion }} · {{ p.confirmedBy }} · {{ fmtDate(p.confirmedAtUtc) }}
              </template>
              <template v-else>এখনো নিশ্চিত হয়নি · not confirmed yet · v{{ p.currentVersion }}</template>
              <template v-if="p.draftInProgress"> · খসড়া v{{ p.currentVersion }} চলছে <span>(draft in progress)</span></template>
              <template v-if="categoryOf(p.category) && p.language"> · {{ p.language === 'bn' ? 'বাংলা' : 'English' }}</template>
            </span>
          </button>
        </li>
      </ul>
    </section>
    </template>
  </div>
</template>
