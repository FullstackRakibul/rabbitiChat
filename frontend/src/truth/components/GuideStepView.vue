<script setup>
// One step of a fix guide (§6): its kind, words, code (copy, wrap), and for a verify
// step the expected output with paste-to-compare; "what this may break" and any
// reviewed warning beneath it. All plain: no theme inside a step (§3A.2, G1.12).
import { useI18n } from '../lib/i18n.js'
import CodeBlock from './CodeBlock.vue'
import PasteCompare from './PasteCompare.vue'
import { lang } from '../lib/lang.js'
import { pick } from '../lib/content.js'
import { digits } from '../lib/time.js'

defineProps({
  step: { type: Object, required: true },   // { order, kind, title…, body…, code, expected, language, sideEffects… }
  n: { type: Number, required: true },
  warnings: { type: Array, default: () => [] },
})
const { t } = useI18n()
const STYLE = { warning: 'border-[var(--red-fg)]', undo: 'border-[var(--amber-fg)]', verify: 'border-[var(--green-fg)]' }
</script>

<template>
  <li class="rounded-2xl border-2 bg-surface p-4" :class="STYLE[step.kind] ?? 'border-line'" data-safety="step" :data-kind="step.kind" :data-step="step.order">
    <p class="m-0 text-sm font-semibold text-muted">{{ digits(n, lang) }} · {{ t(`kind.${step.kind}`) }}</p>
    <h3 v-if="pick(step, 'title', lang).text" class="m-0 mt-1 text-lg font-semibold" data-content :lang="pick(step, 'title', lang).lang">{{ pick(step, 'title', lang).text }}</h3>
    <p v-if="pick(step, 'body', lang).text" class="m-0 mt-1 whitespace-pre-line" data-content :lang="pick(step, 'body', lang).lang">{{ pick(step, 'body', lang).text }}</p>
    <CodeBlock v-if="step.code" :code="step.code" :language="step.language" :testid="`step-${step.order}-code`" />
    <div v-if="step.kind === 'verify' && step.expected" data-safety="verify" class="mt-3">
      <p class="m-0 text-sm font-semibold">{{ t('guide.expected') }}</p>
      <CodeBlock :code="step.expected" :testid="`step-${step.order}-expected`" />
      <PasteCompare :expected="step.expected" />
    </div>
    <div v-if="pick(step, 'sideEffects', lang).text" data-safety="breaks" class="mt-3 rounded-xl bg-[var(--amber-bg)] p-3 text-[var(--amber-fg)]">
      <p class="m-0 text-sm font-semibold">{{ t('guide.mayBreak') }}</p>
      <p class="m-0" data-content :lang="pick(step, 'sideEffects', lang).lang">{{ pick(step, 'sideEffects', lang).text }}</p>
    </div>
    <ul v-if="warnings.length" data-safety="warning" class="m-0 mt-3 list-none space-y-1 rounded-xl bg-[var(--red-bg)] p-3 text-[var(--red-fg)]">
      <li v-for="w in warnings" :key="w.rule" data-testid="step-warning" :data-rule="w.rule"><strong>{{ t('guide.warnings') }}:</strong> {{ t(`safety.${w.rule.replace('.', '_')}`) }}</li>
    </ul>
  </li>
</template>
