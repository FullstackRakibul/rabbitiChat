<script setup>
import { computed, ref, watch } from 'vue'
import { ChevronRight, Brain } from '@lucide/vue'
import { cn } from '@/lib/utils'

/**
 * A reasoning model's scratch work, shown above the answer it produced.
 *
 * Open while it streams so the user sees progress instead of a stalled cursor,
 * then collapsed the moment real content starts — the reasoning has served its
 * purpose by then, and leaving it expanded buries the answer.
 */
const props = defineProps({
  content: { type: String, default: '' },
  /** True while thinking deltas are still arriving. */
  streaming: { type: Boolean, default: false },
  /** True once the visible answer has begun. */
  answered: { type: Boolean, default: false },
})

// Once the user clicks, their choice wins over the automatic collapse.
const userChoice = ref(null)

const open = computed(() =>
  userChoice.value ?? (props.streaming && !props.answered))

watch(() => props.answered, (isAnswered) => {
  // A fresh answer resets the automatic behaviour only if the user never chose.
  if (isAnswered && userChoice.value === null) userChoice.value = false
})

const label = computed(() => {
  if (props.streaming && !props.answered) return 'Thinking…'
  return 'Thought process'
})
</script>

<template>
  <div class="mb-2 overflow-hidden rounded-lg border bg-muted/30">
    <button
      type="button"
      class="flex w-full items-center gap-1.5 px-2.5 py-1.5 text-left text-xs font-medium text-muted-foreground hover:bg-accent/50"
      :aria-expanded="open"
      @click="userChoice = !open"
    >
      <ChevronRight :class="cn('h-3.5 w-3.5 transition-transform', open && 'rotate-90')" />
      <Brain :class="cn('h-3.5 w-3.5', streaming && !answered && 'animate-pulse')" />
      {{ label }}
    </button>

    <div v-if="open" class="border-t px-3 py-2">
      <p class="whitespace-pre-wrap wrap-break-word text-xs leading-relaxed text-muted-foreground">{{ content }}</p>
    </div>
  </div>
</template>
