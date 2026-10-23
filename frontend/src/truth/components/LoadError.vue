<script setup>
// No server and no stored copy (§4): "the telescreen is sleeping" — the friendly
// error, in the built-in text, with the RSS link and a sleeping Little Brother.
import { ref, watch } from 'vue'
import { useI18n } from '../lib/i18n.js'
import Mascot from './Mascot.vue'
import MemeBanner from './MemeBanner.vue'
import SectionTitle from './SectionTitle.vue'
import { lang } from '../lib/lang.js'
import { rssUrl } from '../lib/api.js'
import { pickMeme, view } from '../lib/theme.js'

const { t } = useI18n()
const retry = () => location.reload()
const meme = ref(null)
watch(() => [view.id, view.memeRev], () => {
  view.footerSlot = false
  view.pose = 'sleeping'
  meme.value = pickMeme('backend-down', { anyMood: true })
}, { immediate: true })
</script>

<template>
  <section data-testid="load-error" class="mx-auto max-w-3xl px-4 py-10 text-center">
    <Mascot pose="sleeping" :size="120" class="mx-auto" />
    <SectionTitle tag="h1" plain="offline.errorTitle" nick="nick.sleeping" class="mt-3 items-center" />
    <p class="mx-auto max-w-prose text-muted">{{ t('offline.errorBody') }}</p>
    <div class="mt-6 flex flex-wrap justify-center gap-3">
      <button type="button" class="min-h-11 rounded-full bg-fg px-5 font-semibold text-bg" @click="retry">{{ t('offline.retry') }}</button>
      <a :href="rssUrl(lang)" class="inline-flex min-h-11 items-center rounded-full border border-line px-5 font-semibold">{{ t('footer.rss') }}</a>
    </div>
    <MemeBanner v-if="meme" :meme="meme" class="mt-8" />
  </section>
</template>
