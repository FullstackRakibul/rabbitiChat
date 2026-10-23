<script setup>
// A status pill: its colour family and always its words (docs/TRUTH_PROMPT.md §2, §3).
import { useI18n } from '../lib/i18n.js'
import { tone } from '../lib/content.js'

defineProps({ status: { type: String, required: true } })
const { t } = useI18n()
</script>

<template>
  <span class="pill" :class="`tone-${tone(status)}`" :data-status="status" data-safety="pill">{{ t(`status.${status}`) }}</span>
</template>
