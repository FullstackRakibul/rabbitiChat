<script setup>
/**
 * The pipeline, made visible.
 *
 * Parse → Facts ‖ Reason → Merge → Review → Generate → Validate.
 *
 * Facts and Reason are drawn as one bracketed pair rather than two sequential
 * pills, because their being parallel is the architectural point: the facts do
 * not wait on the model, and the model never sees the facts. A UI that drew them
 * in a row would teach the user the opposite of how this works.
 */
import { computed } from 'vue'
import { Check, X, Loader2, Circle } from '@lucide/vue'
import { cn } from '@/lib/utils'

const props = defineProps({
  /** stage key → 'idle' | 'running' | 'done' | 'failed'. */
  stages: { type: Object, default: () => ({}) },
})

const STAGES = [
  { key: 'parse', label: 'Parse', hint: 'Source → IR' },
  { key: 'facts', label: 'Facts', hint: 'Deterministic', parallel: 'start' },
  { key: 'reason', label: 'Reason', hint: 'AI: pick + name', parallel: 'end' },
  { key: 'merge', label: 'Merge', hint: 'Join by id' },
  { key: 'review', label: 'Review', hint: 'Human gate' },
  { key: 'generate', label: 'Generate', hint: 'From facts' },
  { key: 'validate', label: 'Validate', hint: 'Independent' },
]

const items = computed(() =>
  STAGES.map((s) => ({ ...s, status: props.stages[s.key] || 'idle' })),
)

const iconFor = (status) => ({
  done: Check, failed: X, running: Loader2,
}[status] || Circle)
</script>

<template>
  <div class="flex flex-wrap items-stretch gap-1.5 overflow-x-auto pb-1">
    <template v-for="(item, idx) in items" :key="item.key">
      <!-- The parallel pair gets a bracket so the split is legible at a glance. -->
      <div v-if="item.parallel === 'start'"
        class="flex items-center self-center text-xs font-mono text-muted-foreground"
        title="These two run in parallel from the same IR">
        &#123;
      </div>

      <div :class="cn(
        'flex min-w-[88px] flex-col gap-0.5 rounded-md border px-2.5 py-1.5 transition-colors',
        item.status === 'done' && 'border-emerald-500/40 bg-emerald-500/10',
        item.status === 'running' && 'border-primary/50 bg-primary/10',
        item.status === 'failed' && 'border-destructive/50 bg-destructive/10',
        item.status === 'idle' && 'border-dashed opacity-55',
      )">
        <div class="flex items-center gap-1.5">
          <component :is="iconFor(item.status)" :class="cn(
            'h-3 w-3 shrink-0',
            item.status === 'done' && 'text-emerald-600',
            item.status === 'failed' && 'text-destructive',
            item.status === 'running' && 'animate-spin text-primary',
            item.status === 'idle' && 'text-muted-foreground',
          )" />
          <span class="text-xs font-medium leading-none">{{ item.label }}</span>
        </div>
        <span class="text-[10px] leading-tight text-muted-foreground">{{ item.hint }}</span>
      </div>

      <div v-if="item.parallel === 'end'"
        class="flex items-center self-center text-xs font-mono text-muted-foreground"
        title="These two run in parallel from the same IR">
        &#125;
      </div>

      <div v-if="idx < items.length - 1 && item.parallel !== 'start'"
        class="flex items-center self-center text-muted-foreground/40">
        <span class="text-xs">&rarr;</span>
      </div>
    </template>
  </div>
</template>
