<script setup>
// Above the hero while any item is under active attack or waiting for a scheduled patch (§3.1).
import { computed } from 'vue'
import { useI18n } from '../lib/i18n.js'
import Msg from './Msg.js'
import { lang } from '../lib/lang.js'
import { URGENT } from '../lib/content.js'
import { formatWithUtc } from '../lib/time.js'

const props = defineProps({ items: { type: Array, required: true } })
const { t } = useI18n()

const urgent = computed(() => props.items.filter((i) => URGENT.includes(i.status)).map((i) => ({
  id: i.id,
  tech: i.tech.name,
  key: i.status === 'active_attack' ? 'banner.activeAttack' : i.patchScheduledAt ? 'banner.patchScheduled' : 'banner.patchTimeTba',
  time: i.patchScheduledAt ? formatWithUtc(t, lang.value, i.patchScheduledAt) : null,
})))
</script>

<template>
  <section v-if="urgent.length" data-testid="urgent-banner" data-safety="urgent" aria-labelledby="urgent-title"
    class="border-b-2 border-[var(--red-fg)] bg-[var(--red-bg)] text-[var(--red-fg)]">
    <div class="mx-auto max-w-3xl px-4 py-2">
      <h2 id="urgent-title" class="m-0 text-sm font-semibold uppercase tracking-wide">{{ t('banner.title') }}</h2>
      <ul class="m-0 list-none p-0">
        <li v-for="u in urgent" :key="u.id">
          <a :href="`#item-${u.id}`" class="flex min-h-11 items-center font-semibold text-[var(--red-fg)]">
            <Msg :k="u.key" :values="{ tech: u.tech, time: u.time }" :keep="['tech']" />
          </a>
        </li>
      </ul>
    </div>
  </section>
</template>
