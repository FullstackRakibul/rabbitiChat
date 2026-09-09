<script setup>
/**
 * Renders the table in either view off the shared row model.
 *
 * Report view is the headline: one record occupies `rowHeight` <tr>s, its scalar
 * cells carry rowspan so they read as a single merged block, and each nested
 * group fills its own columns down those rows. Data view emits the same rows
 * flat, with scalars repeated.
 *
 * The two views read the same `rows` prop, so they cannot disagree about the
 * data — only about how it is drawn.
 */
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ArrowDown, ArrowUp, ChevronsUpDown } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { toText } from '@/lib/jsontable'

const props = defineProps({
  rows: { type: Array, default: () => [] },
  /** { scalars: Column[], groups: Group[] } already narrowed to visible columns. */
  schema: { type: Object, required: true },
  view: { type: String, default: 'report' },      // 'report' | 'data'
  filters: { type: Object, default: () => ({}) },
  sort: { type: Object, default: null },
  /**
   * How many leading scalar columns to freeze (EmpCode, Name) so identity stays
   * visible while scrolling a wide table sideways. 0 disables freezing.
   */
  stickyCount: { type: Number, default: 2 },
})

const emit = defineEmits(['update:filters', 'sort'])

const groupColumns = computed(() => props.schema.groups.flatMap((g) => g.columns))

/** Data view materialises every sub-row; report view keeps records intact. */
const flatRows = computed(() => {
  if (props.view !== 'data') return []
  const out = []
  for (const row of props.rows) {
    for (let i = 0; i < row.rowHeight; i++) out.push({ row, i })
  }
  return out
})

function scalarValue(row, col) {
  return toText(row.scalars[col.path])
}

function groupValue(row, col, index) {
  const item = (row.groups[col.group] || [])[index]
  return item ? toText(item[col.path]) : ''
}

function setFilter(columnId, value) {
  emit('update:filters', { ...props.filters, [columnId]: value })
}

function sortIcon(col) {
  if (props.sort?.columnId !== col.id) return ChevronsUpDown
  return props.sort.dir === 'asc' ? ArrowUp : ArrowDown
}

/**
 * Freezing more than one column needs each one's `left` offset to equal the total
 * width of the columns before it. Those widths are content-driven, so they are
 * measured from the rendered header rather than guessed at — a hard-coded offset
 * would drift the moment a column's content changed.
 */
const table = ref(null)
const offsets = ref([])

function measure() {
  const el = table.value
  if (!el || props.stickyCount < 1) { offsets.value = []; return }

  const headerCells = el.querySelectorAll('thead tr:nth-child(2) > th')
  const next = []
  let acc = 0
  for (let i = 0; i < Math.min(props.stickyCount, headerCells.length); i++) {
    next.push(acc)
    acc += headerCells[i].getBoundingClientRect().width
  }
  offsets.value = next
}

onMounted(() => {
  measure()
  // Column widths change with the data and with the container, so re-measure
  // rather than trusting a single startup reading.
  if (typeof ResizeObserver !== 'undefined' && table.value) {
    observer = new ResizeObserver(() => measure())
    observer.observe(table.value)
  }
})

onBeforeUnmount(() => observer?.disconnect())

let observer = null

// A schema change (hidden columns, different file) moves every boundary.
watch(() => [props.schema, props.view, props.rows.length], () => nextTick(measure), { deep: false })

/** Is this scalar column frozen? Group columns never are — they scroll. */
function isSticky(colIndex, isScalar) {
  return isScalar && colIndex < props.stickyCount && offsets.value.length > colIndex
}

/**
 * The opaque background matters as much as the position: a transparent frozen
 * cell lets the columns scrolling underneath show through it, which reads as
 * corrupted text.
 */
function stickyClass(colIndex, isScalar, tone = 'bg-background', z = 'z-20') {
  if (!isSticky(colIndex, isScalar)) return ''
  return `sticky ${z} ${tone}`
}

/** Inline because the offset is a measured pixel value, not a utility class. */
function stickyStyle(colIndex, isScalar) {
  if (!isSticky(colIndex, isScalar)) return null
  return { left: `${offsets.value[colIndex]}px` }
}
</script>

<template>
  <div class="relative h-full overflow-auto rounded-lg border">
    <table ref="table" class="w-max min-w-full border-collapse text-left text-xs">
      <!-- Two-row head: group spans on top, then the leaf columns. -->
      <thead class="sticky top-0 z-30">
        <tr class="bg-muted">
          <th
            v-if="schema.scalars.length"
            :colspan="schema.scalars.length"
            class="whitespace-nowrap border-b border-r bg-muted px-2 py-1.5 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground"
          >
            Record
          </th>
          <th
            v-for="g in schema.groups"
            :key="g.id"
            :colspan="g.columns.length"
            class="whitespace-nowrap border-b border-r bg-accent/60 px-2 py-1.5 text-center text-[11px] font-semibold uppercase tracking-wide"
          >
            {{ g.label }}
          </th>
        </tr>

        <tr class="bg-muted">
          <th
            v-for="(c, i) in schema.scalars"
            :key="c.id"
            :class="cn(
              'whitespace-nowrap border-b border-r bg-muted px-2 py-1.5 font-semibold',
              stickyClass(i, true, 'bg-muted', 'z-40'),
            )"
            :style="stickyStyle(i, true)"
          >
            <button
              type="button"
              class="flex items-center gap-1 hover:text-foreground"
              :title="`Sort by ${c.label}`"
              @click="emit('sort', c.id)"
            >
              {{ c.label }}
              <component :is="sortIcon(c)" class="h-3 w-3 opacity-50" />
            </button>
          </th>

          <th
            v-for="c in groupColumns"
            :key="c.id"
            class="whitespace-nowrap border-b border-r bg-muted px-2 py-1.5 font-semibold"
            :title="`${c.group} › ${c.label} — nested columns are not sortable`"
          >
            {{ c.label }}
          </th>
        </tr>

        <!-- Per-column filter row -->
        <tr class="bg-muted/80">
          <th
            v-for="(c, i) in schema.scalars"
            :key="c.id"
            :class="cn(
              'border-b border-r bg-muted px-1 py-1',
              stickyClass(i, true, 'bg-muted', 'z-40'),
            )"
            :style="stickyStyle(i, true)"
          >
            <input
              :value="filters[c.id] || ''"
              :placeholder="`Filter…`"
              class="w-full min-w-24 rounded border bg-background px-1.5 py-0.5 text-[11px] outline-none focus:ring-1 focus:ring-ring"
              @input="setFilter(c.id, $event.target.value)"
            >
          </th>
          <!-- Nested columns get no filter box: a nested value belongs to one
               sub-row, and filtering on it would either drop the rest of the
               record's rows or silently keep them. The global search covers
               these fields instead. -->
          <th
            v-for="c in groupColumns"
            :key="c.id"
            class="border-b border-r bg-muted px-1 py-1 text-center text-[10px] text-muted-foreground/70"
            title="Nested columns aren't filtered individually — a value here belongs to one sub-row, not the record. Use the global search."
          >
            —
          </th>
        </tr>
      </thead>

      <!-- REPORT VIEW: merged scalar cells spanning each record's rows. -->
      <tbody v-if="view === 'report'">
        <template v-for="row in rows" :key="row.index">
          <tr
            v-for="i in row.rowHeight"
            :key="`${row.index}-${i}`"
            :class="cn(
              'hover:bg-accent/40',
              i === 1 && 'border-t-2 border-t-border',
            )"
          >
            <!-- Scalars appear once per record, spanning its full height. -->
            <template v-if="i === 1">
              <td
                v-for="(c, ci) in schema.scalars"
                :key="c.id"
                :rowspan="row.rowHeight"
                :class="cn(
                  'border-b border-r bg-background px-2 py-1 align-top',
                  stickyClass(ci, true),
                )"
                :style="stickyStyle(ci, true)"
              >
                {{ scalarValue(row, c) }}
              </td>
            </template>

            <td
              v-for="c in groupColumns"
              :key="c.id"
              class="border-b border-r px-2 py-1 align-top"
            >
              {{ groupValue(row, c, i - 1) }}
            </td>
          </tr>
        </template>
      </tbody>

      <!-- DATA VIEW: one row per sub-row, scalars repeated, nothing merged. -->
      <tbody v-else>
        <tr
          v-for="({ row, i }) in flatRows"
          :key="`${row.index}-${i}`"
          :class="cn('hover:bg-accent/40', i === 0 && 'border-t-2 border-t-border')"
        >
          <td
            v-for="(c, ci) in schema.scalars"
            :key="c.id"
            :class="cn('border-b border-r bg-background px-2 py-1 align-top', stickyClass(ci, true))"
            :style="stickyStyle(ci, true)"
          >
            {{ scalarValue(row, c) }}
          </td>
          <td
            v-for="c in groupColumns"
            :key="c.id"
            class="border-b border-r px-2 py-1 align-top"
          >
            {{ groupValue(row, c, i) }}
          </td>
        </tr>
      </tbody>
    </table>

    <p v-if="!rows.length" class="px-3 py-8 text-center text-sm text-muted-foreground">
      No rows match the current search and filters.
    </p>
  </div>
</template>
