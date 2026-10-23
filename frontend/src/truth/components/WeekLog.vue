<script setup>
// This week's log (§3.3): every timeline entry of every item, oldest first, on a
// telescreen (§3A.3) whose power LED is green, and blinks amber slowly while
// something is urgent.
import { computed } from 'vue'
import { useI18n } from '../lib/i18n.js'
import SectionTitle from './SectionTitle.vue'
import { lang } from '../lib/lang.js'
import { pick, URGENT } from '../lib/content.js'
import { formatDateTime } from '../lib/time.js'

const props = defineProps({ items: { type: Array, required: true } })
const { t } = useI18n()

const LEVEL = { ERR: 'text-[#ff8f7a]', WARN: 'text-[#ffd27a]', FIX: 'text-[#8be0a4]', INFO: 'text-[#9cc9ff]' }
const entries = computed(() => props.items
  .flatMap((i) => i.timeline.map((e, n) => ({ key: `${i.id}-${n}`, tech: i.tech.name, ...e })))
  .sort((a, b) => a.at.localeCompare(b.at)))
const urgent = computed(() => props.items.some((i) => URGENT.includes(i.status)))
</script>

<template>
  <section id="log" aria-labelledby="log-title" class="scroll-mt-20">
    <SectionTitle id="log-title" plain="log.title" nick="nick.log" />
    <p v-if="!entries.length" class="text-muted">{{ t('log.empty') }}</p>
    <div v-else class="tele">
      <span class="led" :class="{ 'led-urgent': urgent }" data-testid="led" aria-hidden="true" />
      <ol class="tele-screen m-0 list-none space-y-3 p-4 font-mono text-sm leading-relaxed">
        <li v-for="e in entries" :key="e.key">
          <div class="flex flex-wrap gap-x-2">
            <time :datetime="e.at" class="tele-muted">{{ formatDateTime(t, lang, e.at) }}</time>
            <span class="font-semibold" :class="LEVEL[e.level]">{{ t(`log.${e.level}`) }}</span>
            <span class="font-semibold" translate="no">{{ e.tech }}</span>
          </div>
          <p class="m-0 font-sans text-base" data-content :lang="pick(e, 'text', lang).lang">{{ pick(e, 'text', lang).text }}</p>
        </li>
      </ol>
    </div>
  </section>
</template>
