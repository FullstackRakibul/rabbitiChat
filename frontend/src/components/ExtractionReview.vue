<script setup>
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import {
  Check, ChevronLeft, ChevronRight, Loader2, RotateCcw, X, ZoomIn, ZoomOut,
} from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import {
  extractDocument, fetchDocumentFile, confirmExtraction, listBuyerProfiles,
  getExtraction, HttpError,
} from '@/services/index.js'

const props = defineProps({
  doc: { type: Object, required: true },     // { id, fileName }
  model: { type: String, default: '' },      // chat model for the LLM fallback

  /**
   * Workspace Studio renders this INSIDE its Review section rather than as a
   * full-screen overlay. Same logic, same markup, same checks — only the outer
   * chrome differs, so there is one review implementation rather than two.
   */
  embedded: { type: Boolean, default: false },
})
const emit = defineEmits(['close', 'confirmed'])

// ---- extraction state ----
const loading = ref(false)
const outcome = ref(null)                    // the /extract response
const extractError = ref('')
const profiles = ref([])
const profileId = ref('auto')                // 'auto' = let detection route

async function runExtract() {
  loading.value = true
  extractError.value = ''
  outcome.value = null
  try {
    outcome.value = await extractDocument(props.doc.id, {
      model: props.model || undefined,
      buyerProfileId: profileId.value === 'auto' ? undefined : Number(profileId.value),
    })
    resetAll()
  } catch (e) {
    extractError.value = e instanceof HttpError ? e.message : (e.message || 'Extraction failed.')
  } finally {
    loading.value = false
  }
}

// ---- editable copy ----
// `edit` is what the reviewer changes; `outcome.data` stays as the machine
// original for dirty-comparison and reset.
const edit = reactive({})

function deepClone(x) { return x == null ? x : JSON.parse(JSON.stringify(x)) }

function resetAll() {
  for (const k of Object.keys(edit)) delete edit[k]
  Object.assign(edit, deepClone(outcome.value?.data ?? {}))
  edit.header ??= {}
  edit.orderLine ??= {}
  edit.prepackComponents ??= []
}

function get(obj, path) {
  return path.split('.').reduce((o, k) => (o == null ? o : o[k]), obj)
}
function set(obj, path, value) {
  const keys = path.split('.')
  let o = obj
  for (const k of keys.slice(0, -1)) o = (o[k] ??= {})
  o[keys.at(-1)] = value
}

function machine(path) { return get(outcome.value?.data ?? {}, path) }
function isDirty(path) {
  return String(get(edit, path) ?? '') !== String(machine(path) ?? '')
}
function resetField(path) { set(edit, path, deepClone(machine(path))) }

const anyDirty = computed(() =>
  allPaths().some(p => isDirty(p)))

function allPaths() {
  const paths = [...headerFields.map(f => f.path), ...lineFields.map(f => f.path), 'purchaseOrderTotal']
  const comps = edit.prepackComponents ?? []
  comps.forEach((_, i) => compCols.forEach(c =>
    paths.push(`prepackComponents.${i}.${c.key}`)))
  return paths
}

// ---- field registry ----
const headerFields = [
  { label: 'Buyer', path: 'header.buyer' },
  { label: 'Order #', path: 'header.orderNumber' },
  { label: 'PO type', path: 'header.poType' },
  { label: 'PO date', path: 'header.poDate' },
  { label: 'Cancel date', path: 'header.cancelDate' },
  { label: 'Incoterms', path: 'header.incoterms' },
  { label: 'Delivery start', path: 'header.deliveryWindow.start' },
  { label: 'Delivery end', path: 'header.deliveryWindow.end' },
  { label: 'Shipping start', path: 'header.shippingWindow.start' },
  { label: 'Shipping end', path: 'header.shippingWindow.end' },
  { label: 'Exit factory', path: 'header.exitFactoryEndDate' },
  { label: 'Vendor', path: 'header.vendor.name' },
  { label: 'Plant', path: 'header.manufacturingPlant.name' },
]
const lineFields = [
  { label: 'Style number', path: 'orderLine.styleNumber' },
  { label: 'SKU', path: 'orderLine.sku' },
  { label: 'UPC', path: 'orderLine.upc' },
  { label: 'Store #', path: 'orderLine.storeNumber' },
  { label: 'Unit price', path: 'orderLine.unitPrice', money: true },
  { label: 'Quantity', path: 'orderLine.quantity', int: true },
  { label: 'Item total', path: 'orderLine.itemTotal', money: true },
  { label: 'UOM', path: 'orderLine.uom' },
]
const compCols = [
  { key: 'line', label: '#', int: true },
  { key: 'size', label: 'Size' },
  { key: 'color', label: 'Colour' },
  { key: 'sku', label: 'SKU' },
  { key: 'upc', label: 'UPC' },
  { key: 'qtyPerPrepack', label: 'Qty/pack', int: true },
  { key: 'unitCost', label: 'Unit cost', money: true },
  { key: 'retailPrice', label: 'Retail', money: true },
]

// ---- live derived + client-side checks ----
const num = v => { const n = Number(v); return Number.isFinite(n) ? n : null }

const derived = computed(() => {
  const comps = edit.prepackComponents ?? []
  const packs = num(edit.orderLine?.quantity)
  const piecesPerPack = comps.length ? comps.reduce((s, c) => s + (num(c.qtyPerPrepack) ?? 0), 0) : null
  const packPrice = comps.length ? comps.reduce((s, c) => s + (num(c.qtyPerPrepack) ?? 0) * (num(c.unitCost) ?? 0), 0) : null
  return {
    piecesPerPack,
    totalPacks: comps.length ? packs : null,
    totalPieces: comps.length ? (packs != null && piecesPerPack != null ? packs * piecesPerPack : null) : packs,
    packPrice: packPrice != null ? Math.round(packPrice * 100) / 100 : null,
    sizes: Object.entries(comps.reduce((m, c) => {
      const k = c.size ?? '?'
      m[k] = (m[k] ?? 0) + (num(c.qtyPerPrepack) ?? 0)
      return m
    }, {})),
  }
})

// The reviewer's live gate: fixing a value turns its check green in place.
const liveChecks = computed(() => {
  const d = derived.value
  const unit = num(edit.orderLine?.unitPrice)
  const qty = num(edit.orderLine?.quantity)
  const itemTotal = num(edit.orderLine?.itemTotal)
  const poTotal = num(edit.purchaseOrderTotal)
  const upc = String(edit.orderLine?.upc ?? '')
  const cents = (a, b) => a != null && b != null && Math.abs(a - b) <= 0.01

  const checks = []
  if (d.packPrice != null || unit != null)
    checks.push({
      check: 'unit_price_reconciles',
      passed: cents(d.packPrice, unit),
      detail: `pack contents ${d.packPrice ?? '—'} vs stated unit price ${unit ?? '—'}`,
    })
  checks.push({
    check: 'item_total_reconciles',
    passed: unit != null && qty != null && cents(Math.round(unit * qty * 100) / 100, itemTotal),
    detail: `${qty ?? '—'} × ${unit ?? '—'} vs stated total ${itemTotal ?? '—'}`,
  })
  if (poTotal != null)
    checks.push({
      check: 'po_total_matches_line_total',
      passed: cents(poTotal, itemTotal),
      detail: `PO total ${poTotal} vs line total ${itemTotal ?? '—'}`,
    })
  checks.push({
    check: 'upc_valid',
    passed: /^\d{12,14}$/.test(upc.trim()),
    detail: upc.trim() ? `"${upc.trim()}"` : 'missing',
  })
  return checks
})
const failingLive = computed(() => liveChecks.value.filter(c => !c.passed))

// ---- confirm ----
const overrideChecks = ref(false)
const overrideReason = ref('')
const confirming = ref(false)
const confirmed = ref(false)
const confirmError = ref('')

const canConfirm = computed(() =>
  !confirming.value && !confirmed.value && outcome.value?.data
  && (failingLive.value.length === 0
      || (overrideChecks.value && overrideReason.value.trim().length > 0)))

function coercePayload() {
  const p = deepClone(edit)
  const coerce = (obj, key, int) => {
    if (obj && obj[key] !== undefined && obj[key] !== null && obj[key] !== '') {
      const n = Number(obj[key]); if (Number.isFinite(n)) obj[key] = int ? Math.trunc(n) : n
    }
  }
  coerce(p.orderLine, 'unitPrice'); coerce(p.orderLine, 'quantity', true); coerce(p.orderLine, 'itemTotal')
  coerce(p, 'purchaseOrderTotal')
  for (const c of p.prepackComponents ?? []) {
    coerce(c, 'line', true); coerce(c, 'qtyPerPrepack', true); coerce(c, 'unitCost'); coerce(c, 'retailPrice')
  }
  return p
}

async function doConfirm() {
  if (!canConfirm.value) return
  confirming.value = true
  confirmError.value = ''
  try {
    const edits = allPaths().filter(isDirty)
      .map(p => ({ path: p, from: machine(p) ?? null, to: get(edit, p) ?? null }))
    if (overrideChecks.value)
      edits.push({ path: '_override', from: failingLive.value.map(c => c.check).join(','), to: overrideReason.value.trim() })
    await confirmExtraction(props.doc.id, coercePayload(), edits)
    confirmed.value = true
    emit('confirmed')
  } catch (e) {
    confirmError.value = e.message || 'Confirm failed.'
  } finally {
    confirming.value = false
  }
}

function close() {
  if (anyDirty.value && !confirmed.value
      && !confirm('Discard unsaved edits?')) return
  emit('close')
}

// ---- PDF viewer ----
const canvas = ref(null)
const pdfState = reactive({ page: 1, pages: 0, scale: 0, error: '', isPdf: false })
// The pdfjs document stays OUT of reactive state: Vue's proxy breaks its
// private-field access (#pagesNumber) the moment it is wrapped.
let pdfDoc = null
let renderTask = null

async function loadPdf() {
  pdfState.error = ''
  try {
    const blob = await fetchDocumentFile(props.doc.id)
    if (!(props.doc.fileName || '').toLowerCase().endsWith('.pdf')) {
      pdfState.isPdf = false
      pdfState.error = 'Preview is available for PDF files only — fields on the right still work.'
      return
    }
    pdfState.isPdf = true
    // Import the build entry (not the package root) and pair it with the
    // matching worker — mixing the two builds trips pdfjs's private-field checks.
    const pdfjs = await import('pdfjs-dist/build/pdf.mjs')
    pdfjs.GlobalWorkerOptions.workerSrc = (
      await import('pdfjs-dist/build/pdf.worker.mjs?url')
    ).default

    const buffer = new Uint8Array(await blob.arrayBuffer())
    pdfDoc = await pdfjs.getDocument({ data: buffer }).promise
    pdfState.pages = pdfDoc.numPages
    pdfState.page = 1
    pdfState.scale = 0                      // refit on load
    await nextTick()
    await renderPage()
  } catch (e) {
    pdfState.error = e.message || 'Could not load the source file.'
  }
}

async function renderPage() {
  if (!pdfDoc || !canvas.value) return
  const page = await pdfDoc.getPage(pdfState.page)
  // scale 0 = fit width once, then user-zoomable
  if (!pdfState.scale) {
    const containerWidth = canvas.value.parentElement.clientWidth - 16
    pdfState.scale = containerWidth / page.getViewport({ scale: 1 }).width
  }
  const dpr = window.devicePixelRatio || 1
  const viewport = page.getViewport({ scale: pdfState.scale * dpr })
  const c = canvas.value
  c.width = viewport.width
  c.height = viewport.height
  c.style.width = `${viewport.width / dpr}px`
  c.style.height = `${viewport.height / dpr}px`
  renderTask?.cancel()
  renderTask = page.render({ canvas: c, canvasContext: c.getContext('2d'), viewport })
  try {
    await renderTask.promise
  } catch (e) {
    if (e?.name !== 'RenderingCancelledException') throw e
  }
}

function goPage(delta) {
  pdfState.page = Math.min(Math.max(1, pdfState.page + delta), pdfState.pages)
  renderPage()
}
function zoom(factor) {
  pdfState.scale = Math.min(Math.max(0.3, pdfState.scale * factor), 5)
  renderPage()
}

// ---- lifecycle ----
const showRaw = ref(false)
const mobilePane = ref('fields')          // 'pdf' | 'fields' under lg

onMounted(async () => {
  listBuyerProfiles().then(p => { profiles.value = p }).catch(() => {})
  loadPdf()

  // Show the stored result first — extraction can take a minute on a local
  // model, and re-opening a review shouldn't pay that cost again.
  try {
    const stored = await getExtraction(props.doc.id)
    if (stored?.extracted?.orderLine) {
      outcome.value = {
        status: 'needs_review', parser: 'stored', buyer: stored.buyer,
        detectConfidence: 0, checks: stored.extracted.validation ?? [],
        failedChecks: [], data: stored.extracted,
        note: 'Showing the last saved extraction. Re-extract to run the pipeline again.',
      }
      resetAll()
      return
    }
  } catch { /* nothing stored yet — extract below */ }

  await runExtract()
})
onBeforeUnmount(() => {
  renderTask?.cancel()
  pdfDoc?.destroy?.()
})
watch(() => props.doc.id, () => { loadPdf(); runExtract() })

const statusClass = computed(() => ({
  ok: 'bg-emerald-600 text-white',
  needs_review: 'bg-amber-500 text-white',
  failed: 'bg-destructive text-destructive-foreground',
}[outcome.value?.status] ?? 'bg-muted text-muted-foreground'))
</script>

<template>
  <div :class="embedded ? 'flex h-full min-h-0 flex-col' : 'fixed inset-0 z-40 flex flex-col bg-background'">
    <!-- Toolbar -->
    <div class="flex h-14 shrink-0 items-center gap-2 border-b px-3 sm:px-4">
      <Badge :class="cn('capitalize', statusClass)">
        {{ outcome?.status?.replace('_', ' ') ?? (loading ? 'extracting…' : '—') }}
      </Badge>
      <span class="min-w-0 flex-1 truncate text-sm font-medium" :title="doc.fileName">{{ doc.fileName }}</span>

      <span v-if="outcome" class="hidden items-center gap-2 text-xs text-muted-foreground md:flex">
        <span>{{ outcome.parser }}</span>
        <span v-if="outcome.buyer">· {{ outcome.buyer }} ({{ Math.round(outcome.detectConfidence * 100) }}%)</span>
      </span>

      <Select v-model="profileId">
        <SelectTrigger class="h-8 w-auto min-w-32 text-xs">
          <SelectValue placeholder="Profile" />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="auto">Auto-detect</SelectItem>
          <SelectItem v-for="p in profiles" :key="p.id" :value="String(p.id)">{{ p.name }}</SelectItem>
        </SelectContent>
      </Select>
      <Button variant="outline" size="sm" class="h-8" :disabled="loading" @click="runExtract">
        <Loader2 v-if="loading" class="animate-spin" />
        Re-extract
      </Button>
      <Button v-if="!embedded" variant="ghost" size="icon" aria-label="Close review" @click="close">
        <X />
      </Button>
    </div>

    <!-- Mobile pane switch -->
    <div class="flex shrink-0 gap-1 border-b p-1 lg:hidden">
      <Button :variant="mobilePane === 'pdf' ? 'secondary' : 'ghost'" size="sm" class="flex-1" @click="mobilePane = 'pdf'">Source</Button>
      <Button :variant="mobilePane === 'fields' ? 'secondary' : 'ghost'" size="sm" class="flex-1" @click="mobilePane = 'fields'">Fields</Button>
    </div>

    <div class="flex min-h-0 flex-1">
      <!-- LEFT: source -->
      <div :class="cn('min-h-0 flex-col border-r lg:flex lg:w-1/2', mobilePane === 'pdf' ? 'flex w-full' : 'hidden')">
        <div v-if="pdfState.isPdf && pdfState.pages" class="flex shrink-0 items-center justify-center gap-1 border-b p-1.5">
          <Button variant="ghost" size="icon" class="h-7 w-7" :disabled="pdfState.page <= 1" @click="goPage(-1)"><ChevronLeft /></Button>
          <span class="px-2 text-xs text-muted-foreground">Page {{ pdfState.page }} / {{ pdfState.pages }}</span>
          <Button variant="ghost" size="icon" class="h-7 w-7" :disabled="pdfState.page >= pdfState.pages" @click="goPage(1)"><ChevronRight /></Button>
          <Separator orientation="vertical" class="mx-1 h-5" />
          <Button variant="ghost" size="icon" class="h-7 w-7" @click="zoom(1 / 1.25)"><ZoomOut /></Button>
          <Button variant="ghost" size="icon" class="h-7 w-7" @click="zoom(1.25)"><ZoomIn /></Button>
        </div>
        <div class="min-h-0 flex-1 overflow-auto bg-muted/40 p-2">
          <p v-if="pdfState.error" class="p-4 text-sm text-muted-foreground">{{ pdfState.error }}</p>
          <canvas v-show="pdfState.isPdf && !pdfState.error" ref="canvas" class="mx-auto shadow" />
        </div>
      </div>

      <!-- RIGHT: fields -->
      <div :class="cn('min-h-0 flex-col overflow-y-auto lg:flex lg:w-1/2', mobilePane === 'fields' ? 'flex w-full' : 'hidden')">
        <div class="flex flex-col gap-4 p-4">
          <p v-if="extractError" class="rounded-lg border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive">
            {{ extractError }}
          </p>
          <p v-if="outcome?.note" class="rounded-lg border bg-muted/40 p-3 text-xs text-muted-foreground">
            {{ outcome.note }}
          </p>
          <div v-if="loading" class="flex items-center gap-2 py-10 text-sm text-muted-foreground">
            <Loader2 class="h-4 w-4 animate-spin" /> Extracting…
          </div>

          <template v-if="outcome?.data && !loading">
            <!-- Validation: live checks first (they react to edits), failures pinned top -->
            <section class="rounded-lg border">
              <p class="border-b px-3 py-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
                Checks
              </p>
              <ul class="divide-y">
                <li v-for="c in [...liveChecks].sort((a, b) => Number(a.passed) - Number(b.passed))" :key="c.check"
                  :class="cn('flex items-start gap-2 px-3 py-2 text-sm', !c.passed && 'bg-amber-500/10')">
                  <Check v-if="c.passed" class="mt-0.5 h-4 w-4 shrink-0 text-emerald-600" />
                  <X v-else class="mt-0.5 h-4 w-4 shrink-0 text-amber-600" />
                  <div class="min-w-0">
                    <span class="font-medium">{{ c.check.replaceAll('_', ' ') }}</span>
                    <span class="ml-2 text-xs text-muted-foreground">{{ c.detail }}</span>
                  </div>
                </li>
                <!-- server-side checks that don't recompute client-side -->
                <li v-for="c in (outcome.checks ?? []).filter(c => !liveChecks.some(l => l.check === c.check || (l.check === 'upc_valid' && c.check === 'upc_present_and_numeric')))"
                  :key="c.check"
                  :class="cn('flex items-start gap-2 px-3 py-2 text-sm', !c.passed && 'bg-amber-500/10')">
                  <Check v-if="c.passed" class="mt-0.5 h-4 w-4 shrink-0 text-emerald-600" />
                  <X v-else class="mt-0.5 h-4 w-4 shrink-0 text-amber-600" />
                  <div class="min-w-0">
                    <span class="font-medium">{{ c.check.replaceAll('_', ' ') }}</span>
                    <span class="ml-2 text-xs text-muted-foreground">{{ c.detail }}</span>
                  </div>
                </li>
              </ul>
            </section>

            <!-- Derived: computed, never editable -->
            <section class="rounded-lg border border-dashed bg-muted/30 p-3">
              <p class="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
                Derived — computed, not extracted
              </p>
              <div class="flex flex-wrap items-baseline gap-x-6 gap-y-1">
                <span class="text-2xl font-bold">{{ derived.totalPieces ?? '—' }}
                  <span class="text-sm font-normal text-muted-foreground">total pieces</span></span>
                <span v-if="derived.totalPacks != null" class="text-sm text-muted-foreground">
                  {{ derived.totalPacks }} packs × {{ derived.piecesPerPack }} pieces
                </span>
                <span v-if="derived.packPrice != null" class="text-sm text-muted-foreground">
                  pack price {{ derived.packPrice.toFixed(2) }}
                </span>
              </div>
              <table v-if="derived.sizes.length" class="mt-2 text-xs">
                <tbody>
                  <tr class="text-muted-foreground">
                    <td class="pr-3">Size</td>
                    <td v-for="[s] in derived.sizes" :key="s" class="px-2 text-center font-medium text-foreground">{{ s }}</td>
                  </tr>
                  <tr class="text-muted-foreground">
                    <td class="pr-3">Qty/pack</td>
                    <td v-for="[s, q] in derived.sizes" :key="s" class="px-2 text-center text-foreground">{{ q }}</td>
                  </tr>
                </tbody>
              </table>
            </section>

            <!-- Order line -->
            <section class="rounded-lg border p-3">
              <div class="mb-2 flex items-center gap-2">
                <p class="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">Order line</p>
                <Badge v-if="edit.orderLine?.isPrepack" variant="secondary">Assortment / prepack</Badge>
                <span class="ml-auto text-[11px] text-muted-foreground">PO total</span>
                <input
                  :value="edit.purchaseOrderTotal ?? ''" :title="`machine: ${machine('purchaseOrderTotal') ?? '—'}`"
                  :class="cn('h-7 w-24 rounded border bg-transparent px-2 text-right text-sm', isDirty('purchaseOrderTotal') && 'border-amber-500')"
                  @input="set(edit, 'purchaseOrderTotal', $event.target.value)"
                >
              </div>
              <div class="grid grid-cols-2 gap-2 sm:grid-cols-4">
                <label v-for="f in lineFields" :key="f.path" class="flex flex-col gap-0.5">
                  <span class="flex items-center gap-1 text-[11px] text-muted-foreground">
                    {{ f.label }}
                    <span v-if="isDirty(f.path)" class="h-1.5 w-1.5 rounded-full bg-amber-500" title="edited" />
                    <button v-if="isDirty(f.path)" class="text-muted-foreground hover:text-foreground" title="Reset to machine value" @click="resetField(f.path)">
                      <RotateCcw class="h-3 w-3" />
                    </button>
                  </span>
                  <input
                    :value="get(edit, f.path) ?? ''" :title="`machine: ${machine(f.path) ?? '—'}`"
                    :class="cn('h-8 rounded border bg-transparent px-2 text-sm', isDirty(f.path) && 'border-amber-500')"
                    @input="set(edit, f.path, $event.target.value)"
                  >
                </label>
              </div>
            </section>

            <!-- Line items -->
            <section v-if="(edit.prepackComponents ?? []).length" class="overflow-x-auto rounded-lg border">
              <p class="border-b px-3 py-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
                Line items
              </p>
              <table class="w-full text-sm">
                <thead>
                  <tr class="border-b text-left text-[11px] text-muted-foreground">
                    <th v-for="c in compCols" :key="c.key" class="px-2 py-1.5 font-medium">{{ c.label }}</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="(row, i) in edit.prepackComponents" :key="i" class="border-b last:border-0">
                    <td v-for="c in compCols" :key="c.key" class="p-1">
                      <input
                        :value="row[c.key] ?? ''"
                        :title="`machine: ${machine(`prepackComponents.${i}.${c.key}`) ?? '—'}`"
                        :class="cn('h-7 w-full min-w-14 rounded border bg-transparent px-1.5 text-xs',
                                   (c.money || c.int) && 'text-right',
                                   isDirty(`prepackComponents.${i}.${c.key}`) && 'border-amber-500')"
                        @input="row[c.key] = $event.target.value"
                      >
                    </td>
                  </tr>
                </tbody>
              </table>
            </section>

            <!-- Header -->
            <section class="rounded-lg border p-3">
              <p class="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">PO header</p>
              <div class="grid grid-cols-2 gap-2 sm:grid-cols-3">
                <label v-for="f in headerFields" :key="f.path" class="flex flex-col gap-0.5">
                  <span class="flex items-center gap-1 text-[11px] text-muted-foreground">
                    {{ f.label }}
                    <span v-if="isDirty(f.path)" class="h-1.5 w-1.5 rounded-full bg-amber-500" title="edited" />
                  </span>
                  <input
                    :value="get(edit, f.path) ?? ''" :title="`machine: ${machine(f.path) ?? '—'}`"
                    :class="cn('h-8 rounded border bg-transparent px-2 text-sm', isDirty(f.path) && 'border-amber-500')"
                    @input="set(edit, f.path, $event.target.value)"
                  >
                </label>
              </div>
            </section>

            <!-- Raw JSON -->
            <details class="rounded-lg border">
              <summary class="cursor-pointer px-3 py-2 text-xs text-muted-foreground" @click="showRaw = !showRaw">
                Raw JSON
              </summary>
              <pre class="overflow-x-auto border-t bg-muted/40 p-3 text-xs">{{ JSON.stringify(outcome.data, null, 2) }}</pre>
            </details>
          </template>
        </div>

        <!-- Confirm bar -->
        <div v-if="outcome?.data && !loading" class="sticky bottom-0 mt-auto border-t bg-background/95 p-3 backdrop-blur">
          <p v-if="confirmError" class="mb-2 text-sm text-destructive">{{ confirmError }}</p>
          <div class="flex flex-wrap items-center gap-3">
            <label v-if="failingLive.length" class="flex items-center gap-2 text-xs text-muted-foreground">
              <input v-model="overrideChecks" type="checkbox" class="h-3.5 w-3.5">
              Accept despite {{ failingLive.length }} failed check(s)
            </label>
            <input
              v-if="overrideChecks" v-model="overrideReason"
              placeholder="Why is the document itself wrong? (required)"
              class="h-8 min-w-52 flex-1 rounded border bg-transparent px-2 text-xs"
            >
            <Button variant="outline" size="sm" :disabled="!anyDirty || confirmed" @click="resetAll">
              Reset all
            </Button>
            <Button class="ml-auto" :disabled="!canConfirm" @click="doConfirm">
              <Check v-if="confirmed" />
              {{ confirmed ? 'Confirmed' : confirming ? 'Saving…' : 'Confirm & accept' }}
            </Button>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>
