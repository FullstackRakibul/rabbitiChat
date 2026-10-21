<script setup>
/**
 * ম্যাট্রিক্স — রিপোর্ট Report: what a dataset says, first (docs/MATRIX_PROMPT.md §3.8).
 *
 * The report card's KPI tiles (each opens its rows in the viewer), the highest and
 * lowest of its key breakdowns, and the automatic charts of the dataset's kind run on
 * this dataset. "Compare with previous" appears when an earlier snapshot of the same
 * layout exists: the tiles then show their change, the charts the earlier values.
 * Every number is the server's; this page lays them out.
 */
import { computed, ref, watch } from 'vue'
import { AlertTriangle, ChevronRight, GitCompare, Loader2, Minus, TrendingDown, TrendingUp } from '@lucide/vue'
import { getMatrixCard, runMatrixReport } from '@/services/index.js'
import { Button } from '@/components/ui/button'
import MatrixWidget from '@/components/matrix/MatrixWidget.vue'
import { formatDate, formatValue, lakh } from '@/lib/matrix-format.js'

const props = defineProps({
  dataset: { type: Object, required: true },
})
const emit = defineEmits(['drill', 'quality'])

const card = ref(null)
const run = ref(null)
const compare = ref(false)
const error = ref('')
const loading = ref(false)

const KINDS = {
  master: ['মাস্টার ফাইল', 'Master file'], manpower: ['জনবল তালিকা', 'Manpower list'], 'attendance-daily': ['দৈনিক হাজিরা', 'Daily attendance'],
  'separation-list': ['বিচ্ছেদ তালিকা', 'Separation list'], 'designation-lookup': ['পদবি তালিকা', 'Designation list'], custom: ['অন্যান্য', 'Other'],
}
const UNITS = { people: 'জন', lines: 'লাইন', 'per line': 'লাইনপ্রতি', grades: 'গ্রেড' }

const changes = computed(() => Object.fromEntries((card.value?.comparison?.items ?? []).map((c) => [c.key, c])))
const results = computed(() => Object.fromEntries((run.value?.widgets ?? []).map((w) => [w.id, w])))
const widgets = computed(() => [...(run.value?.layout ?? [])].sort((a, b) => a.y - b.y || a.x - b.x))
const openIssues = computed(() => (card.value?.quality ?? []).reduce((n, q) => n + q.count, 0))

async function load() {
  error.value = ''
  loading.value = true
  try {
    const [c, r] = await Promise.all([getMatrixCard(props.dataset.id), runMatrixReport(props.dataset.id, { compare: compare.value })])
    card.value = c
    run.value = r
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

async function rerun() {
  try { run.value = await runMatrixReport(props.dataset.id, { compare: compare.value }) } catch (e) { error.value = e.message }
}

function drill(cite) {
  if (cite) emit('drill', { datasetId: props.dataset.id, cite })
}

watch(() => props.dataset.id, load, { immediate: true })
watch(compare, rerun)
</script>

<template>
  <section class="flex min-w-0 flex-col gap-4" data-report :data-report-id="dataset.id">
    <!-- Step 2 puts the summary here; Step 3 the Ask panel. -->
    <slot name="top" />

    <div class="flex flex-wrap items-center gap-2">
      <div class="min-w-0 basis-full text-sm text-muted-foreground sm:basis-0 sm:flex-1">
        <span class="font-medium text-foreground">{{ KINDS[dataset.kind]?.[0] ?? dataset.kind }}</span>
        {{ KINDS[dataset.kind]?.[1] }}
        <span v-if="dataset.reportDate"> · {{ formatDate(dataset.reportDate) }}</span>
        <span v-if="card?.scope?.length"> · {{ card.scope.join(', ') }}</span>
      </div>
      <Button v-if="card?.comparison" size="sm" :variant="compare ? 'secondary' : 'outline'" :aria-pressed="compare" data-report-compare @click="compare = !compare">
        <GitCompare class="size-4" /> আগের সাথে তুলনা <span class="text-xs opacity-70">Compare with {{ formatDate(card.comparison.previousDate) }}</span>
      </Button>
    </div>

    <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>
    <p v-if="loading && !card" class="flex items-center gap-2 text-sm text-muted-foreground"><Loader2 class="size-4 animate-spin" /> Loading…</p>

    <!-- KPI tiles from the report card -->
    <div v-if="card" class="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6" data-report-tiles>
      <button v-for="item in card.items" :key="item.key" type="button"
        class="flex min-w-0 flex-col gap-1 rounded-lg border bg-card p-3 text-left transition-colors hover:bg-accent disabled:hover:bg-card"
        :disabled="!item.cite" :data-card-item="item.key" :data-value="item.value" @click="drill(item.cite)">
        <span class="line-clamp-2 text-sm font-semibold leading-tight">{{ item.labelBn }}</span>
        <span class="truncate text-xs text-muted-foreground">{{ item.label }}</span>
        <span class="mt-auto pt-1 text-2xl font-semibold tabular-nums tracking-tight">
          {{ item.text ?? formatValue(item.value, item.unit) }}
          <span v-if="UNITS[item.unit]" class="text-xs font-normal text-muted-foreground">{{ UNITS[item.unit] }}</span>
        </span>
        <span v-if="item.detail" class="text-[11px] text-muted-foreground">{{ item.detail }}</span>
        <span v-if="compare && changes[item.key]" class="flex items-center gap-1 text-xs text-muted-foreground" data-card-change :data-change="changes[item.key].change">
          <component :is="changes[item.key].change > 0 ? TrendingUp : changes[item.key].change < 0 ? TrendingDown : Minus" class="size-3.5" aria-hidden="true" />
          {{ changes[item.key].change > 0 ? '+' : '' }}{{ formatValue(changes[item.key].change, item.unit) }}
          <span class="opacity-70">({{ formatValue(changes[item.key].before, item.unit) }} আগে)</span>
        </span>
      </button>
    </div>

    <!-- highest and lowest of the key breakdowns -->
    <div v-if="card?.breakdowns?.length" class="grid gap-3 md:grid-cols-2" data-report-breakdowns>
      <section v-for="b in card.breakdowns" :key="b.key" class="rounded-lg border bg-card p-3 text-sm" :data-breakdown="b.key">
        <h4 class="font-semibold leading-tight">{{ b.labelBn }} <span class="text-xs font-normal text-muted-foreground">{{ b.label }}</span></h4>
        <div class="mt-2 grid grid-cols-2 gap-3">
          <div v-for="[list, bn, en] in [[b.top, 'সর্বোচ্চ', 'Highest'], [b.bottom, 'সর্বনিম্ন', 'Lowest']].filter((x) => x[0].length)" :key="en">
            <p class="text-xs text-muted-foreground">{{ bn }} <span class="opacity-70">{{ en }}</span></p>
            <ul class="mt-1 space-y-0.5">
              <li v-for="e in list" :key="e.label">
                <button type="button" class="flex w-full items-baseline gap-2 rounded px-1 text-left hover:bg-accent disabled:hover:bg-transparent" :disabled="!e.cite" @click="drill(e.cite)">
                  <span class="min-w-0 flex-1 truncate">{{ e.label }}</span>
                  <span class="tabular-nums">{{ formatValue(e.value, e.unit) }}</span>
                </button>
              </li>
            </ul>
          </div>
        </div>
      </section>
    </div>

    <!-- the automatic charts for this kind -->
    <div v-if="widgets.length" class="mx-grid" data-report-charts>
      <MatrixWidget v-for="w in widgets" :key="w.id" class="mx-cell" :style="{ '--x': w.x + 1, '--w': w.w, '--y': w.y + 1, '--h': w.h }"
        :widget="w" :result="results[w.id] ?? null" :compare-mode="compare" @drill="(d) => emit('drill', d)" />
    </div>

    <button v-if="openIssues" type="button" class="flex items-center gap-2 rounded-lg border bg-card p-3 text-left text-sm hover:bg-accent" data-report-quality @click="emit('quality')">
      <AlertTriangle class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
      <span class="flex-1">ডেটার মানের {{ card.quality.length }}টি বিষয় খোলা <span class="text-muted-foreground">· {{ lakh(openIssues) }} cells or rows in {{ card.quality.length }} open quality checks</span></span>
      <ChevronRight class="size-4 text-muted-foreground" aria-hidden="true" />
    </button>
  </section>
</template>
