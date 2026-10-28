<script setup>
// TELESCREEN · লাইভ অবস্থা (docs/TRUTH_PROMPT_P5.md §4.1): one chip per status source — its state in a
// pill with words, and when it was last checked. A tap opens the open incident and the last 5
// resolved ones. A source with no good check in 30 minutes says "data stale"; the state is never guessed.
import { computed, ref } from 'vue'
import { useI18n } from '../lib/i18n.js'
import { lang } from '../lib/lang.js'
import { serverNow } from '../lib/clock.js'
import { formatDateTime } from '../lib/time.js'
import { ago, STATE_TONE, stateKey, WIRE_STATE_TONE } from '../lib/records.js'
import Lettermark from './Lettermark.vue'
import SectionTitle from './SectionTitle.vue'

const props = defineProps({ sources: { type: Array, required: true } })
const { t } = useI18n()
const openId = ref(null)
const open = computed(() => props.sources.find((s) => s.id === openId.value))

const toggle = (id) => { openId.value = openId.value === id ? null : id }
function checked(s) {
  if (!s.lastChecked) return t('live.never')
  const now = serverNow()
  return now - Date.parse(s.lastChecked) < 60_000 ? t('live.checkedNow') : t('live.checked', { ago: ago(t, lang.value, s.lastChecked, now) })
}
</script>

<template>
  <section id="live" data-testid="live-status" aria-labelledby="live-title" class="scroll-mt-20">
    <SectionTitle id="live-title" plain="live.title" nick="nick.live" />
    <p class="mt-0 mb-3 text-sm text-muted">{{ t('live.intro') }}</p>
    <ul class="m-0 grid list-none grid-cols-1 gap-2 p-0 min-[400px]:grid-cols-2">
      <li v-for="s in sources" :key="s.id">
        <button type="button" class="flex min-h-11 w-full items-center gap-3 rounded-xl border border-line bg-surface px-3 py-2 text-left text-fg"
          :class="{ 'ring-2 ring-[var(--accent)]': openId === s.id }"
          :aria-expanded="openId === s.id" aria-controls="live-panel" data-testid="live-chip" :data-source="s.id" :data-state="s.state"
          @click="toggle(s.id)">
          <Lettermark :tech="s" :size="32" />
          <span class="min-w-0 flex-1">
            <span class="block font-semibold leading-snug" translate="no">{{ s.name }}</span>
            <span class="block text-xs text-muted">{{ checked(s) }}</span>
          </span>
          <span class="pill" :class="`tone-${STATE_TONE[s.state]}`" data-safety="pill">{{ t(`live.${s.state}`) }}</span>
        </button>
      </li>
    </ul>

    <div v-if="open" id="live-panel" data-testid="live-panel" :data-source="open.id" class="mt-3 rounded-2xl border border-line bg-surface p-4" data-safety="live-panel">
      <p v-if="open.state === 'stale'" class="m-0 text-sm">
        {{ open.lastChecked ? t('live.staleNote', { time: formatDateTime(t, lang, open.lastChecked) }) : t('live.staleNever') }}
      </p>
      <template v-else-if="open.open">
        <h3 class="m-0 text-sm font-semibold text-muted">{{ t('live.open') }}</h3>
        <p class="m-0 mt-1 font-semibold" lang="en" data-content>{{ open.open.title }}</p>
        <p class="m-0 mt-1 flex flex-wrap items-center gap-2 text-sm">
          <span class="pill" :class="`tone-${WIRE_STATE_TONE[open.open.state] ?? 'grey'}`">{{ t(stateKey(open.open.state)) }}</span>
          <span class="text-muted">{{ formatDateTime(t, lang, open.open.updatedAt) }} · {{ t('time.zoneNote') }}</span>
        </p>
        <p v-if="open.open.latest" class="m-0 mt-2 text-sm"><span class="text-muted">{{ t('live.latest') }}:</span> <span lang="en" data-content>{{ open.open.latest }}</span></p>
        <a :href="open.open.url" class="mt-2 inline-flex min-h-11 items-center" rel="noopener noreferrer" target="_blank">{{ t('live.viewSource') }}</a>
      </template>
      <h3 class="m-0 mt-3 text-sm font-semibold text-muted">{{ t('live.recent') }}</h3>
      <p v-if="!open.recent.length" class="m-0 mt-1 text-sm text-muted">{{ t('live.none') }}</p>
      <ul v-else class="m-0 mt-1 list-none space-y-2 p-0">
        <li v-for="r in open.recent" :key="r.url + r.at" class="text-sm">
          <a :href="r.url" rel="noopener noreferrer" target="_blank" lang="en" data-content>{{ r.title }}</a>
          <span class="block text-xs text-muted">{{ formatDateTime(t, lang, r.updatedAt) }}</span>
        </li>
      </ul>
      <a v-if="open.homepage" :href="open.homepage" class="mt-2 inline-flex min-h-11 items-center text-sm" rel="noopener noreferrer" target="_blank">{{ t('live.viewSource') }}</a>
    </div>
  </section>
</template>
