<!--
  todorabbit — a creation of Mostisko, the AI.

  The Kanban board. Columns are user-defined at runtime, so a card's status is
  simply the column it sits in.

  Drag handling is optimistic: the local tree is mutated the instant a drop
  lands, the API call goes out behind it, and a failure restores the snapshot
  taken before the drag and raises a toast. Rapid column reorders are debounced
  so a burst of drags costs one request.
-->
<script setup>
import { computed, onMounted, onBeforeUnmount, nextTick, ref, watch } from 'vue'
import draggable from 'vuedraggable'
import { ChevronDown, LayoutGrid, Plus, RefreshCw, Trash2, X } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import KanbanColumn from '@/components/todorabbit/KanbanColumn.vue'
import {
  listBoards, createBoard, getBoard, renameBoard, deleteBoard,
  addColumn, updateColumn, deleteColumn, reorderColumns,
  addCard, updateCard, deleteCard, moveCard,
} from '@/services/todorabbit.service.js'

// Palette keys the API accepts; the dot classes below are their only rendering.
const COLOR_KEYS = ['slate', 'blue', 'green', 'amber', 'red', 'purple', 'pink', 'teal']

const DOT_CLASS = {
  slate: 'bg-slate-400',
  blue: 'bg-blue-500',
  green: 'bg-emerald-500',
  amber: 'bg-amber-500',
  red: 'bg-red-500',
  purple: 'bg-violet-500',
  pink: 'bg-pink-500',
  teal: 'bg-teal-500',
}
const dotClass = (key) => DOT_CLASS[key] || DOT_CLASS.slate

// ---- State ----

const boards = ref([])
const board = ref(null)          // the open board, columns + cards nested
const loading = ref(true)        // first load / board switch — shows skeletons
const busy = ref(false)          // a mutation is in flight
const error = ref('')            // failure of the last load
const toast = ref('')            // transient failure notice (rollbacks etc.)
const boardMenuOpen = ref(false)
const addingColumn = ref(false)
const newColumnTitle = ref('')
const newColumnEl = ref(null)
const renamingBoard = ref(false)
const boardNameDraft = ref('')

let toastTimer = null
let reorderTimer = null

const columns = computed({
  get: () => board.value?.columns ?? [],
  set: (value) => {
    if (!board.value) return
    const snapshot = snapshotColumns()
    board.value.columns = value
    queueColumnReorder(snapshot)
  },
})

// ---- Toast ----

function showToast(message) {
  toast.value = message
  clearTimeout(toastTimer)
  toastTimer = setTimeout(() => { toast.value = '' }, 4000)
}

// ---- Snapshots (rollback) ----

/** Deep-enough copy of the column tree to restore after a failed drag. */
function snapshotColumns() {
  return (board.value?.columns ?? []).map(c => ({
    ...c,
    cards: c.cards.map(card => ({ ...card })),
  }))
}

function restore(snapshot, message) {
  if (board.value) board.value.columns = snapshot
  showToast(message || "Couldn't save — board restored.")
}

// ---- Loading ----

async function loadBoards({ openFirst = true } = {}) {
  loading.value = true
  error.value = ''
  try {
    boards.value = await listBoards()
    if (!openFirst) return

    if (boards.value.length) {
      await openBoard(boards.value[0].id, { silent: true })
    } else {
      board.value = null
    }
  } catch (e) {
    error.value = e.message || 'Could not load your boards.'
  } finally {
    loading.value = false
  }
}

async function openBoard(id, { silent = false } = {}) {
  if (!silent) loading.value = true
  error.value = ''
  boardMenuOpen.value = false
  try {
    board.value = await getBoard(id)
  } catch (e) {
    board.value = null
    error.value = e.message || 'Could not open that board.'
  } finally {
    if (!silent) loading.value = false
  }
}

// ---- Boards ----

async function onCreateBoard() {
  busy.value = true
  try {
    const created = await createBoard('My board')
    board.value = created
    boards.value.unshift({
      id: created.id,
      name: created.name,
      createdAt: created.createdAt,
      columnCount: created.columns.length,
      cardCount: 0,
    })
    boardMenuOpen.value = false
  } catch (e) {
    showToast(e.message || "Couldn't create the board.")
  } finally {
    busy.value = false
  }
}

function beginRenameBoard() {
  boardNameDraft.value = board.value?.name ?? ''
  renamingBoard.value = true
}

async function commitRenameBoard() {
  const name = boardNameDraft.value.trim()
  renamingBoard.value = false
  if (!board.value || !name || name === board.value.name) return

  const previous = board.value.name
  board.value.name = name                                   // optimistic
  syncSummary(board.value.id, { name })
  try {
    await renameBoard(board.value.id, name)
  } catch (e) {
    board.value.name = previous
    syncSummary(board.value.id, { name: previous })
    showToast(e.message || "Couldn't rename the board.")
  }
}

async function onDeleteBoard() {
  if (!board.value) return
  const id = board.value.id
  boardMenuOpen.value = false
  busy.value = true
  try {
    await deleteBoard(id)
    boards.value = boards.value.filter(b => b.id !== id)
    if (boards.value.length) await openBoard(boards.value[0].id)
    else board.value = null
  } catch (e) {
    showToast(e.message || "Couldn't delete the board.")
  } finally {
    busy.value = false
  }
}

/** Keeps the board-picker row in step with the open board. */
function syncSummary(id, patch) {
  const row = boards.value.find(b => b.id === id)
  if (row) Object.assign(row, patch)
}

// ---- Columns ----

function beginAddColumn() {
  addingColumn.value = true
  newColumnTitle.value = ''
  nextTick(() => newColumnEl.value?.focus())
}

async function commitAddColumn() {
  const title = newColumnTitle.value.trim()
  if (!title || !board.value) return cancelAddColumn()

  addingColumn.value = false
  try {
    const column = await addColumn(board.value.id, { title, colorKey: 'slate' })
    board.value.columns.push({ ...column, cards: [] })
  } catch (e) {
    showToast(e.message || "Couldn't add the column.")
  }
}

function cancelAddColumn() {
  addingColumn.value = false
  newColumnTitle.value = ''
}

async function onRenameColumn(column, title) {
  const previous = column.title
  column.title = title                                      // optimistic
  try {
    await updateColumn(column.id, { title })
  } catch (e) {
    column.title = previous
    showToast(e.message || "Couldn't rename the column.")
  }
}

async function onRecolorColumn(column, colorKey) {
  const previous = column.colorKey
  column.colorKey = colorKey
  try {
    await updateColumn(column.id, { colorKey })
  } catch (e) {
    column.colorKey = previous
    showToast(e.message || "Couldn't recolour the column.")
  }
}

async function onDeleteColumn(column) {
  const snapshot = snapshotColumns()
  board.value.columns = board.value.columns.filter(c => c.id !== column.id)
  try {
    await deleteColumn(column.id)
  } catch (e) {
    restore(snapshot, e.message || "Couldn't delete the column.")
  }
}

/**
 * Column drags fire per drop; a burst of them collapses into one request.
 * The snapshot is the tree from before the *first* drag in the burst, so a
 * failure rolls the whole burst back together.
 */
let pendingReorderSnapshot = null

function queueColumnReorder(snapshot) {
  pendingReorderSnapshot ??= snapshot
  clearTimeout(reorderTimer)
  reorderTimer = setTimeout(flushColumnReorder, 400)
}

async function flushColumnReorder() {
  if (!board.value) return
  const snapshot = pendingReorderSnapshot
  pendingReorderSnapshot = null

  try {
    await reorderColumns(board.value.columns.map(c => c.id))
  } catch (e) {
    if (snapshot) restore(snapshot, e.message && e.status !== undefined
      ? "Couldn't save — board restored."
      : "Couldn't save — board restored.")
  }
}

// ---- Cards ----

async function onAddCard(column, title) {
  // Placeholder id keeps the key stable until the server hands back the real one.
  const temp = {
    id: `tmp-${Date.now()}`, columnId: column.id, title, content: '',
    priority: null, sortOrder: column.cards.length, __saving: true,
  }
  column.cards.push(temp)

  try {
    const saved = await addCard(column.id, { title })
    Object.assign(temp, saved, { __saving: false })
  } catch (e) {
    column.cards = column.cards.filter(c => c.id !== temp.id)
    showToast(e.message || "Couldn't add the card.")
  }
}

async function onSaveCard(column, card, patch) {
  const previous = { title: card.title, content: card.content, priority: card.priority }
  Object.assign(card, patch, { __saving: true })            // optimistic

  try {
    const saved = await updateCard(card.id, patch)
    Object.assign(card, saved, { __saving: false })
  } catch (e) {
    Object.assign(card, previous, { __saving: false })
    showToast(e.message || "Couldn't save the card.")
  }
}

async function onDeleteCard(column, card) {
  const snapshot = snapshotColumns()
  column.cards = column.cards.filter(c => c.id !== card.id)
  try {
    await deleteCard(card.id)
  } catch (e) {
    restore(snapshot, e.message || "Couldn't delete the card.")
  }
}

/**
 * A card list changed — either reordered in place or received a card dragged in
 * from another column. The new array is already the post-drop state; we diff it
 * against the pre-drop snapshot to find which card landed where, apply it, then
 * persist that single move.
 */
async function onCardsChanged(column, cards) {
  const snapshot = snapshotColumns()
  const before = column.cards
  column.cards = cards

  // The moved card is the one whose position (or column) actually changed.
  const moved = cards.find((c, i) => before[i]?.id !== c.id)
  if (!moved) return                                         // nothing really moved

  const index = cards.indexOf(moved)
  moved.columnId = column.id

  try {
    await moveCard(moved.id, column.id, index)
  } catch (e) {
    restore(snapshot)
  }
}

// ---- Lifecycle ----

onMounted(loadBoards)

onBeforeUnmount(() => {
  clearTimeout(toastTimer)
  clearTimeout(reorderTimer)
})

// A pending reorder must not be lost when the board is swapped out.
watch(board, () => {
  if (reorderTimer) { clearTimeout(reorderTimer); pendingReorderSnapshot = null }
})
</script>

<template>
  <div class="relative flex h-full flex-col">
    <!-- Toolbar -->
    <div class="flex shrink-0 items-center gap-2 border-b px-4 py-3">
      <template v-if="board">
        <div class="relative">
          <Button variant="ghost" size="sm" class="gap-1.5 px-2" @click="boardMenuOpen = !boardMenuOpen">
            <LayoutGrid class="h-4 w-4 text-muted-foreground" />
            <ChevronDown class="h-3.5 w-3.5 text-muted-foreground" />
          </Button>

          <template v-if="boardMenuOpen">
            <div class="fixed inset-0 z-40" @click="boardMenuOpen = false" />
            <div class="absolute left-0 z-50 mt-1 w-56 rounded-lg border bg-popover p-1 shadow-md">
              <p class="px-2 py-1.5 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
                Boards
              </p>
              <button
                v-for="b in boards"
                :key="b.id"
                :class="cn(
                  'w-full truncate rounded-md px-2 py-1.5 text-left text-sm hover:bg-accent hover:text-accent-foreground',
                  b.id === board.id && 'bg-accent/60 font-medium',
                )"
                @click="openBoard(b.id)"
              >
                {{ b.name }}
              </button>

              <div class="my-1 h-px bg-border" />
              <button
                class="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm hover:bg-accent hover:text-accent-foreground"
                @click="onCreateBoard"
              >
                <Plus class="h-3.5 w-3.5" />
                New board
              </button>
              <button
                class="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm text-destructive hover:bg-destructive/10"
                @click="onDeleteBoard"
              >
                <Trash2 class="h-3.5 w-3.5" />
                Delete this board
              </button>
            </div>
          </template>
        </div>

        <input
          v-if="renamingBoard"
          v-model="boardNameDraft"
          class="min-w-0 flex-1 rounded-sm bg-transparent text-base font-semibold text-foreground outline-none ring-1 ring-ring"
          @keydown.enter.prevent="commitRenameBoard"
          @keydown.esc.prevent="renamingBoard = false"
          @blur="commitRenameBoard"
        >
        <h2
          v-else
          class="min-w-0 flex-1 cursor-text truncate text-base font-semibold text-foreground"
          title="Double-click to rename"
          @dblclick="beginRenameBoard"
        >
          {{ board.name }}
        </h2>

        <Button
          variant="ghost"
          size="icon"
          class="h-8 w-8 text-muted-foreground"
          title="Reload board"
          :disabled="busy"
          @click="openBoard(board.id)"
        >
          <RefreshCw class="h-4 w-4" />
        </Button>

        <Button size="sm" class="gap-1.5" @click="beginAddColumn">
          <Plus class="h-4 w-4" />
          Add column
        </Button>
      </template>

      <template v-else>
        <h2 class="flex-1 text-base font-semibold text-foreground">
          todorabbit
        </h2>
      </template>
    </div>

    <!-- Body -->
    <div class="min-h-0 flex-1 overflow-x-auto overflow-y-hidden p-4">
      <!-- Loading skeletons -->
      <div v-if="loading" class="flex h-full gap-4">
        <div v-for="n in 3" :key="n" class="flex h-full w-72 shrink-0 flex-col rounded-xl border bg-muted/40 p-3">
          <div class="mb-3 flex items-center gap-2">
            <div class="h-2.5 w-2.5 animate-pulse rounded-full bg-muted-foreground/20" />
            <div class="h-4 w-24 animate-pulse rounded bg-muted-foreground/20" />
          </div>
          <div class="flex flex-col gap-2">
            <div v-for="i in (n === 2 ? 2 : 3)" :key="i" class="h-16 animate-pulse rounded-lg bg-muted-foreground/10" />
          </div>
        </div>
      </div>

      <!-- Load error -->
      <div v-else-if="error" class="flex h-full flex-col items-center justify-center gap-3 text-center">
        <p class="text-sm text-destructive">
          {{ error }}
        </p>
        <Button variant="outline" size="sm" @click="loadBoards">
          Try again
        </Button>
      </div>

      <!-- No boards yet -->
      <div v-else-if="!board" class="flex h-full flex-col items-center justify-center gap-3 text-center">
        <LayoutGrid class="h-8 w-8 text-muted-foreground/40" />
        <div>
          <p class="text-sm font-medium text-foreground">
            No boards yet
          </p>
          <p class="mt-1 text-sm text-muted-foreground">
            Create one to start tracking work.
          </p>
        </div>
        <Button size="sm" class="gap-1.5" :disabled="busy" @click="onCreateBoard">
          <Plus class="h-4 w-4" />
          Create a board
        </Button>
      </div>

      <!-- The board -->
      <div v-else class="flex h-full items-start gap-4">
        <draggable
          v-model="columns"
          :item-key="'id'"
          group="todorabbit-columns"
          handle=".tr-column-handle"
          class="flex h-full items-stretch gap-4"
          ghost-class="tr-ghost"
          drag-class="tr-drag"
          :animation="160"
        >
          <template #item="{ element }">
            <KanbanColumn
              :column="element"
              :color-keys="COLOR_KEYS"
              :dot-class="dotClass"
              @rename="(title) => onRenameColumn(element, title)"
              @recolor="(key) => onRecolorColumn(element, key)"
              @delete="onDeleteColumn(element)"
              @add-card="(title) => onAddCard(element, title)"
              @save-card="(card, patch) => onSaveCard(element, card, patch)"
              @delete-card="(card) => onDeleteCard(element, card)"
              @cards-changed="(cards) => onCardsChanged(element, cards)"
            />
          </template>
        </draggable>

        <!-- Inline add-column -->
        <div v-if="addingColumn" class="w-72 shrink-0 rounded-xl border bg-muted/40 p-3">
          <input
            ref="newColumnEl"
            v-model="newColumnTitle"
            class="w-full bg-transparent text-sm font-semibold text-foreground outline-none placeholder:text-muted-foreground"
            placeholder="Column title…"
            @keydown.enter.prevent="commitAddColumn"
            @keydown.esc.prevent="cancelAddColumn"
          >
          <div class="mt-3 flex items-center justify-end gap-2">
            <Button variant="ghost" size="sm" class="h-7" @click="cancelAddColumn">
              Cancel
            </Button>
            <Button size="sm" class="h-7" @click="commitAddColumn">
              Add
            </Button>
          </div>
        </div>

        <button
          v-else
          class="flex w-72 shrink-0 items-center justify-center gap-1.5 rounded-xl border border-dashed py-3 text-sm text-muted-foreground transition hover:border-foreground/30 hover:text-foreground"
          @click="beginAddColumn"
        >
          <Plus class="h-4 w-4" />
          Add column
        </button>
      </div>
    </div>

    <!-- Toast -->
    <Transition
      enter-active-class="transition duration-150 ease-out"
      enter-from-class="translate-y-2 opacity-0"
      leave-active-class="transition duration-150 ease-in"
      leave-to-class="translate-y-2 opacity-0"
    >
      <div
        v-if="toast"
        class="absolute bottom-4 left-1/2 z-50 flex -translate-x-1/2 items-center gap-3 rounded-lg border bg-popover px-4 py-2.5 shadow-lg"
      >
        <span class="text-sm text-foreground">{{ toast }}</span>
        <button class="text-muted-foreground transition hover:text-foreground" @click="toast = ''">
          <X class="h-3.5 w-3.5" />
        </button>
      </div>
    </Transition>
  </div>
</template>

<style scoped>
/* Drag affordances for vuedraggable. Scoped styles need :deep() to reach the
   dragged element, which SortableJS re-parents outside this component. */
:deep(.tr-ghost) {
  opacity: 0.4;
}

:deep(.tr-drag) {
  cursor: grabbing;
}
</style>
