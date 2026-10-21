<script setup>
/**
 * ম্যাট্রিক্স — a dashboard (docs/MATRIX_PROMPT.md §3.8).
 *
 * The layout is a 12-column grid of widgets (x, y, w, h); on a phone the widgets stack
 * in one column, top to bottom. The filters (dates, unit, section, line, emp type,
 * status, shift) go to the server with every run and reach each widget whose dataset
 * has the field; a widget they cannot reach says so. "Compare with previous" adds the
 * previous snapshot. The editor (action:matrix.dashboards.edit — the server checks it
 * again on save) adds, moves, resizes, retypes, renames and removes widgets. The print
 * view (?print=1) is the dashboard alone, for the browser's Print / PDF.
 */
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ArrowLeft, Check, Copy, FileJson, GitCompare, Loader2, Pencil, Plus, Printer, Save, X } from '@lucide/vue'
import {
  createMatrixDashboard, downloadMatrixExport, getMatrixDashboard, listMatrixKpis, runMatrixDashboard, saveMatrixDashboard,
} from '@/services/index.js'
import { Button } from '@/components/ui/button'
import MatrixWidget from '@/components/matrix/MatrixWidget.vue'

const props = defineProps({
  id: { type: Number, required: true },
  print: { type: Boolean, default: false },
  /** action:matrix.dashboards.edit, as /api/me/access gave it. */
  canEditAction: { type: Boolean, default: false },
})
const emit = defineEmits(['drill', 'close'])
const route = useRoute()
const router = useRouter()

const dash = ref(null)
const layout = ref({ widgets: [] })
const run = ref(null)
const error = ref('')
const loading = ref(false)
const editing = ref(false)
const saving = ref(false)
const saved = ref(false)
const compare = ref(false)
const name = ref('')
const kpis = ref([])
const add = reactive({ kpi: '', type: 'bar' })
const filters = reactive({ unit: '', section: '', line: '', emp_type: '', status: '', shift: '', from: '', to: '' })

/** A plain copy of a layout (structuredClone refuses Vue's reactive proxies). */
const copyOf = (x) => JSON.parse(JSON.stringify(x ?? { widgets: [] }))

const FILTERS = [
  ['unit', 'ইউনিট', 'Unit'], ['section', 'সেকশন', 'Section'], ['line', 'লাইন', 'Line'], ['emp_type', 'কর্মীর ধরন', 'Emp type'],
  ['status', 'অবস্থা', 'Status'], ['shift', 'শিফট', 'Shift'],
]
const TYPES = ['kpi', 'bar', 'stacked-bar', 'line', 'donut', 'gauge', 'heatmap', 'table', 'timeline']

const results = computed(() => Object.fromEntries((run.value?.widgets ?? []).map((w) => [w.id, w])))
const ordered = computed(() => [...(layout.value.widgets ?? [])].sort((a, b) => a.y - b.y || a.x - b.x))
const options = computed(() => run.value?.filterOptions ?? {})
const canEdit = computed(() => props.canEditAction && !!dash.value?.canEdit && !props.print)

function body() {
  const one = (v) => (v === '' || v === null ? null : [v])
  return {
    unit: one(filters.unit), section: one(filters.section), line: filters.line === '' ? null : [Number(filters.line)],
    empType: one(filters.emp_type), status: one(filters.status), shift: one(filters.shift),
    from: filters.from || null, to: filters.to || null,
  }
}

async function load() {
  error.value = ''
  loading.value = true
  try {
    dash.value = await getMatrixDashboard(props.id)
    layout.value = copyOf(dash.value.layout)
    name.value = dash.value.name
    await refresh()
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

async function refresh() {
  try {
    run.value = await runMatrixDashboard(props.id, { filters: body(), compare: compare.value })
  } catch (e) {
    error.value = e.message
  }
}

let timer = null
watch(filters, () => { clearTimeout(timer); timer = setTimeout(refresh, 250) })
watch(compare, refresh)
watch(() => props.id, load)

// ---- editor ----
async function startEdit() {
  editing.value = true
  saved.value = false
  if (!kpis.value.length) kpis.value = (await listMatrixKpis().catch(() => ({ kpis: [] }))).kpis
  add.kpi ||= kpis.value[0]?.key ?? ''
}
function cancelEdit() {
  editing.value = false
  layout.value = copyOf(dash.value.layout)
  name.value = dash.value.name
}
function changeWidget(next) {
  layout.value = { ...layout.value, widgets: layout.value.widgets.map((w) => (w.id === next.id ? next : w)) }
}
function removeWidget(id) {
  layout.value = { ...layout.value, widgets: layout.value.widgets.filter((w) => w.id !== id) }
}
function addWidget() {
  const kpi = kpis.value.find((k) => k.key === add.kpi)
  if (!kpi) return
  // The dashboard's own dataset when the KPI reads that kind; else the current one of the KPI's first kind.
  const refs = layout.value.widgets.map((w) => w.dataset ?? w.query?.dataset).filter(Boolean)
  const fit = refs.find((r) => kpi.kinds?.some((k) => r === `current:${k}`))
  const dataset = fit ?? (kpi.kinds?.length ? `current:${kpi.kinds[0]}` : undefined)
  const bottom = Math.max(0, ...layout.value.widgets.map((w) => w.y + w.h))
  const tile = add.type === 'kpi'
  layout.value = { ...layout.value, widgets: [...layout.value.widgets, {
    id: `w-${Date.now().toString(36)}`, type: add.type, kpi: kpi.key, ...(dataset ? { dataset } : {}),
    x: 0, y: bottom, w: tile ? 3 : 6, h: tile ? 2 : 4, options: {},
  }] }
}
async function save() {
  saving.value = true
  error.value = ''
  try {
    dash.value = await saveMatrixDashboard(props.id, { name: name.value, layout: layout.value })
    layout.value = copyOf(dash.value.layout)
    editing.value = false
    saved.value = true
    await refresh()
  } catch (e) {
    error.value = e.message
  } finally {
    saving.value = false
  }
}
async function copy() {
  try {
    const d = await createMatrixDashboard({ copyOf: props.id })
    router.push({ path: route.path, query: { dashboard: d.id } })
  } catch (e) { error.value = e.message }
}

/** The browser's own print dialog — and so its "Save as PDF". */
function printNow() { window.print() }

function printView() {
  router.push({ path: route.path, query: { dashboard: props.id, print: 1 } })
}

onMounted(() => {
  if (props.print) document.documentElement.classList.add('mx-print')
  load()
})
onBeforeUnmount(() => {
  document.documentElement.classList.remove('mx-print')
  clearTimeout(timer)
})
</script>

<template>
  <!-- The print view leaves the app: it is moved to <body>, and the app is hidden around it (index.css). -->
  <Teleport to="body" :disabled="!print">
  <section class="flex min-w-0 flex-col gap-3" :class="print ? 'bg-background p-6' : ''" data-dashboard :data-dashboard-id="id" :data-print-root="print ? '' : undefined">
    <!-- the print view's own bar: outside the printed part -->
    <div v-if="print" class="flex flex-wrap items-center gap-2 print:hidden">
      <Button size="sm" variant="outline" @click="router.push({ path: route.path, query: { dashboard: id } })"><ArrowLeft class="size-4" /> Back</Button>
      <Button size="sm" data-print-button @click="printNow"><Printer class="size-4" /> মুদ্রণ / PDF <span class="text-xs opacity-80">Print / PDF</span></Button>
    </div>

    <div v-else class="flex flex-wrap items-center gap-2">
      <Button size="sm" variant="ghost" aria-label="All dashboards" @click="emit('close')"><ArrowLeft class="size-4" /></Button>
      <div class="min-w-0 flex-1">
        <input v-if="editing" v-model="name" class="h-8 w-full max-w-sm rounded-md border bg-background px-2 text-base font-semibold" aria-label="Dashboard name" data-dash-name />
        <h3 v-else class="truncate text-base font-semibold" data-dash-title>
          {{ dash?.nameBn ?? dash?.name }} <span v-if="dash?.nameBn" class="text-sm font-normal text-muted-foreground">{{ dash?.name }}</span>
          <span v-else-if="dash" class="sr-only">{{ dash.name }}</span>
        </h3>
      </div>
      <span v-if="saved" class="flex items-center gap-1 text-xs text-muted-foreground" role="status" data-dash-saved><Check class="size-3.5" /> সংরক্ষিত Saved</span>
      <Button size="sm" :variant="compare ? 'secondary' : 'outline'" :aria-pressed="compare" data-dash-compare @click="compare = !compare">
        <GitCompare class="size-4" /> আগের সাথে <span class="text-xs opacity-70">Compare</span>
      </Button>
      <template v-if="canEdit && !editing">
        <Button size="sm" variant="outline" data-dash-edit @click="startEdit"><Pencil class="size-4" /> সম্পাদনা <span class="text-xs opacity-70">Edit</span></Button>
        <Button size="sm" variant="ghost" title="Copy" aria-label="Copy dashboard" data-dash-copy @click="copy"><Copy class="size-4" /></Button>
      </template>
      <template v-if="editing">
        <Button size="sm" :disabled="saving" data-dash-save @click="save">
          <Loader2 v-if="saving" class="size-4 animate-spin" /><Save v-else class="size-4" /> সংরক্ষণ <span class="text-xs opacity-70">Save</span>
        </Button>
        <Button size="sm" variant="ghost" @click="cancelEdit"><X class="size-4" /> Cancel</Button>
      </template>
      <Button size="sm" variant="ghost" title="Print / PDF" aria-label="Print view" data-dash-print @click="printView"><Printer class="size-4" /></Button>
      <Button size="sm" variant="ghost" title="Layout as JSON" aria-label="Export layout" data-dash-export @click="downloadMatrixExport('dashboard', id)"><FileJson class="size-4" /></Button>
    </div>

    <!-- filters -->
    <div v-if="!print" class="flex flex-wrap items-end gap-2 rounded-lg border bg-card p-2" data-dash-filters>
      <label class="flex flex-col text-xs text-muted-foreground">
        <span>থেকে <span class="opacity-70">From</span></span>
        <input v-model="filters.from" type="date" class="h-8 rounded-md border bg-background px-1.5 text-sm text-foreground" data-filter="from" />
      </label>
      <label class="flex flex-col text-xs text-muted-foreground">
        <span>পর্যন্ত <span class="opacity-70">To</span></span>
        <input v-model="filters.to" type="date" class="h-8 rounded-md border bg-background px-1.5 text-sm text-foreground" data-filter="to" />
      </label>
      <label v-for="[key, bn, en] in FILTERS.filter(([k]) => options[k]?.length)" :key="key" class="flex min-w-0 flex-col text-xs text-muted-foreground">
        <span>{{ bn }} <span class="opacity-70">{{ en }}</span></span>
        <select v-model="filters[key]" class="h-8 max-w-[11rem] rounded-md border bg-background px-1 text-sm text-foreground" :data-filter="key">
          <option value="">সব · All</option>
          <option v-for="v in options[key]" :key="v" :value="String(v)">{{ key === 'line' ? `Line ${v}` : v }}</option>
        </select>
      </label>
      <Button v-if="Object.values(filters).some((v) => v)" size="sm" variant="ghost"
        @click="Object.assign(filters, { unit: '', section: '', line: '', emp_type: '', status: '', shift: '', from: '', to: '' })">
        <X class="size-4" /> Clear
      </Button>
    </div>

    <!-- add a widget -->
    <div v-if="editing" class="flex flex-wrap items-end gap-2 rounded-lg border border-dashed bg-card p-2 text-sm" data-dash-add>
      <label class="flex min-w-0 flex-col text-xs text-muted-foreground">
        <span>কেপিআই <span class="opacity-70">KPI</span></span>
        <select v-model="add.kpi" class="h-8 max-w-[16rem] rounded-md border bg-background px-1 text-sm text-foreground" data-add-kpi>
          <option v-for="k in kpis" :key="k.key" :value="k.key">{{ k.nameBn }} · {{ k.nameEn }}</option>
        </select>
      </label>
      <label class="flex flex-col text-xs text-muted-foreground">
        <span>ধরন <span class="opacity-70">Type</span></span>
        <select v-model="add.type" class="h-8 rounded-md border bg-background px-1 text-sm text-foreground" data-add-type>
          <option v-for="t in TYPES" :key="t" :value="t">{{ t }}</option>
        </select>
      </label>
      <Button size="sm" variant="outline" data-add-widget @click="addWidget"><Plus class="size-4" /> যোগ করুন <span class="text-xs opacity-70">Add widget</span></Button>
    </div>

    <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>
    <p v-if="loading && !run" class="flex items-center gap-2 text-sm text-muted-foreground"><Loader2 class="size-4 animate-spin" /> Loading…</p>

    <div :data-print-view="print ? '' : undefined" class="min-w-0">
      <h3 v-if="print" class="mb-2 text-base font-semibold">{{ dash?.nameBn ?? dash?.name }} <span class="text-sm font-normal text-muted-foreground">{{ dash?.name }}</span></h3>
      <div class="mx-grid">
        <MatrixWidget v-for="w in ordered" :key="w.id" class="mx-cell" :style="{ '--x': w.x + 1, '--w': w.w, '--y': w.y + 1, '--h': w.h }"
          :widget="w" :result="results[w.id] ?? null" :editing="editing" :compare-mode="compare" :print-mode="print"
          @drill="(d) => emit('drill', d)" @change="changeWidget" @remove="removeWidget(w.id)" />
      </div>
    </div>
  </section>
  </Teleport>
</template>

<style scoped>
.mx-grid {
  display: grid;
  grid-template-columns: repeat(12, minmax(0, 1fr));
  grid-auto-rows: 4.25rem;
  gap: 0.75rem;
}
.mx-cell {
  grid-column: var(--x) / span var(--w);
  grid-row: var(--y) / span var(--h);
}
/* A phone: one column, the widgets in reading order (the list is sorted by y, then x). */
@media (max-width: 639px) {
  .mx-grid { grid-template-columns: minmax(0, 1fr); }
  .mx-cell { grid-column: 1 / -1; grid-row: auto / span var(--h); }
}
@media print {
  .mx-grid { gap: 0.5rem; }
}
</style>
