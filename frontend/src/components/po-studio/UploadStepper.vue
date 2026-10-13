<script setup>
// The six steps of getting a PO ready for review, each showing its own state.
//
// Why a stepper rather than a spinner: the pipeline has genuinely different
// failure modes — a PDF whose text cannot be read, a buyer that matched nothing,
// a model that returned no JSON, a document too long for the window — and a
// single "Uploading…" tells a merchandiser nothing about which one happened or
// what to do next. A failure stops AT its step, says what went wrong in plain
// words, and offers a retry from there.

import { computed } from 'vue'
import { Check, CircleAlert, Loader2 } from '@lucide/vue'
import { Button } from '@/components/ui/button'

/** The steps, in order. The keys are the contract with the parent's progress. */
const STEPS = [
  { key: 'upload', label: 'Uploading' },
  { key: 'text', label: 'Reading text' },
  { key: 'detect', label: 'Detecting buyer' },
  { key: 'extract', label: 'Extracting' },
  { key: 'check', label: 'Checking' },
  { key: 'ready', label: 'Ready for review' },
]

const props = defineProps({
  /** The step currently running, or the one that failed. */
  current: { type: String, default: '' },
  /** Which step failed, if any — it stops there. */
  failedStep: { type: String, default: '' },
  /** Plain-words reason for the failure. */
  error: { type: String, default: '' },
  /** True once every step has passed. */
  done: { type: Boolean, default: false },
})

const emit = defineEmits(['retry'])

const currentIndex = computed(() => STEPS.findIndex((s) => s.key === props.current))
const failedIndex = computed(() => STEPS.findIndex((s) => s.key === props.failedStep))

/** 'done' | 'active' | 'failed' | 'pending' for one step. */
function stateOf(index) {
  if (props.done) return 'done'
  if (failedIndex.value >= 0) {
    if (index === failedIndex.value) return 'failed'
    return index < failedIndex.value ? 'done' : 'pending'
  }
  if (currentIndex.value < 0) return 'pending'
  if (index < currentIndex.value) return 'done'
  return index === currentIndex.value ? 'active' : 'pending'
}
</script>

<template>
  <div class="flex flex-col gap-3 rounded-lg border p-3 sm:p-4">
    <ol class="flex flex-col gap-2">
      <li
        v-for="(step, i) in STEPS"
        :key="step.key"
        class="flex items-start gap-2.5 text-sm"
      >
        <!-- Status mark. Shape as well as colour, so the state does not depend
             on colour vision or on the theme. -->
        <span class="mt-0.5 flex h-4 w-4 shrink-0 items-center justify-center">
          <Check v-if="stateOf(i) === 'done'" class="h-4 w-4 text-primary" />
          <Loader2 v-else-if="stateOf(i) === 'active'" class="h-4 w-4 animate-spin text-primary" />
          <CircleAlert v-else-if="stateOf(i) === 'failed'" class="h-4 w-4 text-destructive" />
          <span v-else class="h-1.5 w-1.5 rounded-full bg-muted-foreground/40" />
        </span>

        <div class="min-w-0 flex-1">
          <p
            :class="[
              'truncate',
              stateOf(i) === 'pending' && 'text-muted-foreground',
              stateOf(i) === 'failed' && 'font-medium text-destructive',
              stateOf(i) === 'active' && 'font-medium',
            ]"
          >
            {{ step.label }}
          </p>

          <!-- The failure's message sits under the step it belongs to, not in a
               banner somewhere else on the page. -->
          <p
            v-if="stateOf(i) === 'failed' && error"
            class="mt-0.5 text-xs text-destructive"
          >
            {{ error }}
          </p>
        </div>
      </li>
    </ol>

    <div v-if="failedStep" class="flex items-center gap-2 border-t pt-3">
      <Button size="sm" variant="outline" @click="emit('retry')">
        Retry
      </Button>
      <p class="text-xs text-muted-foreground">
        Retries from “{{ STEPS.find((s) => s.key === failedStep)?.label }}”.
      </p>
    </div>
  </div>
</template>
