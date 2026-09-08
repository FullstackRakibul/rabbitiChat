<script setup>
import { computed } from 'vue'
import { Plus } from '@lucide/vue'
import ModelPicker from '@/components/ModelPicker.vue'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import ChatMessages from '@/components/ChatMessages.vue'
import ChatComposer from '@/components/ChatComposer.vue'

const props = defineProps({
  models: { type: Array, default: () => [] },
  model: { type: String, default: '' },
  /** Which model source answers: "local" (private) or "cloud" (metered). */
  source: { type: String, default: 'local' },
  /** The selected model's own record, for capability gating and the context bar. */
  selectedModel: { type: Object, default: null },
  messages: { type: Array, default: () => [] },
  input: { type: String, default: '' },
  busy: { type: Boolean, default: false },
  error: { type: String, default: '' },

  /** Every context source of kind "source", plus which are on for this chat. */
  sources: { type: Array, default: () => [] },
  activeSourceIds: { type: Array, default: () => [] },

  webSearch: { type: Boolean, default: false },
  think: { type: Boolean, default: false },
})

const emit = defineEmits([
  'update:model', 'update:source', 'update:input', 'update:webSearch', 'update:think',
  'update:activeSourceIds', 'send', 'new-chat', 'manage-sources', 'scroller',
])

const modelValue = computed({
  get: () => props.model,
  set: (v) => emit('update:model', v),
})

/** The sources switched on for this conversation, for the footer chips. */
const activeSources = computed(() =>
  props.sources.filter((s) => props.activeSourceIds.includes(s.id)))
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <!-- Controls -->
    <div class="shrink-0 border-b px-4 py-3">
      <div class="flex flex-wrap items-center gap-2">
        <!-- Grouped by source: local models are private, cloud ones are metered. -->
        <ModelPicker
          v-model="modelValue"
          :source="source"
          class="w-56"
          @update:source="emit('update:source', $event)"
        />

        <Button variant="outline" size="sm" class="h-8" @click="emit('new-chat')">
          <Plus />
          New Chat
        </Button>
      </div>
    </div>

    <!-- Messages -->
    <div
      :ref="el => emit('scroller', el)"
      class="min-h-0 flex-1 overflow-y-auto px-4 py-6"
    >
      <div class="mx-auto w-full max-w-3xl">
        <p v-if="!messages.length" class="py-16 text-center text-sm text-muted-foreground">
          Pick a model and say hi 👋
        </p>

        <ChatMessages :messages="messages" :busy="busy" assistant-label="মস্তিস্ক" />

        <div
          v-if="messages.length && !busy && activeSources.length"
          class="mt-6 flex flex-wrap items-center gap-2 border-t pt-4"
        >
          <span class="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
            Context
          </span>
          <Badge v-for="s in activeSources" :key="s.id" variant="secondary">
            {{ s.name }}
          </Badge>
        </div>
      </div>
    </div>

    <p v-if="error" class="shrink-0 px-4 pb-2 text-sm font-medium text-destructive">
      {{ error }}
    </p>

    <ChatComposer
      :model-value="input"
      :busy="busy"
      show-web-search
      show-sources
      :web-search="webSearch"
      :think="think"
      :selected-model="selectedModel"
      :sources="sources"
      :active-source-ids="activeSourceIds"
      class="shrink-0"
      @update:model-value="emit('update:input', $event)"
      @update:web-search="emit('update:webSearch', $event)"
      @update:think="emit('update:think', $event)"
      @update:active-source-ids="emit('update:activeSourceIds', $event)"
      @manage-sources="emit('manage-sources')"
      @send="emit('send')"
    />
  </div>
</template>
