<script setup>
// RECORDS DEPT. · তারবার্তা / The Wire (docs/TRUTH_PROMPT_P5.md §4.2): what the collector gathered,
// newest first, by Bangladesh day. One row opens at a time (Enter / Space, aria-expanded); its detail
// is fetched once. Filters and search work on the loaded rows, in this browser. "Load more" follows
// the server's cursor. Every row is auto-collected and carries that stamp; the pills and words say
// the rest — the stamp is decoration (§3A.2).
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from '../lib/i18n.js'
import { lang } from '../lib/lang.js'
import { formatClock, formatDateTime, formatDay } from '../lib/time.js'
import { view } from '../lib/theme.js'
import {
  byDay, filterRows, loadCluster, loadWire, readFilters, readSeen, saveFilters, saveSeen,
  SEVERITIES, SEVERITY_TONE, stateKey, TIERS, tierKey, TYPES, WIRE_STATE_TONE,
} from '../lib/records.js'
import Lettermark from './Lettermark.vue'
import Mascot from './Mascot.vue'
import SectionTitle from './SectionTitle.vue'
import Stamp from './Stamp.vue'
import WireDetail from './WireDetail.vue'

const props = defineProps({ first: { type: Object, default: null } })   // the first page, loaded by the page
const { t } = useI18n()

const rows = ref(props.first?.rows ?? [])
const next = ref(props.first?.next ?? null)
const lastCollected = ref(props.first?.lastCollected ?? null)
const loadingMore = ref(false)
const filters = reactive(readFilters())
const query = ref('')
const openId = ref(null)
const details = reactive({})
const failed = ref(false)

// New since this browser last looked: those rows drop in once (not on a first visit, not in serious mode).
const seen = readSeen()
const fresh = ref(new Set(seen ? rows.value.filter((r) => !seen.has(r.clusterId)).map((r) => r.clusterId) : []))
onMounted(() => saveSeen(new Set([...(seen ?? []), ...rows.value.map((r) => r.clusterId)])))

watch(() => props.first, (f) => {
  if (!f) return
  const known = new Set(rows.value.map((r) => r.clusterId))
  for (const r of f.rows) if (!known.has(r.clusterId) && known.size) fresh.value.add(r.clusterId)
  const older = rows.value.filter((r) => !f.rows.some((x) => x.clusterId === r.clusterId) && f.rows.length >= 50)
  rows.value = [...f.rows, ...older]
  lastCollected.value = f.lastCollected
  if (!next.value) next.value = f.next
  saveSeen(new Set([...(readSeen() ?? []), ...f.rows.map((r) => r.clusterId)]))
})
watch(filters, (f) => saveFilters({ ...f }))

const shown = computed(() => filterRows(rows.value, filters, query.value))
const days = computed(() => byDay(shown.value))
const techs = computed(() => [...new Set(rows.value.flatMap((r) => r.techs))].sort())

async function toggle(id) {
  if (openId.value === id) { openId.value = null; return }
  openId.value = id
  if (!details[id]) {
    try { const r = await loadCluster(id); details[id] = r.notFound ? null : r.data } catch { details[id] = null }
  }
}

async function more() {
  loadingMore.value = true
  try {
    const r = await loadWire(next.value)
    rows.value = [...rows.value, ...r.data.rows.filter((x) => !rows.value.some((y) => y.clusterId === x.clusterId))]
    next.value = r.data.next
  } catch { failed.value = true } finally { loadingMore.value = false }
}

const pillTone = (r) => (r.severity ? SEVERITY_TONE[r.severity] : WIRE_STATE_TONE[r.state] ?? 'grey')
const pillText = (r) => (r.severity ? t(`severity.${r.severity}`) : r.state ? t(stateKey(r.state)) : '')
</script>

<template>
  <section id="wire" data-testid="wire" aria-labelledby="wire-title" class="teleprinter scroll-mt-20">
    <div class="perforation" data-effect="perforation" aria-hidden="true" />
    <div class="relative flex items-start gap-3">
      <div class="min-w-0 flex-1">
        <SectionTitle id="wire-title" plain="wire.title" nick="nick.wire" />
        <p class="mt-0 mb-3 text-sm text-muted">{{ t('wire.intro') }}</p>
      </div>
      <Mascot pose="reading" :size="56" class="flex-none" />
    </div>

    <div class="mb-3 space-y-2" data-testid="wire-filters">
      <div class="chips flex gap-2 overflow-x-auto" role="group" :aria-label="t('wire.type')">
        <button v-for="k in ['', ...TYPES]" :key="k || 'all'" type="button" class="min-h-11 min-w-11 flex-none rounded-full border border-line px-3 text-sm"
          :class="{ 'bg-fg text-bg': filters.type === k }" :aria-pressed="filters.type === k" :data-filter-type="k || 'all'" @click="filters.type = k">
          {{ k ? t(`wire.${k}`) : t('wire.all') }}
        </button>
        <button type="button" class="min-h-11 min-w-11 flex-none rounded-full border border-line px-3 text-sm" :class="{ 'bg-fg text-bg': filters.open }"
          :aria-pressed="filters.open" data-filter-open @click="filters.open = !filters.open">{{ t('wire.onlyOpen') }}</button>
      </div>
      <div class="grid grid-cols-1 gap-2 min-[400px]:grid-cols-3">
        <label class="text-xs text-muted">{{ t('wire.tech') }}
          <select v-model="filters.tech" class="mt-1 block min-h-11 w-full rounded-lg border border-line bg-surface px-2 text-base text-fg" data-filter="tech">
            <option value="">{{ t('wire.all') }}</option>
            <option v-for="x in techs" :key="x" :value="x" translate="no">{{ x }}</option>
          </select>
        </label>
        <label class="text-xs text-muted">{{ t('wire.severity') }}
          <select v-model="filters.severity" class="mt-1 block min-h-11 w-full rounded-lg border border-line bg-surface px-2 text-base text-fg" data-filter="severity">
            <option value="">{{ t('wire.all') }}</option>
            <option v-for="x in SEVERITIES" :key="x" :value="x">{{ t(`severity.${x}`) }}</option>
          </select>
        </label>
        <label class="text-xs text-muted">{{ t('wire.tier') }}
          <select v-model="filters.tier" class="mt-1 block min-h-11 w-full rounded-lg border border-line bg-surface px-2 text-base text-fg" data-filter="tier">
            <option value="">{{ t('wire.all') }}</option>
            <option v-for="x in TIERS" :key="x" :value="x">{{ t(tierKey(x)) }}</option>
          </select>
        </label>
      </div>
      <label class="block text-xs text-muted">{{ t('wire.search') }}
        <input v-model="query" type="search" :placeholder="t('wire.searchHint')" class="mt-1 block min-h-11 w-full rounded-lg border border-line bg-surface px-3 text-base text-fg" data-testid="wire-search">
      </label>
      <p class="m-0 text-xs text-muted">{{ t('wire.filterNote') }}</p>
    </div>

    <p v-if="!rows.length" class="text-muted" data-testid="wire-quiet">
      {{ t('wire.quiet') }} {{ lastCollected ? t('wire.lastCollected', { time: formatDateTime(t, lang, lastCollected) }) : t('wire.neverCollected') }}
    </p>
    <p v-else-if="!shown.length" class="text-muted">{{ t('wire.noMatch') }}</p>

    <div v-for="d in days" :key="d.day" class="mb-4">
      <h3 class="m-0 mb-2 text-sm font-semibold text-muted">{{ formatDay(t, lang, d.day) }}</h3>
      <ul class="m-0 list-none divide-y divide-[var(--border)] overflow-hidden rounded-2xl border border-line bg-surface p-0">
        <li v-for="r in d.rows" :key="r.clusterId" :class="{ 'capsule-in': fresh.has(r.clusterId) && !view.autoSerious }" data-testid="wire-row" :data-cluster="r.clusterId" :data-kind="r.kind">
          <button type="button" class="flex min-h-11 w-full flex-wrap items-start gap-x-3 gap-y-1 px-3 py-3 text-left text-fg"
            :aria-expanded="openId === r.clusterId" :aria-controls="`wire-${r.clusterId}`" @click="toggle(r.clusterId)">
            <span class="w-14 flex-none pt-0.5 text-xs text-muted">{{ formatClock(t, lang, r.at) }}</span>
            <Lettermark :tech="r.source" :size="28" />
            <span class="min-w-0 flex-1 basis-48">
              <span class="flex flex-wrap items-center gap-2 text-xs">
                <span class="font-semibold tracking-wide" data-testid="wire-type">{{ t(`wire.${r.kind}`) }}</span>
                <span class="rounded border border-line px-1.5" :data-tier="r.source.tier">{{ t(tierKey(r.source.tier)) }}</span>
                <span v-if="r.sources !== 1" data-testid="wire-count">{{ t('wire.sources', { count: r.sources }) }}</span>
              </span>
              <span class="mt-1 block font-semibold leading-snug" lang="en" data-content>{{ r.title }}</span>
              <span class="mt-1 flex flex-wrap items-center gap-2">
                <span v-if="pillText(r)" class="pill" :class="`tone-${pillTone(r)}`" data-safety="pill">{{ pillText(r) }}</span>
                <Stamp kind="auto" />
              </span>
            </span>
          </button>
          <div v-if="openId === r.clusterId" :id="`wire-${r.clusterId}`" class="border-t border-line px-3 py-3">
            <p v-if="details[r.clusterId] === undefined" class="m-0 text-sm text-muted">{{ t('wire.loading') }}</p>
            <p v-else-if="details[r.clusterId] === null" class="m-0 text-sm text-muted">{{ t('wire.loadError') }}</p>
            <WireDetail v-else :cluster="details[r.clusterId]" />
          </div>
        </li>
      </ul>
    </div>

    <button v-if="next" type="button" class="min-h-11 rounded-full border border-line px-4 font-semibold" :disabled="loadingMore" data-testid="wire-more" @click="more">
      {{ loadingMore ? t('wire.loading') : t('wire.loadMore') }}
    </button>
    <p v-if="failed" class="text-sm text-muted">{{ t('wire.loadError') }}</p>
  </section>
</template>
