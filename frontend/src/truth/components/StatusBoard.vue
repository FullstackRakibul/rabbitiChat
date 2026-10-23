<script setup>
// The status board (§3.2): icon, name, one line and the pill for every item.
import { useI18n } from '../lib/i18n.js'
import Lettermark from './Lettermark.vue'
import SectionTitle from './SectionTitle.vue'
import StatusPill from './StatusPill.vue'
import { lang } from '../lib/lang.js'
import { pick } from '../lib/content.js'

defineProps({ items: { type: Array, required: true } })
const { t } = useI18n()
</script>

<template>
  <section id="board" data-testid="status-board" aria-labelledby="board-title" class="scroll-mt-20">
    <SectionTitle id="board-title" plain="board.title" nick="nick.board" />
    <p v-if="!items.length" class="text-muted">{{ t('board.empty') }}</p>
    <ul v-else class="m-0 list-none divide-y divide-[var(--border)] overflow-hidden rounded-2xl border border-line bg-surface p-0">
      <li v-for="i in items" :key="i.id">
        <a :href="`#item-${i.id}`" class="flex min-h-11 flex-wrap items-center gap-x-3 gap-y-2 px-4 py-3 text-fg no-underline hover:bg-surface-2">
          <Lettermark :tech="i.tech" :size="36" />
          <span class="min-w-0 flex-1 basis-40">
            <span class="block font-semibold leading-snug" translate="no">{{ i.tech.name }}</span>
            <span class="line-clamp-2 block text-sm leading-normal text-muted" data-content :lang="pick(i, 'headline', lang).lang">{{ pick(i, 'headline', lang).text }}</span>
          </span>
          <StatusPill :status="i.status" />
        </a>
      </li>
    </ul>
  </section>
</template>
