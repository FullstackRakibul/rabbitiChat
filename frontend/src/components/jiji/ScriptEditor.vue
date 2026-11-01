<script setup>
/**
 * Jiji — one item: its notes, its script, approval.
 *
 * Notes (subject, angle, your notes) and the script save separately, each naming the
 * updatedAt it was opened with; a 409 means it changed elsewhere and offers a reload.
 * The server checks every save (code only from your notes, at most 4 lines, YouTube's
 * limits) and refuses a broken one whole. Approve stays off until originalAngle and
 * myTake have text; the server checks the rest and queues the narration.
 * While a job is queued or running, the item is re-read every 5 seconds.
 */
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ArrowDown, ArrowUp, Check, Code2, List, Loader2, Plus, RefreshCw, Save, Sparkles, Trash2, Type } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import ModelPicker from '@/components/ModelPicker.vue'
import {
  JIJI_CONTENT_TYPES, JIJI_STATUSES, approveJijiScript, deleteJijiContent, generateJijiScript, getJijiContent,
  jijiIssues, saveJijiScript, updateJijiContent,
} from '@/services/jiji.service.js'

const props = defineProps({ id: { type: Number, required: true } })
const emit = defineEmits(['changed', 'close'])

const item = ref(null)
const notes = ref({ subject: '', angle: '', sourceNotes: '' })
const script = ref(null)
const savedNotes = ref('')
const savedScript = ref('')
const model = ref('')
const source = ref('local')
const busy = ref('')
const error = ref('')
const issues = ref([])
const stale = ref(false)
const warnings = ref([])

const editable = computed(() => ['idea', 'script_draft'].includes(item.value?.status))
const notesDirty = computed(() => JSON.stringify(notes.value) !== savedNotes.value)
const scriptDirty = computed(() => script.value && JSON.stringify(script.value) !== savedScript.value)
const statusLabel = computed(() => JIJI_STATUSES.find((s) => s.value === item.value?.status)?.label ?? item.value?.status)
const typeLabel = computed(() => JIJI_CONTENT_TYPES.find((t) => t.value === item.value?.contentType)?.label ?? '')

const WORD = /[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*/gu
const countWords = (t) => (t || '').match(WORD)?.length ?? 0
const words = computed(() => {
  const s = script.value
  if (!s) return 0
  return countWords(s.hook) + s.beats.reduce((n, b) => n + countWords(b.text), 0) + countWords(s.myTake) + countWords(s.cta)
})
const seconds = computed(() => Math.round(words.value / 2.4))
const inTarget = computed(() => words.value >= 90 && words.value <= 130)
const canApprove = computed(() =>
  editable.value && script.value && script.value.myTake.trim() && script.value.originalAngle.trim() && !busy.value && !notesDirty.value)

function emptyScript() {
  return { hook: '', originalAngle: item.value?.angle ?? '', beats: [{ id: 1, text: '', visual: 'text' }], myTake: '', cta: 'Follow for practical software engineering.', title: '', description: '', hashtags: [] }
}

/** The server's script as the editor holds it: list items and code always present, so inputs can bind. */
function toEditor(s) {
  if (!s) return null
  return {
    hook: s.hook ?? '', originalAngle: s.originalAngle ?? '', myTake: s.myTake ?? '', cta: s.cta ?? '',
    title: s.title ?? '', description: s.description ?? '', hashtags: [...(s.hashtags ?? [])],
    beats: (s.beats ?? []).map((b) => ({ id: b.id, text: b.text ?? '', visual: b.visual ?? 'text', code: b.code ?? '', itemsText: (b.items ?? []).join('\n') })),
  }
}

function toServer(s) {
  return {
    hook: s.hook, originalAngle: s.originalAngle, myTake: s.myTake, cta: s.cta, title: s.title, description: s.description,
    hashtags: s.hashtags,
    beats: s.beats.map((b, i) => ({
      id: i + 1, text: b.text, visual: b.visual,
      ...(b.visual === 'code' ? { code: b.code } : {}),
      ...(b.visual === 'list' ? { items: b.itemsText.split('\n').map((x) => x.trim()).filter(Boolean) } : {}),
    })),
  }
}

function take(dto, { keepScript = false } = {}) {
  item.value = dto
  notes.value = { subject: dto.subject, angle: dto.angle ?? '', sourceNotes: dto.sourceNotes }
  savedNotes.value = JSON.stringify(notes.value)
  if (!keepScript) {
    script.value = toEditor(dto.script)
    savedScript.value = script.value ? JSON.stringify(script.value) : ''
  }
  stale.value = false
}

function fail(e) {
  error.value = e.message || 'Something went wrong.'
  issues.value = jijiIssues(e)
  if (e.status === 409 && /changed since/.test(error.value)) stale.value = true
}
function clear() { error.value = ''; issues.value = [] }

async function load() {
  clear()
  try { take(await getJijiContent(props.id)) } catch (e) { fail(e) }
}

async function saveNotes() {
  busy.value = 'notes'; clear()
  try {
    take(await updateJijiContent(props.id, { ...notes.value, expectedUpdatedAt: item.value.updatedAt }), { keepScript: true })
    emit('changed')
  } catch (e) { fail(e) } finally { busy.value = '' }
}

async function generate() {
  if (script.value && !window.confirm('Replace the current script with a new one from the model?')) return
  busy.value = 'generate'; clear(); warnings.value = []
  try {
    const r = await generateJijiScript(props.id, { model: model.value, source: source.value, expectedUpdatedAt: item.value.updatedAt })
    take(r.content); warnings.value = r.check.warnings
    emit('changed')
  } catch (e) { fail(e) } finally { busy.value = '' }
}

async function saveScript() {
  busy.value = 'script'; clear()
  try {
    const r = await saveJijiScript(props.id, toServer(script.value), item.value.updatedAt)
    take(r.content); warnings.value = r.check.warnings
    emit('changed')
    return true
  } catch (e) { fail(e); return false } finally { busy.value = '' }
}

async function approve() {
  if (scriptDirty.value && !(await saveScript())) return
  busy.value = 'approve'; clear()
  try {
    take(await approveJijiScript(props.id, item.value.updatedAt))
    emit('changed')
  } catch (e) { fail(e) } finally { busy.value = '' }
}

async function remove() {
  if (!window.confirm('Delete this item? Its notes and script go with it.')) return
  busy.value = 'delete'; clear()
  try { await deleteJijiContent(props.id); emit('changed'); emit('close') } catch (e) { fail(e) } finally { busy.value = '' }
}

function addBeat(visual) {
  script.value.beats.push({ id: script.value.beats.length + 1, text: '', visual, code: '', itemsText: '' })
}
function moveBeat(i, d) {
  const b = script.value.beats; const j = i + d
  if (j < 0 || j >= b.length) return
  ;[b[i], b[j]] = [b[j], b[i]]
}
const codeLines = (b) => (b.code || '').replace(/\n+$/, '').split('\n').length

const hashtagsText = computed({
  get: () => script.value?.hashtags.join(', ') ?? '',
  set: (v) => { script.value.hashtags = v.split(/[,\s]+/).map((h) => h.replace(/^#/, '')).filter(Boolean) },
})

// ---- Live status while a job is queued or running ----
let timer = null
const working = computed(() => item.value?.jobs?.some((j) => j.status === 'Pending' || j.status === 'Running'))
watch(working, (on) => {
  clearInterval(timer)
  if (on) timer = setInterval(async () => {
    try { take(await getJijiContent(props.id), { keepScript: scriptDirty.value }) } catch { /* the next tick tries again */ }
  }, 5000)
}, { immediate: true })
onBeforeUnmount(() => clearInterval(timer))

watch(() => props.id, load, { immediate: true })

const VISUALS = [
  { value: 'text', label: 'Text', icon: Type },
  { value: 'code', label: 'Code', icon: Code2 },
  { value: 'list', label: 'List', icon: List },
]
</script>

<template>
  <div class="flex flex-col gap-4" data-jiji-editor>
    <p v-if="!item && !error" class="text-sm text-muted-foreground">Loading…</p>

    <div v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive" data-jiji-error>
      {{ error }}
      <ul v-if="issues.length" class="mt-1 list-disc pl-5 text-xs"><li v-for="i in issues" :key="i">{{ i }}</li></ul>
      <Button v-if="stale" size="sm" variant="outline" class="mt-2" @click="load"><RefreshCw /> Reload (your unsaved edits are dropped)</Button>
    </div>

    <template v-if="item">
      <header class="flex flex-wrap items-center gap-2">
        <span class="rounded-md bg-muted px-2 py-0.5 text-xs">{{ typeLabel }}</span>
        <span class="rounded-md border px-2 py-0.5 text-xs" data-jiji-status>{{ statusLabel }}</span>
        <span v-if="item.revision" class="text-xs text-muted-foreground">revision {{ item.revision }}</span>
        <span class="ml-auto flex gap-2">
          <Button v-if="editable" size="sm" variant="ghost" :disabled="!!busy" @click="remove"><Trash2 /> Delete</Button>
        </span>
      </header>

      <p v-if="item.rejectReason" class="rounded-md border border-amber-500/40 bg-amber-500/10 px-3 py-2 text-sm">
        Sent back: {{ item.rejectReason }}
      </p>

      <!-- Notes: the only facts the script may use. -->
      <section class="flex flex-col gap-2 rounded-lg border bg-card p-4">
        <h3 class="text-sm font-semibold">Notes</h3>
        <Input v-model="notes.subject" :disabled="!editable" maxlength="200" placeholder="Subject" />
        <Input v-model="notes.angle" :disabled="!editable" maxlength="400" placeholder="Angle: the one idea this video teaches" />
        <Textarea v-model="notes.sourceNotes" :disabled="!editable" rows="7" maxlength="20000" class="font-mono text-xs" placeholder="Your notes" />
        <div v-if="editable" class="flex justify-end">
          <Button size="sm" variant="outline" :disabled="!notesDirty || !!busy" @click="saveNotes">
            <Loader2 v-if="busy === 'notes'" class="animate-spin" /><Save v-else /> Save notes
          </Button>
        </div>
      </section>

      <!-- Generate -->
      <section v-if="editable" class="flex flex-wrap items-center gap-2">
        <ModelPicker v-model="model" v-model:source="source" :disabled="!!busy" />
        <Button :disabled="!model || !!busy || notesDirty" data-jiji-generate @click="generate">
          <Loader2 v-if="busy === 'generate'" class="animate-spin" /><Sparkles v-else />
          {{ script ? 'Write again' : 'Write the script' }}
        </Button>
        <Button v-if="!script" variant="outline" :disabled="!!busy" @click="script = emptyScript()">Write it myself</Button>
        <span v-if="notesDirty" class="text-xs text-muted-foreground">Save the notes first.</span>
        <span v-else-if="busy === 'generate'" class="text-xs text-muted-foreground">A local model can take a few minutes.</span>
      </section>

      <!-- Script -->
      <section v-if="script" class="flex flex-col gap-3 rounded-lg border bg-card p-4" data-jiji-script>
        <div class="flex flex-wrap items-center gap-2">
          <h3 class="text-sm font-semibold">Script</h3>
          <span class="text-xs" :class="inTarget ? 'text-emerald-600 dark:text-emerald-400' : 'text-amber-600 dark:text-amber-400'" data-jiji-words>
            {{ words }} spoken words · about {{ seconds }} s (target 90–130 · 35–55 s)
          </span>
        </div>

        <fieldset :disabled="!editable" class="flex flex-col gap-3">
          <label class="flex flex-col gap-1 text-sm">
            <span class="text-muted-foreground">Hook (first spoken line)</span>
            <Input v-model="script.hook" />
          </label>
          <label class="flex flex-col gap-1 text-sm">
            <span class="text-muted-foreground">Original angle <span class="text-destructive">*</span> — what a generic explainer would not teach</span>
            <Input v-model="script.originalAngle" data-jiji-angle />
          </label>

          <div class="flex flex-col gap-2">
            <span class="text-sm text-muted-foreground">Beats</span>
            <div v-for="(b, i) in script.beats" :key="i" class="flex flex-col gap-2 rounded-md border p-3" :data-jiji-beat="i + 1">
              <div class="flex flex-wrap items-center gap-1">
                <span class="mr-1 text-xs font-medium">{{ i + 1 }}</span>
                <button v-for="v in VISUALS" :key="v.value" type="button"
                  class="inline-flex items-center gap-1 rounded px-2 py-0.5 text-xs"
                  :class="b.visual === v.value ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:bg-muted'"
                  @click="b.visual = v.value"><component :is="v.icon" class="h-3 w-3" />{{ v.label }}</button>
                <span class="ml-auto flex gap-1">
                  <Button size="icon" variant="ghost" class="h-7 w-7" :disabled="i === 0" @click="moveBeat(i, -1)"><ArrowUp /></Button>
                  <Button size="icon" variant="ghost" class="h-7 w-7" :disabled="i === script.beats.length - 1" @click="moveBeat(i, 1)"><ArrowDown /></Button>
                  <Button size="icon" variant="ghost" class="h-7 w-7" @click="script.beats.splice(i, 1)"><Trash2 /></Button>
                </span>
              </div>
              <Textarea v-model="b.text" rows="2" placeholder="What is said" />
              <template v-if="b.visual === 'code'">
                <Textarea v-model="b.code" rows="4" class="font-mono text-xs" placeholder="Paste a snippet from your notes, at most 4 lines" />
                <span class="text-xs" :class="codeLines(b) > 4 ? 'text-destructive' : 'text-muted-foreground'">
                  {{ codeLines(b) }} / 4 lines · must appear in your notes
                </span>
              </template>
              <template v-if="b.visual === 'list'">
                <Textarea v-model="b.itemsText" rows="3" placeholder="One item per line, 2 to 5" />
              </template>
            </div>
            <div v-if="editable" class="flex gap-2">
              <Button size="sm" variant="outline" @click="addBeat('text')"><Plus /> Text beat</Button>
              <Button size="sm" variant="outline" @click="addBeat('code')"><Plus /> Code beat</Button>
              <Button size="sm" variant="outline" @click="addBeat('list')"><Plus /> List beat</Button>
            </div>
          </div>

          <label class="flex flex-col gap-1 text-sm">
            <span class="text-muted-foreground">My take <span class="text-destructive">*</span> — your own real example, in your words</span>
            <Textarea v-model="script.myTake" rows="3" data-jiji-mytake
              :class="!script.myTake.trim() ? 'border-amber-500/60' : ''" placeholder="The model leaves this empty unless your notes contain a first-hand example." />
          </label>
          <label class="flex flex-col gap-1 text-sm">
            <span class="text-muted-foreground">Call to action</span>
            <Input v-model="script.cta" />
          </label>
          <div class="grid gap-3 sm:grid-cols-2">
            <label class="flex flex-col gap-1 text-sm">
              <span class="text-muted-foreground">Title <span class="text-xs">({{ script.title.length }} / 100)</span></span>
              <Input v-model="script.title" maxlength="100" />
            </label>
            <label class="flex flex-col gap-1 text-sm">
              <span class="text-muted-foreground">Hashtags (comma separated, no #)</span>
              <Input v-model="hashtagsText" />
            </label>
          </div>
          <label class="flex flex-col gap-1 text-sm">
            <span class="text-muted-foreground">Description</span>
            <Textarea v-model="script.description" rows="2" />
          </label>
        </fieldset>

        <ul v-if="warnings.length" class="list-disc rounded-md bg-amber-500/10 py-2 pl-8 pr-3 text-xs">
          <li v-for="w in warnings" :key="w">{{ w }}</li>
        </ul>
        <div v-if="editable && item.approvalCheck?.errors?.length && !scriptDirty" class="rounded-md bg-muted px-3 py-2 text-xs">
          Before approval:
          <ul class="list-disc pl-5"><li v-for="e in item.approvalCheck.errors" :key="e">{{ e }}</li></ul>
        </div>

        <div v-if="editable" class="flex flex-wrap justify-end gap-2">
          <Button variant="outline" :disabled="!scriptDirty || !!busy" data-jiji-save @click="saveScript">
            <Loader2 v-if="busy === 'script'" class="animate-spin" /><Save v-else /> Save script
          </Button>
          <Button :disabled="!canApprove" data-jiji-approve
            :title="canApprove ? '' : 'Original angle and my take are required'" @click="approve">
            <Loader2 v-if="busy === 'approve'" class="animate-spin" /><Check v-else /> Approve script
          </Button>
        </div>
      </section>

      <!-- Jobs -->
      <section v-if="item.jobs.length" class="flex flex-col gap-1 rounded-lg border bg-card p-4 text-sm">
        <h3 class="mb-1 font-semibold">Jobs</h3>
        <div v-for="j in item.jobs" :key="j.id" class="flex flex-wrap items-center gap-2 text-xs">
          <span class="w-14 font-medium">{{ j.type }}</span>
          <span class="rounded border px-1.5" :class="j.status === 'Failed' || j.status === 'UnknownOutcome' ? 'border-destructive text-destructive' : ''">{{ j.status }}</span>
          <span class="text-muted-foreground">revision {{ j.contentRevision }} · attempts {{ j.attempts }}</span>
          <span v-if="j.status === 'Pending' && j.type === 'Tts'" class="text-muted-foreground">(narration runs once the worker arrives, day 3)</span>
          <span v-if="j.lastError" class="w-full text-destructive">{{ j.lastError }}</span>
        </div>
      </section>
    </template>
  </div>
</template>
