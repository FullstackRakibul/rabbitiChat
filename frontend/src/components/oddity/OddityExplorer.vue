<script setup>
/**
 * Oddity — API explorer: check a key, send a real request, see exactly what came back.
 *
 * Sends to the address this page was opened on (same origin, so no CORS), through
 * http.external — the one http.js call that carries the gateway key instead of
 * the session token. The key lives in the parent's memory only: never stored and
 * never sent anywhere but the gateway. Everything shown is rendered as text.
 */
import { computed, ref, watch } from 'vue'
import { Check, CircleStop, Copy, Eye, EyeOff, KeyRound, Loader2, RotateCcw, Send } from '@lucide/vue'
import { http, gatewayBaseUrl } from '@/services/index.js'
import { GATEWAY_ENDPOINTS } from '@/lib/oddity-docs.js'
import { cn } from '@/lib/utils'
import DocCodeBlock from '@/components/mostisko-docs/DocCodeBlock.vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'

const props = defineProps({
  /** The gateway key, held by the parent so the examples can use it too. */
  apiKey: { type: String, default: '' },
  /** The model the sample requests start with; the first allowed model when empty. */
  defaultModel: { type: String, default: '' },
})
const emit = defineEmits(['update:apiKey'])

const key = computed({
  get: () => props.apiKey,
  set: (v) => emit('update:apiKey', v),
})
const showKey = ref(false)
const base = gatewayBaseUrl()

/** Never send a header with an empty bearer: no key means no header, and a 401. */
function authHeaders() {
  const k = key.value.trim()
  return k ? { Authorization: `Bearer ${k}` } : {}
}

// ─────────────── Check the key ───────────────

const models = ref([])          // allowed models, from /api/tags once a key works
const check = ref(null)         // { ok, status, message }
const checking = ref(false)

async function checkKey() {
  if (checking.value) return
  checking.value = true
  check.value = null
  try {
    const r = await http.external(`${base}/api/tags`, { headers: authHeaders() })
    const text = await r.text()
    let data = null
    try { data = JSON.parse(text) } catch { /* not JSON */ }
    if (r.ok) {
      models.value = (data?.models ?? []).map((m) => m.name)
      const n = models.value.length
      check.value = { ok: true, status: r.status, message: `The key works. ${n} model${n === 1 ? '' : 's'} allowed.` }
    } else {
      check.value = { ok: false, status: r.status, message: data?.error ?? (text || r.statusText) }
    }
  } catch (e) {
    check.value = { ok: false, status: null, message: `No answer from the gateway: ${e.message}` }
  } finally {
    checking.value = false
  }
}

// ─────────────── The request ───────────────

const endpointPath = ref('/api/chat')
const endpoint = computed(() => GATEWAY_ENDPOINTS.find((e) => e.path === endpointPath.value) ?? GATEWAY_ENDPOINTS[0])

const chatModel = computed(() =>
  props.defaultModel || models.value.find((m) => !/embed/i.test(m)) || 'qwen3.8:27b')
const embedModel = computed(() => models.value.find((m) => /embed/i.test(m)) || 'nomic-embed-text')

/** A starting body for each endpoint. GET endpoints have none. */
function sampleFor(path) {
  const question = 'Say hello in five words.'
  switch (path) {
    case '/api/chat': return { model: chatModel.value, messages: [{ role: 'user', content: question }], stream: true }
    case '/api/generate': return { model: chatModel.value, prompt: question, stream: true }
    case '/api/embed': return { model: embedModel.value, input: 'Ship window 2026-11-01 to 2026-11-15' }
    case '/api/embeddings': return { model: embedModel.value, prompt: 'Ship window 2026-11-01 to 2026-11-15' }
    case '/api/show': return { model: chatModel.value }
    default: return null
  }
}

const bodyText = ref('')
function resetBody() {
  const sample = sampleFor(endpointPath.value)
  bodyText.value = sample ? JSON.stringify(sample, null, 2) : ''
}
watch(endpointPath, resetBody, { immediate: true })

/** none (GET) | ok | invalid. An invalid body can still be sent: the gateway's 400 is worth seeing. */
const bodyState = computed(() => {
  if (endpoint.value.method === 'GET') return 'none'
  try {
    const v = JSON.parse(bodyText.value)
    return v && typeof v === 'object' && !Array.isArray(v) ? 'ok' : 'invalid'
  } catch {
    return 'invalid'
  }
})

function useModel(name) {
  try {
    const v = JSON.parse(bodyText.value)
    v.model = name
    bodyText.value = JSON.stringify(v, null, 2)
  } catch { /* fix the JSON first */ }
}

// ─────────────── Send and read the reply ───────────────

const RAW_LIMIT = 200_000       // characters of raw reply kept on screen
const res = ref(null)
const sending = ref(false)
let controller = null

const ms = (ns) => (ns == null ? '—' : `${Math.round(ns / 1e6).toLocaleString()} ms`)

/** Usage from Ollama's last frame (or whole reply), as label/value rows. */
function usageOf(f) {
  const perSecond = f.eval_count && f.eval_duration ? (f.eval_count / (f.eval_duration / 1e9)).toFixed(1) : '—'
  return [
    ['Done reason', f.done_reason ?? '—'],
    ['Prompt tokens', f.prompt_eval_count ?? '—'],
    ['Cached prompt tokens', f.prompt_eval_cached_count ?? '—'],
    ['Output tokens', f.eval_count ?? '—'],
    ['Tokens a second', perSecond],
    ['Load', ms(f.load_duration)],
    ['Reading the prompt', ms(f.prompt_eval_duration)],
    ['Writing the reply', ms(f.eval_duration)],
    ['Total at Ollama', ms(f.total_duration)],
  ]
}

function appendRaw(text) {
  if (res.value.raw.length >= RAW_LIMIT) { res.value.truncated = true; return }
  res.value.raw += text.slice(0, RAW_LIMIT - res.value.raw.length)
}

function onFrame(line) {
  if (!line.trim()) return
  res.value.frames++
  let f
  try { f = JSON.parse(line) } catch { return }
  const piece = f.message?.content ?? f.response ?? ''
  if (piece) res.value.reply += piece
  const thought = f.message?.thinking ?? f.thinking ?? ''
  if (thought) res.value.thinking += thought
  if (f.error) res.value.streamError = String(f.error)
  if (f.done) res.value.usage = usageOf(f)
}

/** A whole JSON reply: pretty-print it, and lift out the answer and usage when present. */
function onWhole() {
  try {
    const v = JSON.parse(res.value.raw)
    res.value.pretty = JSON.stringify(v, null, 2)
    if (v?.error) res.value.errorMessage = String(v.error)
    const answer = v?.message?.content ?? v?.response
    if (typeof answer === 'string') res.value.reply = answer
    if (v?.done) res.value.usage = usageOf(v)
  } catch { /* not JSON: the raw text is all there is */ }
}

async function send() {
  if (sending.value) return
  const ep = endpoint.value
  const headers = authHeaders()
  let body
  if (ep.method !== 'GET') {
    headers['Content-Type'] = 'application/json'
    body = bodyText.value
  }

  controller = new AbortController()
  sending.value = true
  const started = performance.now()
  res.value = {
    request: `${ep.method} ${base}${ep.path}`,
    status: null, statusText: '', contentType: '', correlationId: '',
    firstByteMs: null, totalMs: null, frames: 0,
    raw: '', truncated: false, reply: '', thinking: '', pretty: '',
    usage: null, streamError: '', errorMessage: '', networkError: '', aborted: false,
  }

  try {
    const r = await http.external(base + ep.path, { method: ep.method, headers, body, signal: controller.signal })
    res.value.status = r.status
    res.value.statusText = r.statusText
    res.value.contentType = r.headers.get('content-type') ?? ''
    res.value.correlationId = r.headers.get('x-correlation-id') ?? ''
    const ndjson = res.value.contentType.includes('ndjson')

    const reader = r.body?.getReader()
    const decoder = new TextDecoder()
    let partial = ''
    while (reader) {
      const { value, done } = await reader.read()
      if (done) break
      res.value.firstByteMs ??= Math.round(performance.now() - started)
      const text = decoder.decode(value, { stream: true })
      appendRaw(text)
      if (ndjson) {
        partial += text
        const lines = partial.split('\n')
        partial = lines.pop()
        lines.forEach(onFrame)
      }
    }
    const tail = decoder.decode()
    if (tail) { appendRaw(tail); partial += tail }
    if (ndjson) onFrame(partial)
    else onWhole()
  } catch (e) {
    if (e.name === 'AbortError') res.value.aborted = true
    else res.value.networkError = e.message
  } finally {
    res.value.totalMs = Math.round(performance.now() - started)
    sending.value = false
    controller = null
  }
}

function stop() {
  controller?.abort()
}

// ─────────────── Display helpers ───────────────

/** The status classes LogsTab uses. */
function statusClass(code) {
  if (!code) return 'text-muted-foreground'
  if (code < 300) return 'text-green-500 dark:text-green-400'
  if (code < 400) return 'text-blue-500'
  if (code < 500) return 'text-yellow-500 dark:text-yellow-400'
  return 'text-red-500 dark:text-red-400'
}

/** The same request as a curl command, with the key typed above (or a placeholder). */
const curl = computed(() => {
  const ep = endpoint.value
  const lines = [`curl ${base}${ep.path}`, `  -H "Authorization: Bearer ${key.value.trim() || 'YOUR_ODDITY_KEY'}"`]
  if (ep.method !== 'GET') {
    let compact = bodyText.value
    try { compact = JSON.stringify(JSON.parse(bodyText.value)) } catch { /* send what was typed */ }
    lines.push(`  -d '${compact.replaceAll("'", "'\\''")}'`)
  }
  return lines.join(' \\\n')
})

const copied = ref('')
async function copy(text, what) {
  try {
    await navigator.clipboard.writeText(text)
    copied.value = what
    setTimeout(() => { if (copied.value === what) copied.value = '' }, 1500)
  } catch { /* clipboard refused: the text is on screen */ }
}

/** Lets the endpoints table's "Try" buttons pick the endpoint. */
defineExpose({ select: (path) => { endpointPath.value = path } })
</script>

<template>
  <div class="space-y-5">
    <!-- Key -->
    <div class="space-y-1.5">
      <Label for="oddity-explorer-key">Gateway key</Label>
      <div class="flex flex-wrap gap-2">
        <div class="relative min-w-0 flex-1">
          <Input
            id="oddity-explorer-key"
            v-model="key"
            :type="showKey ? 'text' : 'password'"
            autocomplete="off"
            spellcheck="false"
            placeholder="odd_…"
            class="pr-10 font-mono"
          />
          <button
            type="button"
            class="absolute right-1 top-1/2 -translate-y-1/2 rounded-sm p-1.5 text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
            :aria-label="showKey ? 'Hide the key' : 'Show the key'"
            @click="showKey = !showKey"
          >
            <EyeOff v-if="showKey" class="h-4 w-4" />
            <Eye v-else class="h-4 w-4" />
          </button>
        </div>
        <Button variant="outline" :disabled="checking" @click="checkKey">
          <Loader2 v-if="checking" class="h-4 w-4 animate-spin" />
          <KeyRound v-else class="h-4 w-4" />
          Check key
        </Button>
      </div>
      <p class="text-xs text-muted-foreground">
        Kept in this page's memory only — never saved. It also fills in the examples below.
      </p>
      <p v-if="check" class="text-sm" role="status">
        <span :class="['font-mono', statusClass(check.status)]">{{ check.status ?? '—' }}</span>
        <span :class="check.ok ? '' : 'text-destructive'"> {{ check.message }}</span>
      </p>
    </div>

    <!-- Endpoint -->
    <div class="space-y-1.5">
      <p class="text-sm font-medium">Endpoint</p>
      <div class="flex flex-wrap gap-1" role="group" aria-label="Endpoint">
        <button
          v-for="e in GATEWAY_ENDPOINTS"
          :key="e.path"
          type="button"
          :aria-pressed="endpointPath === e.path"
          :class="cn(
            'rounded-md border px-2.5 py-1 font-mono text-xs transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring',
            endpointPath === e.path ? 'border-primary bg-primary text-primary-foreground' : 'text-muted-foreground hover:bg-muted hover:text-foreground',
          )"
          @click="endpointPath = e.path"
        >
          {{ e.method }} {{ e.path }}
        </button>
      </div>
      <p class="text-xs text-muted-foreground">
        Sends to <code class="break-all font-mono">{{ base }}{{ endpoint.path }}</code> — the address this page was opened on.
      </p>
    </div>

    <!-- Body -->
    <div v-if="endpoint.method !== 'GET'" class="space-y-1.5">
      <div class="flex flex-wrap items-center justify-between gap-2">
        <Label for="oddity-explorer-body">Request body (JSON)</Label>
        <Button variant="ghost" size="sm" @click="resetBody"><RotateCcw class="h-4 w-4" /> Sample</Button>
      </div>
      <Textarea id="oddity-explorer-body" v-model="bodyText" rows="9" spellcheck="false" class="font-mono text-xs" />
      <p v-if="bodyState === 'invalid'" class="text-xs text-destructive">
        Not a JSON object. You can still send it — the gateway will answer 400.
      </p>
      <div v-if="models.length" class="flex flex-wrap items-center gap-1 text-xs">
        <span class="text-muted-foreground">Model:</span>
        <button
          v-for="m in models"
          :key="m"
          type="button"
          class="rounded border px-1.5 py-0.5 font-mono text-muted-foreground hover:bg-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
          @click="useModel(m)"
        >
          {{ m }}
        </button>
      </div>
    </div>
    <p v-else class="text-sm text-muted-foreground">A GET request has no body.</p>

    <div class="flex flex-wrap gap-2">
      <Button :disabled="sending" @click="send">
        <Loader2 v-if="sending" class="h-4 w-4 animate-spin" />
        <Send v-else class="h-4 w-4" />
        Send
      </Button>
      <Button v-if="sending" variant="outline" @click="stop">
        <CircleStop class="h-4 w-4" /> Stop
      </Button>
    </div>

    <!-- Response -->
    <section v-if="res" class="space-y-3 rounded-lg border bg-card p-4" aria-live="polite" aria-label="Response">
      <div class="flex flex-wrap items-baseline gap-x-3 gap-y-1">
        <span :class="['font-mono text-lg font-semibold', statusClass(res.status)]">{{ res.status ?? '—' }}</span>
        <span class="text-sm text-muted-foreground">{{ res.statusText }}</span>
        <span class="ml-auto text-xs text-muted-foreground">
          first byte {{ res.firstByteMs ?? '—' }} ms · total {{ res.totalMs ?? '…' }} ms
          <template v-if="res.frames"> · {{ res.frames }} frames</template>
        </span>
      </div>
      <p class="break-all font-mono text-xs text-muted-foreground">{{ res.request }}</p>

      <dl class="grid gap-x-4 gap-y-1 text-xs sm:grid-cols-[max-content_1fr]">
        <dt class="text-muted-foreground">Content-Type</dt>
        <dd class="break-all font-mono">{{ res.contentType || '—' }}</dd>
        <dt class="text-muted-foreground">X-Correlation-Id</dt>
        <dd class="flex items-center gap-1 break-all font-mono">
          {{ res.correlationId || '—' }}
          <button
            v-if="res.correlationId"
            type="button"
            class="rounded-sm p-0.5 text-muted-foreground hover:text-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
            aria-label="Copy the correlation id"
            @click="copy(res.correlationId, 'cid')"
          >
            <Check v-if="copied === 'cid'" class="h-3.5 w-3.5" />
            <Copy v-else class="h-3.5 w-3.5" />
          </button>
        </dd>
      </dl>

      <p v-if="res.aborted" class="text-sm text-muted-foreground">Stopped. Ollama stops too.</p>
      <p v-if="res.networkError" class="text-sm text-destructive">No answer: {{ res.networkError }}</p>
      <p v-if="res.errorMessage" class="text-sm text-destructive">{{ res.errorMessage }}</p>
      <p v-if="res.streamError" class="text-sm text-destructive">Error during the stream: {{ res.streamError }}</p>

      <div v-if="res.reply" class="space-y-1">
        <p class="text-xs font-medium text-muted-foreground">Reply</p>
        <p class="whitespace-pre-wrap break-words text-sm">{{ res.reply }}</p>
      </div>

      <details v-if="res.thinking" class="text-sm">
        <summary class="cursor-pointer text-xs font-medium text-muted-foreground">Reasoning</summary>
        <p class="mt-1 whitespace-pre-wrap break-words text-muted-foreground">{{ res.thinking }}</p>
      </details>

      <div v-if="res.usage" class="overflow-x-auto rounded-md border">
        <table class="w-full text-xs">
          <caption class="px-3 pt-2 text-left font-medium text-muted-foreground">Usage, from Ollama's last frame</caption>
          <tbody>
            <tr v-for="[label, value] in res.usage" :key="label" class="border-b last:border-0">
              <th scope="row" class="px-3 py-1.5 text-left font-normal text-muted-foreground">{{ label }}</th>
              <td class="px-3 py-1.5 text-right font-mono tabular-nums">{{ value }}</td>
            </tr>
          </tbody>
        </table>
      </div>

      <details :open="!res.reply && !res.usage" class="text-sm">
        <summary class="cursor-pointer text-xs font-medium text-muted-foreground">
          Raw body ({{ res.raw.length.toLocaleString() }} characters{{ res.truncated ? ', cut' : '' }})
        </summary>
        <div class="mt-2 flex justify-end">
          <Button variant="ghost" size="sm" :disabled="!res.raw" @click="copy(res.pretty || res.raw, 'raw')">
            <Check v-if="copied === 'raw'" class="h-4 w-4" />
            <Copy v-else class="h-4 w-4" /> Copy
          </Button>
        </div>
        <pre class="max-h-80 overflow-auto rounded-md bg-muted p-3 font-mono text-xs">{{ res.pretty || res.raw }}</pre>
      </details>
    </section>

    <DocCodeBlock :code="curl" language="The same request with curl" />
  </div>
</template>
