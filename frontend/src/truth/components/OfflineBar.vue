<script setup>
// Shown over the last stored copy while the backend can't be reached (§4).
import { useI18n } from '../lib/i18n.js'
import { lang } from '../lib/lang.js'
import { formatDateTime } from '../lib/time.js'

defineProps({ savedAt: { type: String, required: true } })
const { t } = useI18n()
</script>

<template>
  <p data-testid="offline-bar" role="status"
    class="m-0 bg-[var(--offline-bg)] px-4 py-2 text-center text-sm text-[var(--offline-fg)]">
    {{ t('offline.bar', { time: formatDateTime(t, lang, savedAt) }) }}
  </p>
</template>
