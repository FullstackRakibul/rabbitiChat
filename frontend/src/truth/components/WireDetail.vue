<script setup>
// One wire row opened (docs/TRUTH_PROMPT_P5.md §4.2): every source with tier, time and link, the
// feed's own excerpt as a quote, an incident's update history, "Sources differ" when they do, the
// extracted CVE / tech / version chips and, once a person published an item on it, the link to it.
import { useI18n } from '../lib/i18n.js'
import { lang } from '../lib/lang.js'
import { formatDateTime } from '../lib/time.js'
import { stateKey, tierKey, WIRE_STATE_TONE } from '../lib/records.js'
import Lettermark from './Lettermark.vue'
import Link from './Link.vue'
import Stamp from './Stamp.vue'

defineProps({ cluster: { type: Object, required: true } })
const { t } = useI18n()
const cveUrl = (id) => `https://www.cve.org/CVERecord?id=${encodeURIComponent(id)}`
</script>

<template>
  <div class="space-y-4" data-testid="wire-detail" data-safety="wire-detail">
    <section v-if="cluster.conflicts.length" class="rounded-xl border-2 border-[var(--amber-fg)] p-3" data-testid="sources-differ">
      <h4 class="m-0 text-sm font-semibold">{{ t('wire.differ') }}</h4>
      <dl class="m-0 mt-2 space-y-2 text-sm">
        <div v-for="c in cluster.conflicts" :key="c.field + (c.line ?? '')" :data-field="c.field">
          <dt class="font-semibold">{{ c.field === 'fixed' ? t('wire.differFixed', { line: c.line }) : t('wire.differPatchTime') }}</dt>
          <dd v-for="v in c.values" :key="v.value" class="m-0" data-testid="differ-value">
            <code translate="no">{{ v.value }}</code> · <span translate="no">{{ v.sources.map((s) => s.name).join(', ') }}</span>
          </dd>
        </div>
      </dl>
    </section>

    <ol class="m-0 list-none space-y-4 p-0">
      <li v-for="(e, n) in cluster.entries" :key="n" class="border-l-2 border-line pl-3" data-testid="wire-source">
        <p class="m-0 flex flex-wrap items-center gap-2 text-sm">
          <Lettermark :tech="e.source" :size="24" />
          <span class="font-semibold" translate="no">{{ e.source.name }}</span>
          <span class="rounded border border-line px-1.5 text-xs" :data-tier="e.source.tier">{{ t(tierKey(e.source.tier)) }}</span>
          <span class="text-muted">{{ formatDateTime(t, lang, e.at) }}</span>
        </p>
        <p class="m-0 mt-1 font-semibold" lang="en" data-content>{{ e.title }}</p>
        <figure v-if="e.excerpt" class="m-0 mt-2">
          <figcaption class="text-xs text-muted">{{ t('wire.fromSource') }}</figcaption>
          <blockquote class="m-0 mt-1 border-l-4 border-line pl-3 text-sm italic" lang="en" data-content data-testid="wire-excerpt">{{ e.excerpt }}</blockquote>
        </figure>
        <p v-if="e.components?.length" class="m-0 mt-2 text-sm"><span class="text-muted">{{ t('wire.components') }}:</span> <span lang="en">{{ e.components.join(', ') }}</span></p>
        <div v-if="e.history[1]" class="mt-2">
          <h5 class="m-0 text-xs font-semibold text-muted">{{ t('wire.history') }}</h5>
          <ol class="m-0 mt-1 list-none space-y-1 p-0 text-sm" data-testid="wire-history">
            <li v-for="(h, k) in e.history" :key="k" class="flex flex-wrap items-center gap-2">
              <span class="text-muted">{{ formatDateTime(t, lang, h.at) }}</span>
              <span class="pill" :class="`tone-${WIRE_STATE_TONE[h.state] ?? 'grey'}`">{{ t(stateKey(h.state)) }}</span>
            </li>
          </ol>
        </div>
        <a :href="e.url" class="mt-1 inline-flex min-h-11 items-center text-sm" rel="noopener noreferrer" target="_blank">{{ t('wire.viewSource') }}</a>
      </li>
    </ol>

    <p v-if="cluster.cves.length || cluster.techs.length || cluster.versions.length" class="m-0 flex flex-wrap gap-2 text-sm" data-testid="wire-chips">
      <a v-for="c in cluster.cves" :key="c" :href="cveUrl(c)" class="rounded-full border border-line px-2 py-0.5 font-mono" rel="noopener noreferrer" target="_blank" translate="no">{{ c }}</a>
      <span v-for="x in cluster.techs" :key="x" class="rounded-full bg-surface-2 px-2 py-0.5" translate="no">{{ x }}</span>
      <span v-for="v in cluster.versions" :key="v" class="rounded-full bg-surface-2 px-2 py-0.5 font-mono" translate="no">{{ v }}</span>
    </p>

    <p v-if="cluster.verified" class="m-0 flex flex-wrap items-center gap-3" data-testid="wire-verified">
      <Stamp kind="verified" />
      <Link :to="`/truth/${cluster.verified.issueSlug}#item-${cluster.verified.itemId}`" class="inline-flex min-h-11 items-center font-semibold">{{ t('wire.readVerified') }}</Link>
    </p>
  </div>
</template>
