<script setup>
import { computed, nextTick, ref, watch } from 'vue'
import {
  Brain, Globe, Mic, Paperclip, Plus, Send, Square,
} from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import SourcesPopover from '@/components/SourcesPopover.vue'

const props = defineProps({
  modelValue: { type: String, default: '' },
  busy: { type: Boolean, default: false },
  placeholder: { type: String, default: 'Ask anything…' },
  disabled: { type: Boolean, default: false },

  /** Show the per-message web search toggle. Off for surfaces that don't support it. */
  showWebSearch: { type: Boolean, default: false },
  webSearch: { type: Boolean, default: false },
  think: { type: Boolean, default: false },

  /**
   * The selected model, as the backend described it:
   * { name, source, contextLength, capabilities, metered }. Null while loading.
   */
  selectedModel: { type: Object, default: null },

  /** Connected sources, for the popover. Empty hides the affordance. */
  sources: { type: Array, default: () => [] },
  activeSourceIds: { type: Array, default: () => [] },
  showSources: { type: Boolean, default: false },

  /**
   * Tokens already used by this conversation, when the caller actually measured
   * it. Null means unknown — and unknown is rendered as nothing, never as a
   * guessed number or percentage.
   */
  usedTokens: { type: Number, default: null },
})

const emit = defineEmits([
  'update:modelValue', 'update:webSearch', 'update:think',
  'update:activeSourceIds', 'send', 'manage-sources', 'attach',
])

// ---- Capability gating ----
// Both toggles are driven by what the model actually declares. A model that
// can't reason gets no Think button, and one without tools gets no Search
// button — an always-visible control that silently does nothing is worse than
// no control at all.
const capabilities = computed(() =>
  (props.selectedModel?.capabilities ?? []).map((c) => String(c).toLowerCase()))

const canThink = computed(() =>
  capabilities.value.some((c) => c === 'thinking' || c === 'reasoning'))

const canUseTools = computed(() => capabilities.value.includes('tools'))

// Search still works without tool-calling — the backend falls back to prepending
// results — so the toggle shows whenever the surface supports search at all.
const showSearchToggle = computed(() => props.showWebSearch)

// ---- Context bar ----
// Every value here is measured or omitted. There is no placeholder percentage.
const contextLength = computed(() => props.selectedModel?.contextLength || null)

const contextLabel = computed(() => {
  if (props.usedTokens == null || !contextLength.value) return null
  return `${props.usedTokens.toLocaleString()}/${contextLength.value.toLocaleString()}`
})

// ---- Textarea ----
const textarea = ref(null)

/** Grow with the content, up to a cap, then scroll inside. */
function autoGrow() {
  const el = textarea.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = `${Math.min(el.scrollHeight, 200)}px`
}

watch(() => props.modelValue, () => nextTick(autoGrow))

function onInput(event) {
  emit('update:modelValue', event.target.value)
  autoGrow()
}

/** Enter sends; Shift+Enter is a newline. Never send an empty message. */
function onKeydown(event) {
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return
  event.preventDefault()
  if (!props.busy && !props.modelValue.trim()) return
  emit('send')
}

const canSend = computed(() => props.busy || props.modelValue.trim().length > 0)
</script>

<template>
  <div class="border-t bg-background/95 p-3 backdrop-blur supports-backdrop-filter:bg-background/60 sm:p-4">
    <div class="mx-auto w-full max-w-3xl">
      <!-- Context bar. Shows the model, and usage only when it was measured. -->
      <div class="mb-1.5 flex items-center gap-2 px-1 text-[11px] text-muted-foreground">
        <span v-if="selectedModel" class="truncate font-medium text-foreground/70">
          {{ selectedModel.name }}
        </span>
        <span v-if="selectedModel?.metered" class="rounded border px-1 py-px">metered</span>

        <span v-if="contextLabel" class="tabular-nums" title="Tokens used / context window">
          {{ contextLabel }}
        </span>
        <span
          v-else-if="contextLength"
          class="tabular-nums"
          title="Model context window"
        >{{ contextLength.toLocaleString() }} ctx</span>

        <span v-if="showSources && activeSourceIds.length" class="ml-auto">
          {{ activeSourceIds.length }} source{{ activeSourceIds.length === 1 ? '' : 's' }} active
        </span>
      </div>

      <div
        class="flex flex-col gap-2 rounded-2xl border bg-card p-2 shadow-sm focus-within:ring-1 focus-within:ring-ring"
      >
        <textarea
          ref="textarea"
          :value="modelValue"
          :placeholder="placeholder"
          :disabled="disabled"
          rows="1"
          class="max-h-[200px] w-full resize-none bg-transparent px-2.5 py-2 text-sm outline-none placeholder:text-muted-foreground disabled:cursor-not-allowed disabled:opacity-50"
          @input="onInput"
          @keydown="onKeydown"
        />

        <div class="flex items-center gap-1.5">
          <Button
            size="icon"
            variant="ghost"
            class="h-8 w-8 shrink-0 rounded-full"
            title="Attach a file"
            :disabled="disabled"
            @click="emit('attach')"
          >
            <Plus class="h-4 w-4" />
            <span class="sr-only">Attach</span>
          </Button>

          <SourcesPopover
            v-if="showSources"
            :sources="sources"
            :active-ids="activeSourceIds"
            :disabled="disabled"
            @update:active-ids="emit('update:activeSourceIds', $event)"
            @manage="emit('manage-sources')"
          />

          <!-- Only for models that declare a reasoning capability. -->
          <button
            v-if="canThink"
            type="button"
            :disabled="disabled"
            :aria-pressed="think"
            title="Show the model's reasoning"
            :class="cn(
              'inline-flex h-8 shrink-0 items-center gap-1.5 rounded-full border px-3 text-xs font-medium transition-colors',
              'hover:bg-accent hover:text-accent-foreground disabled:cursor-not-allowed disabled:opacity-50',
              think && 'border-primary/30 bg-primary/10 text-primary hover:bg-primary/15 hover:text-primary',
            )"
            @click="emit('update:think', !think)"
          >
            <Brain class="h-3.5 w-3.5" />
            Think
          </button>

          <!-- Only where the surface supports web grounding. -->
          <button
            v-if="showSearchToggle"
            type="button"
            :disabled="disabled"
            :aria-pressed="webSearch"
            :title="canUseTools
              ? 'Let the model search the web when it needs to'
              : 'Search the web and add the results to this message'"
            :class="cn(
              'inline-flex h-8 shrink-0 items-center gap-1.5 rounded-full border px-3 text-xs font-medium transition-colors',
              'hover:bg-accent hover:text-accent-foreground disabled:cursor-not-allowed disabled:opacity-50',
              webSearch && 'border-primary/30 bg-primary/10 text-primary hover:bg-primary/15 hover:text-primary',
            )"
            @click="emit('update:webSearch', !webSearch)"
          >
            <Globe class="h-3.5 w-3.5" />
            Search
          </button>

          <div class="ml-auto flex items-center gap-1.5">
            <Button
              size="icon"
              variant="ghost"
              class="h-8 w-8 shrink-0 rounded-full"
              title="Voice input is not wired up yet"
              disabled
            >
              <Mic class="h-4 w-4" />
              <span class="sr-only">Voice input</span>
            </Button>

            <Button
              size="icon"
              :variant="busy ? 'destructive' : 'default'"
              :title="busy ? 'Stop' : 'Send'"
              :disabled="disabled || !canSend"
              class="h-8 w-8 shrink-0 rounded-full"
              @click="emit('send')"
            >
              <Square v-if="busy" class="h-3.5 w-3.5 fill-current" />
              <Send v-else class="h-4 w-4" />
              <span class="sr-only">{{ busy ? 'Stop' : 'Send' }}</span>
            </Button>
          </div>
        </div>
      </div>

      <p class="mt-1.5 px-1 text-[11px] text-muted-foreground">
        Enter to send · Shift+Enter for a new line
      </p>
    </div>
  </div>
</template>
