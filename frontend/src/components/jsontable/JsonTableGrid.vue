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
import { computed } from 'vue'
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
  /** Freeze the first scalar column so wide tables stay readable while scrolling. */
  stickyFirst: { type: Boolean, default: true },
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
 * Freezes the first scalar column. The opaque background matters as much as the
 * position: a transparent frozen cell lets the columns scrolling underneath show
 * through it, which reads as corrupted text.
 */
function stickyClass(colIndex, isScalar) {
  if (!props.stickyFirst || !isScalar || colIndex !== 0) return ''
  return 'sticky left-0 z-20 bg-background'
}
</script>

<template>
  <div class="relative h-full overflow-auto rounded-lg border">
    <table class="w-max min-w-full border-collapse text-left text-xs">
      <!-- Two-row head: group spans on top, then the leaf columns. -->
      <thead class="sticky top-0 z-30">
        <tr class="bg-muted">
          <th
            v-if="schema.scalars.length"
            :colspan="schema.scalars.length"
            class="whitespace-nowrap border-b border-r px-2 py-1.5 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground"
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
              stickyClass(i, true) && 'sticky left-0 z-40 bg-muted',
            )"
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
              'border-b border-r bg-muted/80 px-1 py-1',
              stickyClass(i, true) && 'sticky left-0 z-40 bg-muted',
            )"
          >
            <input
              :value="filters[c.id] || ''"
              :placeholder="`Filter…`"
              class="w-full min-w-24 rounded border bg-background px-1.5 py-0.5 text-[11px] outline-none focus:ring-1 focus:ring-ring"
              @input="setFilter(c.id, $event.target.value)"
            >
          </th>
          <th v-for="c in groupColumns" :key="c.id" class="border-b border-r bg-muted/80 px-1 py-1">
            <input
              :value="filters[c.id] || ''"
              placeholder="Filter…"
              class="w-full min-w-24 rounded border bg-background px-1.5 py-0.5 text-[11px] outline-none focus:ring-1 focus:ring-ring"
              @input="setFilter(c.id, $event.target.value)"
            >
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
