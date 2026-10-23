<script setup>
// "Serious mode" (§3A.2): the visitor turns every meme and effect off. Remembered in this browser.
import { useI18n } from '../lib/i18n.js'
import { seriousChoice } from '../lib/theme.js'

const { t } = useI18n()
</script>

<template>
  <button type="button" role="switch" data-testid="serious-switch" :aria-checked="seriousChoice"
    class="inline-flex min-h-11 items-center gap-2 rounded-full border border-line px-3 text-sm font-semibold text-fg"
    @click="seriousChoice = !seriousChoice">
    <span class="switch-track" :class="{ on: seriousChoice }" aria-hidden="true"><span class="switch-knob" /></span>
    {{ t('serious.label') }}
  </button>
</template>
