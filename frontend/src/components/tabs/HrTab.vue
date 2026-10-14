<script setup>
/**
 * মানব সম্পদ (HR Assistant) — the tab (docs/HR_MODULE_PROMPT.md §3.4).
 *
 * Two sections: প্রশ্ন (Ask), the chat with its own conversation list, and
 * লাইব্রেরি (Library), HR's context. Phase 1 builds the Library; the Ask
 * section arrives with the chat in Phase 2 and says so until then.
 *
 * Bengali labels with English in smaller text, theme tokens only.
 */
import { ref } from 'vue'
import { MessageSquareText } from '@lucide/vue'
import HrLibrary from '@/components/hr/HrLibrary.vue'

const SECTIONS = [
  { key: 'ask', bn: 'প্রশ্ন', en: 'Ask' },
  { key: 'library', bn: 'লাইব্রেরি', en: 'Library' },
]

// The library first while the chat does not exist yet.
const section = ref('library')
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <nav class="shrink-0 overflow-x-auto border-b" aria-label="মানব সম্পদ sections">
      <ul class="flex min-w-max items-center gap-1 px-2 py-1.5">
        <li v-for="s in SECTIONS" :key="s.key">
          <button
            type="button"
            :aria-current="section === s.key ? 'page' : undefined"
            :class="[
              'rounded-md px-3 py-1.5 text-sm transition-colors',
              section === s.key
                ? 'bg-secondary font-medium text-secondary-foreground'
                : 'text-muted-foreground hover:bg-accent hover:text-foreground',
            ]"
            @click="section = s.key"
          >
            {{ s.bn }} <span class="text-xs opacity-70">{{ s.en }}</span>
          </button>
        </li>
      </ul>
    </nav>

    <div class="min-h-0 flex-1 overflow-y-auto p-4 sm:p-6">
      <div class="mx-auto w-full max-w-5xl">
        <HrLibrary v-if="section === 'library'" />

        <div v-else class="rounded-lg border border-dashed px-4 py-12 text-center">
          <MessageSquareText class="mx-auto h-6 w-6 text-muted-foreground" />
          <p class="mt-2 text-sm font-medium">প্রশ্ন শীঘ্রই আসছে</p>
          <p class="text-xs text-muted-foreground">
            The HR chat arrives in the next phase. The library it will answer from is ready.
          </p>
        </div>
      </div>
    </div>
  </div>
</template>
