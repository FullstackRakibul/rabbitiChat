<script setup>
// Room 101 (§3A.6): the page for an address with nothing at it. Little Brother
// stands by a door; a meme hides under a redaction bar that slides away on a tap.
import { onMounted, ref, watch } from 'vue'
import { useI18n } from '../lib/i18n.js'
import Link from '../components/Link.vue'
import Mascot from '../components/Mascot.vue'
import MemeBanner from '../components/MemeBanner.vue'
import SectionTitle from '../components/SectionTitle.vue'
import { pickMeme, view } from '../lib/theme.js'

const { t } = useI18n()
const meme = ref(null)
watch(() => [view.id, view.memeRev], () => {
  view.footerSlot = false
  view.pose = 'calm'
  meme.value = pickMeme('404', { anyMood: true })
}, { immediate: true })
onMounted(() => { document.title = `${t('notFound.title')} · ${t('site.name')}` })
</script>

<template>
  <section class="mx-auto max-w-3xl px-4 py-10 text-center" data-testid="room-101">
    <div class="flex items-end justify-center gap-2" aria-hidden="true">
      <svg width="64" height="104" viewBox="0 0 64 104" class="flex-none">
        <rect x="4" y="4" width="56" height="98" rx="4" class="door" />
        <rect x="12" y="14" width="40" height="34" rx="2" class="door-panel" />
        <rect x="12" y="56" width="40" height="38" rx="2" class="door-panel" />
        <rect x="22" y="22" width="20" height="8" rx="2" class="door-plate" />
        <circle cx="50" cy="60" r="3.2" class="door-knob" />
      </svg>
      <Mascot pose="calm" :size="96" />
    </div>
    <SectionTitle tag="h1" plain="notFound.title" nick="nick.room101" large class="mt-4 items-center" />
    <p class="mx-auto max-w-prose text-muted">{{ t('notFound.body') }}</p>
    <Link to="/truth" class="mt-2 inline-flex min-h-11 items-center rounded-full border border-line px-5 font-semibold">{{ t('notFound.back') }}</Link>
    <MemeBanner v-if="meme" :meme="meme" redact class="mt-8" />
  </section>
</template>
