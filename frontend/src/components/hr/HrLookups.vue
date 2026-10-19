<script setup>
/**
 * রেজিস্ট্রি (Registry) — the lookup registry (docs/HR_PROCEDURE_ANALYZER_PROMPT.md §3.3).
 *
 * What each code value means: (Column, Value) → Label, seeded from
 * docs/hr/hr-lookups.csv and filled by answers. Search, filter by column, and —
 * for DevOps and SystemAdmin — add or edit a row. Every change after the seed is
 * in the change log, which nothing can edit. Theme tokens only; usable at 360 px.
 */
import { computed, onMounted, ref, watch } from 'vue'
import { ArrowLeft, History, Loader2, Pencil, Plus, Save, Search, X } from '@lucide/vue'
import { listLookupLog, listLookups, saveLookup } from '@/services/hr.service.js'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import { toDate } from '@/lib/time'

defineProps({ canEdit: { type: Boolean, default: false } })
const emit = defineEmits(['back'])

const view = ref('rows')   // rows | log
const rows = ref([])
const columns = ref([])
const log = ref([])
const loading = ref(true)
const error = ref('')

const q = ref('')
const column = ref('all')

/** The row being edited: { column, value, label, note, isNew }. */
const editing = ref(null)
const saving = ref(false)

let timer = null
async function load() {
  error.value = ''
  try {
    const res = await listLookups({ q: q.value.trim(), column: column.value === 'all' ? '' : column.value })
    rows.value = res.lookups ?? []
    columns.value = res.columns ?? []
  } catch (e) {
    error.value = e.message || 'The registry could not be loaded.'
  } finally {
    loading.value = false
  }
}
async function loadLog() {
  try { log.value = (await listLookupLog()).log ?? [] } catch (e) { error.value = e.message }
}
onMounted(load)
watch(column, load)
watch(q, () => { clearTimeout(timer); timer = setTimeout(load, 250) })
watch(view, (v) => { if (v === 'log') loadLog() })

const shown = computed(() => rows.value.slice(0, 500))

function fmt(value) {
  const d = toDate(value)
  return d ? d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : ''
}

function edit(r) {
  editing.value = r
    ? { column: r.column, value: r.value, label: r.label ?? '', note: r.note ?? '', isNew: false }
    : { column: column.value === 'all' ? '' : column.value, value: '', label: '', note: '', isNew: true }
}

async function save() {
  const e = editing.value
  saving.value = true
  error.value = ''
  try {
    await saveLookup({ column: e.column.trim(), value: e.value.trim(), label: e.label.trim(), note: e.note.trim() })
    editing.value = null
    await load()
  } catch (err) {
    error.value = err.message || 'The row could not be saved.'
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="mx-auto w-full max-w-5xl p-4 sm:p-6" data-testid="lookups-page">
    <Button size="sm" variant="ghost" @click="emit('back')"><ArrowLeft /> প্রসিডিউর <span class="text-xs opacity-70">Procedures</span></Button>

    <div class="mt-2 flex flex-wrap items-start justify-between gap-3">
      <div class="min-w-0">
        <h2 class="text-lg font-semibold">রেজিস্ট্রি <span class="text-sm font-normal text-muted-foreground">Lookup registry</span></h2>
        <p class="mt-1 text-sm text-muted-foreground">
          প্রতিটি কোডের মানে। এখানে যা নেই, তা অনুমান করা হয় না — প্রশ্ন খোলা হয়।
          <span class="text-xs">What each code value means. A value not here is never guessed; a question is opened.</span>
        </p>
      </div>
      <span class="flex flex-wrap gap-2">
        <Button size="sm" :variant="view === 'log' ? 'secondary' : 'outline'" @click="view = view === 'log' ? 'rows' : 'log'">
          <History /> পরিবর্তনের লগ <span class="text-xs opacity-70">Change log</span>
        </Button>
        <Button v-if="canEdit && view === 'rows'" size="sm" @click="edit(null)"><Plus /> নতুন <span class="text-xs opacity-80">Add</span></Button>
      </span>
    </div>

    <p v-if="error" class="mt-3 text-sm text-destructive" role="alert">{{ error }}</p>

    <!-- add / edit -->
    <div v-if="editing" class="mt-4 grid gap-2 rounded-lg border bg-card p-3 sm:grid-cols-[10rem_8rem_1fr]">
      <Input v-model="editing.column" class="h-8 font-mono" placeholder="Column" aria-label="Column" :disabled="!editing.isNew" />
      <Input v-model="editing.value" class="h-8 font-mono" placeholder="Value" aria-label="Value" :disabled="!editing.isNew" />
      <Input v-model="editing.label" class="h-8" placeholder="লেবেল · Label" aria-label="Label" />
      <Input v-model="editing.note" class="h-8 sm:col-span-3" placeholder="নোট · Note" aria-label="Note" />
      <span class="flex gap-2 sm:col-span-3">
        <Button size="sm" :disabled="saving || !editing.column.trim() || !editing.value.trim()" @click="save">
          <Loader2 v-if="saving" class="animate-spin" /><Save v-else /> সংরক্ষণ <span class="text-xs opacity-80">Save</span>
        </Button>
        <Button size="sm" variant="ghost" @click="editing = null"><X /> বাতিল <span class="text-xs opacity-70">Cancel</span></Button>
      </span>
    </div>

    <template v-if="view === 'rows'">
      <div class="mt-4 grid gap-2 sm:grid-cols-[1fr_auto]">
        <div class="relative">
          <Search class="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
          <Input v-model="q" class="h-9 pl-8" placeholder="মান বা লেবেলে খুঁজুন · Search value and label" aria-label="Search the registry" />
        </div>
        <Select v-model="column">
          <SelectTrigger class="h-9 w-full sm:w-56" aria-label="Column"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value="all">সব কলাম · All columns</SelectItem>
            <SelectItem v-for="c in columns" :key="c" :value="c">{{ c }}</SelectItem>
          </SelectContent>
        </Select>
      </div>

      <div v-if="loading" class="mt-8 flex justify-center text-muted-foreground"><Loader2 class="size-5 animate-spin" aria-label="Loading" /></div>
      <p v-else-if="!rows.length" class="mt-6 rounded-lg border border-dashed p-6 text-center text-sm text-muted-foreground">কিছু মেলেনি। <span class="text-xs">Nothing matches.</span></p>
      <div v-else class="mt-3 max-w-full overflow-x-auto rounded-md border">
        <table class="w-full text-left text-xs">
          <thead class="bg-muted/50 text-muted-foreground">
            <tr>
              <th class="px-2 py-1.5 font-medium">Column</th><th class="px-2 py-1.5 font-medium">Value</th>
              <th class="px-2 py-1.5 font-medium">লেবেল · Label</th><th class="hidden px-2 py-1.5 font-medium sm:table-cell">Source</th>
              <th v-if="canEdit" class="px-2 py-1.5"><span class="sr-only">Edit</span></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="r in shown" :key="`${r.column}:${r.value}`" class="border-t align-top">
              <td class="px-2 py-1.5 font-mono">{{ r.column }}</td>
              <td class="px-2 py-1.5 font-mono">{{ r.value }}</td>
              <td class="px-2 py-1.5">
                <span v-if="r.label">{{ r.label }}</span><span v-else class="text-muted-foreground">— unknown</span>
                <span v-if="r.note" class="block text-muted-foreground">{{ r.note }}</span>
              </td>
              <td class="hidden px-2 py-1.5 text-muted-foreground sm:table-cell">{{ r.source }}<template v-if="r.updatedBy"> · {{ r.updatedBy }}</template></td>
              <td v-if="canEdit" class="px-2 py-1.5 text-right">
                <Button size="icon" variant="ghost" class="size-7" :aria-label="`Edit ${r.column} ${r.value}`" @click="edit(r)"><Pencil /></Button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <p v-if="rows.length > shown.length" class="mt-2 text-xs text-muted-foreground">প্রথম {{ shown.length }} টি · First {{ shown.length }} of {{ rows.length }}; search to narrow.</p>
    </template>

    <!-- the change log -->
    <template v-else>
      <p v-if="!log.length" class="mt-6 rounded-lg border border-dashed p-6 text-center text-sm text-muted-foreground">
        এখনো কোনো পরিবর্তন নেই। <span class="text-xs">No changes since the seed.</span>
      </p>
      <ul v-else class="mt-4 grid gap-1.5" data-testid="lookups-log">
        <li v-for="l in log" :key="l.id" class="rounded-md border bg-card px-3 py-2 text-xs">
          <span class="font-mono font-medium">{{ l.column }} {{ l.value }}</span>:
          <span class="text-muted-foreground line-through" v-if="l.oldLabel">{{ l.oldLabel }}</span>
          {{ l.newLabel ?? '—' }}
          <template v-if="l.newNote && l.newNote !== l.oldNote"> · note “{{ l.newNote }}”</template>
          <span class="block text-muted-foreground">{{ l.reason }} · {{ l.changedBy ?? '—' }} · {{ fmt(l.atUtc) }}</span>
        </li>
      </ul>
    </template>
  </div>
</template>
