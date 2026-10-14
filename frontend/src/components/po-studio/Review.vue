<script setup>
// Review: the split view — the PDF page on the left, the JSON form on the right,
// failed checks pinned above the fields they name.
//
// The work itself is ExtractionReview.vue's, mounted embedded rather than as the
// full-screen dialog it used to be. That component already renders the pdfjs
// canvas, the live checks and the confirm flow, and it already writes
// ConfirmedJson and EditsJson; re-implementing any of that here would be a
// second review screen, which this build is explicitly not allowed to grow.
//
// What this file adds is the studio's framing: which PO is open, the "Ask about
// this PO" drawer, and the empty state when nothing is selected yet.

import { ref } from 'vue'
import { MessageSquare, X } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import ExtractionReview from '@/components/ExtractionReview.vue'
import ChatMessages from '@/components/ChatMessages.vue'
import ChatComposer from '@/components/ChatComposer.vue'

const props = defineProps({
  /** The PO under review: { id, fileName } — null shows the empty state. */
  doc: { type: Object, default: null },
  model: { type: String, default: '' },

  /** The drawer's conversation, owned by the parent so it survives navigation. */
  messages: { type: Array, default: () => [] },
  input: { type: String, default: '' },
  busy: { type: Boolean, default: false },

  /** False when the user lacks the `workspace` tab — the drawer explains itself. */
  canChat: { type: Boolean, default: true },
})

const emit = defineEmits(['update:input', 'send', 'confirmed', 'close'])

const drawer = ref(false)
</script>

<template>
  <div class="relative flex h-full min-h-0 flex-col">
    <template v-if="doc">
      <!-- The studio's own strip above the reused review. -->
      <div class="flex shrink-0 items-center gap-2 border-b px-3 py-2">
        <span class="min-w-0 flex-1 truncate text-sm text-muted-foreground" :title="doc.fileName">
          Reviewing <b class="text-foreground">{{ doc.fileName }}</b>
        </span>
        <Button variant="outline" size="sm" class="h-8" @click="drawer = true">
          <MessageSquare class="h-3.5 w-3.5" />
          Ask about this PO
        </Button>
      </div>

      <div class="min-h-0 flex-1">
        <ExtractionReview
          :key="doc.id"
          embedded
          :doc="doc"
          :model="model"
          @confirmed="emit('confirmed', $event)"
          @close="emit('close')"
        />
      </div>

      <!-- Chat drawer. The same workspace chat as before, grounded on this PO. -->
      <div
        v-if="drawer"
        class="absolute inset-0 z-30 flex justify-end bg-background/60 backdrop-blur-sm"
        @click.self="drawer = false"
      >
        <div class="flex h-full w-full max-w-md flex-col border-l bg-background shadow-xl">
          <div class="flex shrink-0 items-center justify-between border-b px-4 py-3">
            <p class="min-w-0 truncate text-sm font-semibold">
              Ask about {{ doc.fileName }}
            </p>
            <Button
              size="icon"
              variant="ghost"
              class="h-7 w-7"
              aria-label="Close chat"
              @click="drawer = false"
            >
              <X class="h-4 w-4" />
            </Button>
          </div>

          <p
            v-if="!canChat"
            class="m-4 rounded-lg border border-dashed p-3 text-sm text-muted-foreground"
          >
            Asking about a PO needs the <b>Workspace</b> access.
            Ask an administrator to grant it.
          </p>

          <template v-else>
            <div class="min-h-0 flex-1 overflow-y-auto p-4">
              <ChatMessages :messages="messages" :busy="busy" assistant-label="Workspace Studio" />
            </div>
            <ChatComposer
              :model-value="input"
              :busy="busy"
              :placeholder="`Ask about ${doc.fileName}…`"
              class="shrink-0"
              @update:model-value="emit('update:input', $event)"
              @send="emit('send')"
            />
          </template>
        </div>
      </div>
    </template>

    <p v-else class="px-4 py-12 text-center text-sm text-muted-foreground">
      Pick a purchase order in <b>Library</b> and press Review.
    </p>
  </div>
</template>
