<script setup>
// The glossary (§3A.7), nicknamed the Newspeak Dictionary: plain, correct, one line each.
import { computed, onMounted } from 'vue'
import { useI18n } from '../lib/i18n.js'
import SectionTitle from '../components/SectionTitle.vue'
import { lang } from '../lib/lang.js'
import { entries } from '../lib/glossary.js'

const emit = defineEmits(['title'])
const { t } = useI18n()
onMounted(() => emit('title', t('glossary.title')))
const sorted = computed(() => [...entries.value].sort((a, b) => a.term[lang.value].localeCompare(b.term[lang.value], lang.value)))
</script>

<template>
  <article class="mx-auto max-w-3xl px-4 py-8" data-testid="glossary-page">
    <SectionTitle tag="h1" plain="glossary.title" nick="nick.glossary" large />
    <p class="m-0 text-muted">{{ t('glossary.intro') }}</p>
    <dl class="m-0 mt-6 divide-y divide-(--border) rounded-2xl border border-line bg-surface">
      <div v-for="e in sorted" :id="`gloss-${e.id}`" :key="e.id" class="scroll-mt-20 px-4 py-3">
        <dt class="font-semibold" data-content>{{ e.term[lang] }}</dt>
        <dd class="m-0 mt-1" data-content>{{ e.def[lang] }}</dd>
      </div>
    </dl>
  </article>
</template>
