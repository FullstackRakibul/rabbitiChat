<script setup>
/**
 * মানব সম্পদ (HR Assistant) — the tab (docs/HR_MODULE_PROMPT.md §3.4).
 *
 * Three sections: প্রশ্ন (Ask), the HR chat with instant search and its own
 * conversation list; লাইব্রেরি (Library), HR's context; and নিয়মাবলি (Rulebook),
 * HR's own rule pages with their paper trail (docs/HR_RULEBOOK_PROMPT.md). Ask is
 * what HR opens the tab for, so it comes first.
 *
 * Bengali labels with English in smaller text, theme tokens only.
 */
import { nextTick, ref, watch } from 'vue'
import HrAsk from '@/components/hr/HrAsk.vue'
import HrLibrary from '@/components/hr/HrLibrary.vue'
import HrRulebook from '@/components/hr/HrRulebook.vue'

const SECTIONS = [
  { key: 'ask', bn: 'প্রশ্ন', en: 'Ask' },
  { key: 'library', bn: 'লাইব্রেরি', en: 'Library' },
  { key: 'rulebook', bn: 'নিয়মাবলি', en: 'Rulebook' },
]

const section = ref('ask')

/** Sections opened at least once; the Rulebook loads on its first visit and then stays. */
const visited = ref({ ask: true })
watch(section, (s) => { visited.value[s] = true })

/** A Rulebook citation in Ask opens the page, at the cited version, in the Rulebook. */
const rulebook = ref(null)
async function openRule(target) {
  section.value = 'rulebook'
  visited.value.rulebook = true
  await nextTick()
  await rulebook.value?.openAt(target)
}
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
      <HrAsk @open-rule="openRule" />
    </div>

    <div v-if="section === 'library'" class="min-h-0 flex-1 overflow-y-auto p-4 sm:p-6">
      <div class="mx-auto w-full max-w-5xl">
        <HrLibrary />
      </div>
    </div>

    <!-- Kept mounted once opened, so a page being edited survives a look at Ask or the Library. -->
    <div v-if="visited.rulebook" v-show="section === 'rulebook'" class="min-h-0 flex-1 overflow-y-auto">
      <HrRulebook ref="rulebook" />
    </div>
  </div>
</template>
