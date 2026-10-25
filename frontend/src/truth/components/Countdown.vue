<script setup>
// "in 2 d 3 h" to a scheduled patch, by the server's clock (lib/clock.js), so a
// visitor's wrong clock can't change it. Changes once a minute at most; nothing flashes.
import { computed } from 'vue'
import { useI18n } from '../lib/i18n.js'
import { lang } from '../lib/lang.js'
import { digits } from '../lib/time.js'
import { serverNow } from '../lib/clock.js'

const props = defineProps({ at: { type: String, required: true } })
const { t } = useI18n()

const text = computed(() => {
  const ms = Date.parse(props.at) - serverNow()
  if (ms <= 0) return t('countdown.passed')
  const total = Math.ceil(ms / 60_000)
  const d = Math.floor(total / 1440), h = Math.floor((total % 1440) / 60), m = total % 60
  const parts = []
  if (d) parts.push(t('countdown.d', { n: digits(d, lang.value) }))
  if (h || d) parts.push(t('countdown.h', { n: digits(h, lang.value) }))
  if (!d) parts.push(t('countdown.m', { n: digits(m, lang.value) }))
  return t('countdown.in', { time: parts.join(' ') })
})
</script>

<template>
  <span data-testid="countdown" :data-at="at">{{ text }}</span>
</template>
