<script setup>
/**
 * ম্যাট্রিক্স — one dashboard widget (docs/MATRIX_PROMPT.md §3.8).
 *
 * It shows what the server computed for it (`result`: a KPI or a MatrixQuery run with
 * the dashboard's filters) and nothing else: a KPI tile with its threshold colour and
 * the change since the previous snapshot, a chart, a table, or the line board.
 * Clicking a value, bar, cell or line emits `drill` with its citation token, which
 * opens the viewer on exactly those rows. In the editor it carries its own controls:
 * type, position, size, title, remove.
 */
import { computed, ref } from 'vue'
import { AlertTriangle, ArrowDown, ArrowLeft, ArrowRight, ArrowUp, ImageDown, Minus, MoveHorizontal, MoveVertical, Plus, Trash2, TrendingDown, TrendingUp } from '@lucide/vue'
import MatrixChart from '@/components/matrix/MatrixChart.vue'
import { formatDate, lakh } from '@/lib/matrix-format.js'
import { saveBlob } from '@/services/index.js'

const props = defineProps({
  widget: { type: Object, required: true },
  result: { type: Object, default: null },
  editing: { type: Boolean, default: false },
  compareMode: { type: Boolean, default: false },
  printMode: { type: Boolean, default: false },
})
const emit = defineEmits(['drill', 'change', 'remove'])

const TYPES = [
  ['kpi', 'টাইল', 'Tile'], ['bar', 'বার', 'Bar'], ['stacked-bar', 'স্তূপ বার', 'Stacked bar'], ['line', 'রেখা', 'Line'],
  ['donut', 'ডোনাট', 'Donut'], ['gauge', 'গেজ', 'Gauge'], ['heatmap', 'হিটম্যাপ', 'Heatmap'], ['table', 'সারণি', 'Table'],
  ['timeline', 'সময়রেখা', 'Timeline'], ['line-board', 'লাইন বোর্ড', 'Line board'],
]
const CHARTS = new Set(['bar', 'stacked-bar', 'line', 'donut', 'gauge', 'heatmap', 'timeline'])
const MEASURES = {
  value: null, people: ['জন', 'People'], present: ['উপস্থিত', 'Present'], late: ['দেরি', 'Late'], operators: ['অপারেটর', 'Operators'],
  helpers: ['হেলপার', 'Helpers'], qc: ['কিউসি', 'QC'], op_helper: ['অপারেটর : হেলপার', 'Op : helper'], lines: ['লাইন', 'Lines'], n: ['সংখ্যা', 'Count'],
}
const FILTER_NAMES = { unit: 'unit', section: 'section', line: 'line', emp_type: 'emp type', status: 'status', shift: 'shift', date: 'dates' }

const r = computed(() => props.result)
const type = computed(() => props.widget.type)
const titleBn = computed(() => r.value?.nameBn ?? props.widget.title?.bn ?? props.widget.kpi ?? props.widget.id)
const titleEn = computed(() => props.widget.title?.en ?? r.value?.nameEn ?? '')
const chartRef = ref(null)
const groupBy = computed(() => r.value?.groupBy ?? [])
const measures = computed(() => r.value?.measures?.length ? r.value.measures : ['value'])

function measureLabel(m) {
  if (m === 'value') return r.value?.nameEn ?? 'value'
  return MEASURES[m] ? MEASURES[m][1] : m
}
function labelOf(field, v) {
  if (v === null || v === undefined || v === '') return '(none)'
  if (/(^|\.)line_no$/.test(field ?? '')) return `Line ${v}`
  if (/:month$/.test(field ?? '')) return String(v)
  return String(v)
}

/** A value as the unit reads: 12,34,567 people; 5.92%; 1.35 (a ratio). */
function show(v, unit = r.value?.unit) {
  if (v === null || v === undefined) return '—'
  if (typeof v === 'string') return v
  if (unit === '%') return `${lakh(Math.round(v * 100) / 100)}%`
  if (unit === 'ratio') return Number(v).toFixed(2)
  if (unit === 'hours') return `${lakh(Math.round(v * 100) / 100)} h`
  if (unit === 'tk') return `৳ ${lakh(Math.round(v))}`
  return lakh(Math.round(v * 100) / 100)
}

const change = computed(() => r.value?.compare?.change ?? null)
const ignored = computed(() => (r.value?.filtersIgnored ?? []).map((k) => FILTER_NAMES[k] ?? k))
const stateVar = (s) => (s ? `hsl(var(--status-${s}))` : undefined)

function drill(cite, datasetId) {
  if (!cite || props.editing) return
  emit('drill', { cite, datasetId: datasetId ?? r.value?.datasetId })
}
function onChartDrill(e) { drill(e.cite, e.datasetId) }

function png() {
  const url = chartRef.value?.png()
  if (url) saveBlob(url, `${(titleEn.value || props.widget.id).replace(/[^A-Za-z0-9-]+/g, '-')}.png`)
}

// ---- editor ----
function patch(p) { emit('change', { ...props.widget, ...p }) }
function move(dir) {
  const w = props.widget
  if (dir === 'left') patch({ x: Math.max(0, w.x - 1) })
  if (dir === 'right') patch({ x: Math.min(12 - w.w, w.x + 1) })
  if (dir === 'up') patch({ y: Math.max(0, w.y - 1) })
  if (dir === 'down') patch({ y: w.y + 1 })
}
const wider = () => patch({ w: Math.min(12 - props.widget.x, props.widget.w + 1) })
const narrower = () => patch({ w: Math.max(1, props.widget.w - 1) })
const taller = () => patch({ h: Math.min(24, props.widget.h + 1) })
const shorter = () => patch({ h: Math.max(1, props.widget.h - 1) })
function rename(en) {
  patch({ title: { ...(props.widget.title ?? {}), en, bn: props.widget.title?.bn ?? r.value?.nameBn ?? en } })
}

const lineRows = computed(() => (r.value?.rows ?? []))
</script>

<template>
  <article
    class="flex min-h-0 min-w-0 flex-col overflow-hidden rounded-lg border bg-card"
    :style="r?.state ? { borderLeftWidth: '4px', borderLeftColor: stateVar(r.state) } : undefined"
    :data-widget="widget.id" :data-type="type" :data-state="r?.state || undefined"
  >
    <header class="flex items-start gap-2 px-3 pt-2.5">
      <div class="min-w-0 flex-1">
        <h4 class="truncate text-sm font-semibold leading-tight">{{ titleBn }}</h4>
        <p class="truncate text-xs text-muted-foreground">{{ titleEn }}<span v-if="r?.reportDate"> · {{ formatDate(r.reportDate) }}</span></p>
      </div>
      <button v-if="CHARTS.has(type) && !printMode && !editing && r && !r.error" type="button"
        class="rounded p-1 text-muted-foreground hover:bg-accent hover:text-foreground" title="PNG" aria-label="Download PNG"
        data-png @click="png">
        <ImageDown class="size-4" />
      </button>
    </header>

    <!-- editor controls -->
    <div v-if="editing" class="flex flex-wrap items-center gap-1 border-y bg-muted/40 px-2 py-1.5 text-xs" data-w-controls>
      <select class="h-7 rounded border bg-background px-1" :value="type" aria-label="Widget type" data-w-type
        @change="patch({ type: $event.target.value })">
        <option v-for="[k, bn, en] in TYPES" :key="k" :value="k">{{ bn }} · {{ en }}</option>
      </select>
      <input class="h-7 w-32 min-w-0 rounded border bg-background px-1.5" :value="titleEn" aria-label="Widget title" data-w-title
        @change="rename($event.target.value)" />
      <span class="ml-auto inline-flex items-center gap-0.5">
        <button type="button" class="rounded p-1 hover:bg-accent" aria-label="Move left" data-w-move="left" @click="move('left')"><ArrowLeft class="size-3.5" /></button>
        <button type="button" class="rounded p-1 hover:bg-accent" aria-label="Move up" data-w-move="up" @click="move('up')"><ArrowUp class="size-3.5" /></button>
        <button type="button" class="rounded p-1 hover:bg-accent" aria-label="Move down" data-w-move="down" @click="move('down')"><ArrowDown class="size-3.5" /></button>
        <button type="button" class="rounded p-1 hover:bg-accent" aria-label="Move right" data-w-move="right" @click="move('right')"><ArrowRight class="size-3.5" /></button>
        <MoveHorizontal class="ml-1 size-3.5 text-muted-foreground" aria-hidden="true" />
        <button type="button" class="rounded p-1 hover:bg-accent" aria-label="Narrower" data-w-narrower @click="narrower"><Minus class="size-3.5" /></button>
        <button type="button" class="rounded p-1 hover:bg-accent" aria-label="Wider" data-w-wider @click="wider"><Plus class="size-3.5" /></button>
        <MoveVertical class="ml-1 size-3.5 text-muted-foreground" aria-hidden="true" />
        <button type="button" class="rounded p-1 hover:bg-accent" aria-label="Shorter" data-w-shorter @click="shorter"><Minus class="size-3.5" /></button>
        <button type="button" class="rounded p-1 hover:bg-accent" aria-label="Taller" data-w-taller @click="taller"><Plus class="size-3.5" /></button>
        <button type="button" class="ml-1 rounded p-1 text-destructive hover:bg-destructive/10" aria-label="Remove" data-w-remove @click="emit('remove')"><Trash2 class="size-3.5" /></button>
      </span>
    </div>

    <div class="relative min-h-0 flex-1 px-3 pb-2.5 pt-1">
      <p v-if="!r" class="text-xs text-muted-foreground">সংরক্ষণের পর দেখা যাবে <span class="opacity-70">Shown once saved</span></p>

      <p v-else-if="r.error" class="flex items-start gap-1.5 text-xs text-muted-foreground" data-w-error>
        <AlertTriangle class="mt-0.5 size-3.5 shrink-0" aria-hidden="true" /> {{ r.error }}
      </p>

      <!-- KPI tile -->
      <div v-else-if="type === 'kpi'" class="flex h-full flex-col justify-center">
        <button type="button" class="w-fit text-left text-2xl font-semibold tabular-nums tracking-tight hover:underline disabled:no-underline"
          :disabled="!r.cite || editing" data-w-value @click="drill(r.cite)">
          {{ r.text ?? show(r.value) }}
        </button>
        <p v-if="change !== null" class="flex items-center gap-1 text-xs text-muted-foreground">
          <component :is="change > 0 ? TrendingUp : change < 0 ? TrendingDown : Minus" class="size-3.5" aria-hidden="true" />
          <span>{{ change > 0 ? '+' : '' }}{{ show(change) }}</span>
          <span v-if="compareMode" data-compare :data-change="change">· আগের {{ formatDate(r.compare.previousDate) }} তুলনায় <span class="opacity-70">vs {{ show(r.compare.before) }}</span></span>
        </p>
        <p v-if="r.detail" class="mt-0.5 line-clamp-2 text-[11px] text-muted-foreground">{{ r.detail }}</p>
      </div>

      <!-- line board -->
      <div v-else-if="type === 'line-board'" class="grid h-full auto-rows-min grid-cols-[repeat(auto-fill,minmax(7.5rem,1fr))] gap-1.5 overflow-y-auto" data-line-board>
        <button v-for="row in lineRows" :key="row.line_no" type="button"
          class="rounded-md border bg-background p-2 text-left text-xs hover:bg-accent"
          :style="row._state ? { borderLeftWidth: '4px', borderLeftColor: stateVar(row._state) } : undefined"
          :data-line="row.line_no" :data-state="row._state || undefined" @click="drill(row._cites?.operators ?? row._cite)">
          <span class="block text-sm font-semibold">লাইন {{ row.line_no }}</span>
          <span class="block tabular-nums">অপা. {{ lakh(row.operators) }} · হেল. {{ lakh(row.helpers) }}</span>
          <span class="block tabular-nums text-muted-foreground">QC {{ lakh(row.qc) }} · {{ row.op_helper === null ? '—' : Number(row.op_helper).toFixed(2) }}</span>
        </button>
      </div>

      <!-- table -->
      <div v-else-if="type === 'table'" class="h-full overflow-auto">
        <table class="w-full text-xs">
          <thead><tr class="text-left text-muted-foreground">
            <th v-for="g in groupBy" :key="g" class="px-1 py-1 font-medium">{{ g }}</th>
            <th v-for="m in measures" :key="m" class="px-1 py-1 text-right font-medium">{{ measureLabel(m) }}</th>
          </tr></thead>
          <tbody>
            <tr v-for="(row, i) in r.rows" :key="i" class="cursor-pointer border-t hover:bg-accent" @click="drill(row._cite)">
              <td v-for="g in groupBy" :key="g" class="px-1 py-1">{{ labelOf(g, row[g]) }}</td>
              <td v-for="m in measures" :key="m" class="px-1 py-1 text-right tabular-nums"
                :style="row._state && m === measures[0] ? { color: stateVar(row._state) } : undefined">{{ show(row[m]) }}</td>
            </tr>
          </tbody>
        </table>
      </div>

      <!-- charts -->
      <MatrixChart v-else ref="chartRef" :type="type" :rows="r.rows" :group-by="groupBy" :measures="measures"
        :trend="r.trend" :compare-rows="compareMode ? r.compare?.rows ?? null : null"
        :compare-label="r.compare?.previousDate ? formatDate(r.compare.previousDate) : ''"
        :value="r.value" :state="r.state" :target="widget.options?.target ?? null"
        :label-of="labelOf" :measure-label="measureLabel" :title="titleEn" @drill="onChartDrill" />
    </div>

    <p v-if="r && !r.error && ignored.length" class="border-t px-3 py-1 text-[11px] text-muted-foreground" data-w-ignored>
      ফিল্টার প্রযোজ্য নয় <span class="opacity-70">Filter not applicable: {{ ignored.join(', ') }}</span>
    </p>
  </article>
</template>
