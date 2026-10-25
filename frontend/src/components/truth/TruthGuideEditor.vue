<script setup>
/**
 * One fix guide (docs/TRUTH_PROMPT.md §6): its fields, variables, platforms, steps
 * (templates, §6.2) and sources; the variant grid with the safety check's findings;
 * the 4-item checklist; publishing. The simple workflow (§6.3): one person can
 * write and publish, and publishing needs, since the last edit, the grid opened,
 * no block-level finding, every warning acknowledged with a note, and the 4 ticks.
 * Saving resets the grid and the ticks; the server decides all of it.
 */
import { computed, onMounted, ref } from 'vue'
import { ArrowLeft, Plus, X } from '@lucide/vue'
import {
  addTruthGuideCorrection, checkTruthGuide, createTruthGuide, deleteTruthGuide, getTruthGuide, getTruthIssue, listTruthIssues,
  listTruthPlatforms, listTruthTechs, publishTruthGuide, setTruthGuideChecklist, updateTruthGuide, verifyTruthGuideNumber, withdrawTruthGuide,
} from '@/services/index.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import TruthVariantGrid from '@/components/truth/TruthVariantGrid.vue'
import { formatDhaka, SOURCE_KINDS, STATUS_BADGE } from '@/lib/truth-admin.js'

const props = defineProps({
  /** A guide id, or "new". */
  id: { type: [Number, String], required: true },
  can: { type: Object, required: true },
})
const emit = defineEmits(['close', 'created'])

const KINDS = ['note', 'command', 'config', 'verify', 'panel', 'waf_rule', 'warning', 'undo']
const PHASES = ['before_patch', 'after_patch', 'hardening']
const TEMPLATE_HELP = '{{php}} · {{#if php>=8.5}} … {{else}} … {{/if}}'
const field = 'h-9 w-full rounded-md border border-input bg-transparent px-2 text-sm'
const area = 'min-h-16 w-full rounded-md border border-input bg-transparent px-2 py-1 text-sm'
const mono = 'min-h-20 w-full rounded-md border border-input bg-transparent px-2 py-1 font-mono text-xs'

const data = ref(null)            // the server's view of the guide
const form = ref(null)
const techs = ref([])
const platforms = ref({ groups: [], platforms: [] })
const issues = ref([])
const issueItems = ref([])
const issueId = ref(null)
const check = ref(null)
const gridOpen = ref(false)
const error = ref('')
const notice = ref('')
const ticks = ref({ tested: false, verify: false, undo: false, sources: false })
const correction = ref({ textBn: '', textEn: '' })
const notes = ref({})
const isNew = computed(() => props.id === 'new')

function blank() {
  return {
    slug: '', itemId: null, techId: techs.value[0]?.id ?? 0, titleBn: '', titleEn: '', phase: 'before_patch', introBn: '', introEn: '',
    variables: [], platforms: [], backupFirst: true, authorName: '', basedOnSourceOrder: null, steps: [], sources: [],
  }
}

function fromServer(d) {
  const g = d.guide
  return {
    slug: g.slug, itemId: g.itemId, techId: g.techId, titleBn: g.titleBn, titleEn: g.titleEn ?? '', phase: g.phase,
    introBn: g.introBn ?? '', introEn: g.introEn ?? '', backupFirst: g.backupFirst, authorName: g.authorName, basedOnSourceOrder: g.basedOnSourceOrder,
    platforms: [...g.platforms],
    variables: Object.entries(g.variables ?? {}).map(([name, v]) => ({ name, options: v.options.join(', '), default: v.default ?? '', labelBn: v.labelBn ?? '', labelEn: v.labelEn ?? '' })),
    steps: d.steps.map((s) => ({ ...s, platform: s.platform ?? 'any' })),
    sources: d.sources.map((s) => ({ ...s })),
  }
}

function payload() {
  const f = form.value
  const variables = {}
  for (const v of f.variables) {
    if (!v.name.trim()) continue
    const options = v.options.split(',').map((o) => o.trim()).filter(Boolean)
    variables[v.name.trim()] = { options, default: v.default || options[0], ...(v.labelBn ? { labelBn: v.labelBn } : {}), ...(v.labelEn ? { labelEn: v.labelEn } : {}) }
  }
  const nul = (s) => (String(s ?? '').trim() ? s : null)
  return {
    slug: f.slug, itemId: f.itemId || null, techId: Number(f.techId), titleBn: f.titleBn, titleEn: nul(f.titleEn), phase: f.phase,
    introBn: nul(f.introBn), introEn: nul(f.introEn), variables, platforms: f.platforms, backupFirst: f.backupFirst, authorName: f.authorName,
    basedOnSourceOrder: f.basedOnSourceOrder ? Number(f.basedOnSourceOrder) : null,
    steps: f.steps.map((s) => ({
      kind: s.kind, platform: s.platform || 'any', titleBn: nul(s.titleBn), titleEn: nul(s.titleEn), bodyBn: nul(s.bodyBn), bodyEn: nul(s.bodyEn),
      code: nul(s.code), language: nul(s.language), expectedOutput: nul(s.expectedOutput), condition: nul(s.condition), sideEffectsBn: nul(s.sideEffectsBn), sideEffectsEn: nul(s.sideEffectsEn),
    })),
    sources: f.sources.map((s) => ({ url: s.url, publisher: s.publisher, author: nul(s.author), title: s.title, publishedOn: nul(s.publishedOn), kind: s.kind, fetchedText: s.fetchedText ?? '' })),
  }
}

async function load() {
  error.value = ''
  try {
    const [t, p, i] = await Promise.all([listTruthTechs(), listTruthPlatforms(), listTruthIssues()])
    techs.value = t.techs; platforms.value = p; issues.value = i.issues
    if (isNew.value) { form.value = blank(); return }
    data.value = await getTruthGuide(props.id)
    form.value = fromServer(data.value)
    const c = data.value.guide.checklist
    ticks.value = c?.current ? { tested: c.tested, verify: c.verify, undo: c.undo, sources: c.sources } : { tested: false, verify: false, undo: false, sources: false }
    check.value = await checkTruthGuide(props.id)
  } catch (e) { error.value = e.message }
}

async function pickIssue() {
  issueItems.value = issueId.value ? (await getTruthIssue(issueId.value)).items : []
}

async function act(fn, done) {
  error.value = ''; notice.value = ''
  try { await fn(); notice.value = done; await load() } catch (e) { error.value = e.message }
}
async function save() {
  error.value = ''; notice.value = ''
  try {
    if (isNew.value) { const g = await createTruthGuide(payload()); emit('created', g.id); return }
    await updateTruthGuide(props.id, payload())
    notice.value = 'Saved. Open the variant grid and tick the checklist again before publishing.'
    gridOpen.value = false
    await load()
  } catch (e) { error.value = e.message }
}
const saveTicks = () => act(() => setTruthGuideChecklist(props.id, ticks.value), 'Checklist saved.')
async function publish() {
  error.value = ''; notice.value = ''
  try {
    const r = await publishTruthGuide(props.id)
    if (r.ok) notice.value = `Published ${formatDhaka(r.publishedAt)}.`
    else error.value = r.error
    await load()
  } catch (e) { error.value = e.message }
}
const withdraw = () => { if (confirm('Take this guide off the public site?')) act(() => withdrawTruthGuide(props.id), 'Withdrawn.') }
async function remove() {
  if (!confirm('Delete this draft guide?')) return
  try { await deleteTruthGuide(props.id); emit('close') } catch (e) { error.value = e.message }
}
const addCorrection = () => act(() => addTruthGuideCorrection(props.id, { textBn: correction.value.textBn, textEn: correction.value.textEn || null }), 'Correction added.')
const verify = (token) => act(() => verifyTruthGuideNumber(props.id, token, notes.value[token] ?? ''), 'Verified by hand.')

const addStep = () => form.value.steps.push({ kind: 'command', platform: 'any', titleBn: '', titleEn: '', bodyBn: '', bodyEn: '', code: '', language: '', expectedOutput: '', condition: '', sideEffectsBn: '', sideEffectsEn: '' })
const moveStep = (n, d) => { const s = form.value.steps; const [x] = s.splice(n, 1); s.splice(n + d, 0, x) }
const addVariable = () => form.value.variables.push({ name: '', options: '', default: '', labelBn: '', labelEn: '' })
const addSource = () => form.value.sources.push({ url: 'https://', publisher: '', author: '', title: '', publishedOn: '', kind: 'official', fetchedText: '' })
const togglePlatform = (key) => { const p = form.value.platforms; form.value.platforms = p.includes(key) ? p.filter((x) => x !== key) : [...p, key] }
const grouped = computed(() => platforms.value.groups.map((g) => ({ ...g, platforms: platforms.value.platforms.filter((p) => p.group === g.key) })))

onMounted(load)
</script>

<template>
  <section v-if="form" class="flex flex-col gap-4" data-truth-guide-editor>
    <div class="flex flex-wrap items-center gap-2">
      <Button size="sm" variant="ghost" @click="emit('close')"><ArrowLeft class="size-4" /> গাইড <span class="text-xs opacity-70">Guides</span></Button>
      <span class="font-mono text-sm">{{ form.slug || 'new guide' }}</span>
      <template v-if="data">
        <Badge :variant="STATUS_BADGE[data.guide.status]">{{ data.guide.status }}</Badge>
        <Badge :variant="data.guide.gridCurrent ? 'secondary' : 'outline'">{{ data.guide.gridCurrent ? 'grid opened since the last edit' : 'grid not opened since the last edit' }}</Badge>
        <span class="flex-1" />
        <a v-if="data.guide.status === 'published'" :href="`/truth/guide/${data.guide.slug}?lang=bn`" target="_blank" rel="noopener" class="text-sm underline">public page ↗</a>
        <Button v-if="can.publish" size="sm" data-truth-publish-guide :disabled="!check?.ok" @click="publish">প্রকাশ <span class="text-xs opacity-70">{{ data.guide.status === 'published' ? 'Publish again' : 'Publish' }}</span></Button>
        <Button v-if="can.publish && data.guide.status === 'published'" size="sm" variant="outline" @click="withdraw">Withdraw</Button>
        <Button v-if="!data.guide.firstPublishedAt" size="sm" variant="ghost" class="text-destructive" @click="remove">Delete</Button>
      </template>
    </div>
    <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>
    <p v-if="notice" class="rounded-md border bg-card p-3 text-sm">{{ notice }}</p>

    <section v-if="check" class="rounded-lg border p-4" :class="check.ok ? 'bg-card' : 'border-destructive/50 bg-destructive/5'" data-truth-guide-checklist>
      <h4 class="font-semibold">প্রকাশের আগে <span class="text-sm font-normal text-muted-foreground">Before publishing</span></h4>
      <p v-if="check.ok" class="mt-1 text-sm text-green-700 dark:text-green-400">✓ Ready: grid opened, nothing blocked, warnings acknowledged, checklist ticked.</p>
      <ul v-else class="mt-2 grid gap-1 text-sm"><li v-for="(p, n) in check.problems" :key="n" class="text-destructive">{{ p.message }}</li></ul>
      <div v-if="can.publish" class="mt-3 grid gap-1 text-sm" data-truth-ticks>
        <label class="flex gap-2"><input v-model="ticks.tested" type="checkbox"> Commands tested on at least one real or test server</label>
        <label class="flex gap-2"><input v-model="ticks.verify" type="checkbox"> The verify step matches what the server prints</label>
        <label class="flex gap-2"><input v-model="ticks.undo" type="checkbox"> The undo step works</label>
        <label class="flex gap-2"><input v-model="ticks.sources" type="checkbox"> Sources are linked</label>
        <Button size="sm" variant="outline" class="justify-self-start" @click="saveTicks">Save the checklist</Button>
      </div>
      <table v-if="check.numbers.length" class="mt-3 w-full text-xs">
        <tbody>
          <tr v-for="n in check.numbers" :key="n.token + n.kind" class="border-t">
            <td class="py-1 pr-2 font-mono" :class="n.ok ? '' : 'text-destructive'">{{ n.token }}</td>
            <td class="py-1 pr-2 text-muted-foreground">{{ n.kind }} · {{ n.fields.join(', ') }}</td>
            <td class="py-1">
              <span v-if="n.found">✓ in a source</span><span v-else-if="n.handNote">by hand: “{{ n.handNote }}”</span>
              <span v-else class="flex gap-1"><input v-model="notes[n.token]" placeholder="How it was verified" :class="field" class="h-7"><Button size="sm" variant="outline" @click="verify(n.token)">By hand</Button></span>
            </td>
          </tr>
        </tbody>
      </table>
    </section>

    <div v-if="data" class="flex flex-wrap gap-2">
      <Button size="sm" variant="outline" data-truth-open-grid @click="gridOpen = !gridOpen">{{ gridOpen ? 'Hide' : 'Open' }} the variant grid</Button>
    </div>
    <TruthVariantGrid v-if="gridOpen && data" :id="Number(id)" :can-ack="can.publish" @changed="load" />

    <form class="grid gap-3 rounded-lg border bg-card p-4 sm:grid-cols-3" @submit.prevent="save">
      <label class="text-sm">Slug<input v-model="form.slug" :class="field" :disabled="!!data?.guide.firstPublishedAt"></label>
      <label class="text-sm">Technology<select v-model="form.techId" :class="field"><option v-for="t in techs" :key="t.id" :value="t.id">{{ t.name }}</option></select></label>
      <label class="text-sm">Phase<select v-model="form.phase" :class="field"><option v-for="p in PHASES" :key="p">{{ p }}</option></select></label>
      <label class="text-sm sm:col-span-3">শিরোনাম (বাংলা) <span class="text-muted-foreground">Title, Bangla — required</span><input v-model="form.titleBn" :class="field"></label>
      <label class="text-sm sm:col-span-3">Title (English)<input v-model="form.titleEn" :class="field"></label>
      <label class="text-sm sm:col-span-3">ভূমিকা (বাংলা) <span class="text-muted-foreground">Why, and which versions</span><textarea v-model="form.introBn" :class="area" /></label>
      <label class="text-sm sm:col-span-3">Intro (English)<textarea v-model="form.introEn" :class="area" /></label>
      <label class="text-sm">Byline<input v-model="form.authorName" :class="field"></label>
      <label class="flex items-center gap-2 text-sm"><input v-model="form.backupFirst" type="checkbox"> “Back up first” box</label>
      <label class="text-sm">Based on source #<input v-model="form.basedOnSourceOrder" type="number" min="1" :class="field"></label>
      <div class="grid gap-2 text-sm sm:col-span-3 sm:grid-cols-2">
        <label>For the item of issue<select v-model="issueId" :class="field" @change="pickIssue"><option :value="null">—</option><option v-for="i in issues" :key="i.id" :value="i.id">{{ i.slug }}</option></select></label>
        <label>Item (id {{ form.itemId ?? 'none' }})<select v-model="form.itemId" :class="field"><option :value="null">— none —</option><option v-for="i in issueItems" :key="i.id" :value="i.id">{{ i.headlineBn }}</option></select></label>
      </div>

      <fieldset class="sm:col-span-3"><legend class="text-sm font-semibold">Platforms</legend>
        <div v-for="g in grouped" :key="g.key" class="mt-1 flex flex-wrap items-center gap-x-3 text-sm">
          <span class="w-36 text-xs text-muted-foreground">{{ g.en }}</span>
          <label v-for="p in g.platforms" :key="p.key" class="flex items-center gap-1"><input type="checkbox" :checked="form.platforms.includes(p.key)" :data-platform="p.key" @change="togglePlatform(p.key)"> {{ p.nameEn }}</label>
        </div>
      </fieldset>

      <fieldset class="grid gap-2 sm:col-span-3"><legend class="flex w-full items-center gap-2 text-sm font-semibold">Variables <span class="font-normal text-muted-foreground">each a selector; every combination is a variant</span>
        <Button type="button" size="sm" variant="ghost" @click="addVariable"><Plus class="size-4" /></Button></legend>
        <div v-for="(v, n) in form.variables" :key="n" class="grid gap-2 sm:grid-cols-6">
          <input v-model="v.name" placeholder="name, e.g. php" :class="field" class="font-mono">
          <input v-model="v.options" placeholder="8.2, 8.3, 8.4" :class="field" class="sm:col-span-2">
          <input v-model="v.default" placeholder="default" :class="field">
          <input v-model="v.labelBn" placeholder="লেবেল" :class="field">
          <span class="flex gap-1"><input v-model="v.labelEn" placeholder="label" :class="field"><button type="button" aria-label="Remove variable" @click="form.variables.splice(n, 1)"><X class="size-4" /></button></span>
        </div>
      </fieldset>

      <fieldset class="grid gap-2 sm:col-span-3"><legend class="flex w-full items-center gap-2 text-sm font-semibold">Steps <span class="font-normal text-muted-foreground">code and expected output are templates: {{ TEMPLATE_HELP }}</span>
          <Button type="button" size="sm" variant="ghost" data-truth-add-step @click="addStep"><Plus class="size-4" /></Button></legend>
        <div v-for="(s, n) in form.steps" :key="n" class="grid gap-2 rounded-md border p-3 sm:grid-cols-6" :data-step-row="n + 1">
          <span class="text-sm font-semibold">{{ n + 1 }}.</span>
          <select v-model="s.kind" :class="field"><option v-for="k in KINDS" :key="k">{{ k }}</option></select>
          <select v-model="s.platform" :class="field"><option value="any">any platform</option><option v-for="p in form.platforms" :key="p">{{ p }}</option></select>
          <input v-model="s.condition" placeholder="only if, e.g. php>=8.5" :class="field" class="font-mono">
          <input v-model="s.language" placeholder="language (default: the platform's)" :class="field">
          <span class="flex justify-end gap-1">
            <button type="button" :disabled="n === 0" aria-label="Move up" @click="moveStep(n, -1)">↑</button>
            <button type="button" :disabled="n === form.steps.length - 1" aria-label="Move down" @click="moveStep(n, 1)">↓</button>
            <button type="button" aria-label="Remove step" @click="form.steps.splice(n, 1)"><X class="size-4" /></button>
          </span>
          <input v-model="s.titleBn" placeholder="শিরোনাম (বাংলা)" :class="field" class="sm:col-span-3">
          <input v-model="s.titleEn" placeholder="Title (English)" :class="field" class="sm:col-span-3">
          <textarea v-model="s.bodyBn" placeholder="বিবরণ (বাংলা)" :class="area" class="sm:col-span-3" />
          <textarea v-model="s.bodyEn" placeholder="Body (English)" :class="area" class="sm:col-span-3" />
          <textarea v-model="s.code" placeholder="code (template)" :class="mono" class="sm:col-span-6" spellcheck="false" />
          <textarea v-if="s.kind === 'verify'" v-model="s.expectedOutput" placeholder="expected output (template)" :class="mono" class="sm:col-span-6" spellcheck="false" />
          <textarea v-model="s.sideEffectsBn" placeholder="যা ভাঙতে পারে (বাংলা)" :class="area" class="sm:col-span-3" />
          <textarea v-model="s.sideEffectsEn" placeholder="What this may break (English)" :class="area" class="sm:col-span-3" />
        </div>
      </fieldset>

      <fieldset class="grid gap-2 sm:col-span-3"><legend class="flex w-full items-center gap-2 text-sm font-semibold">Sources <span class="font-normal text-muted-foreground">public only; the text is for the number check, never published</span>
        <Button type="button" size="sm" variant="ghost" @click="addSource"><Plus class="size-4" /></Button></legend>
        <div v-for="(s, n) in form.sources" :key="n" class="grid gap-2 rounded-md border p-3 sm:grid-cols-6">
          <span class="font-mono text-sm sm:col-span-6">[{{ n + 1 }}] <button type="button" class="float-right" aria-label="Remove source" @click="form.sources.splice(n, 1)"><X class="size-4" /></button></span>
          <input v-model="s.url" placeholder="https://…" :class="field" class="sm:col-span-3">
          <input v-model="s.publisher" placeholder="Publisher" :class="field" class="sm:col-span-2">
          <select v-model="s.kind" :class="field"><option v-for="k in SOURCE_KINDS" :key="k">{{ k }}</option></select>
          <input v-model="s.title" placeholder="Title" :class="field" class="sm:col-span-3">
          <input v-model="s.author" placeholder="Author (optional)" :class="field" class="sm:col-span-2">
          <input v-model="s.publishedOn" type="date" :class="field">
          <textarea v-model="s.fetchedText" placeholder="The source's public text" :class="area" class="sm:col-span-6" />
        </div>
      </fieldset>
      <div class="sm:col-span-3"><Button type="submit" size="sm" data-truth-save-guide>{{ isNew ? 'Create the draft' : 'Save' }}</Button></div>
    </form>

    <section v-if="data" class="rounded-md border p-3 text-sm">
      <h5 class="font-semibold">সংশোধন <span class="font-normal text-muted-foreground">Corrections — needed when published words change</span></h5>
      <ul class="mt-1 grid gap-1"><li v-for="(c, n) in data.corrections" :key="n" class="text-muted-foreground">{{ formatDhaka(c.at) }} · {{ c.by }} · {{ c.textBn }}</li></ul>
      <form class="mt-2 grid gap-2 sm:grid-cols-3" @submit.prevent="addCorrection">
        <input v-model="correction.textBn" required placeholder="কী সংশোধন হলো (বাংলা)" :class="field">
        <input v-model="correction.textEn" placeholder="What was corrected (English)" :class="field">
        <Button type="submit" size="sm" variant="outline" class="justify-self-start">Add correction</Button>
      </form>
    </section>
  </section>
  <p v-else-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>
</template>
