<script setup>
// A fix guide (§6.1): /truth/guide/:slug. Title, phase (a temporary fix says so),
// byline and "based on"; why, and which versions; the backup box; the platform and
// version selectors; the steps for that choice, rendered here by the same renderer
// the server checked every variant with; the after-patch guide; the references.
// The choice is in the URL (?platform=&php=…), so a shared link opens on it.
import { computed, ref, watch } from 'vue'
import { useI18n } from '../lib/i18n.js'
import Link from '../components/Link.vue'
import Lettermark from '../components/Lettermark.vue'
import Mascot from '../components/Mascot.vue'
import GuideStepView from '../components/GuideStepView.vue'
import LoadError from '../components/LoadError.vue'
import OfflineBar from '../components/OfflineBar.vue'
import Room101 from './Room101.vue'
import { load } from '../lib/api.js'
import { lang, withLang } from '../lib/lang.js'
import { pick } from '../lib/content.js'
import { digits, formatDate, formatDateTime, formatDay } from '../lib/time.js'
import { renderTemplate, stepVisible } from '../lib/template.js'
import { guideIndex, loadGuides } from '../lib/guides.js'
import { view } from '../lib/theme.js'

const props = defineProps({ slug: { type: String, required: true } })
const emit = defineEmits(['title'])
const { t } = useI18n()

const state = ref('loading')
const snap = ref(null)
const savedAt = ref(null)
const values = ref({})

async function fetchGuide() {
  state.value = 'loading'
  try {
    const r = await load(`/guides/${props.slug}`)
    if (r.notFound) { state.value = 'notFound'; return }
    snap.value = r.data
    savedAt.value = r.offline ? r.savedAt : null
    // The URL's choice when it names options this guide has; the defaults otherwise.
    const q = new URLSearchParams(location.search)
    const plats = r.data.platforms.map((p) => p.key)
    const v = { platform: plats.includes(q.get('platform')) ? q.get('platform') : plats[0] }
    for (const [name, def] of Object.entries(r.data.variables ?? {}))
      v[name] = def.options.includes(q.get(name)) ? q.get(name) : def.default ?? def.options[0]
    values.value = v
    state.value = 'ready'
  } catch { state.value = 'error' }
}
watch(() => props.slug, fetchGuide, { immediate: true })
loadGuides()
view.pose = 'reading'

watch(values, (v) => {
  const u = new URL(location.href)
  for (const [k, val] of Object.entries(v)) u.searchParams.set(k, val)
  history.replaceState(history.state, '', u.pathname + u.search + u.hash)
}, { deep: true })

const guide = computed(() => snap.value?.guide)
const title = computed(() => (guide.value ? pick(guide.value, 'title', lang.value) : null))
watch([title, state], () => emit('title', state.value === 'ready' ? title.value?.text : null), { immediate: true })
const platform = computed(() => snap.value?.platforms.find((p) => p.key === values.value.platform))
const variantKey = computed(() => Object.entries(values.value).map(([k, v]) => `${k}=${v}`).join(';'))

/** The steps for this choice: platform and condition, then the templates rendered as text. */
const steps = computed(() => (snap.value?.steps ?? []).filter((s) => stepVisible(s, values.value)).map((s) => ({
  ...s,
  code: s.code ? renderTemplate(s.code, values.value).text : null,
  expected: s.expected ? renderTemplate(s.expected, values.value).text : null,
  language: s.language ?? platform.value?.shell,
})))
const warningsFor = (order) => (snap.value?.warnings ?? []).filter((w) => w.variant === variantKey.value && w.step === order)
const basedOn = computed(() => (guide.value?.basedOn ? snap.value.sources[guide.value.basedOn - 1] : null))
const afterPatch = computed(() => (guide.value?.phase === 'before_patch' && guide.value.itemId
  ? guideIndex.value.find((g) => g.itemId === guide.value.itemId && g.phase === 'after_patch') : null))
const shareUrl = computed(() => location.origin + withLang(`/truth/s/guide/${props.slug}`, lang.value))
const varLabel = (name, def) => (lang.value === 'en' ? def.labelEn : def.labelBn) || name
</script>

<template>
  <div>
    <OfflineBar v-if="savedAt" :saved-at="savedAt" />
    <p v-if="state === 'loading'" class="mx-auto max-w-3xl px-4 py-12 text-center text-muted">{{ t('loading') }}</p>
    <LoadError v-else-if="state === 'error'" />
    <Room101 v-else-if="state === 'notFound'" />
    <article v-else class="mx-auto max-w-3xl px-4 py-6" data-testid="guide-page" :data-slug="slug">
      <div class="flex items-start gap-3">
        <div class="min-w-0 flex-1">
          <p class="m-0"><span class="pill" :class="guide.phase === 'before_patch' ? 'tone-amber' : 'tone-green'" data-safety="phase" data-testid="phase">{{ t(`phase.${guide.phase}`) }}</span></p>
          <h1 class="m-0 mt-2 text-2xl font-semibold leading-snug" data-content :lang="title.lang">{{ title.text }}</h1>
          <p class="m-0 mt-1 flex items-center gap-2 text-sm text-muted">
            <Lettermark :tech="snap.tech" :size="24" /><span translate="no">{{ snap.tech.name }}</span>
            · <span translate="no">{{ guide.author }}</span> · {{ formatDate(t, lang, guide.publishedAt) }}
          </p>
          <p v-if="basedOn" class="m-0 mt-1 text-sm" data-testid="based-on">
            <a :href="basedOn.url" target="_blank" rel="noopener noreferrer" data-inline-link>{{ t('guide.basedOn', { who: basedOn.author ? `${basedOn.author}, ${basedOn.publisher}` : basedOn.publisher }) }}</a>
          </p>
          <p v-if="guide.itemId && guide.issueSlug" class="m-0 mt-1 text-sm">
            <Link :to="`/truth/${guide.issueSlug}#item-${guide.itemId}`" data-inline-link>{{ t('nav.details') }}</Link>
          </p>
        </div>
        <Mascot pose="reading" :size="72" />
      </div>

      <p v-if="pick(guide, 'intro', lang).text" class="m-0 mt-4" data-content :lang="pick(guide, 'intro', lang).lang">{{ pick(guide, 'intro', lang).text }}</p>
      <p v-if="guide.phase === 'before_patch'" class="m-0 mt-3 rounded-xl bg-[var(--amber-bg)] p-3 font-semibold text-[var(--amber-fg)]" data-safety="temporary" data-testid="temporary">{{ t('guide.temporary') }}</p>
      <section v-if="guide.backupFirst" class="mt-3 rounded-xl border-2 border-[var(--amber-fg)] p-3" data-safety="backup">
        <h2 class="m-0 text-base font-semibold">{{ t('guide.backupTitle') }}</h2>
        <p class="m-0">{{ t('guide.backup') }}</p>
      </section>

      <form class="mt-5 grid gap-3 rounded-2xl border border-line bg-surface p-4 sm:grid-cols-2" data-testid="selectors" @submit.prevent>
        <label class="flex flex-col text-sm text-muted">{{ t('guide.platform') }}
          <select v-model="values.platform" data-testid="select-platform" class="mt-1 min-h-11 rounded-lg border border-line bg-surface px-2 text-base text-fg">
            <option v-for="p in snap.platforms" :key="p.key" :value="p.key">{{ lang === 'en' ? p.nameEn : p.nameBn }}</option>
          </select>
        </label>
        <label v-for="(def, name) in snap.variables" :key="name" class="flex flex-col text-sm text-muted">
          <span translate="no">{{ varLabel(name, def) }}</span>
          <select v-model="values[name]" :data-testid="`select-${name}`" class="mt-1 min-h-11 rounded-lg border border-line bg-surface px-2 text-base text-fg">
            <option v-for="o in def.options" :key="o" :value="o" translate="no">{{ o }}</option>
          </select>
        </label>
        <p v-if="platform && (lang === 'en' ? platform.hintEn : platform.hintBn)" class="m-0 text-sm text-muted sm:col-span-2">
          {{ t('guide.howToKnow', { hint: lang === 'en' ? platform.hintEn : platform.hintBn }) }}
        </p>
      </form>

      <h2 class="mt-6 mb-3 text-lg font-semibold">{{ t('guide.steps') }}</h2>
      <p v-if="!steps.length" class="text-muted">{{ t('guide.noSteps') }}</p>
      <ol v-else class="m-0 list-none space-y-4 p-0" data-testid="guide-steps">
        <GuideStepView v-for="(s, n) in steps" :key="s.order" :step="s" :n="n + 1" :warnings="warningsFor(s.order)" />
      </ol>

      <p v-if="afterPatch" class="mt-5">
        <Link :to="`/truth/guide/${afterPatch.slug}`" class="inline-flex min-h-11 items-center rounded-full border border-line px-4 font-semibold" data-testid="after-patch-link">
          {{ t('guide.after', { title: pick(afterPatch, 'title', lang).text }) }}
        </Link>
      </p>

      <section class="mt-6" data-safety="references" aria-labelledby="guide-refs">
        <h2 id="guide-refs" class="m-0 mb-2 text-sm font-semibold text-muted">{{ t('refs.title') }}</h2>
        <ol class="m-0 list-none space-y-1 p-0">
          <li v-for="s in snap.sources" :key="s.n">
            <a :href="s.url" target="_blank" rel="noopener noreferrer" class="flex min-h-11 gap-2 rounded-lg py-1 no-underline hover:bg-surface-2">
              <span class="font-mono text-muted">[{{ digits(s.n, lang) }}]</span>
              <span class="min-w-0">
                <span class="block text-fg underline" translate="no">{{ s.title }}</span>
                <span class="block text-sm text-muted"><span translate="no">{{ s.author ? `${s.author}, ${s.publisher}` : s.publisher }}</span><template v-if="s.publishedOn"> · {{ formatDay(t, lang, s.publishedOn) }}</template> · {{ t(`refs.${s.kind}`) }}</span>
              </span>
            </a>
          </li>
        </ol>
      </section>

      <section v-if="snap.corrections.length" class="mt-5 rounded-xl border border-[var(--amber-fg)] p-3" data-safety="corrections">
        <h2 class="m-0 text-sm font-semibold">{{ t('item.corrections') }}</h2>
        <p v-for="(c, n) in snap.corrections" :key="n" class="m-0 mt-1">
          <time :datetime="c.at" class="block text-sm text-muted">{{ t('item.correctionAt', { time: formatDateTime(t, lang, c.at) }) }}</time>
          <span data-content :lang="pick(c, 'text', lang).lang">{{ pick(c, 'text', lang).text }}</span>
        </p>
      </section>

      <p class="mt-5"><a :href="shareUrl" data-testid="share" :data-url="shareUrl" class="inline-flex min-h-11 items-center rounded-full border border-line px-4 font-semibold text-fg">{{ t('item.share') }}</a></p>
    </article>
  </div>
</template>
