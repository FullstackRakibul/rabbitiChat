<script setup>
// PO Studio — the merged Documents + Workspace tab.
//
// There used to be two tabs for one job: Documents to upload and manage POs,
// Workspace to chat with one and extract its JSON. They fed the same pipeline,
// so a merchandiser had to hold the relationship between them in their head.
// Here it is one guided surface: upload in Library, correct in Review, teach the
// buyer profile in Profiles, and watch the numbers in Accuracy.
//
// This file is the sub-navigation and nothing else. Each section is its own
// component under components/po-studio/, and the state lives in App.vue with
// the rest of the shell's state — a streaming answer has to survive navigating
// between sections.

import { computed } from 'vue'
import Library from '@/components/po-studio/Library.vue'
import Review from '@/components/po-studio/Review.vue'
import Profiles from '@/components/po-studio/Profiles.vue'
import Accuracy from '@/components/po-studio/Accuracy.vue'
import Datasets from '@/components/po-studio/Datasets.vue'
import Training from '@/components/po-studio/Training.vue'

const props = defineProps({
  /** Which section is showing — the hash of /workspace#<section>. */
  section: { type: String, default: 'library' },

  // ---- access ----
  // The tab shows for a user who has `workspace` OR `docs`; a section whose API
  // they cannot call is shown disabled with the access it needs, rather than
  // failing on the first click.
  canDocs: { type: Boolean, default: true },
  canWorkspace: { type: Boolean, default: true },

  // ---- Library ----
  documents: { type: Array, default: () => [] },
  profiles: { type: Array, default: () => [] },
  buyer: { type: String, default: '' },
  file: { type: Object, default: null },
  progress: { type: Object, default: () => ({ current: '', failedStep: '', error: '', done: false }) },
  busy: { type: Boolean, default: false },
  libraryError: { type: String, default: '' },

  // ---- Review ----
  reviewDoc: { type: Object, default: null },
  model: { type: String, default: '' },
  chatMessages: { type: Array, default: () => [] },
  chatInput: { type: String, default: '' },
  chatBusy: { type: Boolean, default: false },

  // ---- Profiles ----
  models: { type: Array, default: () => [] },
  defaultModel: { type: String, default: '' },
  profileSaving: { type: Boolean, default: false },
  profileError: { type: String, default: '' },
  /** The last assembled prompt, from /api/po-studio/prompt-preview. */
  promptPreview: { type: Object, default: null },
})

const emit = defineEmits([
  'update:section', 'update:buyer', 'update:chatInput',
  'file-picked', 'upload', 'retry', 'delete', 'review', 'chat',
  'send', 'confirmed', 'close-review',
  'profile-save', 'profile-create', 'profile-delete', 'profile-preview',
])

/**
 * The sections, in the order of the work: get the PO in, correct it, teach the
 * profile, then measure. `needs` names the access a section's API requires.
 */
const SECTIONS = [
  { key: 'library', label: 'Library', needs: 'docs' },
  { key: 'review', label: 'Review', needs: 'docs' },
  { key: 'profiles', label: 'Profiles', needs: 'docs' },
  { key: 'accuracy', label: 'Accuracy', needs: 'docs' },
  { key: 'datasets', label: 'Datasets', needs: 'docs' },
  { key: 'training', label: 'Training', needs: 'docs' },
]

const current = computed(() =>
  SECTIONS.some((s) => s.key === props.section) ? props.section : 'library')

</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <!-- Sub-navigation. Scrolls sideways at 360 px rather than wrapping into
         two rows that push the content down. -->
    <nav class="shrink-0 overflow-x-auto border-b" aria-label="PO Studio sections">
      <ul class="flex min-w-max items-center gap-1 px-2 py-1.5">
        <li v-for="s in SECTIONS" :key="s.key">
          <button
            type="button"
            :aria-current="current === s.key ? 'page' : undefined"
            :class="[
              'rounded-md px-3 py-1.5 text-sm transition-colors',
              current === s.key
                ? 'bg-secondary font-medium text-secondary-foreground'
                : 'text-muted-foreground hover:bg-accent hover:text-foreground',
            ]"
            @click="emit('update:section', s.key)"
          >
            {{ s.label }}
          </button>
        </li>
      </ul>
    </nav>

    <!-- Review owns its own scrolling (the PDF pane scrolls independently), so
         it is not wrapped in the padded scroller the other sections use. -->
    <div v-if="current === 'review'" class="min-h-0 flex-1">
      <Review
        :doc="reviewDoc"
        :model="model"
        :messages="chatMessages"
        :input="chatInput"
        :busy="chatBusy"
        :can-chat="canWorkspace"
        @update:input="emit('update:chatInput', $event)"
        @send="emit('send')"
        @confirmed="emit('confirmed', $event)"
        @close="emit('close-review')"
      />
    </div>

    <div v-else class="min-h-0 flex-1 overflow-y-auto p-4 sm:p-6">
      <div class="mx-auto w-full max-w-5xl">
        <Library
          v-if="current === 'library'"
          :documents="documents"
          :profiles="profiles"
          :buyer="buyer"
          :file="file"
          :progress="progress"
          :busy="busy"
          :error="libraryError"
          :can-use="canDocs"
          @update:buyer="emit('update:buyer', $event)"
          @file-picked="emit('file-picked', $event)"
          @upload="emit('upload')"
          @retry="emit('retry')"
          @delete="emit('delete', $event)"
          @review="emit('review', $event)"
          @chat="emit('chat', $event)"
        />

        <Profiles
          v-else-if="current === 'profiles'"
          :profiles="profiles"
          :models="models"
          :default-model="defaultModel"
          :saving="profileSaving"
          :error="profileError"
          :preview="promptPreview"
          :can-use="canDocs"
          @save="emit('profile-save', $event)"
          @create="emit('profile-create', $event)"
          @delete="emit('profile-delete', $event)"
          @preview="emit('profile-preview', $event)"
        />

        <Accuracy v-else-if="current === 'accuracy'" />
        <Datasets v-else-if="current === 'datasets'" />
        <Training v-else-if="current === 'training'" />
      </div>
    </div>
  </div>
</template>
