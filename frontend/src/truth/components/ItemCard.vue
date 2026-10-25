<script setup>
// One item's detail card (§3.4): header and status, what happened, the patch
// time in both zones, advisories with their fixed versions, the timeline, the
// status changes with their times, the numbered references and the corrections.
// Staff-written text carries data-content; when English is missing it shows the
// Bangla, marked lang="bn", with the note. The theme stops at data-safety (§3A.2):
// the pill, the patch time, what to do, the advisories, the references and the
// corrections stay plain. Stamps sit beside them, never in them.
import { computed, ref } from 'vue'
import { useI18n } from '../lib/i18n.js'
import Lettermark from './Lettermark.vue'
import StatusPill from './StatusPill.vue'
import Msg from './Msg.js'
import Stamp from './Stamp.vue'
import Countdown from './Countdown.vue'
import Link from './Link.vue'
import { guidesFor, PHASE_KEY } from '../lib/guides.js'
import { entries as glossary, openTerm, splitTerms } from '../lib/glossary.js'
import { lang, withLang } from '../lib/lang.js'
import { anyFallback, pick } from '../lib/content.js'
import { digits, formatDate, formatDateTime, formatDay, formatWithUtc } from '../lib/time.js'

const props = defineProps({
  item: { type: Object, required: true },
  issue: { type: Object, required: true },
})
const { t } = useI18n()

const f = (field) => pick(props.item, field, lang.value)
const fallback = computed(() => anyFallback(props.item, ['headline', 'summary', 'impact', 'whatToDo'], lang.value)
  || props.item.timeline.some((e) => pick(e, 'text', lang.value).fallback)
  || props.item.advisories.some((a) => pick(a, 'title', lang.value).fallback))

/**
 * The summary with each [n] as a link to that reference, and the first use of each
 * glossary word as a button that explains it (§3A.7) — text, anchors and buttons, never HTML.
 */
const summaryParts = computed(() => {
  const text = f('summary').text ?? ''
  const seen = new Set()
  const out = []
  const words = (s) => out.push(...splitTerms(s, glossary.value, seen))
  let last = 0
  for (const m of text.matchAll(/\[(\d{1,2})\]/g)) {
    if (m.index > last) words(text.slice(last, m.index))
    out.push({ ref: Number(m[1]) })
    last = m.index + m[0].length
  }
  if (last < text.length) words(text.slice(last))
  return out
})

/** VERIFIED when every source is official or vendor; UNCONFIRMED for a claim. Decoration: the pill says the status. */
const stamp = computed(() => {
  const kinds = props.item.sources.map((s) => s.kind)
  if (props.item.status === 'unconfirmed' || kinds.includes('claim')) return 'unconfirmed'
  if (kinds.length && kinds.every((k) => k === 'official' || k === 'vendor')) return 'verified'
  return null
})

// The share page (/truth/s/…): Open Graph tags for a link preview, and readable without JavaScript.
const shareUrl = computed(() => location.origin + withLang(`/truth/s/issue/${props.issue.slug}`, lang.value))
const guides = computed(() => guidesFor(props.item.id))
const copied = ref(false)
async function share() {
  try {
    if (navigator.share) await navigator.share({ url: shareUrl.value })
    else { await navigator.clipboard.writeText(shareUrl.value); copied.value = true; setTimeout(() => { copied.value = false }, 2500) }
  } catch { /* the visitor closed the share sheet */ }
}

const statusLine = (e) => (e.from ? t('item.statusChange', { from: t(`status.${e.from}`), to: t(`status.${e.to}`) }) : t('item.statusSet', { to: t(`status.${e.to}`) }))
</script>

<template>
  <article :id="`item-${item.id}`" :data-item-id="item.id" class="scroll-mt-20 rounded-2xl border border-line bg-surface p-4 sm:p-6">
    <header class="flex items-start gap-3">
      <Lettermark :tech="item.tech" :size="44" />
      <div class="min-w-0 flex-1">
        <p class="m-0 text-sm text-muted"><span translate="no">{{ item.tech.name }}</span> · {{ t('severity.label') }}: {{ t(`severity.${item.severity}`) }}</p>
        <h3 data-testid="item-headline" data-content :lang="f('headline').lang" class="m-0 text-lg font-semibold leading-snug">{{ f('headline').text }}</h3>
      </div>
    </header>

    <div class="mt-3 flex flex-wrap items-center gap-x-3 gap-y-1">
      <StatusPill :status="item.status" />
      <span v-if="item.statusSince" class="text-sm text-muted">{{ t('item.since', { time: formatDateTime(t, lang, item.statusSince) }) }}</span>
      <Stamp v-if="stamp" :kind="stamp" class="ml-auto" />
    </div>
    <p v-if="item.patchScheduledAt" data-testid="patch-time" data-safety="patch" class="mt-3 mb-0 rounded-xl bg-[var(--amber-bg)] px-3 py-2 font-semibold text-[var(--amber-fg)]">
      {{ t('item.patchAt', { time: formatWithUtc(t, lang, item.patchScheduledAt) }) }}
      <span class="block text-sm font-normal">{{ t('time.zoneNote') }}</span>
      <Countdown v-if="item.status === 'patch_scheduled'" :at="item.patchScheduledAt" class="block text-sm" />
    </p>

    <p data-testid="item-summary" data-content :lang="f('summary').lang" class="mt-4 mb-0">
      <template v-for="(p, n) in summaryParts" :key="n">
        <button v-if="p.entry" type="button" class="gloss" data-inline-link :data-glossary="p.entry.id" aria-haspopup="dialog"
          @click="openTerm(p.entry, $event.currentTarget)">{{ p.text }}</button>
        <template v-else-if="p.text">{{ p.text }}</template>
        <a v-else :href="`#ref-${item.id}-${p.ref}`" data-inline-link>[{{ digits(p.ref, lang) }}]</a>
      </template>
    </p>
    <p v-if="fallback" data-testid="en-missing" class="mt-2 mb-0 text-sm italic text-muted">{{ t('item.enMissing') }}</p>

    <section v-if="f('impact').text" class="mt-4">
      <h4 class="m-0 text-sm font-semibold text-muted">{{ t('item.impact') }}</h4>
      <p class="m-0" data-content :lang="f('impact').lang">{{ f('impact').text }}</p>
    </section>
    <section v-if="f('whatToDo').text" data-safety="warning" class="mt-4 rounded-xl border border-line bg-surface-2 p-3">
      <h4 class="m-0 text-sm font-semibold">{{ t('item.whatToDo') }}</h4>
      <p class="m-0" data-content :lang="f('whatToDo').lang">{{ f('whatToDo').text }}</p>
    </section>

    <div v-if="guides.length" class="mt-4 flex flex-wrap gap-2" data-testid="guide-buttons">
      <Link v-for="g in guides" :key="g.slug" :to="`/truth/guide/${g.slug}`" :data-phase="g.phase"
        class="inline-flex min-h-11 items-center rounded-full border-2 border-line px-4 font-semibold text-fg no-underline">{{ t(PHASE_KEY[g.phase]) }}</Link>
    </div>

    <section v-if="item.advisories.length" data-safety="advisories" class="mt-5">
      <h4 class="m-0 mb-2 text-sm font-semibold text-muted">{{ t('item.advisories') }}</h4>
      <ul class="m-0 list-none space-y-3 p-0">
        <li v-for="(a, n) in item.advisories" :key="n" class="rounded-xl border border-line p-3">
          <div class="flex flex-wrap items-center gap-2">
            <a v-if="a.cveId" :href="`https://www.cve.org/CVERecord?id=${a.cveId}`" target="_blank" rel="noopener noreferrer"
              class="inline-flex min-h-11 items-center font-mono font-semibold" translate="no">{{ a.cveId }}</a>
            <span v-if="a.cvss !== null" class="pill tone-grey">{{ t('item.cvss', { score: digits(a.cvss.toFixed(1), lang) }) }}</span>
            <span v-if="a.exploitedInWild" class="pill tone-red">{{ t('item.exploited') }}</span>
            <span v-if="a.kevListed" class="pill tone-red">{{ t('item.kev') }}</span>
          </div>
          <p v-if="pick(a, 'title', lang).text" class="m-0 mt-1" data-content :lang="pick(a, 'title', lang).lang">{{ pick(a, 'title', lang).text }}</p>
          <p v-if="a.affectedRange" class="m-0 mt-2 text-sm"><span class="text-muted">{{ t('item.affected') }}:</span> <span class="font-mono" translate="no">{{ a.affectedRange }}</span></p>
          <div v-if="a.fixedVersions.length" class="mt-2">
            <p class="m-0 text-sm text-muted">{{ t('item.fixedIn') }}</p>
            <ul class="m-0 mt-1 flex list-none flex-wrap gap-2 p-0">
              <li v-for="v in a.fixedVersions" :key="v" class="rounded-lg bg-[var(--green-bg)] px-3 py-1 font-mono font-semibold text-[var(--green-fg)]" translate="no">{{ v }}</li>
            </ul>
          </div>
        </li>
      </ul>
    </section>

    <section v-if="item.timeline.length" class="mt-5">
      <h4 class="m-0 mb-2 text-sm font-semibold text-muted">{{ t('item.timeline') }}</h4>
      <ol class="m-0 list-none space-y-2 border-l-2 border-line p-0 pl-4">
        <li v-for="(e, n) in item.timeline" :key="n">
          <time :datetime="e.at" class="block text-sm text-muted">{{ formatDateTime(t, lang, e.at) }} · {{ t(`log.${e.level}`) }}</time>
          <span data-content :lang="pick(e, 'text', lang).lang">{{ pick(e, 'text', lang).text }}</span>
        </li>
      </ol>
    </section>

    <section v-if="item.statusEvents.length" class="mt-5">
      <h4 class="m-0 mb-2 text-sm font-semibold text-muted">{{ t('item.statusHistory') }}</h4>
      <ol class="m-0 list-none space-y-2 p-0">
        <li v-for="(e, n) in item.statusEvents" :key="n" data-testid="status-event">
          <time :datetime="e.at" class="block text-sm text-muted">{{ formatDateTime(t, lang, e.at) }}</time>
          <span class="font-semibold">{{ statusLine(e) }}</span>
          <span v-if="pick(e, 'text', lang).text" class="block" data-content :lang="pick(e, 'text', lang).lang">{{ pick(e, 'text', lang).text }}</span>
        </li>
      </ol>
    </section>

    <section class="mt-5" data-safety="references" :aria-labelledby="`refs-${item.id}`">
      <h4 :id="`refs-${item.id}`" class="m-0 mb-2 text-sm font-semibold text-muted">{{ t('refs.title') }}</h4>
      <ol class="m-0 list-none space-y-1 p-0">
        <li v-for="s in item.sources" :id="`ref-${item.id}-${s.n}`" :key="s.n" class="scroll-mt-20">
          <a :href="s.url" target="_blank" rel="noopener noreferrer" class="flex min-h-11 gap-2 rounded-lg py-1 no-underline hover:bg-surface-2">
            <span class="font-mono text-muted">[{{ digits(s.n, lang) }}]</span>
            <span class="min-w-0">
              <span class="block text-fg underline" translate="no">{{ s.title }}</span>
              <span class="block text-sm text-muted">
                <span translate="no">{{ s.author ? `${s.author}, ${s.publisher}` : s.publisher }}</span>
                <template v-if="s.publishedOn"> · {{ formatDay(t, lang, s.publishedOn) }}</template>
                · {{ t(`refs.${s.kind}`) }}
                <span class="sr-only">({{ t('refs.opens') }})</span>
              </span>
            </span>
          </a>
        </li>
      </ol>
    </section>

    <div v-if="item.corrections.length" class="mt-5 flex items-center gap-3">
      <span data-nickname class="nick">{{ t('nick.corrections') }}</span>
      <Stamp kind="corrected" />
    </div>
    <section v-if="item.corrections.length" class="mt-2 rounded-xl border border-[var(--amber-fg)] p-3" data-testid="corrections" data-safety="corrections">
      <h4 class="m-0 text-sm font-semibold">{{ t('item.corrections') }}</h4>
      <ul class="m-0 list-none space-y-2 p-0">
        <li v-for="(c, n) in item.corrections" :key="n">
          <time :datetime="c.at" class="block text-sm text-muted">{{ t('item.correctionAt', { time: formatDateTime(t, lang, c.at) }) }}</time>
          <span data-content :lang="pick(c, 'text', lang).lang">{{ pick(c, 'text', lang).text }}</span>
        </li>
      </ul>
    </section>

    <footer class="mt-5 flex flex-wrap items-center gap-x-3 gap-y-2 border-t border-line pt-3 text-sm text-muted">
      <Msg k="item.byline" :values="{ author: item.author, date: formatDate(t, lang, issue.firstPublishedAt) }" :keep="['author']" />
      <span v-if="item.tags.length" class="flex flex-wrap gap-1" data-content>
        <span class="sr-only">{{ t('item.tags') }}:</span>
        <span v-for="tag in item.tags" :key="tag" class="rounded-full bg-surface-2 px-2">#{{ tag }}</span>
      </span>
      <button type="button" data-testid="share" :data-url="shareUrl"
        class="ml-auto inline-flex min-h-11 items-center rounded-full border border-line px-4 font-semibold text-fg" @click="share">
        {{ copied ? t('item.copied') : t('item.share') }}
      </button>
    </footer>
  </article>
</template>
