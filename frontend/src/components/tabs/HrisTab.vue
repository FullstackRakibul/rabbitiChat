<script setup>
/**
 * এইচআর ওয়ার্কস্পেস · HRIS Workspace — the tab (docs/HRIS_WORKSPACE_PROMPT.md §1).
 *
 * A front door over মানব সম্পদ and ম্যাট্রিক্স, not a new engine. Three sections:
 * রিপোর্ট (Reports), the board of pinned Matrix figures; প্রশ্ন (Ask), one box for HR
 * rules and Matrix data (Phase W2); ডেটা ও নিয়ম (Data & Rules), the Matrix upload and
 * the Rulebook as they are (Phase W3), with the data status card on top. Someone
 * without section:hris.hub (an Executive) sees only that card there, read-only.
 *
 * The section is in the URL (?section=), so a link opens the same place. Bengali
 * labels with English in smaller text, theme tokens only.
 */
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import HrisReports from '@/components/hris/HrisReports.vue'
import HrisStatusCard from '@/components/hris/HrisStatusCard.vue'

const props = defineProps({
  sections: { type: Array, default: () => [] },
  actions: { type: Array, default: () => [] },
})
const route = useRoute()
const router = useRouter()

const SECTIONS = [
  { key: 'reports', bn: 'রিপোর্ট', en: 'Reports', perm: 'section:hris.reports' },
  { key: 'ask', bn: 'প্রশ্ন', en: 'Ask', perm: 'section:hris.ask' },
  // Everyone with the tab sees the hub's status card; the hub's screens need section:hris.hub.
  { key: 'hub', bn: 'ডেটা ও নিয়ম', en: 'Data & Rules', perm: null },
]
const visible = computed(() => SECTIONS.filter((s) => !s.perm || props.sections.includes(s.perm)))
const active = computed(() => visible.value.find((s) => s.key === route.query.section)?.key ?? visible.value[0]?.key)
const printing = computed(() => !!route.query.print)
const fullHub = computed(() => props.sections.includes('section:hris.hub'))

function open(key) {
  router.replace({ path: route.path, query: { ...route.query, section: key === 'reports' ? undefined : key } })
}
</script>

<template>
  <div class="flex min-h-0 min-w-0 flex-1 flex-col gap-3 overflow-y-auto overflow-x-hidden p-2 sm:p-4" data-hris>
    <nav v-if="!printing" class="flex flex-wrap gap-1 border-b pb-2" aria-label="HRIS Workspace sections">
      <button v-for="s in visible" :key="s.key" type="button" :data-hris-tab="s.key"
        class="rounded-md px-3 py-1.5 text-sm transition-colors"
        :class="active === s.key ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:bg-muted hover:text-foreground'"
        @click="open(s.key)">
        {{ s.bn }} <span class="text-xs opacity-75">{{ s.en }}</span>
      </button>
    </nav>

    <HrisReports v-if="active === 'reports'" :sections="sections" :actions="actions" />

    <section v-else-if="active === 'ask'" class="rounded-lg border bg-card p-4 text-sm text-muted-foreground" data-hris-ask>
      প্রশ্ন বিভাগ পরের ধাপে আসছে। <span class="text-xs">Ask arrives in Phase W2.</span>
    </section>

    <section v-else-if="active === 'hub'" class="flex flex-col gap-3" data-hris-hub>
      <HrisStatusCard />
      <p v-if="fullHub" class="text-sm text-muted-foreground">
        আপলোড ও নিয়মাবলি এখানে পরের ধাপে যুক্ত হবে। <span class="text-xs">The Matrix upload and the Rulebook are embedded here in Phase W3.</span>
      </p>
    </section>
  </div>
</template>
