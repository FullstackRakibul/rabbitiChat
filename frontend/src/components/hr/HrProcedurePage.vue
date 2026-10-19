<script setup>
/**
 * One procedure (docs/HR_PROCEDURE_ANALYZER_PROMPT.md §3.2–§3.4, §3.7), three tabs:
 *
 *   · তথ্য (Facts) — the deterministic scan of a version, as tables with their
 *     lines; a line opens the code around it.
 *   · প্রশ্ন (Questions) — what the scan could not answer, open first, answered
 *     inline: a code value takes its label (written to the registry), the why
 *     questions take a note, and any can be "not a business value".
 *   · সেটআপ তালিকা (Setup list) — what must be set up for the procedure to give
 *     right answers, each line with its evidence; Markdown download and print
 *     with the Rulebook's header and footer.
 *
 * Rules (Phase A2) are not here yet. Theme tokens only; usable at 360 px.
 */
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { ArrowLeft, Ban, Check, Download, Loader2, Printer, X } from '@lucide/vue'
import {
  answerProcedureQuestion, downloadProcedureSetup, getProcedure, getProcedureSetup, listProcedureQuestions,
} from '@/services/hr.service.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import { toDate } from '@/lib/time'

const props = defineProps({
  procedureId: { type: Number, required: true },
  canAnswer: { type: Boolean, default: false },
})
const emit = defineEmits(['back'])

const TABS = [
  { key: 'facts', bn: 'তথ্য', en: 'Facts' },
  { key: 'questions', bn: 'প্রশ্ন', en: 'Questions' },
  { key: 'setup', bn: 'সেটআপ তালিকা', en: 'Setup list' },
]
const tab = ref('facts')

const KIND = {
  lookup: { bn: 'কোডের মান', en: 'Code value' },
  employee: { bn: 'হাতে বসানো কর্মী', en: 'Hard-coded employee' },
  rundate: { bn: 'চালানোর তারিখ', en: 'Run date' },
  table: { bn: 'লেখা টেবিল', en: 'Table written' },
}
const STATUS = {
  open: { bn: 'খোলা', en: 'Open', variant: 'default' },
  answered: { bn: 'উত্তর দেওয়া', en: 'Answered', variant: 'secondary' },
  'not-business': { bn: 'ব্যবসার মান নয়', en: 'Not a business value', variant: 'outline' },
}

const proc = ref(null)
const version = ref('')
const loading = ref(true)
const error = ref('')

async function load(v) {
  error.value = ''
  try {
    proc.value = await getProcedure(props.procedureId, v ? Number(v) : undefined)
    version.value = String(proc.value.version.version)
  } catch (e) {
    error.value = e.message || 'The procedure could not be loaded.'
  } finally {
    loading.value = false
  }
}
onMounted(() => load())
watch(version, (v, old) => { if (old && v && Number(v) !== proc.value?.version?.version) load(v) })

const facts = computed(() => proc.value?.facts ?? {})
const sqlLines = computed(() => (proc.value?.sql ?? '').split('\n'))

function fmt(value) {
  const d = toDate(value)
  return d ? d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : ''
}
const isoDate = (value) => toDate(value)?.toISOString().slice(0, 10) ?? ''

// ───────────────────────── the code around a line ─────────────────────────

/** The line shown in the code pane, or null. */
const codeLine = ref(null)
const codeRange = computed(() => {
  if (!codeLine.value) return []
  const from = Math.max(1, codeLine.value - 4), to = Math.min(sqlLines.value.length, codeLine.value + 4)
  return Array.from({ length: to - from + 1 }, (_, i) => ({ n: from + i, text: sqlLines.value[from + i - 1] }))
})
async function showLine(n) {
  codeLine.value = n
  await nextTick()
  document.getElementById('procedure-code-pane')?.scrollIntoView({ block: 'nearest', behavior: 'smooth' })
}

// ───────────────────────── facts tables ─────────────────────────

const factTables = computed(() => {
  const f = facts.value
  const rows = (list, map) => (list ?? []).map(map)
  return [
    { key: 'sections', bn: 'অংশ', en: 'Sections', cols: ['Section', 'Lines'], rows: rows(f.sections, (s) => ({ cells: [s.name], lines: [s.lineFrom], range: `L${s.lineFrom}–${s.lineTo}` })) },
    { key: 'parameters', bn: 'প্যারামিটার', en: 'Parameters', cols: ['Name', 'Type'], rows: rows(f.parameters, (p) => ({ cells: [p.name, p.type] })) },
    { key: 'conditions', bn: 'প্যারামিটারের শর্ত', en: 'Conditions on parameters', cols: ['Column', 'Operator', 'Values', 'Section', 'Lines'],
      rows: rows(f.conditions, (c) => ({ cells: [c.column, c.operator, c.values.join(', '), c.sections.filter(Boolean).join(', ')], lines: c.lines })) },
    { key: 'read', bn: 'যে টেবিল পড়ে', en: 'Tables read', cols: ['Table', 'Lines'], rows: rows(f.tablesRead, (t) => ({ cells: [t.table], lines: t.lines })) },
    { key: 'written', bn: 'যে টেবিলে লেখে', en: 'Tables written', cols: ['Table', 'Operation', 'Lines'], rows: rows(f.tablesWritten, (t) => ({ cells: [t.table, t.operation], lines: t.lines })) },
    { key: 'calls', bn: 'যা ডাকে', en: 'Calls', cols: ['Name', 'Kind', 'Lines'], rows: rows(f.calls, (c) => ({ cells: [c.name, c.kind], lines: c.lines })) },
    { key: 'dates', bn: 'তারিখের সীমা', en: 'Date cut-offs', cols: ['Date', 'Operator', 'Section', 'Lines'], rows: rows(f.dates, (d) => ({ cells: [d.date, d.operators.join(', '), d.sections.filter(Boolean).join(', ')], lines: d.lines })) },
    { key: 'rundate', bn: 'চালানোর তারিখের উপর নির্ভর', en: 'Run-date dependence', cols: ['Function', 'Section', 'In a condition', 'Line'],
      rows: rows(f.runDate, (r) => ({ cells: [r.function, r.section ?? '', r.business ? 'হ্যাঁ · yes' : 'না · no'], lines: [r.line] })) },
    { key: 'employees', bn: 'হাতে বসানো কর্মী', en: 'Hard-coded employees', cols: ['Employee', 'As written', 'Lines'], rows: rows(f.employees, (e) => ({ cells: [e.value, e.form], lines: e.lines })) },
    { key: 'thresholds', bn: 'সীমা', en: 'Thresholds', cols: ['Variable', 'Operator', 'Value', 'Lines'], rows: rows(f.thresholds, (t) => ({ cells: [t.variable, t.operator, t.value], lines: t.lines })) },
    { key: 'ignored', bn: 'বাদ দেওয়া কোড', en: 'Ignored code (commented out)', cols: ['Lines'], rows: rows(f.ignored, (r) => ({ cells: [], lines: [r.lineFrom], range: `L${r.lineFrom}–${r.lineTo}` })) },
  ]
})

// ───────────────────────── questions ─────────────────────────

const questions = ref([])
const questionsLoading = ref(false)
const drafts = ref({})
const saving = ref(null)
const questionError = ref('')

async function loadQuestions() {
  questionsLoading.value = true
  questionError.value = ''
  try {
    questions.value = (await listProcedureQuestions({ status: 'all', procedureId: props.procedureId })).questions ?? []
  } catch (e) {
    questionError.value = e.message || 'The questions could not be loaded.'
  } finally {
    questionsLoading.value = false
  }
}

const openCount = computed(() => questions.value.filter((q) => q.status === 'open').length)

/** This procedure's places for a question (its current version), and the other procedures it also occurs in. */
const here = (q) => q.occurrences.filter((o) => o.current && o.procedureId === props.procedureId)
const elsewhere = (q) => [...new Set(q.occurrences.filter((o) => o.current && o.procedureId !== props.procedureId).map((o) => o.objectName))]

const draftOf = (id) => (drafts.value[id] ??= { label: '', note: '' })

async function answer(q, notBusiness = false) {
  const d = draftOf(q.id)
  saving.value = q.id
  questionError.value = ''
  try {
    await answerProcedureQuestion(q.id, notBusiness
      ? { notBusiness: true, note: d.note.trim() || undefined }
      : { label: d.label.trim() || undefined, note: d.note.trim() || undefined })
    delete drafts.value[q.id]
    await loadQuestions()
    setup.value = null   // a label changes the setup list
  } catch (e) {
    questionError.value = e.message || 'The answer could not be saved.'
  } finally {
    saving.value = null
  }
}

// ───────────────────────── setup list ─────────────────────────

const setup = ref(null)
const setupLoading = ref(false)
const setupError = ref('')

async function loadSetup() {
  setupLoading.value = true
  setupError.value = ''
  try {
    setup.value = await getProcedureSetup(props.procedureId)
  } catch (e) {
    setupError.value = e.message || 'The setup list could not be loaded.'
  } finally {
    setupLoading.value = false
  }
}

const setupGroups = computed(() => (setup.value?.groups ?? [])
  .map((g) => ({ ...g, items: setup.value.items.filter((i) => i.group === g.key) }))
  .filter((g) => g.items.length))

watch(tab, (t) => {
  if (t === 'questions' && !questions.value.length) loadQuestions()
  if (t === 'setup' && !setup.value) loadSetup()
}, { immediate: false })
onMounted(loadQuestions)

function goToQuestion(id) {
  tab.value = 'questions'
  nextTick(() => document.querySelector(`[data-question="${id}"]`)?.scrollIntoView({ block: 'center', behavior: 'smooth' }))
}

async function download() {
  try { await downloadProcedureSetup(props.procedureId) } catch (e) { setupError.value = e.message }
}

const printedAt = ref(new Date())
/** The browser's print, of the print copy only (index.css), always light — as the Rulebook prints. */
function printSetup() {
  printedAt.value = new Date()
  const root = document.documentElement
  const wasDark = root.classList.contains('dark')
  if (wasDark) root.classList.remove('dark')
  const restore = () => {
    if (wasDark) root.classList.add('dark')
    window.removeEventListener('afterprint', restore)
  }
  window.addEventListener('afterprint', restore)
  nextTick(() => window.print())
}
</script>

<template>
  <div class="mx-auto w-full max-w-6xl p-4 sm:p-6" data-testid="procedure-page">
    <div class="flex flex-wrap items-center gap-2">
      <Button size="sm" variant="ghost" @click="emit('back')"><ArrowLeft /> প্রসিডিউর <span class="text-xs opacity-70">Procedures</span></Button>
    </div>

    <div v-if="loading" class="mt-8 flex justify-center text-muted-foreground"><Loader2 class="size-5 animate-spin" aria-label="Loading" /></div>
    <p v-else-if="error && !proc" class="mt-3 text-sm text-destructive" role="alert">{{ error }}</p>

    <template v-else-if="proc">
      <div class="mt-2 flex flex-wrap items-start justify-between gap-3">
        <div class="min-w-0">
          <h2 class="break-all font-mono text-lg font-semibold">{{ proc.objectName }}</h2>
          <p class="mt-1 text-xs text-muted-foreground">
            {{ proc.kind === 'function' ? 'ফাংশন · function' : 'প্রসিডিউর · procedure' }} ·
            v{{ proc.version.version }} · {{ proc.version.uploadedBy ?? '—' }} · {{ fmt(proc.version.atUtc) }} · {{ proc.version.lines }} lines
            <template v-if="proc.version.note"> · “{{ proc.version.note }}”</template>
          </p>
        </div>
        <Select v-if="proc.versions.length > 1" v-model="version">
          <SelectTrigger class="h-8 w-40" aria-label="Version"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem v-for="v in proc.versions" :key="v.version" :value="String(v.version)">
              v{{ v.version }}{{ v.version === proc.currentVersion ? ' · current' : '' }}
            </SelectItem>
          </SelectContent>
        </Select>
      </div>

      <nav class="mt-3 overflow-x-auto border-b" aria-label="Procedure tabs">
        <ul class="flex min-w-max gap-1 pb-1.5">
          <li v-for="t in TABS" :key="t.key">
            <button
              type="button"
              :aria-current="tab === t.key ? 'page' : undefined"
              :data-procedure-tab="t.key"
              :class="['rounded-md px-3 py-1.5 text-sm transition-colors', tab === t.key ? 'bg-secondary font-medium text-secondary-foreground' : 'text-muted-foreground hover:bg-accent hover:text-foreground']"
              @click="tab = t.key"
            >
              {{ t.bn }} <span class="text-xs opacity-70">{{ t.en }}</span>
              <Badge v-if="t.key === 'questions' && openCount" class="ml-1 px-1.5 py-0 text-[10px]">{{ openCount }}</Badge>
            </button>
          </li>
        </ul>
      </nav>

      <!-- ───── Facts ───── -->
      <div v-if="tab === 'facts'" class="mt-4 grid gap-5" data-testid="procedure-facts">
        <p v-if="proc.version.version !== proc.currentVersion" class="rounded-md border border-dashed px-3 py-2 text-xs text-muted-foreground">
          পুরোনো সংস্করণ v{{ proc.version.version }} দেখছেন। <span>You are looking at an older version; questions and the setup list are for v{{ proc.currentVersion }}.</span>
        </p>

        <section v-for="t in factTables" :key="t.key" :aria-label="t.en">
          <h3 class="mb-1.5 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
            {{ t.bn }} · {{ t.en }} <span class="font-normal normal-case">({{ t.rows.length }})</span>
          </h3>
          <p v-if="!t.rows.length" class="text-xs text-muted-foreground">—</p>
          <div v-else class="max-w-full overflow-x-auto rounded-md border">
            <table class="w-full text-left text-xs">
              <thead class="bg-muted/50 text-muted-foreground">
                <tr><th v-for="c in t.cols" :key="c" class="px-2 py-1.5 font-medium">{{ c }}</th></tr>
              </thead>
              <tbody>
                <tr v-for="(r, i) in t.rows" :key="i" class="border-t align-top">
                  <td v-for="(cell, j) in r.cells" :key="j" class="px-2 py-1.5 font-mono break-all sm:break-normal">{{ cell }}</td>
                  <td v-if="r.lines || r.range" class="px-2 py-1.5">
                    <span class="flex flex-wrap gap-1">
                      <button
                        v-for="n in r.lines"
                        :key="n"
                        type="button"
                        class="rounded border px-1 font-mono text-[11px] hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                        @click="showLine(n)"
                      >{{ r.range ?? `L${n}` }}</button>
                    </span>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </section>
      </div>

      <!-- ───── Questions ───── -->
      <div v-else-if="tab === 'questions'" class="mt-4" data-testid="procedure-questions">
        <p v-if="questionError" class="mb-3 text-sm text-destructive" role="alert">{{ questionError }}</p>
        <div v-if="questionsLoading && !questions.length" class="flex justify-center text-muted-foreground"><Loader2 class="size-5 animate-spin" aria-label="Loading" /></div>
        <p v-else-if="!questions.length" class="rounded-lg border border-dashed p-6 text-center text-sm text-muted-foreground">
          কোনো প্রশ্ন নেই। <span class="text-xs">No questions for this procedure.</span>
        </p>
        <ul v-else class="grid gap-2">
          <li v-for="q in questions" :key="q.id" :data-question="q.id" class="rounded-lg border bg-card p-3">
            <div class="flex flex-wrap items-start justify-between gap-2">
              <p class="min-w-0 text-sm font-medium wrap-break-word">
                <span class="text-muted-foreground">#{{ q.id }}</span> {{ q.text }}
              </p>
              <span class="flex shrink-0 flex-wrap gap-1">
                <Badge variant="outline" class="font-normal">{{ KIND[q.kind]?.bn }} <span class="ml-1 opacity-75">{{ KIND[q.kind]?.en }}</span></Badge>
                <Badge :variant="STATUS[q.status]?.variant" class="font-normal" :data-status="q.status">{{ STATUS[q.status]?.bn }} <span class="ml-1 opacity-75">{{ STATUS[q.status]?.en }}</span></Badge>
              </span>
            </div>
            <div class="mt-1.5 flex flex-wrap gap-1">
              <button
                v-for="o in here(q)"
                :key="`${o.version}:${o.line}`"
                type="button"
                class="rounded border px-1 font-mono text-[11px] hover:bg-accent"
                :title="o.snippet"
                @click="tab = 'facts'; showLine(o.line)"
              >L{{ o.line }}<template v-if="o.section"> · {{ o.section }}</template></button>
            </div>
            <p v-if="elsewhere(q).length" class="mt-1 text-xs text-muted-foreground">
              আরও আছে · Also in: <span class="font-mono">{{ elsewhere(q).join(', ') }}</span>
            </p>
            <p v-if="q.status !== 'open'" class="mt-1.5 text-xs text-muted-foreground">
              <template v-if="q.answerLabel">“{{ q.answerLabel }}”</template>
              <template v-if="q.answerNote"> — {{ q.answerNote }}</template>
              · {{ q.answeredBy ?? '—' }} · {{ fmt(q.answeredAtUtc) }}
            </p>

            <!-- Answer inline -->
            <div v-if="q.status === 'open' && canAnswer" class="mt-2 flex flex-col gap-2 sm:flex-row sm:items-center">
              <Input v-if="q.kind === 'lookup'" v-model="draftOf(q.id).label" class="h-8 sm:w-56" :placeholder="`${q.column} ${q.value} = … (label)`" aria-label="Label" />
              <Input v-model="draftOf(q.id).note" class="h-8 sm:flex-1" :placeholder="q.kind === 'lookup' ? 'নোট (ঐচ্ছিক) · Note (optional)' : 'উত্তর · Answer (note)'" aria-label="Note" />
              <span class="flex gap-2">
                <Button size="sm" :disabled="saving === q.id || (q.kind === 'lookup' ? !draftOf(q.id).label.trim() : !draftOf(q.id).note.trim())" @click="answer(q)">
                  <Loader2 v-if="saving === q.id" class="animate-spin" /><Check v-else /> উত্তর <span class="text-xs opacity-80">Answer</span>
                </Button>
                <Button size="sm" variant="outline" :disabled="saving === q.id" title="Not a business value" @click="answer(q, true)">
                  <Ban /> <span class="text-xs">Not business</span>
                </Button>
              </span>
            </div>
          </li>
        </ul>
      </div>

      <!-- ───── Setup list ───── -->
      <div v-else class="mt-4" data-testid="procedure-setup">
        <div class="flex flex-wrap items-center justify-between gap-2">
          <p class="text-xs text-muted-foreground">
            প্রসিডিউরটি ঠিক উত্তর দিতে যা সেট আপ থাকতে হবে। <span>What must be set up for v{{ proc.currentVersion }} to give right answers; each line names its evidence.</span>
          </p>
          <span class="flex gap-2">
            <Button size="sm" variant="outline" :disabled="!setup" @click="printSetup"><Printer /> প্রিন্ট <span class="text-xs opacity-70">Print</span></Button>
            <Button size="sm" variant="outline" :disabled="!setup" @click="download"><Download /> ডাউনলোড <span class="text-xs opacity-70">.md</span></Button>
          </span>
        </div>
        <p v-if="setupError" class="mt-3 text-sm text-destructive" role="alert">{{ setupError }}</p>
        <div v-if="setupLoading && !setup" class="mt-6 flex justify-center text-muted-foreground"><Loader2 class="size-5 animate-spin" aria-label="Loading" /></div>
        <section v-for="g in setupGroups" :key="g.key" class="mt-4" :aria-label="g.en">
          <h3 class="mb-1.5 text-xs font-semibold uppercase tracking-wide text-muted-foreground">{{ g.bn }} · {{ g.en }} <span class="font-normal normal-case">({{ g.items.length }})</span></h3>
          <ul class="grid gap-1.5">
            <li v-for="(i, k) in g.items" :key="k" class="rounded-md border bg-card px-3 py-2 text-sm" :data-setup-group="g.key">
              <span class="wrap-break-word">{{ i.text }}</span>
              <span class="mt-1 flex flex-wrap gap-1">
                <button
                  v-for="n in i.evidence"
                  :key="n"
                  type="button"
                  class="rounded border px-1 font-mono text-[11px] text-muted-foreground hover:bg-accent"
                  @click="tab = 'facts'; showLine(n)"
                >L{{ n }}</button>
                <button
                  v-for="qid in i.questionIds"
                  :key="`q${qid}`"
                  type="button"
                  class="rounded border border-dashed px-1 text-[11px] hover:bg-accent"
                  @click="goToQuestion(qid)"
                >প্রশ্ন · question #{{ qid }}</button>
              </span>
            </li>
          </ul>
        </section>
      </div>

      <!-- ───── the code around a line ───── -->
      <aside v-if="codeLine" id="procedure-code-pane" class="mt-5 rounded-lg border bg-muted/30" aria-label="Code">
        <div class="flex items-center justify-between border-b px-3 py-1.5 text-xs text-muted-foreground">
          <span class="font-mono">{{ proc.objectName }}.sql L{{ codeLine }} · v{{ proc.version.version }}</span>
          <Button size="icon" variant="ghost" class="size-6" aria-label="Close" @click="codeLine = null"><X /></Button>
        </div>
        <pre class="max-w-full overflow-x-auto p-2 text-[11px] leading-5"><code><span
          v-for="l in codeRange" :key="l.n" :class="['block whitespace-pre', l.n === codeLine && 'bg-accent font-semibold']"
        ><span class="mr-3 inline-block w-10 select-none text-right text-muted-foreground">{{ l.n }}</span>{{ l.text }}</span></code></pre>
      </aside>
    </template>

    <!-- ───── the print copy of the setup list: shown only when printing (index.css) ───── -->
    <Teleport to="body">
      <div v-if="proc && setup" class="rulebook-print" aria-hidden="true">
        <header class="rulebook-print-header">
          <p class="rulebook-print-kicker">মানব সম্পদ · প্রসিডিউর · Procedures</p>
          <h1>{{ setup.objectName }} — সেটআপ তালিকা · Setup list</h1>
          <p>সংস্করণ · Version: v{{ setup.version }} · uploaded by {{ setup.uploadedBy ?? 'unknown' }} on {{ isoDate(setup.uploadedAtUtc) }}</p>
        </header>
        <section v-for="g in setupGroups" :key="g.key">
          <h2 class="mb-1.5 mt-4 text-base font-semibold">{{ g.en }} · {{ g.bn }}</h2>
          <ul class="list-disc pl-5">
            <li v-for="(i, k) in g.items" :key="k" class="my-0.5">
              {{ i.text }} <span class="opacity-70">[{{ setup.objectName }}.sql {{ i.evidence.map((n) => `L${n}`).join(', ') }}]</span>
            </li>
          </ul>
        </section>
        <footer class="rulebook-print-footer">Mostisko · মানব সম্পদ · printed {{ fmt(printedAt) }}</footer>
      </div>
    </Teleport>
  </div>
</template>
