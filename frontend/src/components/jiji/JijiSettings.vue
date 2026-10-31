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

const rows = (s) => [
  { label: 'Storage folder writable', ok: s.storageWritable },
  { label: 'Script model', ok: !!s.model, note: s.model || 'Not chosen (Jiji:Model, day 2)' },
  { label: 'Azure Speech', ok: s.speechConfigured, note: s.speechConfigured ? '' : 'Jiji__AzureSpeech__Key and Region (day 3)' },
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
          <span :class="r.ok ? 'text-emerald-600 dark:text-emerald-400' : 'text-muted-foreground'">{{ r.ok ? 'Ready' : 'Missing' }}</span>
        </span>
      </li>
    </ul>
  </section>
</template>
