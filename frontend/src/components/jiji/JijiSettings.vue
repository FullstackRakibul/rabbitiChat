<script setup>
/**
 * Jiji — Settings. Day 1: what the server has configured. YouTube connect and the
 * voice choice arrive on day 6 and day 3. Flags only; the server never sends a value.
 */
import { onMounted, ref } from 'vue'
import { getJijiStatus } from '@/services/jiji.service.js'

const status = ref(null)
const error = ref('')

async function load() {
  error.value = ''
  try {
    status.value = await getJijiStatus()
  } catch (e) {
    error.value = e.message || 'Could not read the status.'
  }
}

onMounted(load)

/** Tokens as 0.3M / 12.4k, for a prepaid budget at a glance. */
function tokens(n) {
  if (n == null) return ''
  if (n >= 1e6) return `${(n / 1e6).toFixed(n >= 1e7 ? 0 : 1)}M`
  if (n >= 1e3) return `${(n / 1e3).toFixed(n >= 1e4 ? 0 : 1)}k`
  return String(n)
}

const PROVIDER_STATUS = {
  ok: 'Connected', untested: 'Not tested yet', key_rejected: 'Key rejected', no_credit: 'Credit used up — top up',
  rate_limited: 'Rate limited', unreachable: 'Unreachable', error: 'Error', key_unreadable: 'Key unreadable — paste it again',
  disabled: 'Switched off',
}

function providerRow(p) {
  const used = `Jiji ${tokens(p.jijiTokensThisMonth)} this month`
    + (p.monthlyTokenBudget ? ` · all ${tokens(p.tokensThisMonth)} of ${tokens(p.monthlyTokenBudget)}` : '')
  return {
    label: `Model provider · ${p.name}`,
    ok: p.status === 'ok',
    note: `${PROVIDER_STATUS[p.status] || p.status} · ${used}`,
    word: p.status === 'ok' ? 'Ready' : 'Check Settings',
  }
}

const rows = (s) => [
  { label: 'Storage folder writable', ok: s.storageWritable },
  { label: 'Default script model', ok: !!s.model, note: s.model ? `${s.model} (${s.modelSource})` : 'None: pick one each time, or set Jiji:Model' },
  ...(s.provider ? [providerRow(s.provider)] : []),
  { label: 'Writing style', ok: s.voiceFound, note: s.voiceFound ? 'Context source "Jiji voice"' : 'Optional: add a context source named "Jiji voice" in Sources' },
  { label: 'Azure Speech', ok: s.speechConfigured, note: s.speechConfigured ? '' : 'Jiji__AzureSpeech__ApiKey and Jiji:AzureSpeech:Region' },
  { label: 'YouTube OAuth client', ok: s.youTubeConfigured, note: s.youTubeConfigured ? '' : 'Jiji:YouTube:ClientId and Jiji__YouTube__ClientSecret (day 6)' },
  { label: 'YouTube channel', ok: s.youTubeConnected, note: s.channelTitle || (s.youTubeConnected ? '' : 'Not connected') },
]
</script>

<template>
  <section class="mx-auto w-full max-w-2xl rounded-lg border bg-card p-6" data-jiji-settings>
    <h2 class="text-lg font-semibold tracking-tight">Settings</h2>
    <p class="mt-1 text-sm text-muted-foreground">What this server has configured for Jiji.</p>

    <p v-if="error" class="mt-4 rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive">
      {{ error }}
    </p>
    <p v-else-if="!status" class="mt-4 text-sm text-muted-foreground">Loading…</p>
    <ul v-else class="mt-4 divide-y rounded-md border">
      <li v-for="r in rows(status)" :key="r.label" class="flex items-center justify-between gap-3 px-3 py-2 text-sm">
        <span>{{ r.label }}</span>
        <span class="flex items-center gap-2 text-right">
          <span v-if="r.note" class="text-xs text-muted-foreground">{{ r.note }}</span>
          <span :class="r.ok ? 'text-emerald-600 dark:text-emerald-400' : r.word ? 'text-destructive' : 'text-muted-foreground'">{{ r.word || (r.ok ? 'Ready' : 'Missing') }}</span>
        </span>
      </li>
    </ul>
  </section>
</template>
