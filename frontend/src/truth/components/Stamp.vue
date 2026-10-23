<script setup>
// A rubber stamp (§3A.3): decoration only, aria-hidden. The real status is always in the pill.
import { useI18n } from '../lib/i18n.js'

defineProps({ kind: { type: String, required: true } })   // verified · unconfirmed · corrected
const { t } = useI18n()
</script>

<template>
  <span aria-hidden="true" :data-stamp="kind" class="stamp" :class="`stamp-${kind}`">{{ t(`stamp.${kind}`) }}</span>
</template>
