<script setup>
/**
 * Oddity — API documentation, for callers and for Ground Control.
 *
 * One component, two surfaces. The Docs board passes `admin`: it adds the live
 * settings, both addresses (public and this one) and the /api/oddity reference.
 * The public /oddity/docs page has no session, so it shows the caller's part only,
 * with the address the page was opened on.
 *
 * All content comes from lib/oddity-docs.js and lib/oddity-examples.js, which the
 * eval holds to the code. Everything is rendered as text. A key typed into the
 * explorer ("Try it") stays in this component's memory: it fills in the examples
 * and goes only to the gateway, and only when Check or Send is pressed.
 */
import { computed, onMounted, ref } from 'vue'
import { Check, Copy } from '@lucide/vue'
import { getOdditySettings, gatewayUrls } from '@/services/index.js'
import { EXAMPLES, fillExample } from '@/lib/oddity-examples.js'
import {
  ADMIN_ENDPOINTS, BODY_LIMIT_BYTES, CLOSED_ENDPOINTS, DIFFERENCES, GATEWAY_ENDPOINTS, GATEWAY_ERRORS,
  NESTED_SCHEMAS, PASSTHROUGH_ERRORS, REQUEST_CHANGES, RESPONSE_NOTES, SCHEMAS,
} from '@/lib/oddity-docs.js'
import DocCodeBlock from '@/components/mostisko-docs/DocCodeBlock.vue'
import DocSection from '@/components/mostisko-docs/DocSection.vue'
import OddityExplorer from '@/components/oddity/OddityExplorer.vue'
import { Button } from '@/components/ui/button'

const props = defineProps({
  /** The Docs board: live settings, both addresses and the Ground Control API. */
  admin: { type: Boolean, default: false },
})

// ---- Addresses and live settings (board only) ----

const settings = ref(null)
const settingsError = ref('')

const urls = computed(() => gatewayUrls(settings.value))
const base = computed(() => urls.value[0].url)

const liveRows = computed(() => {
  const s = settings.value
  if (!s) return []
  return [
    ['Gateway', s.enabled ? 'On' : 'Off: every call gets 503'],
    ['Default model', s.defaultModel || 'None: a call must name its model'],
    ['Allowed models', s.allowedModels.length ? s.allowedModels.join(', ') : 'Any'],
    ['Context window (num_ctx)', s.numCtx.toLocaleString()],
    ['Keep-alive', s.keepAlive],
    ['Default calls a minute', s.defaultRatePerMinute === 0 ? 'No limit' : String(s.defaultRatePerMinute)],
    ['Body logging', s.logBodies],
  ]
})

// ---- Explorer and examples ----

/** One key, typed into the explorer, also fills the examples. Memory only. */
const pastedKey = ref('')
const explorer = ref(null)

/** The endpoints table's "Try" buttons: pick the endpoint, then scroll to the explorer. */
function tryEndpoint(path) {
  explorer.value?.select(path)
  jump('oddity-try')
}

const exampleKey = computed(() => pastedKey.value.trim() || 'YOUR_ODDITY_KEY')
const exampleId = ref(EXAMPLES[0].id)
const example = computed(() => EXAMPLES.find((e) => e.id === exampleId.value) ?? EXAMPLES[0])
const exampleCode = computed(() => fillExample(example.value.code, { base: base.value, key: exampleKey.value }))

// ---- Ground Control reference (board only) ----

/** Each schema the admin routes use, once, in the order it first appears; nested ones last. */
const schemaOrder = computed(() => {
  const names = []
  for (const e of ADMIN_ENDPOINTS)
    for (const n of [e.body, e.query, e.response?.replace('[]', '')])
      if (n && !names.includes(n)) names.push(n)
  for (const n of NESTED_SCHEMAS) if (!names.includes(n)) names.push(n)
  return names
})

// ---- Page furniture ----

const sections = computed(() => [
  { id: 'oddity-base', title: 'Base URL' },
  { id: 'oddity-start', title: 'Quick start' },
  { id: 'oddity-try', title: 'Try it' },
  { id: 'oddity-auth', title: 'Authentication' },
  { id: 'oddity-endpoints', title: 'Endpoints' },
  { id: 'oddity-changes', title: 'What Oddity changes' },
  { id: 'oddity-errors', title: 'Errors' },
  { id: 'oddity-differences', title: 'Differences from Ollama' },
  { id: 'oddity-examples', title: 'Examples' },
  ...(props.admin
    ? [{ id: 'oddity-admin', title: 'Ground Control API' }, { id: 'oddity-fields', title: 'Fields' }]
    : []),
])

/**
 * Scrolls without touching the URL: a hash change would be a router navigation,
 * and the router's scrollBehavior sends every navigation to the top.
 */
function jump(id) {
  const smooth = !window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
  document.getElementById(id)?.scrollIntoView({ behavior: smooth ? 'smooth' : 'auto', block: 'start' })
}

const copied = ref('')
async function copy(text) {
  try {
    await navigator.clipboard.writeText(text)
    copied.value = text
    setTimeout(() => { if (copied.value === text) copied.value = '' }, 1500)
  } catch {
    /* clipboard refused — the address is on screen to copy by hand */
  }
}

// The board is mounted on first open, so this is the first-open fetch.
onMounted(async () => {
  if (!props.admin) return
  try {
    settings.value = await getOdditySettings()
  } catch (e) {
    settingsError.value = e.message
  }
})
</script>

<template>
  <div class="mx-auto w-full max-w-4xl space-y-10 pb-16">
    <header class="space-y-2">
      <h1 class="text-2xl font-bold tracking-tight">Oddity API</h1>
      <p class="text-muted-foreground">
        An Ollama-compatible gateway. Point any Ollama client at the base URL below and add a
        key: the paths, the request JSON and the streamed replies are Ollama's own.
      </p>
      <nav aria-label="On this page" class="flex flex-wrap gap-x-4 gap-y-1 pt-2 text-sm">
        <button
          v-for="s in sections"
          :key="s.id"
          type="button"
          class="rounded-sm text-muted-foreground underline-offset-4 hover:text-foreground hover:underline focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
          @click="jump(s.id)"
        >
          {{ s.title }}
        </button>
      </nav>
    </header>

    <!-- ── Base URL ── -->
    <DocSection id="oddity-base" title="Base URL">
      <ul class="space-y-2">
        <li v-for="u in urls" :key="u.url" class="flex flex-wrap items-center gap-2">
          <span class="w-28 shrink-0 text-sm text-muted-foreground">{{ u.label }}</span>
          <code class="min-w-0 break-all rounded-md bg-muted px-2 py-1 font-mono text-sm">{{ u.url }}</code>
          <Button variant="ghost" size="icon" :aria-label="`Copy the ${u.label.toLowerCase()} address`" @click="copy(u.url)">
            <Check v-if="copied === u.url" class="h-4 w-4" />
            <Copy v-else class="h-4 w-4" />
          </Button>
        </li>
      </ul>
      <p v-if="!admin" class="text-sm text-muted-foreground">
        This page shows the address it was opened on. If Ground Control gave you a different
        one, use theirs.
      </p>
      <p v-else-if="settings && !settings.publicBaseUrl" class="rounded-md bg-muted p-3 text-sm text-muted-foreground">
        No public URL is set, so only this address is shown. Set one in Settings for callers
        outside this network.
      </p>
      <p v-if="settingsError" class="text-sm text-destructive">{{ settingsError }}</p>

      <details v-if="admin" class="rounded-lg border bg-card text-sm">
        <summary class="cursor-pointer px-3 py-2 font-medium focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring">
          Publishing the gateway on your network
        </summary>
        <ol class="list-decimal space-y-1.5 border-t px-3 py-3 pl-8 leading-relaxed">
          <li>
            Run the API on every interface. <code class="rounded bg-muted px-1 font-mono text-xs">dotnet run</code>
            listens on <code class="rounded bg-muted px-1 font-mono text-xs">http://*:5274</code>; Docker publishes
            its port on every interface already.
          </li>
          <li>Let other machines in: allow inbound TCP 5274 in the firewall of this machine.</li>
          <li>
            Set <span class="font-medium">Public gateway URL</span> in Settings to this machine's address, e.g.
            <code class="rounded bg-muted px-1 font-mono text-xs">http://10.10.60.156:5274/oddity</code>. A DHCP
            address can change; reserve it on the router.
          </li>
          <li>
            Point callers at the API's port, not the Vite dev server's: through Vite every call is
            recorded with the proxy's address, 127.0.0.1.
          </li>
          <li>Change the administrator's secret first (Settings → Account): the seeded one is published.</li>
        </ol>
      </details>

      <div v-if="admin && liveRows.length" class="overflow-x-auto rounded-lg border bg-card">
        <table class="w-full text-sm">
          <caption class="px-3 pt-3 text-left text-sm font-medium">This gateway now</caption>
          <tbody>
            <tr v-for="[label, value] in liveRows" :key="label" class="border-b last:border-0">
              <th scope="row" class="w-56 px-3 py-2 text-left font-normal text-muted-foreground">{{ label }}</th>
              <td class="px-3 py-2">{{ value }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </DocSection>

    <!-- ── Quick start ── -->
    <DocSection id="oddity-start" title="Quick start">
      <ol class="list-decimal space-y-2 pl-5 text-sm leading-relaxed">
        <li>Get a key from Ground Control. It is shown once, when it is created.</li>
        <li>
          Point your Ollama client at the base URL instead of
          <code class="rounded bg-muted px-1 font-mono text-xs">http://localhost:11434</code>.
        </li>
        <li>
          Send the key on every call:
          <code class="rounded bg-muted px-1 font-mono text-xs">Authorization: Bearer odd_…</code>
        </li>
        <li>Use Ollama's own API: the same paths, request JSON and streamed replies.</li>
      </ol>
    </DocSection>

    <!-- ── Try it ── -->
    <DocSection id="oddity-try" title="Try it">
      <p class="text-sm text-muted-foreground">
        Check a key and send a real request, then see exactly what the gateway answered: the
        status, the timings, the headers and the reply as it streams.
      </p>
      <OddityExplorer ref="explorer" v-model:api-key="pastedKey" :default-model="settings?.defaultModel ?? ''" />
    </DocSection>

    <!-- ── Authentication ── -->
    <DocSection id="oddity-auth" title="Authentication">
      <ul class="list-disc space-y-2 pl-5 text-sm leading-relaxed">
        <li>
          A key looks like <code class="rounded bg-muted px-1 font-mono text-xs">odd_</code> + 8 hex
          characters + <code class="rounded bg-muted px-1 font-mono text-xs">_</code> + 43 more. The
          first part identifies the key; the whole key is the secret.
        </li>
        <li>Send it only in the Authorization header, as a bearer token. A key in the URL is not accepted.</li>
        <li>
          A missing, unknown, disabled, expired or revoked key gets the same 401, so a caller cannot
          tell which.
        </li>
        <li>
          A key works only while the Ground Control user who made it still has the Oddity tab;
          otherwise every call gets 403.
        </li>
        <li>A key cannot sign in to the app, and an app session cannot call the gateway.</li>
      </ul>
    </DocSection>

    <!-- ── Endpoints ── -->
    <DocSection id="oddity-endpoints" title="Endpoints">
      <p class="text-sm text-muted-foreground">
        Paths are relative to the base URL. Calls marked “logged” leave a row in Ground
        Control's request log and count toward the key's calls-a-minute limit.
      </p>
      <div class="overflow-x-auto rounded-lg border bg-card">
        <table class="w-full min-w-144 text-sm">
          <thead class="border-b text-left text-xs text-muted-foreground">
            <tr>
              <th class="px-3 py-2 font-medium">Method</th>
              <th class="px-3 py-2 font-medium">Path</th>
              <th class="px-3 py-2 font-medium">What it does</th>
              <th class="px-3 py-2"><span class="sr-only">Try</span></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="e in GATEWAY_ENDPOINTS" :key="e.method + e.path" class="border-b align-top last:border-0">
              <td class="px-3 py-2 font-mono text-xs">{{ e.method }}</td>
              <td class="whitespace-nowrap px-3 py-2 font-mono text-xs">{{ e.path }}</td>
              <td class="px-3 py-2">
                {{ e.summary }}
                <span v-if="e.recorded" class="ml-1 rounded bg-muted px-1.5 py-0.5 text-xs text-muted-foreground">logged</span>
              </td>
              <td class="px-3 py-1.5 text-right">
                <Button variant="ghost" size="sm" :aria-label="`Try ${e.method} ${e.path}`" @click="tryEndpoint(e.path)">Try</Button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <p class="text-sm leading-relaxed">
        <span class="text-muted-foreground">Closed — 403 for every method:</span>
        <template v-for="(p, i) in CLOSED_ENDPOINTS" :key="p">
          <code class="rounded bg-muted px-1 font-mono text-xs">{{ p }}</code><span v-if="i < CLOSED_ENDPOINTS.length - 1">, </span>
        </template>.
        <span class="text-muted-foreground"> Any other path: 404.</span>
      </p>
    </DocSection>

    <!-- ── What Oddity changes ── -->
    <DocSection id="oddity-changes" title="What Oddity changes">
      <div class="space-y-2">
        <h3 class="font-semibold">In a request</h3>
        <ul class="list-disc space-y-1.5 pl-5 text-sm leading-relaxed">
          <li v-for="c in REQUEST_CHANGES" :key="c">{{ c }}</li>
        </ul>
      </div>
      <div class="space-y-2">
        <h3 class="font-semibold">In a reply</h3>
        <ul class="list-disc space-y-1.5 pl-5 text-sm leading-relaxed">
          <li v-for="c in RESPONSE_NOTES" :key="c">{{ c }}</li>
        </ul>
      </div>
      <div class="space-y-2">
        <h3 class="font-semibold">Limits</h3>
        <ul class="list-disc space-y-1.5 pl-5 text-sm leading-relaxed">
          <li>A request body can be at most {{ BODY_LIMIT_BYTES.toLocaleString() }} bytes (about 28 MB).</li>
          <li>Each key has a calls-a-minute limit, set per key or by the gateway's default.</li>
          <li>Ground Control can limit which models may be called; /api/tags lists only those.</li>
        </ul>
      </div>
    </DocSection>

    <!-- ── Errors ── -->
    <DocSection id="oddity-errors" title="Errors">
      <p class="text-sm leading-relaxed">
        An error the gateway answers itself has the body
        <code class="rounded bg-muted px-1 font-mono text-xs">{"error": "oddity: …"}</code> —
        the shape Ollama's clients already turn into an error.
      </p>
      <div class="overflow-x-auto rounded-lg border bg-card">
        <table class="w-full text-sm">
          <thead class="border-b text-left text-xs text-muted-foreground">
            <tr>
              <th class="w-20 px-3 py-2 font-medium">Status</th>
              <th class="px-3 py-2 font-medium">When</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="e in GATEWAY_ERRORS" :key="e.status" class="border-b align-top last:border-0">
              <td class="px-3 py-2 font-mono">{{ e.status }}</td>
              <td class="px-3 py-2">{{ e.when }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <ul class="list-disc space-y-1.5 pl-5 text-sm leading-relaxed">
        <li v-for="p in PASSTHROUGH_ERRORS" :key="p">{{ p }}</li>
      </ul>
    </DocSection>

    <!-- ── Differences ── -->
    <DocSection id="oddity-differences" title="Differences from Ollama">
      <ul class="list-disc space-y-1.5 pl-5 text-sm leading-relaxed">
        <li v-for="d in DIFFERENCES" :key="d">{{ d }}</li>
      </ul>
    </DocSection>

    <!-- ── Examples ── -->
    <DocSection id="oddity-examples" title="Examples">
      <p class="text-sm text-muted-foreground">
        A key typed into <span class="font-medium text-foreground">Try it</span> fills in the examples too.
      </p>
      <div class="flex flex-wrap gap-1" role="group" aria-label="Example language">
        <button
          v-for="e in EXAMPLES"
          :key="e.id"
          type="button"
          :aria-pressed="exampleId === e.id"
          :class="[
            'rounded-md px-3 py-1.5 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring',
            exampleId === e.id ? 'bg-primary text-primary-foreground shadow-sm' : 'text-muted-foreground hover:bg-muted hover:text-foreground',
          ]"
          @click="exampleId = e.id"
        >
          {{ e.label }}
        </button>
      </div>
      <DocCodeBlock :code="exampleCode" :language="example.label" />
      <p class="text-xs text-muted-foreground">
        Every example on this page is run, unchanged, by the gateway's test suite.
      </p>
    </DocSection>

    <!-- ── Ground Control API (board only) ── -->
    <template v-if="admin">
      <DocSection id="oddity-admin" title="Ground Control API">
        <ul class="list-disc space-y-1.5 pl-5 text-sm leading-relaxed">
          <li>
            Under <code class="rounded bg-muted px-1 font-mono text-xs">/api/oddity</code> on the app,
            with the session token the app holds after sign-in
            (<code class="rounded bg-muted px-1 font-mono text-xs">Authorization: Bearer &lt;session token&gt;</code>,
            from <code class="rounded bg-muted px-1 font-mono text-xs">POST /api/auth/login</code>).
            Gateway keys do not work here.
          </li>
          <li>A user without the Oddity tab gets 403; no token gets 401.</li>
          <li>Keys and requests are yours alone: someone else's id answers 404. Settings are shared by every Ground Control user.</li>
          <li>JSON field names are camelCase. Times are UTC.</li>
        </ul>
        <div class="overflow-x-auto rounded-lg border bg-card">
          <table class="w-full min-w-192 text-sm">
            <thead class="border-b text-left text-xs text-muted-foreground">
              <tr>
                <th class="px-3 py-2 font-medium">Method</th>
                <th class="px-3 py-2 font-medium">Path</th>
                <th class="px-3 py-2 font-medium">What it does</th>
                <th class="px-3 py-2 font-medium">Sends</th>
                <th class="px-3 py-2 font-medium">Returns</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="e in ADMIN_ENDPOINTS" :key="e.method + e.path" class="border-b align-top last:border-0">
                <td class="px-3 py-2 font-mono text-xs">{{ e.method }}</td>
                <td class="whitespace-nowrap px-3 py-2 font-mono text-xs">{{ e.path }}</td>
                <td class="px-3 py-2">
                  {{ e.summary }}
                  <span v-if="e.notes" class="mt-1 block text-xs text-muted-foreground">{{ e.notes }}</span>
                </td>
                <td class="px-3 py-2 font-mono text-xs">{{ e.body ?? (e.query ? `?${e.query}` : '—') }}</td>
                <td class="px-3 py-2 font-mono text-xs">{{ e.response ?? '—' }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </DocSection>

      <DocSection id="oddity-fields" title="Fields">
        <details v-for="name in schemaOrder" :key="name" class="rounded-lg border bg-card">
          <summary class="cursor-pointer px-3 py-2 text-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring">
            <code class="font-mono">{{ name }}</code>
            <span class="text-muted-foreground"> · {{ SCHEMAS[name].length }} fields{{ name.endsWith('Filter') ? ', as query parameters' : '' }}</span>
          </summary>
          <div class="overflow-x-auto border-t">
            <table class="w-full min-w-144 text-sm">
              <tbody>
                <tr v-for="f in SCHEMAS[name]" :key="f.name" class="border-b align-top last:border-0">
                  <td class="whitespace-nowrap px-3 py-2 font-mono text-xs">{{ f.name }}</td>
                  <td class="whitespace-nowrap px-3 py-2 font-mono text-xs text-muted-foreground">{{ f.type }}</td>
                  <td class="px-3 py-2">{{ f.about }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </details>
      </DocSection>
    </template>
  </div>
</template>
