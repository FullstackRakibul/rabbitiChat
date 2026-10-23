<script setup>
// The sticky chip nav (§3.1): the page's sections and each item. It scrolls sideways
// inside itself; the page never does.
import { useI18n } from '../lib/i18n.js'

defineProps({ items: { type: Array, required: true }, archive: { type: Boolean, default: false } })
const { t } = useI18n()
</script>

<template>
  <nav data-testid="chip-nav" :aria-label="t('nav.sections')"
    class="sticky top-0 z-10 border-b border-line bg-bg/95 backdrop-blur">
    <ul class="chips mx-auto flex max-w-3xl list-none gap-2 overflow-x-auto px-4 py-2">
      <li><a href="#board" class="inline-flex min-h-11 items-center whitespace-nowrap rounded-full border border-line px-4 text-sm font-semibold text-fg no-underline">{{ t('nav.board') }}</a></li>
      <li><a href="#log" class="inline-flex min-h-11 items-center whitespace-nowrap rounded-full border border-line px-4 text-sm font-semibold text-fg no-underline">{{ t('nav.log') }}</a></li>
      <li v-for="i in items" :key="i.id">
        <a :href="`#item-${i.id}`" class="inline-flex min-h-11 items-center whitespace-nowrap rounded-full border border-line px-4 text-sm font-semibold text-fg no-underline"
          translate="no">{{ i.tech.name }}</a>
      </li>
      <li v-if="archive"><a href="#archive" class="inline-flex min-h-11 items-center whitespace-nowrap rounded-full border border-line px-4 text-sm font-semibold text-fg no-underline">{{ t('nav.archive') }}</a></li>
    </ul>
  </nav>
</template>
