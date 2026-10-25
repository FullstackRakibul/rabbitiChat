<script setup>
// Paste-to-compare (§6): a verify step's expected output against what the reader's
// server printed. In the browser only — what is pasted is never sent (§11, G2.5).
// Lines are compared trimmed and with runs of spaces made one; an expected line
// written "…" matches anything.
import { computed, ref } from 'vue'
import { useI18n } from '../lib/i18n.js'
import { lang } from '../lib/lang.js'
import { digits } from '../lib/time.js'

const props = defineProps({ expected: { type: String, required: true } })
const { t } = useI18n()
const pasted = ref('')

const norm = (s) => s.split('\n').map((l) => l.trim().replace(/\s+/g, ' ')).filter(Boolean)
const result = computed(() => {
  if (!pasted.value.trim()) return null
  const want = norm(props.expected)
  const got = norm(pasted.value)
  const missing = want.filter((w) => w !== '…' && w !== '...' && !got.includes(w))
  return { ok: missing.length === 0, missing: missing.length }
})
</script>

<template>
  <div class="mt-2">
    <label class="block text-sm text-muted">{{ t('guide.compare') }}
      <textarea v-model="pasted" rows="3" data-testid="paste-compare" class="mt-1 block w-full rounded-lg border border-line bg-surface p-2 font-mono text-sm text-fg" spellcheck="false" />
    </label>
    <p class="m-0 mt-1 text-xs text-muted">{{ t('guide.compareNote') }}</p>
    <p v-if="result" data-testid="compare-result" class="m-0 mt-1 font-semibold" :class="result.ok ? 'text-[var(--green-fg)]' : 'text-[var(--red-fg)]'">
      {{ result.ok ? t('guide.match') : t('guide.mismatch', { count: digits(result.missing, lang) }) }}
    </p>
  </div>
</template>
