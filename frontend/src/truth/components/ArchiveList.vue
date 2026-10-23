<script setup>
// Past issues (§3.7), from /truth-api/issues — what was published, never a draft.
import { useI18n } from '../lib/i18n.js'
import Link from './Link.vue'
import SectionTitle from './SectionTitle.vue'
import { lang } from '../lib/lang.js'
import { pick } from '../lib/content.js'
import { digits, formatRange } from '../lib/time.js'

defineProps({ issues: { type: Array, required: true }, current: { type: String, default: null } })
const { t } = useI18n()
</script>

<template>
  <section id="archive" aria-labelledby="archive-title" class="scroll-mt-20">
    <SectionTitle id="archive-title" plain="archive.title" nick="nick.archive" />
    <p v-if="!issues.length" class="text-muted">{{ t('archive.empty') }}</p>
    <ul v-else class="m-0 list-none divide-y divide-(--border) overflow-hidden rounded-2xl border border-line bg-surface p-0">
      <li v-for="i in issues" :key="i.slug">
        <Link :to="`/truth/${i.slug}`" class="flex min-h-11 flex-wrap items-baseline gap-x-3 px-4 py-3 text-fg no-underline hover:bg-surface-2">
          <span class="min-w-0 flex-1 basis-48 font-semibold" data-content :lang="pick(i, 'title', lang).lang">{{ pick(i, 'title', lang).text }}</span>
          <span class="text-sm text-muted">{{ formatRange(t, lang, i.weekStart, i.weekEnd) }} · {{ i.itemCount === 1 ? t('archive.itemsOne', { count: digits(1, lang) }) : t('archive.items', { count: digits(i.itemCount, lang) }) }}</span>
          <span v-if="i.slug === current" class="pill tone-grey">{{ t('archive.current') }}</span>
        </Link>
      </li>
    </ul>
  </section>
</template>
