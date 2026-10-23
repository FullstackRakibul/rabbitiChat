<script setup>
// The tapped term's explanation (§3A.7): a sheet at the bottom of the screen, so it
// works by tap on a phone and never pushes the page sideways. Esc or Close shuts it.
import { nextTick, ref, watch } from 'vue'
import { useI18n } from '../lib/i18n.js'
import Link from './Link.vue'
import { lang } from '../lib/lang.js'
import { closeTerm, openEntry } from '../lib/glossary.js'

const { t } = useI18n()
const close = ref(null)
watch(openEntry, async (e) => { if (e) { await nextTick(); close.value?.focus() } })
const onKey = (e) => { if (e.key === 'Escape') closeTerm() }
</script>

<template>
  <div v-if="openEntry" role="dialog" aria-labelledby="gloss-sheet-title" data-testid="glossary-sheet"
    class="fixed inset-x-0 bottom-0 z-30 border-t-2 border-[var(--poster)] bg-surface shadow-2xl" @keydown="onKey">
    <div class="mx-auto max-w-3xl px-4 py-4">
      <div class="flex items-start gap-3">
        <h2 id="gloss-sheet-title" class="m-0 flex-1 text-lg font-semibold" data-content>{{ openEntry.term[lang] }}</h2>
        <button ref="close" type="button" class="min-h-11 rounded-full border border-line px-4 font-semibold text-fg" @click="closeTerm">{{ t('glossary.close') }}</button>
      </div>
      <p class="m-0 mt-2" data-content>{{ openEntry.def[lang] }}</p>
      <Link to="/truth/glossary" class="mt-2 inline-flex min-h-11 items-center" @click="closeTerm">{{ t('glossary.all') }}</Link>
    </div>
  </div>
</template>
