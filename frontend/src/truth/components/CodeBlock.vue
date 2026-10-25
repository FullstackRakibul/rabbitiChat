<script setup>
// A code block (§3): its own horizontal scroll, a wrap toggle, and a copy button.
// The code is text — a template already rendered by lib/template.js — never HTML.
import { ref } from 'vue'
import { useI18n } from '../lib/i18n.js'

const props = defineProps({
  code: { type: String, required: true },
  language: { type: String, default: null },
  testid: { type: String, default: 'code' },
})
const { t } = useI18n()
const wrap = ref(false)
const copied = ref(false)

async function copy() {
  try { await navigator.clipboard.writeText(props.code); copied.value = true; setTimeout(() => { copied.value = false }, 2000) } catch { /* no clipboard */ }
}
</script>

<template>
  <div class="tele mt-2" data-code-block>
    <div class="flex items-center justify-end gap-1 border-b border-line bg-surface-2 px-2">
      <span v-if="language" class="mr-auto font-mono text-xs text-muted" translate="no">{{ language }}</span>
      <button type="button" class="min-h-11 rounded px-3 text-sm text-muted" :aria-pressed="wrap" @click="wrap = !wrap">{{ t('guide.wrap') }}</button>
      <button type="button" class="min-h-11 rounded px-3 text-sm font-semibold text-fg" :data-testid="`${testid}-copy`" @click="copy">{{ copied ? t('guide.copied') : t('guide.copy') }}</button>
    </div>
    <pre class="tele-screen m-0 overflow-x-auto p-3 text-sm leading-relaxed" :class="wrap ? 'whitespace-pre-wrap break-words' : 'whitespace-pre'" translate="no"><code :data-testid="testid">{{ code }}</code></pre>
  </div>
</template>
