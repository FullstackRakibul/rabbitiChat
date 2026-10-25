<script setup>
// সত্য মন্ত্রণালয় · Ministry of Truth — the public shell (docs/TRUTH_PROMPT.md §3, §3A, §4).
// No login, no admin code (G1.5). The site text, memes and glossary an
// administrator published are laid over the built-in ones once they arrive;
// until then, and whenever the backend can't be reached, the built-in ones keep
// the page readable. Each route is a new view for the theme: a fresh mood and
// mascot pose, and memes that don't repeat within it.
import { computed, defineAsyncComponent, onMounted, ref, watch } from 'vue'
import { useI18n } from './lib/i18n.js'
import SiteHeader from './components/SiteHeader.vue'
import SiteFooter from './components/SiteFooter.vue'
import GlossarySheet from './components/GlossarySheet.vue'
import IssuePage from './pages/IssuePage.vue'
import Room101 from './pages/Room101.vue'
import { route } from './lib/router.js'
import { applySiteText } from './lib/i18n.js'
import { applyGlossary, closeTerm } from './lib/glossary.js'
import { applyMemes, newView } from './lib/theme.js'
import { load, rssUrl } from './lib/api.js'

// The issue page is the first paint; the other pages load when they are opened.
const AboutPage = defineAsyncComponent(() => import('./pages/AboutPage.vue'))
const GlossaryPage = defineAsyncComponent(() => import('./pages/GlossaryPage.vue'))
const GuidePage = defineAsyncComponent(() => import('./pages/GuidePage.vue'))
const GuidesPage = defineAsyncComponent(() => import('./pages/GuidesPage.vue'))

const { t, locale } = useI18n()
const pageTitle = ref(null)

newView()
watch(() => [route.name, route.slug], () => { pageTitle.value = null; closeTerm(); newView() })

onMounted(async () => {
  // The admin's site-text editor frames this page at ?preview=1 and sends its unpublished text,
  // same origin only. It is laid over the default like published text: strings at known keys, shown as text.
  if (new URLSearchParams(location.search).has('preview') && window.parent !== window) {
    window.addEventListener('message', (e) => {
      if (e.origin === location.origin && e.data?.type === 'truth-preview') applySiteText(e.data.text)
    })
  }
  try {
    const r = await load('/site-content')
    if (!r.notFound) {
      applySiteText(r.data.text)
      if (r.data.memes) applyMemes(r.data.memes)
      if (r.data.glossary) applyGlossary(r.data.glossary)
    }
  } catch { /* the built-in text, memes and glossary stay */ }
})

const docTitle = computed(() => (pageTitle.value ? `${pageTitle.value} · ${t('site.name')}` : `${t('site.name')} · ${t('site.series')}`))
watch([docTitle, locale], () => {
  document.title = docTitle.value
  document.querySelector('meta[name="description"]')?.setAttribute('content', t('site.description'))
  // The feed the browser offers follows the language too.
  const feed = document.querySelector('link[rel="alternate"][type="application/rss+xml"]')
  feed?.setAttribute('href', rssUrl(locale.value))
  feed?.setAttribute('title', `${t('site.name')} · ${t('site.series')}`)
}, { immediate: true })
</script>

<template>
  <!-- The rubber stamps' rough edge (§3A.3). -->
  <svg width="0" height="0" class="absolute" aria-hidden="true" focusable="false">
    <filter id="truth-rough"><feTurbulence type="fractalNoise" baseFrequency="0.9" numOctaves="1" result="n" /><feDisplacementMap in="SourceGraphic" in2="n" scale="2" /></filter>
  </svg>
  <a href="#main" class="absolute top-0 left-0 z-20 inline-flex min-h-11 -translate-y-full items-center bg-fg px-4 text-bg focus:translate-y-0">{{ t('nav.skip') }}</a>
  <SiteHeader />
  <main id="main" class="min-h-screen">
    <AboutPage v-if="route.name === 'about'" @title="pageTitle = $event" />
    <GlossaryPage v-else-if="route.name === 'glossary'" @title="pageTitle = $event" />
    <GuidesPage v-else-if="route.name === 'guides'" @title="pageTitle = $event" />
    <GuidePage v-else-if="route.name === 'guide'" :slug="route.slug" @title="pageTitle = $event" />
    <Room101 v-else-if="route.name === 'notFound'" />
    <IssuePage v-else :slug="route.slug" @title="pageTitle = $event" />
  </main>
  <SiteFooter />
  <GlossarySheet />
</template>
