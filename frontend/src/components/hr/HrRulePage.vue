<script setup>
/**
 * One Rulebook page (docs/HR_RULEBOOK_PROMPT.md §3.6): read it, edit it, and
 * keep its paper trail.
 *
 *   · The page view reads like a printed page — large type, numbered rules — and
 *     each worked-out amount shows its formula on hover, as in Ask (the server
 *     works [[= … ]] out with the evaluator Ask uses; lib/hr-text marks them).
 *   · The editor is plain Markdown in a text area with a live preview — the same
 *     pattern, sanitizer and renderer as the Notes editor (MarkdownBody). Save
 *     asks for an optional note; leaving with unsaved changes asks first.
 *   · Actions (editors only; the server checks every one): Save, Confirm (a
 *     required note, and a summary of what changed since the last confirmation),
 *     Retire (a required note), Restore a version from the history.
 *   · History: every version the person may see, with kind, author, time and
 *     note; two picked are compared line by line (lib/line-diff).
 *   · Print (the browser's, with a header and a footer) and Markdown download.
 *
 * Theme tokens only, light and dark; one column at 360 px.
 */
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import {
  ArrowLeft, Check, Download, GitCompare, History, Loader2, Pencil, Printer, RotateCcw, Save, Undo2, X,
} from '@lucide/vue'
import {
  confirmRulePage, createRulePage, downloadRulePage, getRulePage, listRuleVersions, restoreRuleVersion,
  retireRulePage, saveRulePage,
} from '@/services/hr.service.js'
import MarkdownBody from '@/components/MarkdownBody.vue'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { diffLines, diffStats } from '@/lib/line-diff'
import { withFormulas } from '@/lib/hr-text'
import { toDate } from '@/lib/time'

const props = defineProps({
  /** The page to open; null writes a new one. */
  pageId: { type: Number, default: null },
  categories: { type: Array, default: () => [] },
  canEdit: { type: Boolean, default: false },
})
const emit = defineEmits(['back', 'created', 'changed'])

const STATUS = {
  draft: { bn: 'খসড়া', en: 'Draft', variant: 'outline' },
  confirmed: { bn: 'নিশ্চিত', en: 'Confirmed', variant: 'default' },
  retired: { bn: 'বাতিল', en: 'Retired', variant: 'secondary' },
}
const KIND = {
  written: { bn: 'লেখা', en: 'Written' },
  generated: { bn: 'মডেলের খসড়া', en: 'Generated' },
  edited: { bn: 'সম্পাদিত', en: 'Edited' },
  confirmed: { bn: 'নিশ্চিত', en: 'Confirmed' },
  retired: { bn: 'বাতিল', en: 'Retired' },
  restored: { bn: 'পুনরুদ্ধার', en: 'Restored' },
}

/** What a new page starts with: the headings HR writes a rule under. */
const SKELETON = `#
**বিভাগ / Category:** …   **প্রযোজ্য / Applies to:** …

## উদ্দেশ্য · Purpose

## নিয়ম · Rules
1.

## হিসাব · Calculation

## ব্যতিক্রম · Exceptions
`

const page = ref(null)            // the page, with .version: the version shown
const versions = ref([])
const loading = ref(false)
const busy = ref(false)
const error = ref('')

const editing = ref(props.pageId === null)
const form = ref({ title: '', category: 'other', language: 'bn', body: SKELETON })
const baseline = ref(JSON.stringify(form.value))
const baseVersion = ref(null)
const tab = ref('write')          // phones: write or preview
const showHistory = ref(false)    // phones: the history under the page

const compare = ref([])           // up to two version numbers
const diff = ref(null)            // { from, to, lines, stats } once two are picked
const dialog = ref(null)          // { action, version?, note, summary?, firstConfirmation? }
const printedAt = ref(new Date())

const dirty = computed(() => editing.value && JSON.stringify(form.value) !== baseline.value)
defineExpose({ dirty })

const shown = computed(() => page.value?.version ?? null)
const latest = computed(() => !page.value || shown.value?.version === page.value.currentVersion)
const category = computed(() => props.categories.find((c) => c.key === (editing.value ? form.value.category : page.value?.category)))
const pageHtml = computed(() => (shown.value ? withFormulas(shown.value.display, shown.value.formulas) : ''))
const retired = computed(() => page.value?.status === 'retired')
const canConfirm = computed(() => props.canEdit && page.value && !retired.value && page.value.confirmedVersion !== page.value.currentVersion)

function fmt(value) {
  const d = toDate(value)
  return d ? d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : ''
}
/** The date as the header line writes it (UTC), so the printout and the download agree. */
const isoDate = (value) => toDate(value)?.toISOString().slice(0, 10) ?? ''

// ───────────────────────── load ─────────────────────────

async function load(version) {
  if (!props.pageId) return
  loading.value = true
  error.value = ''
  try {
    const [p, h] = await Promise.all([getRulePage(props.pageId, version), listRuleVersions(props.pageId)])
    page.value = p
    versions.value = h.versions ?? []
  } catch (e) {
    error.value = e.message || 'The page could not be loaded.'
  } finally {
    loading.value = false
  }
}

function onBeforeUnload(e) {
  if (!dirty.value) return
  e.preventDefault()
  e.returnValue = ''
}

onMounted(() => {
  load()
  window.addEventListener('beforeunload', onBeforeUnload)
})
onBeforeUnmount(() => window.removeEventListener('beforeunload', onBeforeUnload))

// ───────────────────────── edit ─────────────────────────

async function startEdit() {
  if (!latest.value) await load()
  const p = page.value
  form.value = { title: p.title, category: p.category, language: p.language, body: p.version.body }
  baseline.value = JSON.stringify(form.value)
  baseVersion.value = p.currentVersion
  tab.value = 'write'
  editing.value = true
}

function cancelEdit() {
  if (dirty.value && !window.confirm('পরিবর্তনগুলো বাদ দেবেন? / Discard your changes?')) return
  if (!props.pageId) { emit('back'); return }
  editing.value = false
}

function goBack() {
  emit('back')   // the list asks before leaving unsaved changes
}

// ───────────────────────── actions ─────────────────────────

async function openDialog(action, version) {
  error.value = ''
  const d = { action, version, note: '', summary: null, firstConfirmation: false }
  if (action === 'confirm') {
    // What changed since the last confirmation: the confirmed text against the latest.
    try {
      const current = latest.value ? page.value.version.body : (await getRulePage(props.pageId)).version.body
      if (page.value.confirmedVersion) {
        const before = (await getRulePage(props.pageId, page.value.confirmedVersion)).version.body
        const lines = diffLines(before, current)
        d.summary = { from: page.value.confirmedVersion, lines: lines.filter((l) => l.type !== 'same'), stats: diffStats(lines) }
      } else {
        d.firstConfirmation = true
      }
    } catch (e) {
      error.value = e.message
      return
    }
  }
  dialog.value = d
}

const noteRequired = computed(() => dialog.value && dialog.value.action !== 'save')
const dialogReady = computed(() => dialog.value && (!noteRequired.value || dialog.value.note.trim().length > 0))

async function submitDialog() {
  const d = dialog.value
  if (!d || !dialogReady.value) return
  busy.value = true
  error.value = ''
  try {
    if (d.action === 'save' && !props.pageId) {
      const created = await createRulePage({ ...form.value, note: d.note })
      editing.value = false
      dialog.value = null
      emit('created', created.id)
      return
    }
    if (d.action === 'save') {
      page.value = await saveRulePage(props.pageId, { ...form.value, note: d.note, baseVersion: baseVersion.value })
      editing.value = false
    } else if (d.action === 'confirm') {
      page.value = await confirmRulePage(props.pageId, d.note)
    } else if (d.action === 'retire') {
      page.value = await retireRulePage(props.pageId, d.note)
    } else if (d.action === 'restore') {
      page.value = await restoreRuleVersion(props.pageId, d.version, d.note)
    }
    versions.value = (await listRuleVersions(props.pageId)).versions ?? []
    compare.value = []
    diff.value = null
    dialog.value = null
    emit('changed')
  } catch (e) {
    error.value = e.message
    dialog.value = null
  } finally {
    busy.value = false
  }
}

// ───────────────────────── history and diff ─────────────────────────

async function toggleCompare(v, on) {
  compare.value = on ? [...compare.value.filter((x) => x !== v), v].slice(-2) : compare.value.filter((x) => x !== v)
  diff.value = null
  if (compare.value.length !== 2) return
  const [from, to] = [...compare.value].sort((a, b) => a - b)
  try {
    const [a, b] = await Promise.all([getRulePage(props.pageId, from), getRulePage(props.pageId, to)])
    const lines = diffLines(a.version.body, b.version.body)
    diff.value = { from, to, lines, stats: diffStats(lines) }
  } catch (e) {
    error.value = e.message
  }
}

async function viewVersion(v) {
  if (editing.value) return
  await load(v)
}

// ───────────────────────── print and download ─────────────────────────

/** The browser's print, of the print copy only (index.css), always light. */
function printPage() {
  printedAt.value = new Date()
  const root = document.documentElement
  const wasDark = root.classList.contains('dark')
  if (wasDark) root.classList.remove('dark')
  const restore = () => {
    if (wasDark) root.classList.add('dark')
    window.removeEventListener('afterprint', restore)
  }
  window.addEventListener('afterprint', restore)
  nextTick(() => window.print())
}

async function download() {
  try {
    await downloadRulePage(props.pageId, shown.value?.version)
  } catch (e) {
    error.value = e.message
  }
}
</script>

<template>
  <div class="mx-auto w-full max-w-6xl p-4 sm:p-6" data-testid="rulebook-page">
    <!-- Title bar -->
    <div class="flex flex-wrap items-center gap-2">
      <Button variant="ghost" size="sm" class="-ml-2" @click="goBack">
        <ArrowLeft />
        নিয়মাবলি <span class="text-xs opacity-70">Rulebook</span>
      </Button>
      <div class="ml-auto flex flex-wrap items-center gap-2">
        <template v-if="page && !editing">
          <Button size="sm" variant="outline" @click="printPage"><Printer /> প্রিন্ট <span class="text-xs opacity-70">Print</span></Button>
          <Button size="sm" variant="outline" @click="download"><Download /> ডাউনলোড <span class="text-xs opacity-70">.md</span></Button>
          <template v-if="canEdit">
            <Button v-if="!retired" size="sm" variant="outline" data-action="edit" @click="startEdit"><Pencil /> সম্পাদনা <span class="text-xs opacity-70">Edit</span></Button>
            <Button v-if="canConfirm" size="sm" data-action="confirm" @click="openDialog('confirm')"><Check /> নিশ্চিত করুন <span class="text-xs opacity-80">Confirm</span></Button>
            <Button v-if="!retired" size="sm" variant="outline" data-action="retire" @click="openDialog('retire')"><X /> বাতিল <span class="text-xs opacity-70">Retire</span></Button>
          </template>
        </template>
        <template v-if="editing">
          <Button size="sm" variant="ghost" @click="cancelEdit"><Undo2 /> বাদ দিন <span class="text-xs opacity-70">Cancel</span></Button>
          <Button size="sm" data-action="save" :disabled="busy || !form.title.trim() || !form.body.trim() || (pageId && !dirty)" @click="openDialog('save')">
            <Save /> সংরক্ষণ <span class="text-xs opacity-80">Save</span>
          </Button>
        </template>
      </div>
    </div>

    <p v-if="error" class="mt-3 rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive dark:text-foreground" role="alert">{{ error }}</p>

    <div v-if="loading && !page" class="mt-10 flex justify-center text-muted-foreground"><Loader2 class="size-5 animate-spin" aria-label="Loading" /></div>

    <div v-else class="mt-4 grid gap-6 lg:grid-cols-[minmax(0,1fr)_19rem]">
      <!-- ───── the page, or the editor ───── -->
      <div class="min-w-0">
        <!-- Editor -->
        <template v-if="editing">
          <div class="grid gap-3 sm:grid-cols-[1fr_12rem_8rem]">
            <div class="grid gap-1.5">
              <Label for="rule-title">শিরোনাম <span class="text-xs text-muted-foreground">Title</span></Label>
              <Input id="rule-title" v-model="form.title" maxlength="120" placeholder="যেমন: সার্ভিস বেনিফিট · e.g. Service benefit" />
            </div>
            <div class="grid gap-1.5">
              <Label>বিভাগ <span class="text-xs text-muted-foreground">Category</span></Label>
              <Select v-model="form.category">
                <SelectTrigger class="h-9 w-full"><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem v-for="c in categories" :key="c.key" :value="c.key">{{ c.bn }} · {{ c.en }}</SelectItem>
                </SelectContent>
              </Select>
            </div>
            <div class="grid gap-1.5">
              <Label>ভাষা <span class="text-xs text-muted-foreground">Language</span></Label>
              <Select v-model="form.language">
                <SelectTrigger class="h-9 w-full"><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="bn">বাংলা</SelectItem>
                  <SelectItem value="en">English</SelectItem>
                </SelectContent>
              </Select>
            </div>
          </div>

          <p v-if="page?.status === 'confirmed'" class="mt-3 text-xs text-muted-foreground">
            নিশ্চিত v{{ page.confirmedVersion }} বহাল থাকবে; এই সংরক্ষণ একটি খসড়া, পরের নিশ্চিতকরণ পর্যন্ত লাইব্রেরি ও প্রশ্নে যাবে না।
            Confirmed v{{ page.confirmedVersion }} stays in use; this save is a draft until the next confirmation.
          </p>

          <div class="mt-3 flex gap-1 rounded-lg bg-muted p-1 lg:hidden" role="tablist">
            <button v-for="t in [['write', 'লিখুন', 'Write'], ['preview', 'দেখুন', 'Preview']]" :key="t[0]" type="button" role="tab"
              :aria-selected="tab === t[0]"
              :class="['flex-1 rounded-md px-3 py-1 text-xs transition-colors', tab === t[0] ? 'bg-background font-medium shadow-sm' : 'text-muted-foreground']"
              @click="tab = t[0]">{{ t[1] }} · {{ t[2] }}</button>
          </div>

          <div class="mt-3 grid gap-3 lg:grid-cols-2">
            <div :class="tab === 'preview' && 'hidden lg:block'">
              <Textarea v-model="form.body" class="min-h-[55vh] font-mono text-sm leading-relaxed" aria-label="Page text (Markdown)" spellcheck="false" />
              <p class="mt-1 text-xs text-muted-foreground">
                Markdown: <code>## শিরোনাম</code>, <code>1.</code> নিয়ম। হিসাবের উদাহরণ <code>[[= (12000/30)*15*7 ]]</code> — সংরক্ষণের পর ফল দেখাবে।
              </p>
            </div>
            <div :class="['rounded-lg border bg-card p-4 sm:p-6', tab === 'write' && 'hidden lg:block']" aria-label="Preview">
              <MarkdownBody :content="form.body" class="rule-page" />
            </div>
          </div>
        </template>

        <!-- Page view -->
        <template v-else-if="page">
          <div class="flex flex-wrap items-center gap-2 text-sm">
            <Badge :variant="STATUS[page.status]?.variant ?? 'outline'" class="font-normal" :data-status="page.status">
              {{ STATUS[page.status]?.bn }} <span class="ml-1 opacity-75">{{ STATUS[page.status]?.en }}</span>
            </Badge>
            <span v-if="page.confirmedVersion" class="text-muted-foreground" data-testid="rulebook-status-line">
              নিশ্চিত v{{ page.confirmedVersion }} · {{ page.confirmedBy }} · {{ fmt(page.confirmedAtUtc) }}
              <template v-if="page.draftInProgress"> · খসড়া v{{ page.currentVersion }} চলছে <span class="text-xs">(Confirmed v{{ page.confirmedVersion }} · draft v{{ page.currentVersion }} in progress)</span></template>
            </span>
            <span v-else class="text-muted-foreground">এখনো নিশ্চিত হয়নি <span class="text-xs">not confirmed yet</span></span>
          </div>

          <p v-if="!latest" class="mt-3 flex flex-wrap items-center gap-2 rounded-md border bg-muted/50 px-3 py-2 text-sm">
            আপনি v{{ shown.version }} ({{ KIND[shown.kind]?.bn }} · {{ KIND[shown.kind]?.en }}) দেখছেন।
            <Button size="sm" variant="link" class="h-auto p-0" @click="load()">সর্বশেষ দেখুন · View latest</Button>
          </p>

          <article class="mt-4 rounded-lg border bg-card px-5 py-6 sm:px-10 sm:py-10">
            <header class="mb-6 border-b pb-4">
              <h1 class="text-2xl font-semibold leading-tight sm:text-3xl">{{ page.title }}</h1>
              <p class="mt-2 text-sm text-muted-foreground">
                {{ category?.bn }} · {{ category?.en }} · v{{ shown.version }} · {{ KIND[shown.kind]?.bn }} {{ shown.author }} · {{ fmt(shown.atUtc) }}
              </p>
              <p v-if="shown.changeNote" class="mt-1 text-sm text-muted-foreground">“{{ shown.changeNote }}”</p>
            </header>
            <MarkdownBody :content="pageHtml" class="rule-page" />
          </article>
        </template>
      </div>

      <!-- ───── history ───── -->
      <aside v-if="page" class="min-w-0" aria-label="History">
        <button type="button" class="flex w-full items-center justify-between rounded-md border px-3 py-2 text-sm lg:hidden" :aria-expanded="showHistory" @click="showHistory = !showHistory">
          <span class="flex items-center gap-2"><History class="size-4" /> ইতিহাস · History ({{ versions.length }})</span>
          <span class="text-xs text-muted-foreground">{{ showHistory ? 'লুকান · Hide' : 'দেখুন · Show' }}</span>
        </button>

        <div :class="['lg:block', showHistory ? 'mt-3 block' : 'hidden']">
          <h2 class="hidden items-center gap-2 text-sm font-semibold lg:flex"><History class="size-4" /> ইতিহাস <span class="text-xs font-normal text-muted-foreground">History</span></h2>
          <p class="mt-1 text-xs text-muted-foreground">দুটি সংস্করণ বেছে তুলনা করুন। Pick two versions to compare.</p>

          <ol class="mt-3 grid gap-2">
            <li v-for="v in [...versions].reverse()" :key="v.version"
              :class="['rounded-md border px-3 py-2 text-sm', shown?.version === v.version && 'border-primary/60 bg-accent/40']" :data-version="v.version">
              <div class="flex items-center gap-2">
                <Checkbox :model-value="compare.includes(v.version)" :aria-label="`Compare v${v.version}`"
                  @update:model-value="(on) => toggleCompare(v.version, on)" />
                <button type="button" class="font-medium hover:underline disabled:no-underline" :disabled="editing" @click="viewVersion(v.version)">v{{ v.version }}</button>
                <Badge variant="outline" class="font-normal" :data-kind="v.kind">{{ KIND[v.kind]?.bn }} · {{ KIND[v.kind]?.en }}</Badge>
              </div>
              <p class="mt-1 text-xs text-muted-foreground">{{ v.author }} · {{ fmt(v.atUtc) }}</p>
              <p v-if="v.changeNote" class="mt-1 text-xs wrap-break-word">“{{ v.changeNote }}”</p>
              <Button v-if="canEdit && !editing && v.version !== page.currentVersion" size="sm" variant="ghost" class="-ml-2 mt-1 h-7 text-xs"
                data-action="restore" @click="openDialog('restore', v.version)">
                <RotateCcw /> পুনরুদ্ধার <span class="opacity-70">Restore as a new draft</span>
              </Button>
            </li>
          </ol>
        </div>
      </aside>
    </div>

    <!-- ───── a two-version diff ───── -->
    <section v-if="diff" class="mt-6 rounded-lg border bg-card" aria-label="Comparison" data-testid="rulebook-diff">
      <div class="flex flex-wrap items-center justify-between gap-2 border-b px-4 py-2">
        <p class="flex items-center gap-2 text-sm font-medium">
          <GitCompare class="size-4" /> v{{ diff.from }} → v{{ diff.to }}
          <span class="text-xs font-normal text-muted-foreground">+{{ diff.stats.added }} যোগ · −{{ diff.stats.removed }} বাদ (added · removed)</span>
        </p>
        <Button size="sm" variant="ghost" class="h-7" @click="compare = []; diff = null"><X /> বন্ধ · Close</Button>
      </div>
      <ol class="overflow-x-auto py-2 font-mono text-xs leading-relaxed">
        <li v-for="(d, i) in diff.lines" :key="i" :data-diff="d.type"
          :class="['flex gap-2 whitespace-pre-wrap px-4 wrap-break-word',
            d.type === 'add' && 'bg-primary/10 text-foreground',
            d.type === 'del' && 'bg-destructive/10 text-muted-foreground line-through']">
          <span class="w-3 shrink-0 select-none text-muted-foreground" aria-hidden="true">{{ d.type === 'add' ? '+' : d.type === 'del' ? '−' : ' ' }}</span>
          <span class="min-w-0">{{ d.text || ' ' }}</span>
        </li>
      </ol>
    </section>

    <!-- ───── the note dialog: save (optional note), confirm, retire, restore (required) ───── -->
    <Dialog :open="!!dialog" @update:open="(o) => { if (!o) dialog = null }">
      <DialogContent v-if="dialog" class="max-h-[90dvh] overflow-y-auto sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>
            <template v-if="dialog.action === 'save'">সংরক্ষণ · Save</template>
            <template v-else-if="dialog.action === 'confirm'">নিশ্চিত করুন · Confirm</template>
            <template v-else-if="dialog.action === 'retire'">বাতিল করুন · Retire</template>
            <template v-else>v{{ dialog.version }} পুনরুদ্ধার · Restore</template>
          </DialogTitle>
          <DialogDescription>
            <template v-if="dialog.action === 'save'">একটি নতুন সংস্করণ হবে। চাইলে কারণ লিখুন। A new version is saved; a note is optional.</template>
            <template v-else-if="dialog.action === 'confirm'">
              v{{ page.currentVersion }} অনুমোদিত নিয়ম হবে, লাইব্রেরিতে যাবে এবং প্রশ্নের উত্তরে ব্যবহার হবে। কেন, তা লিখুন।
              v{{ page.currentVersion }} becomes the approved rule, in the Library and in Ask. Say why.
            </template>
            <template v-else-if="dialog.action === 'retire'">পাতাটি আর ব্যবহার হবে না; ইতিহাস থেকে যাবে। কেন, তা লিখুন। The page stops being used; its history stays. Say why.</template>
            <template v-else>v{{ dialog.version }}-এর লেখা একটি নতুন খসড়া হবে। The text of v{{ dialog.version }} becomes a new draft. Say why.</template>
          </DialogDescription>
        </DialogHeader>

        <!-- Confirm: what changed since the last confirmation -->
        <div v-if="dialog.action === 'confirm'" class="rounded-md border bg-muted/40 p-3 text-sm" data-testid="confirm-summary">
          <p v-if="dialog.firstConfirmation">প্রথম নিশ্চিতকরণ — পুরো পাতাটি অনুমোদিত নিয়ম হবে। First confirmation: the whole page becomes the approved rule.</p>
          <template v-else-if="dialog.summary">
            <p class="font-medium">v{{ dialog.summary.from }} থেকে পরিবর্তন · Changed since v{{ dialog.summary.from }}:
              <span class="font-normal text-muted-foreground">+{{ dialog.summary.stats.added }} · −{{ dialog.summary.stats.removed }}</span></p>
            <ol class="mt-2 max-h-48 overflow-y-auto font-mono text-xs">
              <li v-for="(d, i) in dialog.summary.lines" :key="i" :class="['whitespace-pre-wrap wrap-break-word px-1', d.type === 'add' ? 'bg-primary/10' : 'bg-destructive/10 text-muted-foreground line-through']">
                {{ d.type === 'add' ? '+' : '−' }} {{ d.text || ' ' }}
              </li>
            </ol>
            <p v-if="!dialog.summary.lines.length" class="mt-1 text-xs text-muted-foreground">লেখা একই, শুধু শিরোনাম/বিভাগ বদলেছে। Same text; only the title or category changed.</p>
          </template>
        </div>

        <div class="grid gap-1.5">
          <Label for="rule-note">
            নোট <span class="text-xs text-muted-foreground">{{ noteRequired ? 'Note — required' : 'Note — optional' }}</span>
          </Label>
          <Textarea id="rule-note" v-model="dialog.note" rows="3" :placeholder="noteRequired ? 'যেমন: HR_SETTLEMENT_RULES §5 অনুযায়ী · e.g. Matches HR_SETTLEMENT_RULES §5' : ''" />
        </div>

        <DialogFooter>
          <Button variant="ghost" @click="dialog = null">বাদ দিন · Cancel</Button>
          <Button :variant="dialog.action === 'retire' ? 'destructive' : 'default'" :disabled="busy || !dialogReady" data-action="submit" @click="submitDialog">
            <Loader2 v-if="busy" class="animate-spin" />
            <template v-if="dialog.action === 'save'">সংরক্ষণ · Save</template>
            <template v-else-if="dialog.action === 'confirm'">নিশ্চিত করুন · Confirm</template>
            <template v-else-if="dialog.action === 'retire'">বাতিল করুন · Retire</template>
            <template v-else>পুনরুদ্ধার · Restore</template>
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>

    <!-- ───── the print copy: shown only when printing (index.css) ───── -->
    <Teleport to="body">
      <div v-if="page && shown" class="rulebook-print" aria-hidden="true">
        <header class="rulebook-print-header">
          <p class="rulebook-print-kicker">মানব সম্পদ · নিয়মাবলি · Rulebook</p>
          <h1>{{ page.title }}</h1>
          <p>বিভাগ · Category: {{ category?.bn }} · {{ category?.en }} &nbsp;|&nbsp; সংস্করণ · Version: v{{ shown.version }} ({{ KIND[shown.kind]?.en }})</p>
          <p v-if="page.confirmedVersion">
            Confirmed v{{ page.confirmedVersion }} · confirmed by {{ page.confirmedBy }} on {{ isoDate(page.confirmedAtUtc) }}
          </p>
          <p v-else>Not confirmed — draft</p>
          <p v-if="shown.version !== page.confirmedVersion && shown.kind !== 'retired'" class="rulebook-print-warning">
            এই প্রিন্ট v{{ shown.version }}, নিশ্চিত লেখা নয়। This printout is v{{ shown.version }}, not the confirmed text.
          </p>
        </header>
        <MarkdownBody :content="pageHtml" class="rule-page" />
        <footer class="rulebook-print-footer">Mostisko · মানব সম্পদ · printed {{ fmt(printedAt) }}</footer>
      </div>
    </Teleport>
  </div>
</template>
