<script setup>
// All fix guides (§3.7), filtered by technology and platform. Its nickname: the Ministry of Repair.
import { computed, onMounted, ref } from 'vue'
import { useI18n } from '../lib/i18n.js'
import Link from '../components/Link.vue'
import Lettermark from '../components/Lettermark.vue'
import SectionTitle from '../components/SectionTitle.vue'
import { lang } from '../lib/lang.js'
import { pick } from '../lib/content.js'
import { formatDate } from '../lib/time.js'
import { guideIndex, loadGuides } from '../lib/guides.js'

const emit = defineEmits(['title'])
const { t } = useI18n()
const tech = ref('')
const platform = ref('')
onMounted(() => { emit('title', t('guides.title')); loadGuides() })

const techs = computed(() => [...new Map(guideIndex.value.map((g) => [g.tech.slug, g.tech])).values()])
const platforms = computed(() => [...new Map(guideIndex.value.flatMap((g) => g.platforms).map((p) => [p.key, p])).values()])
const shown = computed(() => guideIndex.value.filter((g) => (!tech.value || g.tech.slug === tech.value) && (!platform.value || g.platforms.some((p) => p.key === platform.value))))
</script>

<template>
  <article class="mx-auto max-w-3xl px-4 py-8" data-testid="guides-page">
    <SectionTitle tag="h1" plain="guides.title" nick="nick.guides" large />
    <p class="m-0 text-muted">{{ t('guides.intro') }}</p>
    <p v-if="!guideIndex.length" class="mt-6 text-muted">{{ t('guides.empty') }}</p>
    <template v-else>
      <form class="mt-4 grid gap-3 sm:grid-cols-2" @submit.prevent>
        <label class="flex flex-col text-sm text-muted">{{ t('guides.tech') }}
          <select v-model="tech" data-testid="filter-tech" class="mt-1 min-h-11 rounded-lg border border-line bg-surface px-2 text-base text-fg">
            <option value="">{{ t('guides.all') }}</option>
            <option v-for="x in techs" :key="x.slug" :value="x.slug" translate="no">{{ x.name }}</option>
          </select>
        </label>
        <label class="flex flex-col text-sm text-muted">{{ t('guides.platform') }}
          <select v-model="platform" data-testid="filter-platform" class="mt-1 min-h-11 rounded-lg border border-line bg-surface px-2 text-base text-fg">
            <option value="">{{ t('guides.all') }}</option>
            <option v-for="p in platforms" :key="p.key" :value="p.key">{{ lang === 'en' ? p.nameEn : p.nameBn }}</option>
          </select>
        </label>
      </form>
      <p v-if="!shown.length" class="mt-6 text-muted">{{ t('guides.none') }}</p>
      <ul class="m-0 mt-4 list-none space-y-2 p-0">
        <li v-for="g in shown" :key="g.slug" :data-guide="g.slug">
          <Link :to="`/truth/guide/${g.slug}`" class="flex min-h-11 items-start gap-3 rounded-2xl border border-line bg-surface p-3 text-fg no-underline hover:bg-surface-2">
            <Lettermark :tech="g.tech" :size="36" />
            <span class="min-w-0 flex-1">
              <span class="block font-semibold" data-content :lang="pick(g, 'title', lang).lang">{{ pick(g, 'title', lang).text }}</span>
              <span class="block text-sm text-muted">{{ t(`phase.${g.phase}`) }} · {{ formatDate(t, lang, g.publishedAt) }}</span>
              <span class="block text-sm text-muted">{{ g.platforms.map((p) => (lang === 'en' ? p.nameEn : p.nameBn)).join(' · ') }}</span>
            </span>
          </Link>
        </li>
      </ul>
    </template>
  </article>
</template>
