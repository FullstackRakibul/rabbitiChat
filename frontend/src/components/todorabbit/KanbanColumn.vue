<!--
  todorabbit — a creation of Mostisko, the AI.

  One column: coloured dot, double-click-to-rename title, card count, a "…" menu
  (recolor / delete) and the draggable card list. The column owns only its own
  presentation — every mutation is emitted up to the board, which is where the
  optimistic-update and rollback logic lives.
-->
<script setup>
import { computed, nextTick, ref } from 'vue'
import draggable from 'vuedraggable'
import { GripVertical, MoreHorizontal, Plus } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import KanbanCard from './KanbanCard.vue'

const props = defineProps({
  column: { type: Object, required: true },
  colorKeys: { type: Array, default: () => [] },
  dotClass: { type: Function, required: true },
})

const emit = defineEmits([
  'rename', 'recolor', 'delete', 'add-card', 'save-card', 'delete-card', 'cards-changed',
])

const renaming = ref(false)
const draftTitle = ref('')
const titleEl = ref(null)
const menuOpen = ref(false)
const adding = ref(false)
const newTitle = ref('')
const newTitleEl = ref(null)

// v-model target for the draggable list. Reads the column's cards; on drop it
// hands the whole reordered array to the board, which persists the move.
const cards = computed({
  get: () => props.column.cards,
  set: (value) => emit('cards-changed', value),
})

function beginRename() {
  draftTitle.value = props.column.title
  renaming.value = true
  nextTick(() => titleEl.value?.select())
}

function commitRename() {
  const title = draftTitle.value.trim()
  renaming.value = false
  if (title && title !== props.column.title) emit('rename', title)
}

function beginAdd() {
  adding.value = true
  newTitle.value = ''
  nextTick(() => newTitleEl.value?.focus())
}

function commitAdd() {
  const title = newTitle.value.trim()
  if (!title) return cancelAdd()
  emit('add-card', title)
  // Stay open so several cards can be added in a row.
  newTitle.value = ''
  nextTick(() => newTitleEl.value?.focus())
}

function cancelAdd() {
  adding.value = false
  newTitle.value = ''
}

function recolor(key) {
  menuOpen.value = false
  emit('recolor', key)
}
</script>

<template>
  <div class="flex h-full w-72 shrink-0 flex-col rounded-xl border bg-muted/40">
    <!-- Header -->
    <div class="flex items-center gap-2 px-3 py-2.5">
      <GripVertical
        class="tr-column-handle h-4 w-4 shrink-0 cursor-grab text-muted-foreground/40 transition hover:text-muted-foreground"
      />
      <span :class="cn('h-2.5 w-2.5 shrink-0 rounded-full', dotClass(column.colorKey))" />

      <input
        v-if="renaming"
        ref="titleEl"
        v-model="draftTitle"
        class="min-w-0 flex-1 rounded-sm bg-transparent text-sm font-semibold text-foreground outline-none ring-1 ring-ring"
        @keydown.enter.prevent="commitRename"
        @keydown.esc.prevent="renaming = false"
        @blur="commitRename"
      >
      <h3
        v-else
        class="min-w-0 flex-1 cursor-text truncate text-sm font-semibold text-foreground"
        :title="column.title"
        @dblclick="beginRename"
      >
        {{ column.title }}
      </h3>

      <span class="shrink-0 rounded-full bg-background px-1.5 py-0.5 text-[11px] font-medium text-muted-foreground">
        {{ column.cards.length }}
      </span>

      <!-- "…" menu -->
      <div class="relative shrink-0">
        <Button
          variant="ghost"
          size="icon"
          class="h-6 w-6 text-muted-foreground hover:text-foreground"
          title="Column options"
          @click="menuOpen = !menuOpen"
        >
          <MoreHorizontal class="h-4 w-4" />
        </Button>

        <template v-if="menuOpen">
          <!-- Click-away layer, so the menu closes on any outside click. -->
          <div class="fixed inset-0 z-40" @click="menuOpen = false" />
          <div class="absolute right-0 z-50 mt-1 w-44 rounded-lg border bg-popover p-1 shadow-md">
            <button
              class="w-full rounded-md px-2 py-1.5 text-left text-sm hover:bg-accent hover:text-accent-foreground"
              @click="menuOpen = false; beginRename()"
            >
              Rename
            </button>

            <div class="px-2 py-1.5">
              <p class="mb-1.5 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
                Colour
              </p>
              <div class="flex flex-wrap gap-1.5">
                <button
                  v-for="key in colorKeys"
                  :key="key"
                  :class="cn(
                    'h-4 w-4 rounded-full ring-offset-2 ring-offset-popover transition',
                    dotClass(key),
                    column.colorKey === key ? 'ring-2 ring-ring' : 'hover:ring-2 hover:ring-border',
                  )"
                  :title="key"
                  @click="recolor(key)"
                />
              </div>
            </div>

            <div class="my-1 h-px bg-border" />
            <button
              class="w-full rounded-md px-2 py-1.5 text-left text-sm text-destructive hover:bg-destructive/10"
              @click="menuOpen = false; emit('delete')"
            >
              Delete column
            </button>
          </div>
        </template>
      </div>
    </div>

    <!-- Cards -->
    <div class="min-h-0 flex-1 overflow-y-auto px-2 pb-2">
      <draggable
        v-model="cards"
        :item-key="'id'"
        group="todorabbit-cards"
        handle=".tr-card-handle"
        class="flex min-h-[2rem] flex-col gap-2"
        ghost-class="tr-ghost"
        drag-class="tr-drag"
        :animation="160"
      >
        <template #item="{ element }">
          <KanbanCard
            :card="element"
            :saving="element.__saving === true"
            @save="(patch) => emit('save-card', element, patch)"
            @delete="emit('delete-card', element)"
          />
        </template>
      </draggable>

      <p
        v-if="!column.cards.length && !adding"
        class="px-1 py-6 text-center text-xs text-muted-foreground"
      >
        No cards yet
      </p>

      <!-- Inline new-card composer -->
      <div v-if="adding" class="mt-2 rounded-lg border bg-card p-2 shadow-sm">
        <input
          ref="newTitleEl"
          v-model="newTitle"
          class="w-full bg-transparent text-sm text-foreground outline-none placeholder:text-muted-foreground"
          placeholder="Card title…"
          @keydown.enter.prevent="commitAdd"
          @keydown.esc.prevent="cancelAdd"
        >
        <div class="mt-2 flex items-center justify-end gap-2">
          <Button variant="ghost" size="sm" class="h-7" @click="cancelAdd">
            Cancel
          </Button>
          <Button size="sm" class="h-7" @click="commitAdd">
            Add
          </Button>
        </div>
      </div>
    </div>

    <!-- Footer -->
    <div class="px-2 pb-2">
      <Button
        variant="ghost"
        size="sm"
        class="w-full justify-start text-muted-foreground hover:text-foreground"
        @click="beginAdd"
      >
        <Plus class="h-4 w-4" />
        New card
      </Button>
    </div>
  </div>
</template>
