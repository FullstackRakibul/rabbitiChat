<!--
  todorabbit — a creation of Mostisko, the AI.

  One card. Double-click anywhere on it to edit; Enter saves, Shift+Enter adds a
  line, Escape cancels. The body preserves blank lines verbatim, so a card can
  hold a real multi-paragraph note rather than a single line of text.
-->
<script setup>
import { computed, nextTick, ref, watch } from 'vue'
import { GripVertical, Trash2 } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'

const props = defineProps({
  card: { type: Object, required: true },
  saving: { type: Boolean, default: false },
})

const emit = defineEmits(['save', 'delete'])

const editing = ref(false)
const draftTitle = ref('')
const draftContent = ref('')
const titleEl = ref(null)

// Priority drives a small badge; null means the card simply doesn't show one.
const priorityClass = {
  low: 'bg-slate-500/10 text-slate-600 dark:text-slate-300 ring-slate-500/20',
  medium: 'bg-amber-500/10 text-amber-700 dark:text-amber-400 ring-amber-500/20',
  high: 'bg-red-500/10 text-red-600 dark:text-red-400 ring-red-500/20',
}

const priority = computed(() => props.card.priority || null)

// Cycles none → low → medium → high → none, so priority is one click to set.
const nextPriority = computed(() => {
  const order = [null, 'low', 'medium', 'high']
  return order[(order.indexOf(priority.value) + 1) % order.length]
})

function beginEdit() {
  if (editing.value) return
  draftTitle.value = props.card.title
  draftContent.value = props.card.content || ''
  editing.value = true
  nextTick(() => titleEl.value?.focus())
}

function cancel() {
  editing.value = false
}

function save() {
  const title = draftTitle.value.trim()
  if (!title) return cancel()   // a card with no title is a cancelled edit

  editing.value = false
  if (title === props.card.title && draftContent.value === (props.card.content || '')) return
  emit('save', { title, content: draftContent.value })
}

function cyclePriority() {
  // '' clears the badge server-side; null would read as "leave unchanged".
  emit('save', { priority: nextPriority.value ?? '' })
}

// A card replaced under us (reload, rollback) shouldn't strand an open editor.
watch(() => props.card.id, cancel)
</script>

<template>
  <div
    :class="cn(
      'group/card relative rounded-lg border bg-card p-3 shadow-sm transition-colors',
      editing ? 'ring-1 ring-ring' : 'hover:border-foreground/20',
      saving && 'opacity-60',
    )"
    @dblclick.stop="beginEdit"
  >
    <!-- Read mode -->
    <template v-if="!editing">
      <div class="flex items-start gap-2">
        <GripVertical
          class="tr-card-handle mt-0.5 h-4 w-4 shrink-0 cursor-grab text-muted-foreground/40 opacity-0 transition group-hover/card:opacity-100"
        />

        <div class="min-w-0 flex-1">
          <p class="text-sm font-medium leading-snug text-foreground">
            {{ card.title }}
          </p>

          <!-- whitespace-pre-wrap is what preserves the blank lines. -->
          <p
            v-if="card.content"
            class="mt-1.5 whitespace-pre-wrap break-words text-xs leading-relaxed text-muted-foreground"
          >{{ card.content }}</p>

          <div v-if="priority" class="mt-2">
            <button
              type="button"
              :class="cn(
                'rounded-full px-2 py-0.5 text-[10px] font-medium uppercase tracking-wide ring-1 ring-inset transition',
                priorityClass[priority],
              )"
              title="Change priority"
              @click.stop="cyclePriority"
            >
              {{ priority }}
            </button>
          </div>
        </div>

        <div class="flex shrink-0 items-center gap-0.5 opacity-0 transition group-hover/card:opacity-100 focus-within:opacity-100">
          <Button
            v-if="!priority"
            variant="ghost"
            size="icon"
            class="h-6 w-6 text-muted-foreground hover:text-foreground"
            title="Set priority"
            @click.stop="cyclePriority"
          >
            <span class="text-[10px] font-semibold">!</span>
          </Button>
          <Button
            variant="ghost"
            size="icon"
            class="h-6 w-6 text-muted-foreground hover:text-destructive"
            title="Delete card"
            @click.stop="emit('delete')"
          >
            <Trash2 class="h-3.5 w-3.5" />
          </Button>
        </div>
      </div>
    </template>

    <!-- Edit mode -->
    <template v-else>
      <input
        ref="titleEl"
        v-model="draftTitle"
        class="w-full rounded-sm border-0 bg-transparent p-0 text-sm font-medium text-foreground outline-none placeholder:text-muted-foreground"
        placeholder="Card title"
        @keydown.enter.prevent="save"
        @keydown.esc.prevent="cancel"
        @click.stop
      >
      <textarea
        v-model="draftContent"
        rows="4"
        class="mt-2 w-full resize-y rounded-sm border-0 bg-transparent p-0 text-xs leading-relaxed text-muted-foreground outline-none placeholder:text-muted-foreground/60"
        placeholder="Add a note… Enter saves, Shift+Enter for a new line."
        @keydown.enter.exact.prevent="save"
        @keydown.esc.prevent="cancel"
        @click.stop
      />
      <div class="mt-2 flex items-center justify-end gap-2">
        <Button variant="ghost" size="sm" class="h-7" @click.stop="cancel">
          Cancel
        </Button>
        <Button size="sm" class="h-7" @click.stop="save">
          Save
        </Button>
      </div>
    </template>
  </div>
</template>
