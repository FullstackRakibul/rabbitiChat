<script setup>
import { computed, ref } from 'vue'
import { Braces, Paperclip, Plus } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import ChatMessages from '@/components/ChatMessages.vue'
import ChatComposer from '@/components/ChatComposer.vue'

const props = defineProps({
  buyerProfiles: { type: Array, default: () => [] },
  buyer: { type: String, default: '' },
  models: { type: Array, default: () => [] },
  model: { type: String, default: '' },
  doc: { type: Object, default: null },
  file: { type: Object, default: null },
  messages: { type: Array, default: () => [] },
  input: { type: String, default: '' },
  busy: { type: Boolean, default: false },
  uploading: { type: Boolean, default: false },
  extracting: { type: Boolean, default: false },
  error: { type: String, default: '' },
})

const emit = defineEmits([
  'update:buyer', 'update:model', 'update:input', 'file-picked',
  'upload', 'extract', 'send', 'new-chat', 'scroller',
])

const buyerValue = computed({
  get: () => props.buyer,
  set: (v) => emit('update:buyer', v),
})
const modelValue = computed({
  get: () => props.model,
  set: (v) => emit('update:model', v),
})

const fileInput = ref(null)
const dragging = ref(false)

function onDrop(e) {
  dragging.value = false
  const f = e.dataTransfer?.files?.[0]
  if (f) emit('file-picked', f)
}

function onPicked(e) {
  const f = e.target.files?.[0] ?? null
  if (f) emit('file-picked', f)
}

// Let the parent clear the native input after a successful ingest.
defineExpose({ reset: () => { if (fileInput.value) fileInput.value.value = '' } })
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <!-- Controls -->
    <div class="shrink-0 border-b px-4 py-3">
      <div class="flex flex-wrap items-center gap-2">
        <Select v-model="buyerValue">
          <SelectTrigger class="h-8 w-auto min-w-32 text-xs">
            <SelectValue placeholder="Buyer" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem v-for="b in buyerProfiles" :key="b" :value="b">
              {{ b }}
            </SelectItem>
          </SelectContent>
        </Select>

        <Select v-model="modelValue">
          <SelectTrigger class="h-8 w-auto min-w-36 text-xs">
            <SelectValue placeholder="Model" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem v-for="m in models" :key="m" :value="m">
              {{ m }}
            </SelectItem>
          </SelectContent>
        </Select>

        <Badge v-if="doc" variant="secondary" class="gap-1.5 font-normal" :title="doc.fileName">
          <Paperclip class="h-3 w-3" />
          <span class="max-w-40 truncate">{{ doc.fileName }}</span>
          <span class="text-muted-foreground">· {{ doc.chunks }} chunks</span>
        </Badge>

        <Button
          v-if="doc"
          variant="outline"
          size="sm"
          class="h-8"
          :disabled="extracting"
          @click="emit('extract')"
        >
          <Braces />
          {{ extracting ? 'Extracting…' : 'Extract JSON' }}
        </Button>

        <Button variant="outline" size="sm" class="ml-auto h-8" @click="emit('new-chat')">
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
        <!-- Empty-state hero: buyer → upload → ask -->
        <div v-if="!messages.length" class="py-12 text-center">
          <div class="mb-3 text-4xl">
            🧠
          </div>
          <h3 class="mb-1 text-lg font-semibold">
            Agentic PO Workspace
          </h3>
          <p class="mx-auto mb-6 max-w-md text-sm text-muted-foreground">
            Pick a buyer profile, drop in a purchase order, then ask anything about it.
          </p>

          <div class="flex flex-wrap items-center justify-center gap-2">
            <Badge variant="outline" class="gap-1.5 py-1 font-normal">
              <b>1</b> Buyer: {{ buyer }}
            </Badge>
            <Badge :variant="doc ? 'default' : 'outline'" class="gap-1.5 py-1 font-normal">
              <b>2</b> {{ doc ? doc.fileName : 'Upload the PO' }}
            </Badge>
            <Badge variant="outline" class="gap-1.5 py-1 font-normal">
              <b>3</b> Ask &amp; extract
            </Badge>
          </div>
        </div>

        <ChatMessages :messages="messages" :busy="busy" assistant-label="Workspace Agent" />
      </div>
    </div>

    <p v-if="error" class="shrink-0 px-4 pb-2 text-sm font-medium text-destructive">
      {{ error }}
    </p>

    <!-- Upload strip -->
    <div class="shrink-0 border-t px-4 py-3">
      <div
        :class="cn(
          'mx-auto flex w-full max-w-3xl flex-wrap items-center gap-2 rounded-lg border border-dashed p-2 transition-colors',
          dragging && 'border-primary bg-accent',
        )"
        @dragover.prevent="dragging = true"
        @dragleave.prevent="dragging = false"
        @drop.prevent="onDrop"
      >
        <input
          ref="fileInput"
          type="file"
          accept=".pdf,.xlsx,.txt,.csv"
          class="hidden"
          @change="onPicked"
        >
        <Button variant="ghost" size="sm" class="flex-1 justify-start" @click="fileInput?.click()">
          <Paperclip />
          <span class="truncate">
            {{ file ? file.name : 'Attach PO (PDF, XLSX, TXT, CSV) — or drop it here' }}
          </span>
        </Button>
        <Button v-if="file" size="sm" :disabled="uploading" @click="emit('upload')">
          {{ uploading ? 'Ingesting…' : `Ingest for ${buyer}` }}
        </Button>
      </div>
    </div>

    <ChatComposer
      :model-value="input"
      :busy="busy"
      :placeholder="doc ? `Ask about ${doc.fileName}…` : 'Ask the workspace agent…'"
      class="shrink-0"
      @update:model-value="emit('update:input', $event)"
      @send="emit('send')"
    />
  </div>
</template>
