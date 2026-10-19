<script setup>
/**
 * JSON → HTML table.
 *
 * A pure client-side utility: paste or upload JSON, get an Excel-like table with
 * nested arrays rendered as merged cells. Nothing here talks to the backend or a
 * model — the whole feature is a transform, and keeping it local means it works
 * on data the user may not want to send anywhere.
 */
import { computed, ref, watch } from 'vue'
import {
  Braces, ChevronDown, Code, Columns3, Copy, Download, FileJson,
  Table as TableIcon, Search, Upload, X,
} from '@lucide/vue'
import {
  PopoverContent, PopoverPortal, PopoverRoot, PopoverTrigger,
} from 'reka-ui'
import {
  parseJson, findRowSets, selectRowSet, buildSchema, buildRows,
  applyView, flatten, countRows, toCsv,
} from '@/lib/jsontable'
import { toHtmlTable, downloadText, copyText } from '@/lib/jsontable-export'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Checkbox } from '@/components/ui/checkbox'
import { Badge } from '@/components/ui/badge'
import JsonTableGrid from '@/components/jsontable/JsonTableGrid.vue'

/** Refuse files big enough to lock the tab up while parsing. */
const MAX_BYTES = 50 * 1024 * 1024

/** Above this many rendered rows the table pages instead of emitting everything. */
const PAGE_THRESHOLD = 2000
const PAGE_SIZE = 200

const raw = ref('')
const fileName = ref('')
const error = ref('')
const notice = ref('')

const rowSets = ref([])
const rowSetPath = ref('')
const records = ref([])
const schema = ref({ scalars: [], groups: [] })
const rows = ref([])

const view = ref('report')
const search = ref('')
const filters = ref({})
const sort = ref(null)
const hidden = ref(new Set())        // column ids the user switched off
const page = ref(0)
const inputOpen = ref(true)

// ---- Parse -----------------------------------------------------------------

function reset() {
  rowSets.value = []
  records.value = []
  schema.value = { scalars: [], groups: [] }
  rows.value = []
  filters.value = {}
  sort.value = null
  hidden.value = new Set()
  page.value = 0
}

function parse() {
  error.value = ''
  notice.value = ''
  reset()

  const result = parseJson(raw.value)
  if (!result.ok) {
    error.value = result.error
    return
  }

  const sets = findRowSets(result.value)
  if (!sets.length) {
    error.value = 'That JSON parsed, but holds no array of objects to tabulate. '
      + 'Expected either a top-level array, or an object with an array property.'
    return
  }

  rowSets.value = sets
  rowSetPath.value = sets[0].path
  load()
}

/** Builds the model for the currently chosen row set. */
function load() {
  const result = parseJson(raw.value)
  if (!result.ok) return

  records.value = selectRowSet(result.value, rowSetPath.value)
  schema.value = buildSchema(records.value)
  rows.value = buildRows(records.value, schema.value)
  page.value = 0

  if (!schema.value.scalars.length && !schema.value.groups.length) {
    error.value = 'Those rows have no readable fields.'
  }
}

watch(rowSetPath, () => { if (rowSets.value.length) load() })

function onFile(event) {
  const file = event.target.files?.[0]
  if (!file) return

  if (file.size > MAX_BYTES) {
    error.value = `That file is ${(file.size / 1048576).toFixed(1)} MB — the limit is `
      + `${MAX_BYTES / 1048576} MB so the browser stays responsive.`
    event.target.value = ''
    return
  }

  const reader = new FileReader()
  reader.onload = () => {
    raw.value = String(reader.result || '')
    fileName.value = file.name
    parse()
    if (!error.value) inputOpen.value = false   // give the table the room once it works
  }
  reader.onerror = () => { error.value = 'Could not read that file.' }
  reader.readAsText(file)          // reads as UTF-8; parseJson strips any BOM
  event.target.value = ''
}

function clearAll() {
  raw.value = ''
  fileName.value = ''
  error.value = ''
  notice.value = ''
  search.value = ''
  reset()
}

// ---- View state ------------------------------------------------------------

const hasFilters = computed(() =>
  Object.values(filters.value).some((v) => String(v ?? '').trim()))

const isFiltering = computed(() => Boolean(search.value.trim()) || hasFilters.value)

// Merged cells and filtering are incompatible: a rowspan asserts "these rows
// belong to one record", which stops being true once rows are removed. So an
// active filter forces the flat view rather than rendering a lie.
// Remembers that the flat view was imposed rather than chosen, so clearing the
// filters can put the report back — without overriding a deliberate switch to
// Data view, which must survive filtering and un-filtering alike.
const forcedFlat = ref(false)

watch(isFiltering, (on) => {
  if (on) {
    if (view.value === 'report') {
      view.value = 'data'
      forcedFlat.value = true
    }
  } else if (forcedFlat.value) {
    view.value = 'report'
    forcedFlat.value = false
  }
})

function setView(next) {
  if (next === 'report' && isFiltering.value) return   // guarded in the UI too
  view.value = next
  // Picking a view by hand takes ownership of it from the auto-switch.
  if (next === 'data') forcedFlat.value = false
}

const visibleSchema = computed(() => ({
  scalars: schema.value.scalars.filter((c) => !hidden.value.has(c.id)),
  groups: schema.value.groups
    .map((g) => ({ ...g, columns: g.columns.filter((c) => !hidden.value.has(c.id)) }))
    .filter((g) => g.columns.length && !hidden.value.has(g.id)),
}))

const viewRows = computed(() =>
  applyView(rows.value, schema.value, {
    search: search.value,
    filters: filters.value,
    sort: sort.value,
  }))

const totalRenderRows = computed(() => countRows(viewRows.value))

/** Only page once the DOM cost justifies it — small tables render whole. */
const paged = computed(() => totalRenderRows.value > PAGE_THRESHOLD)

const pageCount = computed(() =>
  paged.value ? Math.max(1, Math.ceil(viewRows.value.length / PAGE_SIZE)) : 1)

const pageRows = computed(() => {
  if (!paged.value) return viewRows.value
  const start = page.value * PAGE_SIZE
  return viewRows.value.slice(start, start + PAGE_SIZE)
})

watch([viewRows, pageCount], () => {
  if (page.value >= pageCount.value) page.value = 0
})

function toggleSort(columnId) {
  if (sort.value?.columnId !== columnId) sort.value = { columnId, dir: 'asc' }
  else if (sort.value.dir === 'asc') sort.value = { columnId, dir: 'desc' }
  else sort.value = null
}

function toggleColumn(id, on) {
  const next = new Set(hidden.value)
  if (on) next.delete(id)
  else next.add(id)
  hidden.value = next
}

function toggleGroup(group, on) {
  const next = new Set(hidden.value)
  for (const c of group.columns) {
    if (on) next.delete(c.id)
    else next.add(c.id)
  }
  if (on) next.delete(group.id)
  else next.add(group.id)
  hidden.value = next
}

function groupShown(group) {
  return group.columns.some((c) => !hidden.value.has(c.id))
}

function clearFilters() {
  filters.value = {}
  search.value = ''
}

// ---- Export ----------------------------------------------------------------

const exportColumns = computed(() => [
  ...visibleSchema.value.scalars,
  ...visibleSchema.value.groups.flatMap((g) => g.columns),
])

function baseName() {
  return (fileName.value || 'json-table').replace(/\.json$/i, '')
}

function flash(message) {
  notice.value = message
  setTimeout(() => { notice.value = '' }, 3000)
}

// Exports always cover every filtered row, not just the visible page.
function exportHtml(download = true) {
  const html = toHtmlTable(viewRows.value, visibleSchema.value, baseName())
  if (download) {
    downloadText(`${baseName()}.html`, html, 'text/html;charset=utf-8')
    flash(`Downloaded ${viewRows.value.length} records as HTML (merged cells intact).`)
  } else {
    copyText(html).then((ok) => flash(ok ? 'Table HTML copied.' : 'Clipboard blocked by the browser.'))
  }
}

function exportCsv() {
  const csv = toCsv(exportColumns.value, flatten(viewRows.value, visibleSchema.value))
  downloadText(`${baseName()}.csv`, csv, 'text/csv;charset=utf-8')
  flash(`Downloaded ${totalRenderRows.value} flat rows as CSV.`)
}

const hasData = computed(() => rows.value.length > 0)
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <!-- Toolbar -->
    <div class="shrink-0 border-b px-4 py-2.5">
      <div class="flex flex-wrap items-center gap-2">
        <Button variant="outline" size="sm" class="h-8" @click="inputOpen = !inputOpen">
          <FileJson class="h-4 w-4" />
          {{ inputOpen ? 'Hide input' : 'JSON input' }}
        </Button>

        <div class="relative min-w-0 flex-1 sm:max-w-xs">
          <Search class="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
          <Input
            v-model="search"
            placeholder="Search all fields…"
            class="h-8 pl-8 text-xs"
            :disabled="!hasData"
          />
        </div>

        <!-- View toggle -->
        <div class="flex items-center rounded-md border p-0.5">
          <button
            type="button"
            :disabled="!hasData || isFiltering"
            :title="isFiltering
              ? 'Unavailable while filtering — merged cells cannot represent a filtered subset'
              : 'Merged report view'"
            :class="cn(
              'flex items-center gap-1 rounded px-2 py-1 text-xs transition-colors disabled:opacity-40',
              view === 'report' ? 'bg-accent text-accent-foreground' : 'text-muted-foreground',
            )"
            @click="setView('report')"
          >
            <TableIcon class="h-3.5 w-3.5" />
            Report
          </button>
          <button
            type="button"
            :disabled="!hasData"
            title="Flat view — one row per sub-row"
            :class="cn(
              'flex items-center gap-1 rounded px-2 py-1 text-xs transition-colors disabled:opacity-40',
              view === 'data' ? 'bg-accent text-accent-foreground' : 'text-muted-foreground',
            )"
            @click="setView('data')"
          >
            <Braces class="h-3.5 w-3.5" />
            Data
          </button>
        </div>

        <!-- Column visibility -->
        <PopoverRoot>
          <PopoverTrigger as-child>
            <Button variant="outline" size="sm" class="h-8" :disabled="!hasData">
              <Columns3 class="h-4 w-4" />
              Columns
              <ChevronDown class="h-3 w-3 opacity-60" />
            </Button>
          </PopoverTrigger>
          <PopoverPortal>
            <PopoverContent
              side="bottom"
              align="end"
              :side-offset="6"
              class="z-50 max-h-96 w-64 overflow-y-auto rounded-lg border bg-popover p-2 text-popover-foreground shadow-lg outline-none"
            >
              <p class="mb-1 px-1 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
                Record fields
              </p>
              <label
                v-for="c in schema.scalars"
                :key="c.id"
                class="flex cursor-pointer items-center gap-2 rounded px-1 py-0.5 text-xs hover:bg-accent"
              >
                <Checkbox
                  :model-value="!hidden.has(c.id)"
                  @update:model-value="toggleColumn(c.id, $event)"
                />
                <span class="truncate">{{ c.label }}</span>
              </label>

              <template v-for="g in schema.groups" :key="g.id">
                <div class="mt-2 flex items-center gap-2 border-t px-1 pt-1.5">
                  <Checkbox
                    :model-value="groupShown(g)"
                    @update:model-value="toggleGroup(g, $event)"
                  />
                  <span class="text-[11px] font-semibold uppercase tracking-wide">{{ g.label }}</span>
                </div>
                <label
                  v-for="c in g.columns"
                  :key="c.id"
                  class="flex cursor-pointer items-center gap-2 rounded px-1 py-0.5 pl-5 text-xs hover:bg-accent"
                >
                  <Checkbox
                    :model-value="!hidden.has(c.id)"
                    @update:model-value="toggleColumn(c.id, $event)"
                  />
                  <span class="truncate">{{ c.label }}</span>
                </label>
              </template>
            </PopoverContent>
          </PopoverPortal>
        </PopoverRoot>

        <!-- Export -->
        <PopoverRoot>
          <PopoverTrigger as-child>
            <Button variant="outline" size="sm" class="h-8" :disabled="!hasData">
              <Download class="h-4 w-4" />
              Export
              <ChevronDown class="h-3 w-3 opacity-60" />
            </Button>
          </PopoverTrigger>
          <PopoverPortal>
            <PopoverContent
              side="bottom"
              align="end"
              :side-offset="6"
              class="z-50 w-60 rounded-lg border bg-popover p-1.5 text-popover-foreground shadow-lg outline-none"
            >
              <button
                type="button"
                class="flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-xs hover:bg-accent"
                @click="exportHtml(true)"
              >
                <Code class="h-3.5 w-3.5" />
                <span>Download HTML <span class="text-muted-foreground">(merged cells)</span></span>
              </button>
              <button
                type="button"
                class="flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-xs hover:bg-accent"
                @click="exportHtml(false)"
              >
                <Copy class="h-3.5 w-3.5" />
                Copy table HTML
              </button>
              <button
                type="button"
                class="flex w-full items-center gap-2 rounded px-2 py-1.5 text-left text-xs hover:bg-accent"
                @click="exportCsv"
              >
                <Download class="h-3.5 w-3.5" />
                <span>Download CSV <span class="text-muted-foreground">(flat)</span></span>
              </button>
            </PopoverContent>
          </PopoverPortal>
        </PopoverRoot>

        <!-- Counts -->
        <div class="ml-auto flex items-center gap-2 text-xs text-muted-foreground">
          <span v-if="hasData">
            Showing <b class="text-foreground">{{ viewRows.length }}</b> of
            <b class="text-foreground">{{ rows.length }}</b> records
            <span class="opacity-70">· {{ totalRenderRows }} rows</span>
          </span>
          <Button
            v-if="isFiltering"
            variant="ghost"
            size="sm"
            class="h-7 px-2 text-xs"
            @click="clearFilters"
          >
            <X class="h-3 w-3" />
            Clear
          </Button>
        </div>
      </div>

      <!-- Why the view switched -->
      <p v-if="isFiltering" class="mt-1.5 text-[11px] text-muted-foreground">
        Showing the flat view: merged cells span a whole record, so they cannot represent a filtered
        subset of its rows. Clear the search and filters to return to the report view.
      </p>

      <p v-if="notice" class="mt-1.5 text-[11px] font-medium text-foreground">
        {{ notice }}
      </p>
    </div>

    <!-- Input panel -->
    <div v-if="inputOpen" class="shrink-0 border-b bg-muted/30 px-4 py-3">
      <div class="flex flex-col gap-2">
        <div class="flex flex-wrap items-center gap-2">
          <label class="inline-flex">
            <input type="file" accept=".json,application/json,text/plain" class="hidden" @change="onFile">
            <span class="inline-flex h-8 cursor-pointer items-center gap-1.5 rounded-md border px-3 text-xs font-medium hover:bg-accent">
              <Upload class="h-3.5 w-3.5" />
              Upload JSON
            </span>
          </label>

          <Button size="sm" class="h-8" :disabled="!raw.trim()" @click="parse">
            Parse
          </Button>
          <Button variant="ghost" size="sm" class="h-8" :disabled="!raw" @click="clearAll">
            Clear
          </Button>

          <Badge v-if="fileName" variant="secondary" class="font-normal">
            {{ fileName }}
          </Badge>

          <!-- Which array to tabulate, when the JSON offers a choice. -->
          <template v-if="rowSets.length > 1">
            <span class="ml-2 text-xs text-muted-foreground">Rows from:</span>
            <select
              v-model="rowSetPath"
              class="h-8 rounded-md border bg-background px-2 text-xs outline-none"
            >
              <option v-for="s in rowSets" :key="s.path" :value="s.path">
                {{ s.label }} ({{ s.count }})
              </option>
            </select>
          </template>

          <span class="ml-auto text-[11px] text-muted-foreground">
            Runs entirely in your browser — nothing is uploaded.
          </span>
        </div>

        <textarea
          v-model="raw"
          placeholder='Paste JSON here — a top-level array, or an object containing one…'
          spellcheck="false"
          class="h-28 w-full resize-y rounded-md border bg-background p-2 font-mono text-xs outline-none focus:ring-1 focus:ring-ring"
        />
      </div>
    </div>

    <!-- Error -->
    <div v-if="error" class="shrink-0 px-4 py-2">
      <pre class="whitespace-pre-wrap rounded-lg border border-destructive/40 bg-destructive/5 px-3 py-2 text-xs font-medium text-destructive">{{ error }}</pre>
    </div>

    <!-- Table -->
    <div class="min-h-0 flex-1 overflow-hidden p-4 pt-2">
      <div v-if="!hasData && !error" class="flex h-full items-center justify-center">
        <div class="text-center">
          <TableIcon class="mx-auto mb-3 h-8 w-8 text-muted-foreground/50" />
          <p class="text-sm text-muted-foreground">
            Paste or upload JSON to build a table.
          </p>
          <p class="mt-1 text-xs text-muted-foreground/80">
            Nested arrays become merged column groups.
          </p>
        </div>
      </div>

      <div v-else-if="hasData" class="flex h-full min-h-0 flex-col gap-2">
        <JsonTableGrid
          v-model:filters="filters"
          :rows="pageRows"
          :schema="visibleSchema"
          :view="view"
          :sort="sort"
          class="min-h-0 flex-1"
          @sort="toggleSort"
        />

        <!-- Paging, only once the row count warrants it -->
        <div v-if="paged" class="flex shrink-0 items-center justify-center gap-2 text-xs">
          <Button variant="outline" size="sm" class="h-7" :disabled="page === 0" @click="page--">
            Previous
          </Button>
          <span class="text-muted-foreground">
            Page {{ page + 1 }} of {{ pageCount }}
            <span class="opacity-70">· {{ PAGE_SIZE }} records per page</span>
          </span>
          <Button
            variant="outline"
            size="sm"
            class="h-7"
            :disabled="page >= pageCount - 1"
            @click="page++"
          >
            Next
          </Button>
        </div>
      </div>
    </div>
  </div>
</template>
