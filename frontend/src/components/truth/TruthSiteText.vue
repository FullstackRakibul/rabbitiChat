<script setup>
/**
 * The global site text (docs/TRUTH_PROMPT.md §5): every fixed string of the
 * public site, Bangla and English side by side per key. Checked here with the
 * public shell's own rules (src/truth/lib/site-text.js) as you type, and again
 * by the server on publish; published as the next version and live for every
 * visitor without a redeploy. The preview is the real public page at 360 px,
 * given this unpublished text in the browser (never sent anywhere).
 */
import { computed, onMounted, ref, watch } from 'vue'
import { RotateCcw } from '@lucide/vue'
import { getTruthContent, publishTruthContent, rollbackTruthContent, saveTruthContentDraft } from '@/services/index.js'
import { Button } from '@/components/ui/button'
import { checkString, lookup, schemaKeys } from '@/truth/lib/site-text.js'
import { formatDhaka } from '@/lib/truth-admin.js'

const state = ref(null)
const doc = ref(null)
const search = ref('')
const filter = ref('all')     // all | missing-en | errors | changed
const note = ref('')
const error = ref('')
const notice = ref('')
const serverProblems = ref([])
const previewLang = ref('bn')
const frame = ref(null)

const keys = computed(() => (state.value ? schemaKeys(state.value.schema) : []))

/** Every key of the schema, from the draft, else the published text, else the default — so the editor always has the full shape. */
function fill(source) {
  const out = { bn: {}, en: {} }
  for (const lang of ['bn', 'en']) {
    for (const { key } of keys.value) {
      const v = lookup(source?.[lang], key)
      const parts = key.split('.')
      let node = out[lang]
      for (const p of parts.slice(0, -1)) node = node[p] ??= {}
      node[parts.at(-1)] = typeof v === 'string' ? v : lookup(state.value.default[lang], key)
    }
  }
  return out
}

async function load() {
  error.value = ''
  try {
    state.value = await getTruthContent('site')
    doc.value = fill(state.value.draft?.text ?? state.value.published?.text ?? state.value.default)
  } catch (e) { error.value = e.message }
}

const get = (lang, key) => lookup(doc.value[lang], key)
function set(lang, key, value) {
  const parts = key.split('.')
  let node = doc.value[lang]
  for (const p of parts.slice(0, -1)) node = node[p]
  node[parts.at(-1)] = value
}

const errorsOf = (k) => ({
  bn: checkString(get('bn', k.key), { ...k, required: true }),
  en: checkString(get('en', k.key), { ...k, required: false }),
})
const published = (lang, key) => lookup(state.value.published?.text?.[lang] ?? state.value.default[lang], key)

const rows = computed(() => {
  if (!doc.value) return []
  const q = search.value.trim().toLowerCase()
  return keys.value.map((k) => ({ ...k, errors: errorsOf(k) })).filter((r) => {
    if (q && !`${r.key} ${get('bn', r.key)} ${get('en', r.key)}`.toLowerCase().includes(q)) return false
    if (filter.value === 'missing-en') return !String(get('en', r.key)).trim()
    if (filter.value === 'errors') return r.errors.bn.length + r.errors.en.length > 0
    if (filter.value === 'changed') return ['bn', 'en'].some((l) => get(l, r.key) !== published(l, r.key))
    return true
  })
})
/** "{time} {count}" — the placeholders a key takes (a template literal in the template would end its mustache early). */
const braces = (list) => list.map((p) => '{' + p + '}').join(' ')
const errorCount = computed(() => keys.value.reduce((n, k) => { const e = errorsOf(k); return n + e.bn.length + e.en.length }, 0))
const missingEn = computed(() => keys.value.filter((k) => !String(get('en', k.key)).trim()).length)

async function saveDraft() {
  error.value = ''; notice.value = ''
  try { await saveTruthContentDraft('site', doc.value); notice.value = 'Draft saved (not public).' } catch (e) { error.value = e.message }
}
async function publish() {
  error.value = ''; notice.value = ''; serverProblems.value = []
  try {
    const r = await publishTruthContent('site', doc.value, note.value || null)
    if (r.ok) { notice.value = `Published as version ${r.version}. Live for every visitor now.`; note.value = ''; await load() }
    else { error.value = r.error; serverProblems.value = r.problems }
  } catch (e) { error.value = e.message }
}
async function rollback(v) {
  if (!confirm(`Publish version ${v} again?`)) return
  error.value = ''; notice.value = ''
  try { const r = await rollbackTruthContent('site', v); notice.value = `Version ${v} is live again (as version ${r.version}).`; await load() } catch (e) { error.value = e.message }
}

// The preview: the public page in a frame, told about this text by message (same origin only).
function sendPreview() {
  frame.value?.contentWindow?.postMessage({ type: 'truth-preview', text: JSON.parse(JSON.stringify(doc.value)) }, location.origin)
}
let timer = null
watch(doc, () => { clearTimeout(timer); timer = setTimeout(sendPreview, 300) }, { deep: true })

onMounted(load)
</script>

<template>
  <section v-if="doc" class="flex flex-col gap-3" data-truth-site-text>
    <div class="flex flex-wrap items-center gap-2">
      <h3 class="flex-1 font-semibold">সাইটের লেখা <span class="text-sm font-normal text-muted-foreground">Site text · live v{{ state.published?.version ?? 0 }}{{ state.draft ? ' · draft saved ' + formatDhaka(state.draft.editedAt) : '' }}</span></h3>
      <Button size="sm" variant="outline" @click="saveDraft">খসড়া <span class="text-xs opacity-70">Save draft</span></Button>
      <input v-model="note" placeholder="What changed (for the history)" class="h-8 rounded-md border border-input bg-transparent px-2 text-sm">
      <Button size="sm" data-truth-publish-text :disabled="errorCount > 0" @click="publish">প্রকাশ <span class="text-xs opacity-70">Publish</span></Button>
    </div>
    <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}
      <span v-for="(p, n) in serverProblems" :key="n" class="block">{{ p.field }}: {{ p.message }}</span></p>
    <p v-if="notice" class="rounded-md border bg-card p-3 text-sm">{{ notice }}</p>
    <p class="text-xs text-muted-foreground">Plain text only: no HTML, links must be https, and only the placeholders a key lists. An empty English value uses the built-in English.</p>

    <div class="grid grid-cols-[minmax(0,1fr)] gap-4 xl:grid-cols-[minmax(0,1fr)_380px]">
      <div class="flex min-w-0 flex-col gap-2">
        <div class="flex flex-wrap gap-2">
          <input v-model="search" type="search" placeholder="Search keys and text" class="h-9 min-w-0 flex-1 basis-40 rounded-md border border-input bg-transparent px-3 text-sm">
          <select v-model="filter" class="h-9 rounded-md border border-input bg-transparent px-2 text-sm">
            <option value="all">All ({{ keys.length }})</option>
            <option value="missing-en">Missing in English ({{ missingEn }})</option>
            <option value="errors">With problems ({{ errorCount }})</option>
            <option value="changed">Changed from live</option>
          </select>
        </div>
        <ul class="grid gap-2">
          <li v-for="r in rows" :key="r.key" class="rounded-md border bg-card p-2" :data-site-key="r.key">
            <div class="flex flex-wrap items-baseline gap-2 text-xs">
              <span class="font-mono font-medium">{{ r.key }}</span>
              <span v-if="r.placeholders.length" class="text-muted-foreground">{{ braces(r.placeholders) }}</span>
            </div>
            <div class="mt-1 grid gap-2 md:grid-cols-2">
              <label class="text-xs text-muted-foreground">বাংলা
                <textarea :value="get('bn', r.key)" rows="1" lang="bn" class="block min-h-9 w-full rounded-md border bg-transparent px-2 py-1 text-sm text-foreground"
                  :class="r.errors.bn.length ? 'border-destructive' : 'border-input'" @input="set('bn', r.key, $event.target.value)" />
                <span v-for="m in r.errors.bn" :key="m" class="block text-destructive">{{ m }}</span>
              </label>
              <label class="text-xs text-muted-foreground">English
                <textarea :value="get('en', r.key)" rows="1" lang="en" class="block min-h-9 w-full rounded-md border bg-transparent px-2 py-1 text-sm text-foreground"
                  :class="r.errors.en.length ? 'border-destructive' : 'border-input'" @input="set('en', r.key, $event.target.value)" />
                <span v-for="m in r.errors.en" :key="m" class="block text-destructive">{{ m }}</span>
              </label>
            </div>
          </li>
        </ul>
      </div>

      <aside class="flex flex-col gap-3">
        <div class="rounded-md border bg-card p-2">
          <div class="mb-2 flex items-center gap-2 text-sm">
            <span class="flex-1 font-medium">প্রিভিউ <span class="font-normal text-muted-foreground">Preview at 360 px</span></span>
            <button v-for="l in ['bn', 'en']" :key="l" type="button" class="rounded px-2 py-0.5" :class="previewLang === l ? 'bg-secondary' : ''" @click="previewLang = l">{{ l }}</button>
          </div>
          <iframe ref="frame" :key="previewLang" :src="`/truth?lang=${previewLang}&preview=1`" title="Preview of the public page" width="360" height="640"
            class="mx-auto block max-w-full rounded border bg-white" @load="sendPreview" />
        </div>
        <div class="rounded-md border bg-card p-3 text-sm">
          <h4 class="font-semibold">ইতিহাস <span class="font-normal text-muted-foreground">Versions</span></h4>
          <p v-if="!state.history.length" class="text-muted-foreground">Only the built-in text so far.</p>
          <ul class="mt-1 grid gap-1">
            <li v-for="h in state.history" :key="h.version" class="flex items-center gap-2">
              <span class="font-mono">v{{ h.version }}</span>
              <span class="min-w-0 flex-1 truncate text-muted-foreground">{{ formatDhaka(h.editedAt) }} · {{ h.editedBy }}{{ h.note ? ' · ' + h.note : '' }}</span>
              <Button v-if="h.version !== state.published?.version" size="sm" variant="ghost" :aria-label="`Roll back to version ${h.version}`" @click="rollback(h.version)"><RotateCcw class="size-4" /></Button>
              <span v-else class="text-xs text-green-700 dark:text-green-400">live</span>
            </li>
          </ul>
        </div>
      </aside>
    </div>
  </section>
  <p v-else-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>
</template>
