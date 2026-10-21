<script setup>
/**
 * ম্যাট্রিক্স — the one chart component (docs/MATRIX_PROMPT.md §3.8): Apache ECharts,
 * wrapped so nothing else imports it.
 *
 * It draws a widget's rows as the server returned them — no number is computed here.
 * Colours are the theme's tokens (--chart-1…8, --status-*, --foreground, --border),
 * read from the document at draw time and again whenever the theme changes, so a
 * chart is never a different palette from the page around it.
 *
 * A click on a bar, slice, point or cell emits `drill` with the row it stands for
 * (its citation token opens the viewer on exactly those rows). `png()` is ECharts'
 * own export. The element carries __mxChart and __mxRows (the rows in the order
 * drawn) and data-chart-ready once drawn, for the evals.
 */
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import * as echarts from 'echarts/core'
import { BarChart, LineChart, PieChart, GaugeChart, HeatmapChart } from 'echarts/charts'
import { GridComponent, TooltipComponent, LegendComponent, VisualMapComponent, MarkLineComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { lakh } from '@/lib/matrix-format.js'

echarts.use([BarChart, LineChart, PieChart, GaugeChart, HeatmapChart, GridComponent, TooltipComponent, LegendComponent,
  VisualMapComponent, MarkLineComponent, CanvasRenderer])

const props = defineProps({
  type: { type: String, required: true },          // bar | stacked-bar | line | timeline | donut | gauge | heatmap
  rows: { type: Array, default: () => [] },
  groupBy: { type: Array, default: () => [] },
  measures: { type: Array, default: () => ['value'] },
  /** For a trend: [{ datasetId, reportDate, value, cite }]. */
  trend: { type: Array, default: null },
  /** The previous snapshot's rows, drawn beside these. */
  compareRows: { type: Array, default: null },
  compareLabel: { type: String, default: '' },
  value: { type: Number, default: null },
  state: { type: String, default: null },
  target: { type: Number, default: null },
  /** (field, value) → label shown for a group value. */
  labelOf: { type: Function, default: (_f, v) => (v === null || v === undefined ? '(none)' : String(v)) },
  measureLabel: { type: Function, default: (m) => m },
  title: { type: String, default: '' },
})
const emit = defineEmits(['drill'])

const el = ref(null)
const ready = ref(false)
let chart = null
let drawn = []          // the rows (or points) in the order drawn: index → row
let observer = null
let resize = null
// Printing lays the page out at paper width: draw the chart at that width too.
const fit = () => chart?.resize()
const printQuery = window.matchMedia('print')

/** A theme token as a colour ECharts can parse: "221 83% 53%" → "hsl(221, 83%, 53%)". */
function token(name, alpha) {
  const raw = getComputedStyle(document.documentElement).getPropertyValue(name).trim()
  const [h, s, l] = raw.split(/\s+/)
  if (!l) return undefined
  return alpha === undefined ? `hsl(${h}, ${s}, ${l})` : `hsla(${h}, ${s}, ${l}, ${alpha})`
}
const palette = () => [1, 2, 3, 4, 5, 6, 7, 8].map((i) => token(`--chart-${i}`))
const stateColour = (s) => (s ? token(`--status-${s}`) : undefined)

const num = (v) => (typeof v === 'number' ? v : v === null || v === undefined ? null : Number(v))
const g0 = () => props.groupBy[0]
const g1 = () => props.groupBy[1]

function axisStyle() {
  const text = token('--muted-foreground')
  const line = token('--border')
  return {
    axisLabel: { color: text, fontSize: 11, hideOverlap: true },
    axisLine: { lineStyle: { color: line } },
    splitLine: { lineStyle: { color: line } },
    axisTick: { show: false },
  }
}

function build() {
  const colors = palette()
  const fg = token('--foreground')
  const base = {
    color: colors,
    backgroundColor: 'transparent',
    textStyle: { color: fg, fontFamily: getComputedStyle(document.body).fontFamily },
    tooltip: { trigger: 'item', confine: true, valueFormatter: (v) => lakh(v) },
    animationDuration: 300,
  }
  const ax = axisStyle()
  const grid = { left: 8, right: 16, top: 24, bottom: 8, containLabel: true }

  if (props.type === 'gauge') {
    drawn = []
    const v = num(props.value) ?? 0
    const max = Math.max(1, Math.ceil(v * 1.5 * 10) / 10)
    return { ...base, series: [{
      type: 'gauge', min: 0, max, radius: '95%', center: ['50%', '60%'], startAngle: 200, endAngle: -20,
      progress: { show: true, width: 12, itemStyle: { color: stateColour(props.state) ?? colors[0] } },
      axisLine: { lineStyle: { width: 12, color: [[1, token('--muted')]] } },
      axisTick: { show: false }, splitLine: { show: false }, axisLabel: { show: false }, pointer: { show: false },
      detail: { valueAnimation: true, fontSize: 22, color: fg, offsetCenter: [0, '10%'], formatter: (x) => lakh(x) },
      data: [{ value: v }],
    }] }
  }

  if (props.trend) {
    drawn = props.trend
    return { ...base, tooltip: { ...base.tooltip, trigger: 'axis' }, grid,
      xAxis: { type: 'category', data: props.trend.map((p) => p.reportDate ?? '—'), ...ax },
      yAxis: { type: 'value', ...ax },
      series: [{ type: 'line', data: props.trend.map((p) => num(p.value)), symbolSize: 8, smooth: false, areaStyle: { opacity: 0.08 } }] }
  }

  if (props.type === 'donut') {
    drawn = props.rows
    return { ...base, legend: { type: 'scroll', bottom: 0, textStyle: { color: fg }, pageTextStyle: { color: fg } },
      series: [{ type: 'pie', radius: ['45%', '70%'], center: ['50%', '45%'], avoidLabelOverlap: true,
        itemStyle: { borderColor: token('--card'), borderWidth: 2 }, label: { show: false },
        data: props.rows.map((r) => ({ name: props.labelOf(g0(), r[g0()]), value: num(r[props.measures[0]]) })) }] }
  }

  if (props.type === 'heatmap') {
    const xs = [...new Set(props.rows.map((r) => r[g0()]))]
    const ys = [...new Set(props.rows.map((r) => r[g1()]))]
    drawn = props.rows
    const values = props.rows.map((r) => num(r[props.measures[0]]) ?? 0)
    return { ...base, grid: { ...grid, bottom: 40 },
      xAxis: { type: 'category', data: xs.map((x) => props.labelOf(g0(), x)), ...ax },
      yAxis: { type: 'category', data: ys.map((y) => props.labelOf(g1(), y)), ...ax },
      visualMap: { min: 0, max: Math.max(1, ...values), calculable: false, orient: 'horizontal', left: 'center', bottom: 0,
        itemHeight: 80, textStyle: { color: fg }, inRange: { color: [token('--muted'), colors[0]] } },
      series: [{ type: 'heatmap', label: { show: true, color: fg, formatter: (p) => lakh(p.value[2]) },
        data: props.rows.map((r, i) => [xs.indexOf(r[g0()]), ys.indexOf(r[g1()]), values[i]]) }] }
  }

  if (props.type === 'stacked-bar') {
    const xs = [...new Set(props.rows.map((r) => r[g0()]))]
    const series = [...new Set(props.rows.map((r) => r[g1()]))]
    drawn = props.rows
    return { ...base, tooltip: { ...base.tooltip, trigger: 'axis', axisPointer: { type: 'shadow' } }, grid: { ...grid, bottom: 32 },
      legend: { type: 'scroll', bottom: 0, textStyle: { color: fg }, pageTextStyle: { color: fg } },
      xAxis: { type: 'category', data: xs.map((x) => props.labelOf(g0(), x)), ...ax },
      yAxis: { type: 'value', ...ax },
      series: series.map((s) => ({ type: 'bar', stack: 'all', name: props.labelOf(g1(), s),
        data: xs.map((x) => num(props.rows.find((r) => r[g0()] === x && r[g1()] === s)?.[props.measures[0]]) ?? 0) })) }
  }

  // bar, line and timeline over one group-by field.
  const rows = props.type === 'timeline' ? [...props.rows].sort((a, b) => String(a[g0()]).localeCompare(String(b[g0()]))) : props.rows
  drawn = rows
  const labels = rows.map((r) => props.labelOf(g0(), r[g0()]))
  const long = labels.length > 8 && labels.reduce((n, l) => n + l.length, 0) / Math.max(1, labels.length) > 10
  const horizontal = props.type === 'bar' && long
  const cat = { type: 'category', data: horizontal ? [...labels].reverse() : labels, ...ax }
  const val = { type: 'value', ...ax }
  const lineType = props.type === 'line' || props.type === 'timeline'
  const series = props.measures.map((m, k) => {
    const data = rows.map((r) => ({ value: num(r[m]), itemStyle: r._state && k === 0 ? { color: stateColour(r._state) } : undefined }))
    return {
      type: lineType ? 'line' : 'bar', name: props.measureLabel(m), data: horizontal ? data.reverse() : data,
      areaStyle: props.type === 'timeline' ? { opacity: 0.1 } : undefined, symbolSize: lineType ? 6 : undefined,
      barMaxWidth: 28,
      markLine: k === 0 && props.target !== null ? { silent: true, symbol: 'none', label: { color: fg, formatter: `${props.target}` },
        lineStyle: { color: token('--status-amber'), type: 'dashed' }, data: [horizontal ? { xAxis: props.target } : { yAxis: props.target }] } : undefined,
    }
  })
  if (props.compareRows && !lineType) {
    const before = (r) => num(props.compareRows.find((x) => x[g0()] === r[g0()])?.[props.measures[0]])
    const data = rows.map(before)
    series.push({ type: 'bar', name: props.compareLabel || 'previous', data: horizontal ? data.reverse() : data, barMaxWidth: 28,
      itemStyle: { color: token('--muted-foreground', 0.45) } })
  }
  if (horizontal) drawn = [...rows].reverse()
  return { ...base, tooltip: { ...base.tooltip, trigger: 'axis', axisPointer: { type: 'shadow' } },
    legend: series.length > 1 ? { top: 0, textStyle: { color: fg } } : undefined,
    grid: { ...grid, top: series.length > 1 ? 28 : 16 },
    xAxis: horizontal ? val : cat, yAxis: horizontal ? cat : val, series }
}

function draw() {
  if (!chart) return
  ready.value = false
  chart.setOption(build(), { notMerge: true })
  el.value.__mxRows = drawn
}

function onClick(p) {
  if (props.type === 'gauge') return
  if (props.trend) { const point = props.trend[p.dataIndex]; if (point?.cite) emit('drill', { row: point, cite: point.cite, datasetId: point.datasetId }); return }
  let row
  if (props.type === 'stacked-bar' || props.type === 'heatmap') {
    const opt = chart.getOption()
    const x = props.type === 'heatmap' ? p.value[0] : p.dataIndex
    const xs = [...new Set(props.rows.map((r) => r[g0()]))]
    const ys = [...new Set(props.rows.map((r) => r[g1()]))]
    const y = props.type === 'heatmap' ? ys[p.value[1]] : ys[opt.series.findIndex((s) => s.name === p.seriesName)]
    row = props.rows.find((r) => r[g0()] === xs[x] && r[g1()] === y)
  } else row = drawn[p.dataIndex]
  if (!row) return
  const measure = props.measures[p.seriesIndex] ?? props.measures[0]
  const cite = row._cites?.[measure] ?? row._cite
  if (cite) emit('drill', { row, cite })
}

/** ECharts' own PNG of this chart, on the card's background. */
function png() {
  return chart?.getDataURL({ type: 'png', pixelRatio: 2, backgroundColor: token('--card') })
}
defineExpose({ png })

onMounted(() => {
  chart = echarts.init(el.value, null, { renderer: 'canvas' })
  el.value.__mxChart = chart
  chart.on('click', onClick)
  chart.on('finished', () => { ready.value = true })
  draw()
  // The theme is a class on <html>: draw again with its tokens when it changes.
  observer = new MutationObserver(() => draw())
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] })
  resize = new ResizeObserver(() => chart?.resize())
  resize.observe(el.value)
  window.addEventListener('beforeprint', fit)
  printQuery.addEventListener('change', fit)
})

watch(() => [props.rows, props.trend, props.compareRows, props.value, props.state, props.type, props.target], draw, { deep: true })

onBeforeUnmount(() => {
  observer?.disconnect()
  resize?.disconnect()
  window.removeEventListener('beforeprint', fit)
  printQuery.removeEventListener('change', fit)
  chart?.dispose()
  chart = null
})
</script>

<template>
  <div ref="el" class="size-full min-h-0" role="img" :aria-label="title" data-matrix-chart :data-chart-ready="ready ? '' : undefined" />
</template>
