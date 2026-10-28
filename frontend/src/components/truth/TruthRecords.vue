<script setup>
// রেকর্ড বিভাগ · Records (docs/TRUTH_PROMPT_P5.md §5): the collector's sources and their health, the
// inbox (news, community and flagged rows wait here for a person), the suggestions (possible urgent,
// version line changed — never acted on automatically) and the audit of every decision.
// A cluster opens in a panel: its entries, a model's draft summary, and "create item" (a draft item;
// it goes public only when a person publishes its issue).
import { onMounted, ref } from 'vue'
import {
  decideRecordsSuggestion, draftRecordsDigest, dropRecordsUrl, getRecordsCluster, getRecordsInbox, itemFromRecordsCluster,
  listRecordsAudit, listRecordsSources, listRecordsSuggestions, listTruthIssues, removeRecordsOverride, runRecordsSource,
  saveRecordsSource, setRecordsSourceEnabled, setRecordsVisibility, testRecordsSource,
} from '@/services/index.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { formatDhaka } from '@/lib/truth-admin.js'

defineProps({ can: { type: Object, required: true } })
const emit = defineEmits(['open-issue'])

const panel = ref('sources')
const error = ref('')
const notice = ref('')
const sources = ref(null)
const inbox = ref(null)
const suggestions = ref([])
const audit = ref([])
const tests = ref({})
const dropUrl = ref('')
const cluster = ref(null)
const issues = ref([])
const itemIssue = ref(null)
const busy = ref('')
const editing = ref('')
const hideNote = ref({})
const madeIn = ref(null)

const PANELS = [['sources', 'উৎস', 'Sources'], ['inbox', 'ইনবক্স', 'Inbox'], ['suggestions', 'পরামর্শ', 'Suggestions'], ['audit', 'হিসাব', 'Audit']]

async function run(label, fn) {
  error.value = ''; notice.value = ''; busy.value = label
  try { return await fn() } catch (e) { error.value = e.message } finally { busy.value = '' }
}
const load = {
  sources: async () => { sources.value = await listRecordsSources() },
  inbox: async () => { inbox.value = await getRecordsInbox() },
  suggestions: async () => { suggestions.value = (await listRecordsSuggestions()).suggestions },
  audit: async () => { audit.value = (await listRecordsAudit()).audit },
}
const show = (p) => { panel.value = p; run('load', load[p]) }
onMounted(() => show('sources'))

const health = (s) => s.health
const stateBadge = (s) => (s.health.flagged ? 'destructive' : s.health.lastError && s.health.failures ? 'outline' : 'secondary')

async function test(id) { const r = await run(`test:${id}`, () => testRecordsSource(id)); if (r) tests.value = { ...tests.value, [id]: r } }
async function collect(id) { const r = await run(`run:${id}`, () => runRecordsSource(id)); if (r) { tests.value = { ...tests.value, [id]: r }; await load.sources() } }
async function toggle(s) { await run('toggle', () => setRecordsSourceEnabled(s.definition.id, !s.definition.enabled)); await load.sources() }
function edit(s) { editing.value = JSON.stringify(s?.definition ?? { id: '', name: '', kind: 'news', adapter: 'rss-atom', url: 'https://', tech: [], tier: 'news', everyMinutes: 60 }, null, 2) }
async function saveEdit() {
  let def
  try { def = JSON.parse(editing.value) } catch { error.value = 'That is not valid JSON.'; return }
  const ok = await run('save', () => saveRecordsSource(def))
  if (ok !== undefined) { editing.value = ''; await load.sources() }
}
async function unoverride(id) { await run('unoverride', () => removeRecordsOverride(id)); await load.sources() }

async function drop() {
  const r = await run('drop', () => dropRecordsUrl(dropUrl.value))
  if (r) { notice.value = `Saved to the inbox (entry ${r.id}). The page text is kept privately for the number check.`; dropUrl.value = ''; await load.inbox() }
}
async function visibility(e, v) {
  await run(`vis:${e.id}`, () => setRecordsVisibility(e.id, v, hideNote.value[e.id] ?? null))
  if (!error.value) { await load.inbox(); if (cluster.value) await openCluster(cluster.value.clusterId) }
}
async function decide(s, d) { await run(`sug:${s.id}`, () => decideRecordsSuggestion(s.id, d)); await load.suggestions() }

async function openCluster(id) {
  cluster.value = await run('cluster', () => getRecordsCluster(id)) ?? cluster.value
  if (!issues.value.length) issues.value = (await listTruthIssues()).issues ?? []
}
async function digest() { await run('digest', () => draftRecordsDigest(cluster.value.clusterId)); await openCluster(cluster.value.clusterId) }
async function makeItem(digestId) {
  if (!itemIssue.value) { error.value = 'Choose the issue the draft item goes into.'; return }
  const r = await run('item', () => itemFromRecordsCluster(cluster.value.clusterId, { issueId: itemIssue.value, digestId: digestId ?? null }))
  if (r) { notice.value = `Draft item ${r.id} created. Review it in the issue, then publish the issue as usual.`; madeIn.value = itemIssue.value; await openCluster(cluster.value.clusterId) }
}
</script>

<template>
  <section class="flex flex-col gap-4" data-truth-records>
    <div class="flex flex-wrap items-center gap-2">
      <h3 class="flex-1 font-semibold">রেকর্ড বিভাগ <span class="text-sm font-normal text-muted-foreground">Records Department · automatic collection</span></h3>
      <nav class="flex flex-wrap gap-1" aria-label="Records panels">
        <button v-for="[key, bn, en] in PANELS" :key="key" type="button" :data-records-panel="key"
          class="rounded-md px-2.5 py-1 text-sm" :class="panel === key ? 'bg-secondary text-secondary-foreground' : 'text-muted-foreground hover:bg-accent'"
          @click="show(key)">{{ bn }} <span class="text-xs opacity-70">{{ en }}</span></button>
      </nav>
    </div>
    <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive" data-records-error>{{ error }}</p>
    <p v-if="notice" class="rounded-md border bg-card p-3 text-sm" data-records-notice>{{ notice }}
      <Button v-if="madeIn" size="sm" variant="outline" class="ml-2" @click="emit('open-issue', madeIn)">Open the issue</Button></p>

    <form class="flex flex-wrap gap-2 rounded-lg border bg-card p-3" @submit.prevent="drop">
      <label class="min-w-0 flex-1 text-sm">লিংক জমা <span class="text-xs text-muted-foreground">URL drop — a page with no feed. Fetched once (robots.txt honoured), text kept privately, title and link to the inbox.</span>
        <input v-model="dropUrl" type="url" required placeholder="https://" class="mt-1 h-9 w-full rounded-md border border-input bg-transparent px-2" data-records-drop>
      </label>
      <Button type="submit" size="sm" class="self-end" :disabled="busy === 'drop'">Fetch to inbox</Button>
    </form>

    <!-- Sources -->
    <template v-if="panel === 'sources' && sources">
      <p v-for="e in sources.loadErrors" :key="e" class="rounded-md border border-destructive/40 bg-destructive/10 p-2 text-sm text-destructive">{{ sources.file }}: {{ e }}</p>
      <div class="flex justify-end"><Button v-if="can.publish" size="sm" variant="outline" @click="edit(null)">Add a source</Button></div>
      <div v-if="editing" class="rounded-lg border bg-card p-3">
        <p class="text-sm">Definition (JSON). Checked like the file's; at least every 5 minutes; https only.</p>
        <textarea v-model="editing" rows="12" class="mt-2 w-full rounded-md border border-input bg-transparent p-2 font-mono text-xs" data-records-editor />
        <div class="mt-2 flex gap-2"><Button size="sm" :disabled="busy === 'save'" @click="saveEdit">Save override</Button><Button size="sm" variant="ghost" @click="editing = ''">Cancel</Button></div>
      </div>
      <ul class="grid gap-2">
        <li v-for="s in sources.sources" :key="s.definition.id" class="rounded-lg border bg-card p-3" :data-records-source="s.definition.id">
          <div class="flex flex-wrap items-center gap-2">
            <span class="font-medium">{{ s.definition.name }}</span>
            <span class="font-mono text-xs text-muted-foreground">{{ s.definition.id }}</span>
            <Badge variant="outline">{{ s.definition.kind }}</Badge>
            <Badge variant="outline">{{ s.definition.tier }}</Badge>
            <Badge variant="outline">{{ s.definition.publish === 'auto' ? 'auto to the wire' : 'to the inbox' }}</Badge>
            <Badge :variant="stateBadge(s)">{{ health(s).flagged ? 'stopped: 10 failures' : s.definition.enabled ? 'on' : 'off' }}</Badge>
            <Badge v-if="s.overridden" variant="outline">admin override</Badge>
          </div>
          <p class="mt-1 break-all font-mono text-xs text-muted-foreground">{{ s.definition.adapter }} · {{ s.definition.url || '(editor drops)' }} · every {{ s.definition.everyMinutes }} min</p>
          <p class="mt-1 text-xs text-muted-foreground">
            last success {{ formatDhaka(health(s).lastSuccess) || '—' }} · {{ health(s).entries24h }} new in 24 h · next {{ formatDhaka(health(s).nextRun) || 'soon' }}
            <template v-if="health(s).failures"> · {{ health(s).failures }} failure(s) in a row</template>
          </p>
          <p v-if="health(s).lastError" class="mt-1 text-xs text-destructive">last error {{ formatDhaka(health(s).lastErrorAt) }}: {{ health(s).lastError }}</p>
          <div class="mt-2 flex flex-wrap gap-2">
            <Button size="sm" variant="outline" :disabled="busy === `test:${s.definition.id}`" :data-records-test="s.definition.id" @click="test(s.definition.id)">Test now</Button>
            <Button v-if="can.publish" size="sm" variant="outline" :disabled="busy === `run:${s.definition.id}`" @click="collect(s.definition.id)">Collect now</Button>
            <Button v-if="can.publish" size="sm" variant="ghost" :data-records-toggle="s.definition.id" @click="toggle(s)">{{ s.definition.enabled ? 'Switch off' : health(s).flagged ? 'Switch on (clears the failures)' : 'Switch on' }}</Button>
            <Button v-if="can.publish" size="sm" variant="ghost" @click="edit(s)">Edit</Button>
            <Button v-if="can.publish && s.overridden" size="sm" variant="ghost" @click="unoverride(s.definition.id)">Back to the file</Button>
          </div>
          <div v-if="tests[s.definition.id]" class="mt-2 rounded-md bg-muted p-2 text-xs" :data-records-result="s.definition.id">
            <p>{{ tests[s.definition.id].ok ? 'OK' : 'Failed' }} · HTTP {{ tests[s.definition.id].status }} · {{ tests[s.definition.id].ms }} ms ·
              {{ tests[s.definition.id].notModified ? 'not modified (304)' : `${tests[s.definition.id].parsed} entries` }}
              <template v-if="tests[s.definition.id].added || tests[s.definition.id].updated"> · {{ tests[s.definition.id].added }} new, {{ tests[s.definition.id].updated }} updated</template>
              <template v-if="tests[s.definition.id].error"> · {{ tests[s.definition.id].error }}</template></p>
            <ul v-if="tests[s.definition.id].preview" class="mt-1 list-disc pl-4">
              <li v-for="p in tests[s.definition.id].preview" :key="p.url + p.title">{{ p.title }} <span class="text-muted-foreground">{{ formatDhaka(p.at) }} {{ p.state }}</span></li>
            </ul>
          </div>
        </li>
      </ul>
    </template>

    <!-- Inbox -->
    <template v-if="panel === 'inbox' && inbox">
      <p v-if="!inbox.entries.length && !inbox.flagged.length" class="rounded-lg border bg-card p-4 text-sm text-muted-foreground">ইনবক্স খালি। <span class="opacity-70">Nothing waiting.</span></p>
      <div v-if="inbox.flagged.length" class="rounded-lg border border-amber-500/50 bg-card p-3">
        <p class="text-sm font-semibold">Sources differ <span class="font-normal text-muted-foreground">— clusters whose sources give different numbers</span></p>
        <ul class="mt-1 text-sm"><li v-for="f in inbox.flagged" :key="f.clusterId"><button type="button" class="underline" :data-records-flagged="f.clusterId" @click="openCluster(f.clusterId)">cluster {{ f.clusterId }}</button>: {{ f.conflicts.map((c) => `${c.field}${c.line ? ` ${c.line}` : ''}: ${c.values.map((v) => v.value).join(' / ')}`).join('; ') }}</li></ul>
      </div>
      <ul class="grid gap-2">
        <li v-for="e in inbox.entries" :key="e.id" class="rounded-lg border bg-card p-3" :data-records-entry="e.id">
          <p class="text-xs text-muted-foreground">{{ e.source }} · {{ e.tier }} · {{ e.kind }} · {{ formatDhaka(e.at) }}<template v-if="e.pageTextLength"> · page text {{ e.pageTextLength }} chars (private)</template></p>
          <p class="font-medium">{{ e.title }}</p>
          <p v-if="e.excerpt" class="text-sm text-muted-foreground">{{ e.excerpt }}</p>
          <a :href="e.url" target="_blank" rel="noopener noreferrer" class="break-all text-xs underline">{{ e.url }}</a>
          <div class="mt-2 flex flex-wrap items-center gap-2">
            <Button v-if="can.publish" size="sm" variant="outline" :data-records-publish="e.id" @click="visibility(e, 'public')">Publish to wire</Button>
            <input v-if="can.publish" v-model="hideNote[e.id]" placeholder="Why hide (required)" class="h-8 rounded-md border border-input bg-transparent px-2 text-sm">
            <Button v-if="can.publish" size="sm" variant="ghost" @click="visibility(e, 'hidden')">Hide</Button>
            <Button size="sm" variant="ghost" @click="openCluster(e.clusterId)">Open cluster</Button>
          </div>
        </li>
      </ul>
    </template>

    <!-- Suggestions -->
    <template v-if="panel === 'suggestions'">
      <p v-if="!suggestions.length" class="rounded-lg border bg-card p-4 text-sm text-muted-foreground">কোনো পরামর্শ নেই। <span class="opacity-70">No suggestions.</span></p>
      <ul class="grid gap-2">
        <li v-for="s in suggestions" :key="s.id" class="rounded-lg border bg-card p-3" :data-records-suggestion="s.id" :data-kind="s.kind">
          <div class="flex flex-wrap items-center gap-2">
            <Badge :variant="s.kind === 'urgent' ? 'destructive' : 'outline'">{{ s.kind === 'urgent' ? 'possible urgent' : 'version line' }}</Badge>
            <Badge variant="outline">{{ s.status }}</Badge>
            <span class="text-xs text-muted-foreground">{{ formatDhaka(s.createdAt) }}</span>
          </div>
          <p class="mt-1 text-sm">{{ s.reason }}</p>
          <div v-if="s.status === 'open'" class="mt-2 flex flex-wrap gap-2">
            <Button v-if="s.clusterId" size="sm" variant="outline" @click="openCluster(s.clusterId)">Open cluster (create item / guide)</Button>
            <Button v-if="can.publish && s.kind === 'versionLine'" size="sm" variant="outline" :data-records-approve="s.id" @click="decide(s, 'approve')">Approve: add {{ s.payload.line }} to {{ s.techSlug }}</Button>
            <Button v-if="can.publish" size="sm" variant="ghost" @click="decide(s, 'done')">Done</Button>
            <Button v-if="can.publish" size="sm" variant="ghost" @click="decide(s, 'ignore')">Ignore</Button>
          </div>
        </li>
      </ul>
    </template>

    <!-- Audit -->
    <template v-if="panel === 'audit'">
      <table class="w-full text-sm">
        <thead><tr class="text-left text-xs text-muted-foreground"><th class="py-1 pr-2">When</th><th class="py-1 pr-2">Who</th><th class="py-1 pr-2">What</th><th class="py-1">Note</th></tr></thead>
        <tbody><tr v-for="(a, n) in audit" :key="n" class="border-t align-top"><td class="py-1 pr-2 text-xs">{{ formatDhaka(a.at) }}</td><td class="py-1 pr-2">{{ a.by }}</td><td class="py-1 pr-2 font-mono text-xs">{{ a.action }} {{ a.target }}</td><td class="py-1 text-xs">{{ a.note }}</td></tr></tbody>
      </table>
    </template>

    <!-- A cluster -->
    <section v-if="cluster" class="rounded-lg border bg-card p-4" :data-records-cluster="cluster.clusterId">
      <div class="flex items-center gap-2"><h4 class="flex-1 font-semibold">Cluster {{ cluster.clusterId }}</h4><Button size="sm" variant="ghost" @click="cluster = null">Close</Button></div>
      <p v-if="cluster.conflicts.length" class="mt-2 text-sm text-amber-700 dark:text-amber-300">Sources differ: {{ cluster.conflicts.map((c) => `${c.field}${c.line ? ` ${c.line}` : ''}: ${c.values.map((v) => `${v.value} (${v.sources.join(', ')})`).join(' / ')}`).join('; ') }}</p>
      <ul class="mt-2 grid gap-2 text-sm">
        <li v-for="e in cluster.entries" :key="e.id" class="rounded border p-2">
          <p class="text-xs text-muted-foreground">{{ e.source }} · {{ e.tier }} · {{ e.visibility }} · {{ formatDhaka(e.at) }}</p>
          <p class="font-medium">{{ e.title }}</p>
          <p v-if="e.excerpt" class="text-muted-foreground">{{ e.excerpt }}</p>
        </li>
      </ul>
      <div class="mt-3 flex flex-wrap items-end gap-2">
        <Button size="sm" variant="outline" :disabled="busy === 'digest'" data-records-digest @click="digest">Ask the model for a draft summary</Button>
        <label class="text-sm">Issue for a draft item
          <select v-model="itemIssue" class="ml-1 h-8 rounded-md border border-input bg-transparent px-2 text-sm" data-records-issue>
            <option :value="null">—</option>
            <option v-for="i in issues" :key="i.id" :value="i.id">{{ i.slug }} ({{ i.status }})</option>
          </select>
        </label>
        <Button size="sm" variant="outline" :disabled="busy === 'item'" data-records-item @click="makeItem(null)">Create draft item from cluster</Button>
      </div>
      <ul v-if="cluster.digests.length" class="mt-3 grid gap-2">
        <li v-for="d in cluster.digests" :key="d.id" class="rounded border p-2 text-sm" :data-records-digest-row="d.id">
          <p class="text-xs text-muted-foreground">draft by {{ d.model }} · {{ formatDhaka(d.createdAt) }}<template v-if="d.itemId"> · made into draft item {{ d.itemId }}</template></p>
          <p lang="bn">{{ d.textBn }}</p>
          <p class="text-muted-foreground">{{ d.textEn }}</p>
          <p v-if="d.numberProblems.length" class="text-destructive">Not in any source: {{ d.numberProblems.join(', ') }} — the number check will block publishing until fixed.</p>
          <Button v-if="!d.itemId" size="sm" variant="outline" class="mt-1" @click="makeItem(d.id)">Create draft item with this summary</Button>
        </li>
      </ul>
    </section>
  </section>
</template>
