<script setup>
import { computed } from 'vue'
import { FileText, Lightbulb, Plus, ChartColumn } from '@lucide/vue'
import ModelPicker from '@/components/ModelPicker.vue'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import ChatMessages from '@/components/ChatMessages.vue'
import ChatComposer from '@/components/ChatComposer.vue'

const props = defineProps({
  models: { type: Array, default: () => [] },
  model: { type: String, default: '' },
  /** Which model source answers: "local" (private) or "cloud" (metered). */
  source: { type: String, default: 'local' },
  contexts: { type: Array, default: () => [] },
  context: { type: String, default: '' },
  messages: { type: Array, default: () => [] },
  input: { type: String, default: '' },
  busy: { type: Boolean, default: false },
  error: { type: String, default: '' },
  activeSources: { type: Array, default: () => [] },
  topicsCount: { type: Number, default: 0 },
  ideasCount: { type: Number, default: 0 },
  docsCount: { type: Number, default: 0 },
  webSearch: { type: Boolean, default: false },
})

const emit = defineEmits([
  'update:model', 'update:source', 'update:context', 'update:input', 'update:webSearch',
  'send', 'new-chat', 'scroller',
])

const modelValue = computed({
  get: () => props.model,
  set: (v) => emit('update:model', v),
})
const contextValue = computed({
  get: () => props.context,
  set: (v) => emit('update:context', v),
})
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <!-- Controls + metrics -->
    <div class="shrink-0 border-b px-4 py-3">
      <div class="flex flex-wrap items-center gap-2">
        <!-- Grouped by source: local models are private, cloud ones are metered. -->
        <ModelPicker
          v-model="modelValue"
          :source="source"
          class="w-56"
          @update:source="emit('update:source', $event)"
        />

        <Select v-model="contextValue">
          <SelectTrigger class="h-8 w-auto min-w-32 text-xs">
            <SelectValue placeholder="Context" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem v-for="c in contexts" :key="c" :value="c">
              {{ c }}
            </SelectItem>
          </SelectContent>
        </Select>

        <Button variant="outline" size="sm" class="h-8" @click="emit('new-chat')">
          <Plus />
          New Chat
        </Button>

        <div class="ml-auto flex flex-wrap items-center gap-3 text-xs text-muted-foreground">
          <span class="flex items-center gap-1.5">
            <ChartColumn class="h-3.5 w-3.5" />
            Topics <b class="text-foreground">{{ topicsCount }}</b>
          </span>
          <span class="flex items-center gap-1.5">
            <Lightbulb class="h-3.5 w-3.5" />
            Ideas <b class="text-foreground">{{ ideasCount }}</b>
          </span>
          <span class="flex items-center gap-1.5">
            <FileText class="h-3.5 w-3.5" />
            Documents <b class="text-foreground">{{ docsCount }}</b>
          </span>
        </div>
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

        <ChatMessages :messages="messages" :busy="busy" assistant-label="RabbitiC" />

        <div
          v-if="messages.length && !busy && activeSources.length"
          class="mt-6 flex flex-wrap items-center gap-2 border-t pt-4"
        >
          <span class="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
            Sources
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
      :web-search="webSearch"
      class="shrink-0"
      @update:model-value="emit('update:input', $event)"
      @update:web-search="emit('update:webSearch', $event)"
      @send="emit('send')"
    />
  </div>
</template>
