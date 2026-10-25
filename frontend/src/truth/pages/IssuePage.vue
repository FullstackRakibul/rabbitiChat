<script setup>
// An issue: the current one at /truth, a published one at /truth/:slug (§3).
// Sections in the spec's order — banner, hero, chip nav, status board, log, the
// detail cards, then the archive. The version checker, "do now", guides and
// subscribe arrive in P2 and P3.
//
// The hero is a telescreen (§3A.3): scanlines, the crack, a once-only glitch on
// the headline and Little Brother in the corner, his pose following the news.
// While anything is under active attack the view turns serious by itself: no
// glitch, no crack, no memes but the footer's, and a worried mascot.
import { computed, defineAsyncComponent, ref, watch } from 'vue'
import { useI18n } from '../lib/i18n.js'
import UrgentBanner from '../components/UrgentBanner.vue'
import ChipNav from '../components/ChipNav.vue'
import StatusBoard from '../components/StatusBoard.vue'
import WeekLog from '../components/WeekLog.vue'
import ItemCard from '../components/ItemCard.vue'
import ArchiveList from '../components/ArchiveList.vue'
import LoadError from '../components/LoadError.vue'
import OfflineBar from '../components/OfflineBar.vue'
import Mascot from '../components/Mascot.vue'
import CrackOverlay from '../components/CrackOverlay.vue'
import MemeBanner from '../components/MemeBanner.vue'
import SeriousSwitch from '../components/SeriousSwitch.vue'
import DoNow from '../components/DoNow.vue'
import Link from '../components/Link.vue'
import { loadGuides } from '../lib/guides.js'
import { startTicking, syncClock } from '../lib/clock.js'
import Room101 from './Room101.vue'
import { load, rssUrl } from '../lib/api.js'
import { lang } from '../lib/lang.js'
import { pick } from '../lib/content.js'
import { formatDateTime, formatRange } from '../lib/time.js'
import { pickMeme, setIssueState, view } from '../lib/theme.js'

// Below the items: loaded after the first paint.
const VersionChecker = defineAsyncComponent(() => import('../components/VersionChecker.vue'))

const props = defineProps({ slug: { type: String, default: null } })
const emit = defineEmits(['title'])
const { t } = useI18n()

const state = ref('loading')        // loading | ready | empty | notFound | error
const snap = ref(null)
const savedAt = ref(null)
const archive = ref([])

async function fetchIssue() {
  state.value = 'loading'
  try {
    const r = await load(props.slug ? `/issues/${props.slug}` : '/current')
    if (r.notFound) { state.value = props.slug ? 'notFound' : 'empty'; return }
    snap.value = r.data
    savedAt.value = r.offline ? r.savedAt : null
    state.value = 'ready'
  } catch {
    state.value = 'error'
  }
  try {
    const a = await load('/issues')
    archive.value = a.notFound ? [] : a.data.issues
  } catch { archive.value = [] }
}
watch(() => props.slug, fetchIssue, { immediate: true })
loadGuides()
syncClock()
startTicking()

const items = computed(() => snap.value?.items ?? [])
const title = computed(() => (snap.value ? pick(snap.value.issue, 'title', lang.value) : null))
const subtitle = computed(() => (snap.value ? pick(snap.value.issue, 'subtitle', lang.value) : null))
watch([title, state], () => emit('title', state.value === 'ready' ? title.value?.text : null), { immediate: true })

// The view's mood and pose follow the news; a page with a meme of its own keeps the footer's off.
const divider = ref(null)
const emptyMeme = ref(null)
watch([state, items, () => view.memeRev, () => view.id], () => {
  if (state.value === 'ready') {
    setIssueState(items.value)
    view.footerSlot = true
    divider.value = view.autoSerious ? null : pickMeme('divider')
  } else if (state.value === 'empty') {
    view.footerSlot = false
    emptyMeme.value = pickMeme('empty', { anyMood: true })
  }
}, { immediate: true })
</script>

<template>
  <div>
    <OfflineBar v-if="savedAt" :saved-at="savedAt" />
    <p v-if="state === 'loading'" class="mx-auto max-w-3xl px-4 py-12 text-center text-muted">{{ t('loading') }}</p>
    <LoadError v-else-if="state === 'error'" />
    <Room101 v-else-if="state === 'notFound'" />
    <section v-else-if="state === 'empty'" class="mx-auto max-w-3xl px-4 py-12 text-center">
      <Mascot pose="calm" :size="120" class="mx-auto" />
      <h1 class="text-xl font-semibold">{{ t('empty.title') }}</h1>
      <p class="text-muted">{{ t('empty.body') }}</p>
      <a :href="rssUrl(lang)" class="inline-flex min-h-11 items-center">{{ t('footer.rss') }}</a>
      <MemeBanner v-if="emptyMeme" :meme="emptyMeme" class="mt-8" />
    </section>

    <template v-else>
      <UrgentBanner :items="items" />
      <section class="mx-auto max-w-3xl px-4 pt-5 pb-4">
        <div class="tele px-4 pt-4 pb-5 sm:px-6" data-testid="hero">
          <div v-if="!view.autoSerious" class="scanlines" data-effect="scanlines" aria-hidden="true" />
          <CrackOverlay v-if="!view.autoSerious" />
          <div class="relative flex items-start gap-3">
            <div class="min-w-0 flex-1">
              <p class="m-0 text-sm font-semibold text-accent">{{ t('site.series') }} · {{ formatRange(t, lang, snap.issue.weekStart, snap.issue.weekEnd) }}</p>
              <h1 class="display m-0 mt-2 text-3xl sm:text-4xl" :class="{ glitch: !view.autoSerious }" data-testid="hero-title" data-content :lang="title.lang">{{ title.text }}</h1>
              <p v-if="subtitle.text" class="m-0 mt-2 text-muted" data-content :lang="subtitle.lang">{{ subtitle.text }}</p>
              <p class="m-0 mt-3 text-sm text-muted">{{ t('hero.updated', { time: formatDateTime(t, lang, snap.issue.publishedAt) }) }} · {{ t('time.zoneNote') }}</p>
            </div>
            <Mascot :pose="view.pose" :size="76" class="mt-1 sm:hidden" data-testid="hero-mascot" />
            <Mascot :pose="view.pose" :size="120" class="mt-1 hidden sm:block" />
          </div>
          <div class="relative mt-4"><SeriousSwitch /></div>
        </div>
      </section>
      <ChipNav :items="items" :archive="archive.length > 0" />
      <div class="mx-auto max-w-3xl space-y-10 px-4 pt-6">
        <StatusBoard :items="items" />
        <WeekLog :items="items" />
        <MemeBanner v-if="divider" :meme="divider" data-testid="divider-meme" />
        <section id="details" aria-labelledby="details-title" class="scroll-mt-20">
          <h2 id="details-title" class="mb-3 text-lg font-semibold">{{ t('nav.details') }}</h2>
          <div class="space-y-6">
            <ItemCard v-for="i in items" :key="i.id" :item="i" :issue="snap.issue" />
          </div>
        </section>
        <VersionChecker :items="items" />
        <DoNow :items="items" />
        <ArchiveList v-if="archive.length" :issues="archive" :current="snap.issue.slug" />
        <p><Link to="/truth/guides" class="inline-flex min-h-11 items-center font-semibold">{{ t('nav.guides') }}</Link></p>
      </div>
    </template>
  </div>
</template>
