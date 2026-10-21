<script setup>
/**
 * ম্যাট্রিক্স — the dataset viewer (docs/MATRIX_PROMPT.md §3.11).
 *
 * A virtualised grid: only the rows on screen exist in the page, pages of rows
 * are fetched as they scroll into view, the header stays put, and the grid
 * scrolls inside its own frame (never the page). Column filters run on the
 * server. A highlight token — from a quality issue now, a chat citation later —
 * opens the viewer on those rows, outlined, with the cited cells filled in the
 * theme's accent. Masked values ("•••") come from the server: sensitive columns
 * are not here at all, personal and salary ones are masked without permission.
 */
import { computed, onMounted, ref, watch } from 'vue'
import { VList } from 'virtua/vue'
import { Download, Filter, Loader2, X } from '@lucide/vue'
import { downloadMatrixExport, getMatrixRows } from '@/services/index.js'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { formatCell, formatDate, lakh } from '@/lib/matrix-format.js'
import { cn } from '@/lib/utils'

const props = defineProps({
  dataset: { type: Object, required: true },
  /** A highlight token; empty for the plain viewer. */
  highlight: { type: String, default: '' },
})
const emit = defineEmits(['close', 'clear-highlight'])

const exporting = ref(false)
/** The cleaned dataset as a file; the server leaves out sensitive columns and what this user may not see. */
async function exportAs(kind) {
  exporting.value = true
  try { await downloadMatrixExport(kind, props.dataset.id) } catch (e) { error.value = e.message } finally { exporting.value = false }
}

const PAGE = 300
const ROW_H = 34
const COL_W = 168

const columns = ref([])
const total = ref(0)
const items = ref([])            // one entry per row: a row object, or null until its page arrives
const loaded = new Set()         // page numbers fetched or in flight
const loading = ref(false)
const error = ref('')
const filters = ref({})          // column key → text
const showFilters = ref(false)
const onlyHighlighted = ref(true)
const hl = ref({ rows: new Set(), cells: new Set() })
const list = ref(null)

const activeFilter = computed(() =>
  Object.entries(filters.value).filter(([, v]) => v && v.trim()).map(([k, v]) => [k, 'contains', v.trim()]))

const gridWidth = computed(() => 72 + columns.value.length * COL_W)   // 72 px = w-18, the row-number column

async function fetchPage(page) {
  if (loaded.has(page)) return
  loaded.add(page)
  try {
    const res = await getMatrixRows(props.dataset.id, {
      offset: page * PAGE, limit: PAGE, filter: activeFilter.value,
      highlight: props.highlight || undefined, only: props.highlight ? onlyHighlighted.value : undefined,
    })
    if (!columns.value.length) columns.value = res.columns
    if (res.highlight) hl.value = { rows: new Set(res.highlight.rows), cells: new Set(res.highlight.cells) }
    if (total.value !== res.total || items.value.length !== res.total) {
      total.value = res.total
      items.value = Array.from({ length: res.total }, (_, i) => items.value[i] ?? null)
    }
    const next = items.value.slice()
    res.rows.forEach((r, i) => { next[page * PAGE + i] = r })
    items.value = next
  } catch (e) {
    loaded.delete(page)
    error.value = e.message
  }
}

async function reload() {
  loading.value = true
  error.value = ''
  loaded.clear()
  items.value = []
  total.value = 0
  await fetchPage(0)
  loading.value = false
}

/** The pages under the visible rows, plus one either side. */
function onScroll(offset) {
  const l = list.value
  if (!l) return
  const first = l.findItemIndex(offset)
  const last = l.findItemIndex(offset + l.viewportSize)
  for (let p = Math.floor(first / PAGE) - 1; p <= Math.floor(last / PAGE) + 1; p++) if (p >= 0 && p * PAGE < total.value) fetchPage(p)
}

let filterTimer = 0
watch(filters, () => { clearTimeout(filterTimer); filterTimer = setTimeout(reload, 350) }, { deep: true })
watch(() => [props.dataset.id, props.highlight, onlyHighlighted.value], () => { columns.value = []; reload() })
onMounted(reload)

const isHlRow = (row) => !!row && hl.value.rows.has(row._row)
const isHlCell = (row, key) => isHlRow(row) && hl.value.cells.has(key)

function sensitivityLabel(c) {
  if (c.masked) return c.sensitivity === 'salary' ? 'salary — needs view-salary' : 'personal — needs view-personal'
  return c.sensitivity
}
</script>

<template>
  <section class="flex min-h-0 min-w-0 flex-col rounded-lg border bg-card" data-matrix-viewer :data-total="total">
    <header class="flex flex-wrap items-center gap-2 border-b p-3">
      <div class="min-w-0 basis-full sm:basis-0 sm:flex-1">
        <h3 class="truncate font-semibold">{{ dataset.name }}</h3>
        <p class="text-xs text-muted-foreground">
          {{ lakh(total) }} সারি rows<span v-if="highlight"> highlighted</span> · {{ columns.length }} কলাম columns
          <span v-if="dataset.reportDate"> · {{ formatDate(dataset.reportDate) }}</span>
        </p>
      </div>
      <label v-if="highlight" class="flex items-center gap-1.5 text-xs text-muted-foreground">
        <input v-model="onlyHighlighted" type="checkbox" class="accent-primary" data-only-highlighted />
        শুধু চিহ্নিত <span class="opacity-70">only highlighted</span>
      </label>
      <Button v-if="highlight" size="sm" variant="ghost" @click="emit('clear-highlight')">
        <X class="size-4" /> Clear highlight
      </Button>
      <Button size="sm" variant="ghost" :aria-pressed="showFilters" @click="showFilters = !showFilters">
        <Filter class="size-4" /> ফিল্টার <span class="text-xs opacity-70">Filters</span>
      </Button>
      <Button v-for="kind in ['xlsx', 'csv']" :key="kind" size="sm" variant="ghost" :disabled="exporting"
        :title="`Download .${kind} — without sensitive columns, and without what you may not see`" :data-export="kind" @click="exportAs(kind)">
        <Download class="size-4" /> .{{ kind }}
      </Button>
      <Button size="sm" variant="outline" @click="emit('close')">Close</Button>
    </header>

    <p v-if="error" class="border-b bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>

    <!-- The frame scrolls sideways; the list inside it scrolls down. The page never scrolls sideways. -->
    <div class="min-h-0 min-w-0 flex-1 overflow-x-auto">
      <div :style="{ width: gridWidth + 'px' }" class="min-w-full">
        <div class="sticky top-0 z-10 flex border-b bg-muted/60 text-xs font-medium" role="row">
          <div class="w-18 shrink-0 px-2 py-2 text-muted-foreground">সারি Row</div>
          <div v-for="c in columns" :key="c.key" class="shrink-0 border-l px-2 py-1.5" :style="{ width: COL_W + 'px' }" :title="`${c.header} · ${c.type} · ${sensitivityLabel(c)}`">
            <div class="truncate">{{ c.bn ?? c.header }}</div>
            <div class="truncate text-[10px] font-normal text-muted-foreground">
              {{ c.en ?? c.key }}<span v-if="c.masked"> · masked</span>
            </div>
            <Input v-if="showFilters" v-model="filters[c.key]" class="mt-1 h-7 text-xs" :placeholder="'contains…'" :data-filter="c.key" />
          </div>
        </div>

        <div v-if="loading && !items.length" class="flex items-center gap-2 p-6 text-sm text-muted-foreground">
          <Loader2 class="size-4 animate-spin" /> Loading rows…
        </div>
        <p v-else-if="!total" class="p-6 text-sm text-muted-foreground">No rows match.</p>

        <!-- VList fills its parent (it sets height: 100% itself), so the parent carries the height. -->
        <div v-else class="h-[min(60vh,560px)]">
        <VList
          ref="list"
          :data="items"
          :item-size="ROW_H"
          data-matrix-grid
          @scroll="onScroll"
          v-slot="{ item: row, index }"
        >
          <div
            :key="row?._row ?? `p${index}`"
            data-row
            :data-src-row="row?._row"
            :data-hl-row="isHlRow(row) ? '' : undefined"
            :class="cn(
              'flex border-b text-xs',
              index % 2 === 1 && 'bg-muted/20',
              isHlRow(row) && 'relative z-1 outline-2 outline-solid -outline-offset-2 outline-primary',
            )"
            :style="{ height: ROW_H + 'px' }"
          >
            <div class="flex w-18 shrink-0 items-center px-2 tabular-nums text-muted-foreground">{{ row?._row ?? '…' }}</div>
            <template v-if="row">
              <div
                v-for="c in columns"
                :key="c.key"
                :data-col="c.key"
                :data-hl-cell="isHlCell(row, c.key) ? '' : undefined"
                :class="cn(
                  'flex shrink-0 items-center truncate border-l px-2',
                  (c.type === 'integer' || c.type === 'real') && 'justify-end tabular-nums',
                  row[c.key] === '•••' && 'text-muted-foreground',
                  isHlCell(row, c.key) && 'bg-accent font-medium text-accent-foreground',
                )"
                :style="{ width: COL_W + 'px' }"
              >{{ formatCell(row[c.key], c.type) }}</div>
            </template>
          </div>
        </VList>
        </div>
      </div>
    </div>
  </section>
</template>
