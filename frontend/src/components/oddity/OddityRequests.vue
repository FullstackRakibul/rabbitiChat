<script setup>
/**
 * Oddity — Requests board (basic list; filters and the detail dialog grow in Phase 5).
 *
 * Every value here came from a caller or from Ollama, so all of it is rendered as
 * text, never as markup.
 */
import { onMounted, ref, watch } from 'vue'
import { Activity, Loader2, RefreshCcw } from '@lucide/vue'
import { listOddityRequests } from '@/services/index.js'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'

// reka reserves '' on a Select, so "no filter" is the ALL sentinel, as in LogsTab.
const ALL = 'all'

const items = ref([])
const nextBefore = ref(null)
const loading = ref(false)
const error = ref('')

const outcome = ref(ALL)
const search = ref('')

function filters() {
  return {
    status: outcome.value === ALL ? undefined : outcome.value,
    q: search.value.trim() || undefined,
  }
}

async function load(reset) {
  if (loading.value) return
  loading.value = true
  error.value = ''
  try {
    const page = await listOddityRequests({ ...filters(), before: reset ? undefined : nextBefore.value })
    items.value = reset ? page.items : [...items.value, ...page.items]
    nextBefore.value = page.nextBefore
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

watch(outcome, () => load(true))

/** Same classes LogsTab uses for a status code. */
function statusClass(code) {
  if (!code) return ''
  if (code < 300) return 'text-green-500 dark:text-green-400'
  if (code < 400) return 'text-blue-500'
  if (code < 500) return 'text-yellow-500 dark:text-yellow-400'
  return 'text-red-500 dark:text-red-400'
}

function tokensPerSecond(r) {
  if (!r.outputTokens || !r.generationMs) return '—'
  return (r.outputTokens / r.generationMs * 1000).toFixed(1)
}

const num = (v) => (v === null || v === undefined ? '—' : v.toLocaleString())

onMounted(() => load(true))
</script>

<template>
  <div class="mx-auto flex w-full max-w-6xl flex-col gap-4">
    <div class="flex flex-wrap items-center gap-2">
      <h2 class="mr-auto flex items-center gap-2 text-lg font-semibold tracking-tight">
        <Activity class="h-5 w-5" /> Requests
      </h2>
      <Select v-model="outcome">
        <SelectTrigger class="h-8 w-40 text-xs" aria-label="Outcome"><SelectValue placeholder="Outcome" /></SelectTrigger>
        <SelectContent>
          <SelectItem :value="ALL">All outcomes</SelectItem>
          <SelectItem value="ok">ok</SelectItem>
          <SelectItem value="rejected">rejected</SelectItem>
          <SelectItem value="upstream_error">upstream_error</SelectItem>
          <SelectItem value="client_abort">client_abort</SelectItem>
        </SelectContent>
      </Select>
      <Input
        v-model="search"
        class="h-8 w-48 text-xs"
        placeholder="Search key, model, error…"
        aria-label="Search requests"
        @keydown.enter="load(true)"
      />
      <Button variant="outline" size="sm" :disabled="loading" @click="load(true)">
        <RefreshCcw class="h-4 w-4" /> Refresh
      </Button>
    </div>

    <p v-if="error" class="rounded-md bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>

    <div class="overflow-x-auto rounded-lg border bg-card">
      <table class="w-full min-w-208 text-sm">
        <thead class="border-b text-left text-xs text-muted-foreground">
          <tr>
            <th class="px-3 py-2 font-medium">Time</th>
            <th class="px-3 py-2 font-medium">Status</th>
            <th class="px-3 py-2 font-medium">Key</th>
            <th class="px-3 py-2 font-medium">Endpoint</th>
            <th class="px-3 py-2 font-medium">Model</th>
            <th class="px-3 py-2 font-medium">IP</th>
            <th class="px-3 py-2 text-right font-medium">In / out</th>
            <th class="px-3 py-2 text-right font-medium">Tok/s</th>
            <th class="px-3 py-2 text-right font-medium">Total</th>
          </tr>
        </thead>
        <tbody>
          <tr v-if="!loading && !items.length">
            <td colspan="9" class="px-3 py-8 text-center text-muted-foreground">No requests yet.</td>
          </tr>
          <tr v-for="r in items" :key="r.id" class="border-b last:border-0">
            <td class="whitespace-nowrap px-3 py-2 text-muted-foreground">{{ new Date(r.timestampUtc).toLocaleString() }}</td>
            <td class="whitespace-nowrap px-3 py-2">
              <span :class="['font-mono', statusClass(r.statusCode)]">{{ r.statusCode }}</span>
              <span class="ml-1.5 text-xs text-muted-foreground">{{ r.outcome }}</span>
              <span
                v-if="r.doneReason === 'length'"
                class="ml-1.5 text-xs text-muted-foreground"
                title="The reply was cut off at the output limit"
              >cut off</span>
            </td>
            <td class="max-w-40 truncate px-3 py-2">{{ r.keyName }}</td>
            <td class="px-3 py-2 font-mono text-xs">{{ r.endpoint }}</td>
            <td class="max-w-48 truncate px-3 py-2 font-mono text-xs">{{ r.model ?? '—' }}</td>
            <td class="px-3 py-2 font-mono text-xs">{{ r.clientIp ?? '—' }}</td>
            <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums">{{ num(r.promptTokens) }} / {{ num(r.outputTokens) }}</td>
            <td class="px-3 py-2 text-right tabular-nums">{{ tokensPerSecond(r) }}</td>
            <td class="whitespace-nowrap px-3 py-2 text-right tabular-nums">{{ num(r.totalMs) }} ms</td>
          </tr>
        </tbody>
      </table>
    </div>

    <div class="flex justify-center">
      <Button v-if="nextBefore" variant="outline" size="sm" :disabled="loading" @click="load(false)">
        <Loader2 v-if="loading" class="h-4 w-4 animate-spin" /> Load more
      </Button>
    </div>
  </div>
</template>
