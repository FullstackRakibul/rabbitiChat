<script setup>
/**
 * সত্য মন্ত্রণালয় · Ministry of Truth — the staff side of the public bulletin (docs/TRUTH_PROMPT.md §8).
 *
 * Sections: Issues (each issue's items, the pre-publish checklist and the number
 * check), Technologies (the neutral lettermarks), and — for whoever may publish —
 * Site text (§5), Memes (§3A.5) and Glossary (§3A.7). Fix guides arrive in P2,
 * subscribers and mail in P3.
 *
 * Two permissions, kept minimal: the tab opens it and edits drafts;
 * action:truth.publish puts anything in front of the public. The server checks
 * every route again. The view is in the URL: ?issue=, ?section=techs|site-text|memes|glossary.
 */
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Newspaper } from '@lucide/vue'
import TruthIssues from '@/components/truth/TruthIssues.vue'
import TruthIssueEditor from '@/components/truth/TruthIssueEditor.vue'
import TruthTechs from '@/components/truth/TruthTechs.vue'
import TruthSiteText from '@/components/truth/TruthSiteText.vue'
import TruthMemes from '@/components/truth/TruthMemes.vue'
import TruthGlossary from '@/components/truth/TruthGlossary.vue'

const props = defineProps({
  /** The user's action keys, as /api/me/access gave them. */
  actions: { type: Array, default: () => [] },
})

const route = useRoute()
const router = useRouter()

// Having the tab is what lets someone edit drafts (§8); publishing is the one action.
const can = computed(() => ({ edit: true, publish: props.actions.includes('action:truth.publish') }))

const PUBLISH_ONLY = ['site-text', 'memes', 'glossary']
const section = computed(() => {
  const s = String(route.query.section ?? '')
  if (s === 'techs') return 'techs'
  if (PUBLISH_ONLY.includes(s) && can.value.publish) return s
  return 'issues'
})
const issueId = computed(() => Number(route.query.issue || 0) || null)

const SECTIONS = computed(() => [
  ['issues', 'সংখ্যা ও বিষয়', 'Issues & items', {}, true],
  ['techs', 'প্রযুক্তি', 'Technologies', { section: 'techs' }, true],
  ['site-text', 'সাইটের লেখা', 'Site text', { section: 'site-text' }, can.value.publish],
  ['memes', 'মিম', 'Memes', { section: 'memes' }, can.value.publish],
  ['glossary', 'শব্দকোষ', 'Glossary', { section: 'glossary' }, can.value.publish],
].filter((s) => s[4]))

const go = (query) => router.push({ path: route.path, query })
</script>

<template>
  <div class="flex h-full min-h-0 min-w-0 flex-col gap-4 overflow-y-auto p-4 sm:p-6" data-truth-tab>
    <div>
      <h2 class="flex items-center gap-2 text-lg font-semibold tracking-tight">
        <Newspaper class="size-5" aria-hidden="true" />
        সত্য মন্ত্রণালয় <span class="text-sm font-normal text-muted-foreground">Ministry of Truth</span>
        <a href="/truth" target="_blank" rel="noopener" class="ml-auto text-sm font-normal text-muted-foreground underline">/truth ↗</a>
      </h2>
      <nav class="mt-2 flex flex-wrap gap-1" aria-label="Ministry of Truth sections">
        <button v-for="[key, bn, en, query] in SECTIONS" :key="key" type="button" :data-truth-section="key"
          :aria-current="section === key ? 'page' : undefined"
          class="rounded-md px-3 py-1.5 text-sm font-medium"
          :class="section === key ? 'bg-secondary text-secondary-foreground' : 'text-muted-foreground hover:bg-accent hover:text-foreground'"
          @click="go(query)">{{ bn }} <span class="text-xs opacity-70">{{ en }}</span></button>
      </nav>
    </div>

    <TruthTechs v-if="section === 'techs'" :can-edit="can.edit" />
    <TruthSiteText v-else-if="section === 'site-text'" />
    <TruthMemes v-else-if="section === 'memes'" />
    <TruthGlossary v-else-if="section === 'glossary'" />
    <TruthIssueEditor v-else-if="issueId" :id="issueId" :key="issueId" :can="can" @close="go({})" />
    <TruthIssues v-else :can-edit="can.edit" @open="(id) => go({ issue: id })" />
  </div>
</template>
