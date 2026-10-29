<script setup>
/**
 * এইচআর ওয়ার্কস্পেস — রিপোর্ট · Reports (docs/HRIS_WORKSPACE_PROMPT.md §3.2).
 *
 * A gallery of pins: the shared board HR curates (what DMD Sir opens) or the user's
 * own. Pins are references, so the board is run again on every open and every filter
 * change: the server hands each chart and KPI pin to Matrix with the filter bar added,
 * and a pin whose dataset lacks a filter's field says "not applicable". The filters
 * live in the URL, so a filtered board can be shared as a link.
 *
 * One column on a phone, two on a tablet, three on a desktop; S, M and L span more.
 * Editors (action:hris.pin for the shared board; anyone for their own) drag to
 * reorder, resize and remove — the server checks again. Exports: a pin or the board
 * as .xlsx (the server's), a chart as PNG (the widget's own button), and the board
 * as PDF through the print view (?print=1) and the browser's Print.
 */
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { FileSpreadsheet, Loader2, Printer, RefreshCw, X } from '@lucide/vue'
import {
  deleteHrisPin, downloadHrisExport, getMatrixDataset, listHrisPins, orderHrisPins, renderHrisPins, updateHrisPin,
} from '@/services/index.js'
import { Button } from '@/components/ui/button'
import HrisPin from '@/components/hris/HrisPin.vue'
import MatrixViewer from '@/components/matrix/MatrixViewer.vue'
import { formatDate } from '@/lib/matrix-format.js'

const props = defineProps({
  sections: { type: Array, default: () => [] },
  actions: { type: Array, default: () => [] },
})
const route = useRoute()
const router = useRouter()

const FILTERS = [
  ['unit', 'ইউনিট', 'Unit'], ['department', 'বিভাগ', 'Department'], ['section', 'সেকশন', 'Section'],
  ['line_type', 'লাইনের ধরন', 'Line type'], ['line', 'লাইন', 'Line'],
]
const SPAN = { S: '', M: 'xl:col-span-2', L: 'md:col-span-2 xl:col-span-3' }

const board = computed(() => (route.query.board === 'personal' ? 'personal' : 'shared'))
const printing = computed(() => !!route.query.print)
const filters = reactive({ unit: '', department: '', section: '', line_type: '', line: '', from: '', to: '' })
const pins = ref([])
const run = ref(null)
const loading = ref(false)
const error = ref('')
const dragging = ref(null)
const viewer = ref(null)

const canExport = computed(() => props.actions.includes('action:hris.export'))
const canEdit = computed(() => !printing.value && (board.value === 'personal' || props.actions.includes('action:hris.pin')))
const canDrill = computed(() => props.sections.includes('section:matrix.data'))
const renders = computed(() => Object.fromEntries((run.value?.pins ?? []).map((p) => [p.id, p])))
const options = computed(() => run.value?.filterOptions ?? {})
const asked = computed(() => FILTERS.filter(([k]) => filters[k] !== '').map(([k, bn, en]) => `${bn} ${en}: ${filters[k]}`)
  .concat(filters.from || filters.to ? [`তারিখ Dates: ${filters.from || '…'} – ${filters.to || '…'}`] : []))

/** The filter bar as the server takes it. */
function body() {
  const one = (v) => (v === '' ? null : [v])
  return {
    unit: one(filters.unit), department: one(filters.department), section: one(filters.section), lineType: one(filters.line_type),
    line: filters.line === '' ? null : [Number(filters.line)], from: filters.from || null, to: filters.to || null,
  }
}

/** URL → filters. */
function readUrl() {
  for (const k of Object.keys(filters)) filters[k] = String(route.query[`f_${k}`] ?? '')
}
/** Filters → URL (replace: a filter change is not a step back). */
function writeUrl() {
  const q = { ...route.query }
  for (const [k, v] of Object.entries(filters)) { if (v === '') delete q[`f_${k}`]; else q[`f_${k}`] = v }
  router.replace({ path: route.path, query: q })
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    pins.value = await listHrisPins(board.value)
    await refresh()
  } catch (e) { error.value = e.message } finally { loading.value = false }
}

async function refresh() {
  run.value = null
  try { run.value = await renderHrisPins(board.value, body()) } catch (e) { error.value = e.message }
}

function setBoard(b) {
  router.replace({ path: route.path, query: { ...route.query, board: b === 'shared' ? undefined : b } })
}

function changeFilter() { writeUrl() }
function clearFilters() { for (const k of Object.keys(filters)) filters[k] = ''; writeUrl() }

async function remove(id) {
  if (!confirm('এই পিনটি সরাবেন? Remove this pin?')) return
  try { await deleteHrisPin(id); pins.value = pins.value.filter((p) => p.id !== id) } catch (e) { error.value = e.message }
}
async function resize(id, size) {
  try { const p = await updateHrisPin(id, { size }); pins.value = pins.value.map((x) => (x.id === id ? p : x)) } catch (e) { error.value = e.message }
}
async function drop(targetId) {
  const from = dragging.value
  dragging.value = null
  if (!from || from === targetId) return
  const ids = pins.value.map((p) => p.id)
  ids.splice(ids.indexOf(from), 1)
  ids.splice(ids.indexOf(targetId), 0, from)
  const before = pins.value
  pins.value = ids.map((i) => before.find((p) => p.id === i))
  try { pins.value = await orderHrisPins(board.value, ids) } catch (e) { pins.value = before; error.value = e.message }
}

async function exportPin(id) {
  try { await downloadHrisExport('pin', { pin: id, filters: body() }) } catch (e) { error.value = e.message }
}
async function exportBoard() {
  try { await downloadHrisExport('board', { board: board.value, filters: body() }) } catch (e) { error.value = e.message }
}
function printNow() { window.print() }
function printView() { router.push({ path: route.path, query: { ...route.query, print: 1 } }) }
function closePrint() { const q = { ...route.query }; delete q.print; router.push({ path: route.path, query: q }) }

/** A drill-down: Matrix's viewer on the clicked rows — for someone who may open Matrix's data. */
async function drill({ datasetId, cite }) {
  if (!canDrill.value || !datasetId) return
  try { viewer.value = { dataset: await getMatrixDataset(datasetId), highlight: cite ?? '' } } catch (e) { error.value = e.message }
}

watch(board, load)
watch(() => Object.keys(filters).map((k) => route.query[`f_${k}`]).join('|'), () => { readUrl(); refresh() })
watch(printing, (p) => document.documentElement.classList.toggle('mx-print', p), { immediate: true })
onMounted(() => { readUrl(); load() })
onBeforeUnmount(() => document.documentElement.classList.remove('mx-print'))
</script>

<template>
  <!-- The print view leaves the app: moved to <body>, the app hidden around it (index.css, Matrix's print rule). -->
  <Teleport to="body" :disabled="!printing">
  <section class="flex min-w-0 flex-col gap-3" :class="printing ? 'bg-background p-6' : ''" data-hris-reports
    :data-print-root="printing ? '' : undefined" :data-hris-print="printing ? '' : undefined">
    <div v-if="printing" class="flex flex-wrap items-center gap-2 print:hidden">
      <Button size="sm" variant="outline" @click="closePrint"><X class="size-4" /> বন্ধ <span class="text-xs opacity-80">Close</span></Button>
      <Button size="sm" data-hris-print-button @click="printNow"><Printer class="size-4" /> মুদ্রণ / PDF <span class="text-xs opacity-80">Print / PDF</span></Button>
    </div>
    <header v-if="printing" class="border-b pb-2" data-hris-print-head>
      <h2 class="text-lg font-semibold">{{ board === 'shared' ? 'শেয়ার্ড বোর্ড · Shared board' : 'আমার পিন · My pins' }}</h2>
      <p class="text-sm text-muted-foreground">{{ asked.length ? asked.join(' · ') : 'ফিল্টার নেই · No filters' }}</p>
      <p class="text-xs text-muted-foreground">{{ formatDate(new Date().toISOString().slice(0, 10)) }} · Mostisko · HRIS Workspace</p>
    </header>

    <template v-if="!printing">
      <div class="flex flex-wrap items-center gap-2">
        <div class="flex rounded-md border p-0.5" role="group" aria-label="Board">
          <button v-for="[b, bn, en] in [['shared', 'শেয়ার্ড বোর্ড', 'Shared board'], ['personal', 'আমার পিন', 'My pins']]" :key="b" type="button"
            class="rounded px-2 py-1 text-sm" :class="board === b ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:text-foreground'"
            :data-hris-board="b" @click="setBoard(b)">{{ bn }} <span class="hidden text-xs opacity-75 sm:inline">{{ en }}</span></button>
        </div>
        <div class="ml-auto flex flex-wrap gap-1">
          <Button size="sm" variant="ghost" title="Refresh" aria-label="Refresh" @click="load"><RefreshCw class="size-4" /></Button>
          <Button v-if="canExport" size="sm" variant="outline" data-hris-board-xlsx @click="exportBoard"><FileSpreadsheet class="size-4" /> <span class="text-xs">.xlsx</span></Button>
          <Button v-if="canExport" size="sm" variant="outline" data-hris-board-print @click="printView"><Printer class="size-4" /> <span class="text-xs">PDF</span></Button>
        </div>
      </div>

      <!-- The filter bar: sticky, every value from the data the board reads. -->
      <div class="sticky top-0 z-10 grid grid-cols-2 gap-2 rounded-lg border bg-card/95 p-2 backdrop-blur sm:flex sm:flex-wrap sm:items-end" data-hris-filters>
        <label v-for="[k, bn, en] in FILTERS" :key="k" class="flex min-w-0 flex-col gap-0.5 text-xs text-muted-foreground">
          <span>{{ bn }} <span class="opacity-75">{{ en }}</span></span>
          <select v-model="filters[k]" class="h-8 min-w-0 rounded border bg-background px-1 text-sm text-foreground sm:w-36" :data-hris-filter="k" @change="changeFilter">
            <option value="">সব · All</option>
            <option v-for="v in options[k] ?? []" :key="v" :value="String(v)">{{ k === 'line' ? `Line ${v}` : v }}</option>
          </select>
        </label>
        <label class="flex min-w-0 flex-col gap-0.5 text-xs text-muted-foreground">
          <span>থেকে <span class="opacity-75">From</span></span>
          <input v-model="filters.from" type="date" class="h-8 min-w-0 rounded border bg-background px-1 text-sm text-foreground" data-hris-filter="from" @change="changeFilter" />
        </label>
        <label class="flex min-w-0 flex-col gap-0.5 text-xs text-muted-foreground">
          <span>পর্যন্ত <span class="opacity-75">To</span></span>
          <input v-model="filters.to" type="date" class="h-8 min-w-0 rounded border bg-background px-1 text-sm text-foreground" data-hris-filter="to" @change="changeFilter" />
        </label>
        <Button v-if="asked.length" size="sm" variant="ghost" class="col-span-2 sm:col-span-1" @click="clearFilters"><X class="size-4" /> মুছুন <span class="text-xs">Clear</span></Button>
      </div>
    </template>

    <p v-if="error" class="text-sm text-destructive" role="alert">{{ error }}</p>
    <p v-if="loading && !pins.length" class="flex items-center gap-2 text-sm text-muted-foreground"><Loader2 class="size-4 animate-spin" /> লোড হচ্ছে… <span class="text-xs">Loading</span></p>
    <p v-else-if="!pins.length" class="rounded-lg border border-dashed p-6 text-center text-sm text-muted-foreground" data-hris-empty>
      এখানে এখনো কোনো পিন নেই। <span class="text-xs">No pins yet — pin a chart or an answer from Ask.</span>
    </p>

    <div class="grid min-w-0 grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-3">
      <HrisPin v-for="p in pins" :key="p.id" :class="SPAN[p.size] ?? ''" :pin="p" :render="renders[p.id] ?? null"
        :can-edit="canEdit" :can-export="canExport && !printing" :print-mode="printing"
        @drill="drill" @remove="remove" @resize="resize" @export="exportPin" @dragstart="(id) => (dragging = id)" @drop="drop" />
    </div>

    <div v-if="viewer" class="fixed inset-0 z-50 flex flex-col bg-background p-2 sm:p-4" data-hris-viewer>
      <MatrixViewer :dataset="viewer.dataset" :highlight="viewer.highlight" @close="viewer = null" @clear-highlight="viewer = { ...viewer, highlight: '' }" />
    </div>
  </section>
  </Teleport>
</template>
