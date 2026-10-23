<script setup>
// The footer: a meme (the one place memes stay during an attack, §3A.2), the
// privacy promise (§3A.9, true, not a joke), the serious-mode switch and the links.
import { ref, watch } from 'vue'
import { useI18n } from '../lib/i18n.js'
import Link from './Link.vue'
import MemeBanner from './MemeBanner.vue'
import SeriousSwitch from './SeriousSwitch.vue'
import { lang } from '../lib/lang.js'
import { rssUrl } from '../lib/api.js'
import { pickMeme, view } from '../lib/theme.js'

const { t } = useI18n()

// Picked once a view knows its mood, and again if the memes change.
const meme = ref(null)
watch(() => [view.id, view.mood, view.memeRev, view.footerSlot], () => {
  meme.value = view.footerSlot ? pickMeme('footer') : null
}, { immediate: true })
</script>

<template>
  <footer class="mt-12 border-t border-line bg-surface">
    <div class="mx-auto max-w-3xl px-4 py-6 text-sm text-muted">
      <MemeBanner v-if="meme" :meme="meme" class="mb-6" />
      <p class="m-0">{{ t('footer.note') }}</p>
      <Link to="/truth/about#privacy" class="mt-2 inline-flex min-h-11 items-center font-semibold" data-testid="privacy-link">{{ t('nick.privacy') }}</Link>
      <div class="mt-2"><SeriousSwitch /></div>
      <ul class="m-0 mt-3 flex list-none flex-wrap gap-x-4 p-0">
        <li><Link to="/truth" class="inline-flex min-h-11 items-center">{{ t('nav.home') }}</Link></li>
        <li><Link to="/truth/glossary" class="inline-flex min-h-11 items-center">{{ t('nav.glossary') }}</Link></li>
        <li><Link to="/truth/about" class="inline-flex min-h-11 items-center">{{ t('nav.about') }}</Link></li>
        <li><a :href="rssUrl(lang)" class="inline-flex min-h-11 items-center">{{ t('footer.rss') }}</a></li>
      </ul>
    </div>
  </footer>
</template>
