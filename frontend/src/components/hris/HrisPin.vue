<script setup>
/**
 * এইচআর ওয়ার্কস্পেস — one pin on the board (docs/HRIS_WORKSPACE_PROMPT.md §3.2).
 *
 * It shows what the server rendered for it and nothing else. A chart or KPI pin is
 * Matrix's own widget (MatrixWidget: threshold colour, change since the previous
 * snapshot, the chart's PNG button, drill-down on a click). A KPI the data cannot
 * give shows the report card's reason and no number. A card pin lists the latest
 * report card; a summary pin the saved, checked summary; an answer pin the answer as
 * of its date. Each says its report date and its source, and which of the board's
 * filters it could not take.
 */
import { computed } from 'vue'
import { AlertTriangle, FileSpreadsheet, GripVertical, Trash2 } from '@lucide/vue'
import MatrixWidget from '@/components/matrix/MatrixWidget.vue'
import { renderChecked } from '@/lib/matrix-text.js'
import { formatDate, formatValue } from '@/lib/matrix-format.js'

const props = defineProps({
  pin: { type: Object, required: true },
  /** The server's render of this pin; null while it runs. */
  render: { type: Object, default: null },
  canEdit: { type: Boolean, default: false },
  canExport: { type: Boolean, default: false },
  printMode: { type: Boolean, default: false },
})
const emit = defineEmits(['drill', 'remove', 'resize', 'export', 'dragstart', 'drop'])

const FILTER_NAMES = { unit: 'ইউনিট', date: 'তারিখ', department: 'বিভাগ', section: 'সেকশন', line_type: 'লাইনের ধরন', line: 'লাইন' }

const r = computed(() => props.render)
const widgets = computed(() => props.pin.payload?.widgets ?? [])
const resultOf = (w) => r.value?.widgets?.find((x) => x.id === `p${props.pin.id}-${w.id}`) ?? null
const unavailableOf = (w) => (w.kpi ? r.value?.unavailable?.find((u) => u.kpi === w.kpi) : null) ?? null
/** The filters this pin could not take: for the whole pin, or named per widget when only some could not. */
const notApplicable = computed(() => {
  const keys = r.value?.notApplicable ?? []
  const ws = r.value?.widgets ?? []
  if (!keys.length) return []
  if (!ws.length || keys.every((k) => ws.every((w) => w.filtersIgnored.includes(k)))) return keys.map((k) => FILTER_NAMES[k] ?? k)
  return ws.filter((w) => w.filtersIgnored.length).map((w) => `${w.nameBn}: ${w.filtersIgnored.map((k) => FILTER_NAMES[k] ?? k).join(', ')}`)
})
const summaryHtml = computed(() => {
  const s = r.value?.summary
  return s ? renderChecked(s.text, { formulas: s.formulas, unverified: s.unverified, unknown: s.unknownCitations, known: (l) => s.labels.some((x) => x.label === l) }) : ''
})
const answer = computed(() => r.value?.answer ?? (props.pin.kind === 'answer' ? props.pin.payload : null))
</script>

<template>
  <article class="flex min-w-0 flex-col gap-2 rounded-lg border bg-card p-3" data-hris-pin :data-pin-id="pin.id" :data-pin-kind="pin.kind"
    :draggable="canEdit && !printMode" @dragstart="emit('dragstart', pin.id)" @dragover.prevent @drop.prevent="emit('drop', pin.id)">
    <header class="flex min-w-0 items-start gap-2">
      <GripVertical v-if="canEdit && !printMode" class="mt-0.5 size-4 shrink-0 cursor-grab text-muted-foreground" data-hris-pin-drag aria-label="Drag to reorder" />
      <div class="min-w-0 flex-1">
        <h4 class="truncate text-sm font-semibold" :title="pin.titleEn">{{ pin.titleBn || pin.titleEn }} <span class="text-xs font-normal text-muted-foreground">{{ pin.titleEn }}</span></h4>
        <p class="truncate text-xs text-muted-foreground" data-hris-asof>
          <template v-if="pin.kind === 'answer'">উত্তরের তারিখ · Answered on {{ answer?.date ? formatDate(answer.date.slice(0, 10)) : '—' }}</template>
          <template v-else>হালনাগাদ · as of {{ r?.reportDate ? formatDate(r.reportDate) : '—' }}<template v-if="r?.source"> · {{ r.source }}</template></template>
        </p>
      </div>
      <div v-if="!printMode" class="flex shrink-0 items-center gap-1">
        <button v-if="canExport" type="button" class="rounded p-1 text-muted-foreground hover:bg-muted hover:text-foreground" title="Excel (.xlsx)" aria-label="Export this pin as xlsx"
          data-hris-pin-xlsx @click="emit('export', pin.id)"><FileSpreadsheet class="size-4" /></button>
        <select v-if="canEdit" :value="pin.size" class="h-7 rounded border bg-background px-1 text-xs" aria-label="Size" data-hris-pin-size
          @change="emit('resize', pin.id, $event.target.value)">
          <option v-for="s in ['S', 'M', 'L']" :key="s" :value="s">{{ s }}</option>
        </select>
        <button v-if="canEdit" type="button" class="rounded p-1 text-muted-foreground hover:bg-muted hover:text-destructive" title="Remove" aria-label="Remove pin"
          data-hris-pin-remove @click="emit('remove', pin.id)"><Trash2 class="size-4" /></button>
      </div>
    </header>

    <p v-if="notApplicable.length" class="w-fit rounded bg-muted px-1.5 py-0.5 text-[11px] text-muted-foreground" data-hris-na>
      এই ফিল্টার প্রযোজ্য নয় · filter not applicable: {{ notApplicable.join(', ') }}
    </p>

    <p v-if="!r" class="h-24 animate-pulse rounded bg-muted" />
    <p v-else-if="r.error" class="flex gap-1 text-sm text-muted-foreground" data-hris-pin-error><AlertTriangle class="mt-0.5 size-4 shrink-0" /> {{ r.error }}</p>

    <!-- chart and KPI pins: Matrix's own widgets -->
    <div v-if="r && widgets.length" class="grid min-w-0 gap-2" :class="widgets.length > 1 && widgets.every((w) => w.type === 'kpi') ? 'grid-cols-2' : 'grid-cols-1'">
      <template v-for="w in widgets" :key="w.id">
        <div v-if="unavailableOf(w)" class="rounded-md border border-dashed p-2 text-sm" data-hris-unavailable :data-kpi="w.kpi">
          <p class="font-medium">{{ unavailableOf(w).labelBn }} <span class="text-xs text-muted-foreground">{{ unavailableOf(w).label }}</span></p>
          <p class="mt-1 flex gap-1 text-xs text-muted-foreground"><AlertTriangle class="mt-0.5 size-3 shrink-0" /> পাওয়া যায়নি · not available: {{ unavailableOf(w).reason }}</p>
        </div>
        <!-- A chart fills its box (on a dashboard the grid row gives it); here the pin does. -->
        <div v-else class="flex min-w-0 flex-col" :class="w.type === 'kpi' ? '' : 'h-72'">
          <MatrixWidget class="flex-1" :widget="w" :result="resultOf(w)" :print-mode="printMode" @drill="(d) => emit('drill', d)" />
        </div>
      </template>
    </div>

    <!-- card pin: the latest report card -->
    <dl v-if="r?.card" class="grid grid-cols-2 gap-2 text-sm" data-hris-card>
      <div v-for="i in r.card.items" :key="i.key" class="min-w-0 rounded-md bg-muted/50 p-2">
        <dt class="truncate text-xs text-muted-foreground" :title="i.label">{{ i.labelBn }} · {{ i.label }}</dt>
        <dd class="font-semibold">{{ i.value === null || i.value === undefined ? (i.text ?? '—') : formatValue(i.value, i.unit) }}</dd>
      </div>
    </dl>
    <ul v-if="r?.card && r.unavailable.length" class="flex flex-col gap-1 text-xs text-muted-foreground">
      <li v-for="u in r.unavailable" :key="u.kpi" class="flex gap-1" data-hris-unavailable><AlertTriangle class="mt-0.5 size-3 shrink-0" /> {{ u.labelBn }} · {{ u.label }}: {{ u.reason }}</li>
    </ul>

    <!-- summary pin: checked by Matrix before it arrives -->
    <div v-if="r?.summary" class="prose prose-sm max-w-none text-sm dark:prose-invert" data-hris-summary v-html="summaryHtml" />

    <!-- answer pin: frozen as of its date -->
    <div v-if="answer?.text" class="whitespace-pre-wrap text-sm" data-hris-answer>{{ answer.text }}</div>
  </article>
</template>
