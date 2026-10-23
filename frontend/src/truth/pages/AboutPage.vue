<script setup>
// How we verify (/truth/about): the rules of §1.1 in the visitor's words; the
// privacy promise the footer links to (§3A.9, and true); and Little Brother.
import { nextTick, onMounted } from 'vue'
import { useI18n } from '../lib/i18n.js'
import Mascot from '../components/Mascot.vue'
import SectionTitle from '../components/SectionTitle.vue'

const emit = defineEmits(['title'])
const { t } = useI18n()
const POSES = ['calm', 'alert', 'worried', 'relieved', 'reading', 'sleeping']
onMounted(async () => {
  emit('title', t('about.title'))
  await nextTick()
  if (location.hash) document.getElementById(location.hash.slice(1))?.scrollIntoView()
})
</script>

<template>
  <article class="mx-auto max-w-3xl px-4 py-8">
    <h1 class="m-0 text-2xl font-semibold">{{ t('about.title') }}</h1>
    <p class="mt-4 mb-0 max-w-prose">{{ t('about.p1') }}</p>
    <p class="mt-4 mb-0 max-w-prose">{{ t('about.p2') }}</p>
    <p class="mt-4 mb-0 max-w-prose">{{ t('about.p3') }}</p>
    <p class="mt-4 mb-0 max-w-prose">{{ t('about.p4') }}</p>
    <p class="mt-4 mb-0 max-w-prose">{{ t('about.p5') }}</p>
    <p class="mt-4 mb-0 max-w-prose">{{ t('about.p6') }}</p>

    <section id="privacy" class="mt-10 scroll-mt-20" aria-labelledby="privacy-title">
      <SectionTitle id="privacy-title" plain="privacy.title" nick="nick.privacy" />
      <p class="m-0 max-w-prose">{{ t('privacy.p1') }}</p>
      <p class="mt-3 mb-0 max-w-prose">{{ t('privacy.p2') }}</p>
      <p class="mt-3 mb-0 max-w-prose">{{ t('privacy.p3') }}</p>
    </section>

    <section class="mt-10" aria-labelledby="mascot-title" data-testid="mascot-gallery">
      <h2 id="mascot-title" class="m-0 text-lg font-semibold">{{ t('mascot.title') }}</h2>
      <p class="mt-2 mb-0 max-w-prose text-muted">{{ t('mascot.body') }}</p>
      <ul class="m-0 mt-4 grid list-none grid-cols-3 gap-4 p-0 sm:grid-cols-6">
        <li v-for="p in POSES" :key="p" class="flex flex-col items-center text-sm text-muted">
          <Mascot :pose="p" :size="80" />
          <span>{{ t(`mascot.${p}`) }}</span>
        </li>
      </ul>
    </section>
  </article>
</template>
