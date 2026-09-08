<script setup>
/**
 * "Recent searches" — the user's remembered queries.
 *
 * Clicking a row re-runs the query rather than loading anything: results are
 * never stored, so a re-run is the only honest form of reuse. Delete is soft,
 * which is what makes the Undo affordance a real restore.
 */
import { computed, nextTick, onMounted, ref } from 'vue'
import { Check, Pencil, RotateCcw, Search, Trash2, X } from '@lucide/vue'
import {
  listHistory, renameHistory, deleteHistory, restoreHistory,
} from '@/services/search.service.js'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'

const props = defineProps({
  /** Disables the rows while a search is in flight. */
  busy: { type: Boolean, default: false },
  /** The query currently showing results, so its row can be marked. */
  activeQuery: { type: String, default: '' },
})

const emit = defineEmits(['run'])

const items = ref([])
const deletedItems = ref([])
const loading = ref(true)
const error = ref('')
const showDeleted = ref(false)

// Inline rename state.
const editingId = ref(null)
const draft = ref('')
const draftInput = ref(null)

// The undo affordance for a soft delete: { id, label }.
const undo = ref(null)
let undoTimer = null

async function refresh() {
  loading.value = true
  error.value = ''
  try {
    items.value = await listHistory()
    if (showDeleted.value) deletedItems.value = await listHistory(true)
  } catch (e) {
    error.value = e.message || 'Could not load your search history.'
  } finally {
    loading.value = false
  }
}

onMounted(refresh)

/** What a row shows: its title when set, otherwise the query itself. */
function labelOf(h) {
  return h.title?.trim() || h.query
}

const hasAny = computed(() => items.value.length > 0)

// ---- Relative time -------------------------------------------------------
// Only ever derived from the server's timestamp; nothing here is invented.
const UNITS = [
  [60, 'second'], [60, 'minute'], [24, 'hour'], [7, 'day'], [4.35, 'week'], [12, 'month'],
]

function relativeTime(iso) {
  const then = Date.parse(iso)
  if (Number.isNaN(then)) return ''

  let diff = (Date.now() - then) / 1000
  if (diff < 45) return 'just now'

  let unit = 'second'
  for (const [size, name] of UNITS) {
    if (Math.abs(diff) < size) break
    diff /= size
    unit = name
  }
  const n = Math.round(diff)
  return `${n} ${unit}${n === 1 ? '' : 's'} ago`
}

// ---- Actions -------------------------------------------------------------

function run(h) {
  if (props.busy || editingId.value === h.id) return
  emit('run', { query: h.query, maxResults: h.maxResults })
}

async function startEdit(h) {
  editingId.value = h.id
  draft.value = h.title || h.query
  await nextTick()
  draftInput.value?.[0]?.focus?.() ?? draftInput.value?.focus?.()
}

function cancelEdit() {
  editingId.value = null
  draft.value = ''
}

async function saveEdit(h) {
  const title = draft.value.trim()
  cancelEdit()
  try {
    const updated = await renameHistory(h.id, title)
    const i = items.value.findIndex((x) => x.id === h.id)
    if (i !== -1) items.value[i] = updated
  } catch (e) {
    error.value = e.message || 'Rename failed.'
    refresh()
  }
}

async function remove(h) {
  // Optimistic: the row leaves at once, and the undo bar carries the way back.
  const previous = items.value
  items.value = items.value.filter((x) => x.id !== h.id)

  try {
    await deleteHistory(h.id)
    showUndo(h)
    if (showDeleted.value) deletedItems.value = await listHistory(true)
  } catch (e) {
    items.value = previous
    error.value = e.message || 'Delete failed.'
  }
}

function showUndo(h) {
  clearTimeout(undoTimer)
  undo.value = { id: h.id, label: labelOf(h) }
  undoTimer = setTimeout(() => { undo.value = null }, 8000)
}

async function undoDelete() {
  const target = undo.value
  if (!target) return
  undo.value = null
  clearTimeout(undoTimer)

  try {
    // A real restore, not a re-create: the row was only flagged, so its
    // run count and original dates come back untouched.
    await restoreHistory(target.id)
    await refresh()
  } catch (e) {
    error.value = e.message || 'Restore failed.'
  }
}

async function restoreRow(h) {
  try {
    await restoreHistory(h.id)
    await refresh()
  } catch (e) {
    error.value = e.message || 'Restore failed.'
  }
}

async function toggleDeleted() {
  showDeleted.value = !showDeleted.value
  if (showDeleted.value) {
    try {
      deletedItems.value = await listHistory(true)
    } catch (e) {
      error.value = e.message || 'Could not load deleted searches.'
    }
  }
}

defineExpose({ refresh })
</script>

<template>
  <div class="flex flex-col gap-1.5">
    <div class="flex items-center gap-2">
      <span class="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
        Recent searches
      </span>

      <button
        v-if="hasAny || showDeleted"
        type="button"
        class="ml-auto text-[11px] text-muted-foreground underline-offset-2 hover:underline"
        @click="toggleDeleted"
      >
        {{ showDeleted ? 'Hide deleted' : 'Show deleted' }}
      </button>
    </div>

    <p v-if="error" class="text-xs font-medium text-destructive">
      {{ error }}
    </p>

    <p v-if="loading" class="py-1 text-xs text-muted-foreground">
      Loading…
    </p>

    <template v-else>
      <!-- Active history -->
      <div v-if="hasAny" class="flex flex-wrap items-center gap-1.5">
        <div
          v-for="h in items"
          :key="h.id"
          :class="cn(
            'group flex items-center gap-1 rounded-md border px-1.5 py-0.5 text-xs transition-colors',
            h.query === activeQuery
              ? 'border-primary/30 bg-accent text-accent-foreground'
              : 'text-muted-foreground hover:bg-accent hover:text-accent-foreground',
          )"
        >
          <!-- Rename in place -->
          <template v-if="editingId === h.id">
            <input
              ref="draftInput"
              v-model="draft"
              class="w-40 bg-transparent px-0.5 text-xs outline-none"
              @keydown.enter.prevent="saveEdit(h)"
              @keydown.esc.prevent="cancelEdit"
            >
            <button type="button" class="opacity-70 hover:opacity-100" title="Save" @click="saveEdit(h)">
              <Check class="h-3 w-3" />
            </button>
            <button type="button" class="opacity-70 hover:opacity-100" title="Cancel" @click="cancelEdit">
              <X class="h-3 w-3" />
            </button>
          </template>

          <template v-else>
            <button
              type="button"
              class="max-w-56 truncate"
              :disabled="busy"
              :title="`Run “${h.query}” again`"
              @click="run(h)"
              @dblclick="startEdit(h)"
            >
              {{ labelOf(h) }}
            </button>

            <span
              v-if="h.runCount > 1"
              class="tabular-nums opacity-70"
              :title="`Run ${h.runCount} times`"
            >×{{ h.runCount }}</span>

            <span class="hidden opacity-60 sm:inline" :title="h.lastRunAt">
              · {{ relativeTime(h.lastRunAt) }}
            </span>

            <button
              type="button"
              class="opacity-0 transition-opacity hover:opacity-100 focus:opacity-100 group-hover:opacity-60"
              title="Rename"
              @click="startEdit(h)"
            >
              <Pencil class="h-3 w-3" />
            </button>
            <button
              type="button"
              class="opacity-0 transition-opacity hover:opacity-100 focus:opacity-100 group-hover:opacity-60"
              title="Delete"
              @click="remove(h)"
            >
              <Trash2 class="h-3 w-3" />
            </button>
          </template>
        </div>
      </div>

      <p v-else class="py-1 text-xs text-muted-foreground">
        No searches yet — run one and it will show up here.
      </p>

      <!-- Soft-deleted rows, restorable -->
      <div v-if="showDeleted" class="mt-1 flex flex-col gap-1">
        <p class="text-[11px] uppercase tracking-wide text-muted-foreground/70">
          Deleted
        </p>
        <div v-if="deletedItems.length" class="flex flex-wrap items-center gap-1.5">
          <div
            v-for="h in deletedItems"
            :key="h.id"
            class="flex items-center gap-1 rounded-md border border-dashed px-1.5 py-0.5 text-xs text-muted-foreground"
          >
            <span class="max-w-56 truncate line-through opacity-70">{{ labelOf(h) }}</span>
            <button type="button" class="hover:text-foreground" title="Restore" @click="restoreRow(h)">
              <RotateCcw class="h-3 w-3" />
            </button>
          </div>
        </div>
        <p v-else class="text-xs text-muted-foreground">Nothing deleted.</p>
      </div>
    </template>

    <!-- Undo bar. The restore is genuine because the delete was only a flag. -->
    <div
      v-if="undo"
      class="flex items-center gap-2 rounded-md border bg-card px-2 py-1 text-xs shadow-sm"
      role="status"
    >
      <Search class="h-3 w-3 shrink-0 text-muted-foreground" />
      <span class="min-w-0 flex-1 truncate text-muted-foreground">
        Deleted “{{ undo.label }}”
      </span>
      <Button variant="ghost" size="sm" class="h-6 px-2 text-xs" @click="undoDelete">
        <RotateCcw class="h-3 w-3" />
        Undo
      </Button>
    </div>
  </div>
</template>
