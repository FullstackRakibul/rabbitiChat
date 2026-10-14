<script setup>
/**
 * মানব সম্পদ (HR Assistant) — the tab (docs/HR_MODULE_PROMPT.md §3.4).
 *
 * Two sections: প্রশ্ন (Ask), the HR chat with instant search and its own
 * conversation list, and লাইব্রেরি (Library), HR's context. Ask is what HR opens
 * the tab for, so it comes first.
 *
 * Bengali labels with English in smaller text, theme tokens only.
 */
import { ref } from 'vue'
import HrAsk from '@/components/hr/HrAsk.vue'
import HrLibrary from '@/components/hr/HrLibrary.vue'

const SECTIONS = [
  { key: 'ask', bn: 'প্রশ্ন', en: 'Ask' },
  { key: 'library', bn: 'লাইব্রেরি', en: 'Library' },
]

const section = ref('ask')
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

    <!-- Ask keeps its own scrolling (the conversation, the list and the search each scroll). -->
    <div v-show="section === 'ask'" class="min-h-0 flex-1">
      <HrAsk />
    </div>

    <div v-if="section === 'library'" class="min-h-0 flex-1 overflow-y-auto p-4 sm:p-6">
      <div class="mx-auto w-full max-w-5xl">
        <HrLibrary />
      </div>
    </div>
  </div>
</template>
