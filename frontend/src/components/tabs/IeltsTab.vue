<script setup>
/**
 * IELTS — Reading practice, built around the mistake log.
 *
 * Four sub-views: the library (authoring), a sitting with a timer, the review,
 * and the weakness summary. The review is the point of the module — scoring a
 * test and forgetting it teaches nothing, so every wrong answer becomes a
 * mistake row the weakness view can aggregate.
 *
 * Scoring happens entirely server-side and deterministically. Nothing here ever
 * sees an answer key before submission: the take-test payload has no field for
 * one.
 */
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import {
  AlertTriangle, ArrowLeft, BookOpen, CheckCircle2, ClipboardList,
  Clock, Loader, Plus, Save, Target, Trash2, XCircle,
} from '@lucide/vue'
import {
  listTests, getTest, createTest, deleteTest, questionTypes, addQuestion, deleteQuestion,
  startAttempt, submitAttempt, listAttempts, getAttempt,
  listMistakes, mistakeSummary, markMistakeReviewed, tagMistake,
} from '@/services/ielts.service.js'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'

const VIEWS = [
  { id: 'library', label: 'Library', icon: BookOpen },
  { id: 'attempts', label: 'History', icon: ClipboardList },
  { id: 'weakness', label: 'Weakness', icon: Target },
]

/** Default sitting length. IELTS Reading is 60 min for 40 questions; a practice set is shorter. */
const DEFAULT_MINUTES = 20

const view = ref('library')
const busy = ref(false)
const error = ref('')
const notice = ref('')

// ---- library ----
const tests = ref([])
const types = ref([])
const loadingTests = ref(true)

// ---- authoring ----
const authoring = ref(false)
const draft = ref({ title: '', source: 'self', passage: '' })
const editingTest = ref(null)      // full test (with keys) once created
const qDraft = ref(newQuestionDraft())

// ---- sitting ----
const attempt = ref(null)          // { attemptId, title, passage, questions }
const answers = ref({})            // questionId -> given
const examMode = ref(false)
const minutes = ref(DEFAULT_MINUTES)
const secondsLeft = ref(0)
const elapsed = ref(0)
let timer = null

// ---- review ----
const result = ref(null)
const attemptHistory = ref([])

// ---- weakness ----
const summary = ref([])
const dueMistakes = ref([])

function newQuestionDraft() {
  return {
    questionType: 'true_false_not_given',
    prompt: '',
    optionsText: '',
    correctAnswer: '',
    skill: '',
    topic: '',
    difficulty: '',
  }
}

function flash(message, isError = false) {
  if (isError) error.value = message
  else notice.value = message
  setTimeout(() => { if (isError) error.value = ''; else notice.value = '' }, 5000)
}

async function guard(fn) {
  busy.value = true
  error.value = ''
  try { return await fn() }
  catch (e) { flash(e.message || 'Something went wrong.', true); return null }
  finally { busy.value = false }
}

// ---- library -------------------------------------------------------------

async function refreshTests() {
  loadingTests.value = true
  try { tests.value = await listTests() }
  catch (e) { flash(e.message, true) }
  finally { loadingTests.value = false }
}

async function refreshTypes() {
  try { types.value = await questionTypes() } catch { /* the picker falls back below */ }
}

function startAuthoring() {
  authoring.value = true
  draft.value = { title: '', source: 'self', passage: '' }
  editingTest.value = null
  qDraft.value = newQuestionDraft()
}

async function saveTest() {
  if (!draft.value.title.trim()) return flash('Give the test a title.', true)

  const created = await guard(() => createTest(draft.value))
  if (!created) return

  editingTest.value = created
  await refreshTests()
  flash('Test created — now add questions.')
}

async function saveQuestion() {
  if (!editingTest.value) return
  if (!qDraft.value.prompt.trim()) return flash('The question needs a prompt.', true)
  if (!qDraft.value.correctAnswer.trim()) return flash('The question needs a correct answer.', true)

  const options = qDraft.value.optionsText
    .split('\n').map((o) => o.trim()).filter(Boolean)

  const saved = await guard(() => addQuestion(editingTest.value.id, {
    questionType: qDraft.value.questionType,
    prompt: qDraft.value.prompt,
    options: options.length ? options : null,
    correctAnswer: qDraft.value.correctAnswer,
    skill: qDraft.value.skill || null,
    topic: qDraft.value.topic || null,
    difficulty: qDraft.value.difficulty || null,
  }))
  if (!saved) return

  editingTest.value = await getTest(editingTest.value.id)
  qDraft.value = newQuestionDraft()
  await refreshTests()
}

async function removeQuestion(id) {
  await guard(() => deleteQuestion(id))
  editingTest.value = await getTest(editingTest.value.id)
  await refreshTests()
}

async function removeTest(id) {
  if (!confirm('Delete this test and its questions?')) return
  await guard(() => deleteTest(id))
  await refreshTests()
}

async function openForEditing(id) {
  const full = await guard(() => getTest(id))
  if (!full) return
  editingTest.value = full
  draft.value = { title: full.title, source: full.source, passage: full.passage }
  authoring.value = true
}

// ---- sitting -------------------------------------------------------------

async function beginAttempt(testId) {
  const started = await guard(() => startAttempt(testId))
  if (!started) return

  attempt.value = started
  answers.value = {}
  result.value = null
  view.value = 'sitting'

  secondsLeft.value = minutes.value * 60
  elapsed.value = 0
  clearInterval(timer)
  timer = setInterval(() => {
    elapsed.value++
    if (secondsLeft.value > 0) secondsLeft.value--
    // Exam mode enforces the clock; practice lets it run to zero and keep going.
    if (secondsLeft.value === 0 && examMode.value) finishAttempt()
  }, 1000)
}

async function finishAttempt() {
  if (!attempt.value) return
  clearInterval(timer)

  const payload = attempt.value.questions.map((q) => ({
    questionId: q.id,
    given: answers.value[q.id] ?? '',
  }))

  const scored = await guard(() =>
    submitAttempt(attempt.value.attemptId, payload, elapsed.value))
  if (!scored) return

  result.value = scored
  attempt.value = null
  view.value = 'review'
  await Promise.all([refreshWeakness(), refreshHistory()])
}

function abandonAttempt() {
  if (examMode.value && !confirm('Leave the exam? The sitting will be abandoned.')) return
  clearInterval(timer)
  attempt.value = null
  view.value = 'library'
}

const clock = computed(() => {
  const s = Math.max(0, secondsLeft.value)
  return `${String(Math.floor(s / 60)).padStart(2, '0')}:${String(s % 60).padStart(2, '0')}`
})

const answeredCount = computed(() =>
  attempt.value ? attempt.value.questions.filter((q) => (answers.value[q.id] ?? '').trim()).length : 0)

onBeforeUnmount(() => clearInterval(timer))

// ---- review --------------------------------------------------------------

async function refreshHistory() {
  try { attemptHistory.value = await listAttempts() } catch (e) { flash(e.message, true) }
}

async function openAttempt(id) {
  const full = await guard(() => getAttempt(id))
  if (!full) return
  result.value = full
  view.value = 'review'
}

/** The user's own diagnosis. Nothing infers this — only a person knows why. */
async function tagWrongAnswer(questionId, mistakeType) {
  if (!mistakeType) return
  // Find the mistake row this question produced.
  const rows = await guard(() => listMistakes({ module: 'reading' }))
  if (!rows) return
  const row = rows.find((m) => m.sourceId === questionId)
  if (!row) return flash('No mistake row for that question yet.', true)

  await guard(() => tagMistake(row.id, { mistakeType }))
  flash('Tagged.')
  await refreshWeakness()
}

// ---- weakness ------------------------------------------------------------

async function refreshWeakness() {
  try {
    const [s, d] = await Promise.all([
      mistakeSummary('reading'),
      listMistakes({ module: 'reading', due: true }),
    ])
    summary.value = s
    dueMistakes.value = d
  } catch (e) { flash(e.message, true) }
}

async function reviewMistake(id, resolved) {
  await guard(() => markMistakeReviewed(id, resolved))
  await refreshWeakness()
  flash(resolved ? 'Resolved.' : 'Review scheduled.')
}

/** The type with the most repeated misses — what to work on next. */
const weakest = computed(() =>
  summary.value.length
    ? summary.value.reduce((a, b) => (b.timesSeen > a.timesSeen ? b : a))
    : null)

const maxSeen = computed(() =>
  summary.value.reduce((m, s) => Math.max(m, s.timesSeen), 0) || 1)

function prettyType(t) {
  return String(t || '').replace(/_/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase())
}

onMounted(async () => {
  await Promise.all([refreshTests(), refreshTypes(), refreshHistory(), refreshWeakness()])
})
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <!-- Sub-view switch. Hidden during a sitting so an exam can't be navigated away from. -->
    <div v-if="view !== 'sitting'" class="flex shrink-0 items-center gap-2 border-b px-4 py-2.5">
      <div class="flex items-center rounded-md border p-0.5">
        <button
          v-for="v in VIEWS"
          :key="v.id"
          type="button"
          :class="cn(
            'flex items-center gap-1.5 rounded px-2.5 py-1 text-xs transition-colors',
            view === v.id ? 'bg-accent text-accent-foreground' : 'text-muted-foreground',
          )"
          @click="view = v.id"
        >
          <component :is="v.icon" class="h-3.5 w-3.5" />
          {{ v.label }}
        </button>
      </div>

      <Button v-if="view === 'library'" size="sm" class="h-8" @click="startAuthoring">
        <Plus class="h-4 w-4" />
        New test
      </Button>

      <span class="ml-auto hidden text-[11px] text-muted-foreground sm:inline">
        Reading is scored deterministically — no AI marking
      </span>
    </div>

    <p v-if="notice" class="shrink-0 border-b bg-muted/40 px-4 py-1.5 text-xs font-medium">{{ notice }}</p>
    <p v-if="error" class="shrink-0 border-b bg-destructive/10 px-4 py-1.5 text-xs font-medium text-destructive">
      {{ error }}
    </p>

    <div class="min-h-0 flex-1 overflow-y-auto p-4">
      <!-- ============================ LIBRARY ============================ -->
      <div v-if="view === 'library'" class="mx-auto flex w-full max-w-4xl flex-col gap-4">
        <!-- authoring -->
        <div v-if="authoring" class="flex flex-col gap-3 rounded-xl border bg-card p-4">
          <div class="flex items-center justify-between">
            <h2 class="text-sm font-semibold">
              {{ editingTest ? `Editing: ${editingTest.title}` : 'New reading test' }}
            </h2>
            <Button variant="ghost" size="sm" class="h-7" @click="authoring = false">Close</Button>
          </div>

          <div v-if="!editingTest" class="flex flex-col gap-3">
            <div class="grid gap-3 sm:grid-cols-2">
              <div class="flex flex-col gap-1">
                <Label for="i-title">Title</Label>
                <Input id="i-title" v-model="draft.title" placeholder="e.g. Urban Farming" />
              </div>
              <div class="flex flex-col gap-1">
                <Label for="i-source">Source</Label>
                <Input id="i-source" v-model="draft.source" placeholder="Cambridge 18 Test 2 / self" />
              </div>
            </div>
            <div class="flex flex-col gap-1">
              <Label for="i-passage">Passage</Label>
              <textarea
                id="i-passage"
                v-model="draft.passage"
                rows="8"
                placeholder="Paste the reading passage here…"
                class="w-full resize-y rounded-md border bg-background p-2 text-sm outline-none focus:ring-1 focus:ring-ring"
              />
            </div>
            <Button class="self-start" :disabled="busy" @click="saveTest">
              <Save class="h-4 w-4" />
              Create test
            </Button>
          </div>

          <!-- questions, once the test exists -->
          <div v-else class="flex flex-col gap-3">
            <div v-if="editingTest.questions.length" class="flex flex-col gap-1.5">
              <div
                v-for="q in editingTest.questions"
                :key="q.id"
                class="flex items-start gap-2 rounded-lg border p-2 text-xs"
              >
                <span class="w-5 shrink-0 text-center font-semibold text-muted-foreground">{{ q.order }}</span>
                <div class="min-w-0 flex-1">
                  <p class="font-medium">{{ q.prompt }}</p>
                  <p class="text-muted-foreground">
                    <Badge variant="secondary" class="mr-1 font-normal">{{ prettyType(q.questionType) }}</Badge>
                    answer: <b class="text-foreground">{{ q.correctAnswer }}</b>
                    <span v-if="q.skill"> · {{ q.skill }}</span>
                    <span v-if="q.topic"> · {{ q.topic }}</span>
                  </p>
                </div>
                <Button size="icon" variant="ghost" class="h-6 w-6" @click="removeQuestion(q.id)">
                  <Trash2 class="h-3 w-3" />
                </Button>
              </div>
            </div>

            <div class="flex flex-col gap-2 rounded-lg border border-dashed p-3">
              <p class="text-xs font-semibold uppercase tracking-wide text-muted-foreground">Add question</p>
              <div class="grid gap-2 sm:grid-cols-2">
                <div class="flex flex-col gap-1">
                  <Label class="text-xs">Type</Label>
                  <select
                    v-model="qDraft.questionType"
                    class="h-9 rounded-md border bg-background px-2 text-sm outline-none"
                  >
                    <option v-for="t in (types.length ? types : ['true_false_not_given'])" :key="t" :value="t">
                      {{ prettyType(t) }}
                    </option>
                  </select>
                </div>
                <div class="flex flex-col gap-1">
                  <Label class="text-xs">Correct answer</Label>
                  <Input v-model="qDraft.correctAnswer" placeholder="TRUE  ·  B  ·  carbon dioxide|CO2" />
                </div>
              </div>

              <div class="flex flex-col gap-1">
                <Label class="text-xs">Prompt</Label>
                <Input v-model="qDraft.prompt" placeholder="The statement or question…" />
              </div>

              <div class="flex flex-col gap-1">
                <Label class="text-xs">Options — one per line (MCQ / matching only)</Label>
                <textarea
                  v-model="qDraft.optionsText"
                  rows="3"
                  class="w-full resize-y rounded-md border bg-background p-2 text-xs outline-none focus:ring-1 focus:ring-ring"
                />
              </div>

              <div class="grid gap-2 sm:grid-cols-3">
                <Input v-model="qDraft.skill" placeholder="skill — e.g. scanning" class="text-xs" />
                <Input v-model="qDraft.topic" placeholder="topic — e.g. environment" class="text-xs" />
                <Input v-model="qDraft.difficulty" placeholder="difficulty" class="text-xs" />
              </div>

              <p class="text-[11px] text-muted-foreground">
                Completion and short answers accept variants — separate them with <code>|</code>.
              </p>

              <Button size="sm" class="self-start" :disabled="busy" @click="saveQuestion">
                <Plus class="h-3.5 w-3.5" />
                Add question
              </Button>
            </div>
          </div>
        </div>

        <!-- test list -->
        <p v-if="loadingTests" class="py-8 text-center text-sm text-muted-foreground">Loading…</p>

        <div v-else-if="tests.length" class="flex flex-col gap-2">
          <div
            v-for="t in tests"
            :key="t.id"
            class="flex items-center gap-3 rounded-xl border bg-card p-3"
          >
            <div class="min-w-0 flex-1">
              <p class="flex items-center gap-1.5 truncate text-sm font-medium">
                {{ t.title }}
                <!-- Shared seeded content: everyone sees it, nobody may edit it. -->
                <Badge v-if="t.isSystem" variant="secondary" class="shrink-0 font-normal text-[10px]">
                  Demo
                </Badge>
              </p>
              <p class="truncate text-xs text-muted-foreground">
                {{ t.source }} · {{ t.questionCount }} question{{ t.questionCount === 1 ? '' : 's' }}
                <span v-if="t.isSystem"> · read-only</span>
              </p>
            </div>
            <!-- Edit/Delete are hidden rather than disabled on demo tests: the
                 server refuses them anyway, so offering the control would only
                 promise something that cannot happen. -->
            <Button
              v-if="!t.isSystem"
              size="sm"
              variant="outline"
              class="h-8"
              @click="openForEditing(t.id)"
            >
              Edit
            </Button>
            <Button size="sm" class="h-8" :disabled="!t.questionCount" @click="beginAttempt(t.id)">
              Take
            </Button>
            <Button
              v-if="!t.isSystem"
              size="icon"
              variant="ghost"
              class="h-7 w-7"
              @click="removeTest(t.id)"
            >
              <Trash2 class="h-3.5 w-3.5" />
            </Button>
          </div>
        </div>

        <p v-else class="py-10 text-center text-sm text-muted-foreground">
          No tests yet — author one to start practising.
        </p>

        <div class="flex items-center gap-3 rounded-lg border border-dashed p-3 text-xs text-muted-foreground">
          <Clock class="h-4 w-4 shrink-0" />
          <span>Timer</span>
          <Input v-model.number="minutes" type="number" min="1" max="120" class="h-7 w-16 text-xs" />
          <span>minutes</span>
          <label class="ml-2 flex cursor-pointer items-center gap-1.5">
            <input v-model="examMode" type="checkbox">
            Exam mode (timer enforced, no going back)
          </label>
        </div>
      </div>

      <!-- ============================ SITTING ============================ -->
      <div v-else-if="view === 'sitting' && attempt" class="mx-auto w-full max-w-5xl">
        <div class="mb-3 flex items-center gap-3 rounded-xl border bg-card p-3">
          <div class="min-w-0 flex-1">
            <p class="truncate text-sm font-semibold">{{ attempt.title }}</p>
            <p class="text-xs text-muted-foreground">
              {{ answeredCount }} of {{ attempt.questions.length }} answered
              <span v-if="examMode"> · exam mode</span>
            </p>
          </div>
          <div
            :class="cn(
              'flex items-center gap-1.5 rounded-md border px-2.5 py-1 font-mono text-sm tabular-nums',
              secondsLeft <= 60 && 'border-destructive text-destructive',
            )"
          >
            <Clock class="h-3.5 w-3.5" />
            {{ clock }}
          </div>
          <Button variant="outline" size="sm" class="h-8" @click="abandonAttempt">Leave</Button>
          <Button size="sm" class="h-8" :disabled="busy" @click="finishAttempt">
            <Loader v-if="busy" class="h-3.5 w-3.5 animate-spin" />
            Submit
          </Button>
        </div>

        <div class="grid gap-3 lg:grid-cols-2">
          <div class="rounded-xl border bg-card p-4">
            <p class="mb-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Passage</p>
            <p class="whitespace-pre-wrap text-sm leading-relaxed">{{ attempt.passage }}</p>
          </div>

          <div class="flex flex-col gap-2">
            <div v-for="q in attempt.questions" :key="q.id" class="rounded-xl border bg-card p-3">
              <p class="mb-1.5 text-sm">
                <span class="mr-1.5 font-semibold text-muted-foreground">{{ q.order }}.</span>
                {{ q.prompt }}
              </p>
              <Badge variant="secondary" class="mb-2 font-normal text-[10px]">
                {{ prettyType(q.questionType) }}
              </Badge>

              <div v-if="q.options?.length" class="flex flex-col gap-1">
                <label
                  v-for="(o, i) in q.options"
                  :key="i"
                  class="flex cursor-pointer items-center gap-2 rounded px-1.5 py-1 text-sm hover:bg-accent"
                >
                  <input v-model="answers[q.id]" type="radio" :value="o" :name="`q${q.id}`">
                  {{ o }}
                </label>
              </div>
              <Input v-else v-model="answers[q.id]" placeholder="Your answer…" class="h-8 text-sm" />
            </div>
          </div>
        </div>
      </div>

      <!-- ============================ REVIEW ============================ -->
      <div v-else-if="view === 'review' && result" class="mx-auto flex w-full max-w-4xl flex-col gap-4">
        <div class="flex flex-wrap items-center gap-4 rounded-xl border bg-card p-4">
          <div>
            <p class="text-xs uppercase tracking-wide text-muted-foreground">Raw score</p>
            <p class="text-2xl font-bold tabular-nums">{{ result.rawScore }} / {{ result.totalQuestions }}</p>
          </div>
          <div>
            <p class="text-xs uppercase tracking-wide text-muted-foreground">Band</p>
            <p class="text-2xl font-bold tabular-nums">{{ result.bandEstimate?.toFixed(1) }}</p>
            <!-- Labelled at the point of display so the number is never read as a verdict. -->
            <p class="text-[11px] text-muted-foreground">{{ result.bandBasis }}</p>
          </div>
          <div>
            <p class="text-xs uppercase tracking-wide text-muted-foreground">Mistakes logged</p>
            <p class="text-2xl font-bold tabular-nums">{{ result.mistakesRecorded }}</p>
          </div>
          <Button variant="outline" size="sm" class="ml-auto h-8" @click="view = 'library'">
            <ArrowLeft class="h-3.5 w-3.5" />
            Library
          </Button>
        </div>

        <div class="flex flex-col gap-2">
          <div
            v-for="a in result.answers"
            :key="a.questionId"
            :class="cn(
              'rounded-xl border p-3',
              a.isCorrect ? 'border-emerald-500/40 bg-emerald-500/5' : 'border-destructive/40 bg-destructive/5',
            )"
          >
            <div class="flex items-start gap-2">
              <component
                :is="a.isCorrect ? CheckCircle2 : XCircle"
                :class="cn('mt-0.5 h-4 w-4 shrink-0', a.isCorrect ? 'text-emerald-600' : 'text-destructive')"
              />
              <div class="min-w-0 flex-1">
                <p class="text-sm">
                  <span class="mr-1.5 font-semibold text-muted-foreground">{{ a.order }}.</span>
                  {{ a.prompt }}
                </p>
                <p class="mt-1 text-xs">
                  <span class="text-muted-foreground">You:</span>
                  <b :class="a.isCorrect ? 'text-emerald-700 dark:text-emerald-400' : 'text-destructive'">
                    {{ a.givenAnswer || '—' }}
                  </b>
                  <span v-if="!a.isCorrect" class="text-muted-foreground"> · Correct: </span>
                  <b v-if="!a.isCorrect">{{ a.correctAnswer }}</b>
                </p>
                <p class="mt-1 flex flex-wrap items-center gap-1">
                  <Badge variant="secondary" class="font-normal text-[10px]">
                    {{ prettyType(a.questionType) }}
                  </Badge>
                  <Badge v-if="a.skill" variant="secondary" class="font-normal text-[10px]">{{ a.skill }}</Badge>
                  <Badge v-if="a.topic" variant="secondary" class="font-normal text-[10px]">{{ a.topic }}</Badge>
                </p>

                <!-- Why it went wrong: the user's call, never inferred. -->
                <div v-if="!a.isCorrect" class="mt-2 flex flex-wrap items-center gap-1.5">
                  <span class="text-[11px] text-muted-foreground">Why?</span>
                  <button
                    v-for="m in ['inference_error', 'keyword_trap', 'misread', 'ran_out_of_time', 'vocabulary']"
                    :key="m"
                    type="button"
                    class="rounded border px-1.5 py-0.5 text-[11px] hover:bg-accent"
                    @click="tagWrongAnswer(a.questionId, m)"
                  >
                    {{ prettyType(m) }}
                  </button>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- ============================ HISTORY ============================ -->
      <div v-else-if="view === 'attempts'" class="mx-auto flex w-full max-w-3xl flex-col gap-2">
        <div v-if="attemptHistory.length" class="flex flex-col gap-2">
          <button
            v-for="a in attemptHistory"
            :key="a.id"
            type="button"
            class="flex items-center gap-3 rounded-xl border bg-card p-3 text-left hover:bg-accent/40"
            @click="openAttempt(a.id)"
          >
            <div class="min-w-0 flex-1">
              <p class="truncate text-sm font-medium">{{ a.title }}</p>
              <p class="text-xs text-muted-foreground">
                {{ new Date(a.startedAt).toLocaleString() }}
                <span v-if="a.durationSeconds"> · {{ Math.round(a.durationSeconds / 60) }} min</span>
              </p>
            </div>
            <Badge variant="secondary" class="tabular-nums">{{ a.rawScore }}/{{ a.totalQuestions }}</Badge>
            <Badge v-if="a.bandEstimate" class="tabular-nums">Band {{ a.bandEstimate.toFixed(1) }}</Badge>
          </button>
        </div>
        <p v-else class="py-10 text-center text-sm text-muted-foreground">No attempts yet.</p>
      </div>

      <!-- =========================== WEAKNESS =========================== -->
      <div v-else-if="view === 'weakness'" class="mx-auto flex w-full max-w-3xl flex-col gap-4">
        <div class="rounded-xl border bg-card p-4">
          <div class="mb-3 flex items-center justify-between">
            <h2 class="text-sm font-semibold">Mistakes by question type</h2>
            <span v-if="weakest" class="flex items-center gap-1.5 text-xs text-destructive">
              <AlertTriangle class="h-3.5 w-3.5" />
              Weakest: {{ prettyType(weakest.questionType) }}
            </span>
          </div>

          <div v-if="summary.length" class="flex flex-col gap-2">
            <div v-for="s in summary" :key="s.questionType" class="flex items-center gap-2">
              <span class="w-40 shrink-0 truncate text-xs">{{ prettyType(s.questionType) }}</span>
              <div class="h-4 flex-1 overflow-hidden rounded bg-muted">
                <div
                  :class="cn(
                    'h-full rounded',
                    weakest && s.questionType === weakest.questionType ? 'bg-destructive' : 'bg-primary/60',
                  )"
                  :style="{ width: `${Math.round((s.timesSeen / maxSeen) * 100)}%` }"
                />
              </div>
              <span class="w-24 shrink-0 text-right text-[11px] tabular-nums text-muted-foreground">
                {{ s.timesSeen }}× · {{ s.unresolved }} open
              </span>
            </div>
          </div>
          <p v-else class="py-6 text-center text-sm text-muted-foreground">
            No mistakes logged yet — take a test and they'll appear here.
          </p>
        </div>

        <div class="rounded-xl border bg-card">
          <div class="flex items-center justify-between border-b px-4 py-2.5">
            <h2 class="text-sm font-semibold">Due for review</h2>
            <Badge variant="secondary">{{ dueMistakes.length }}</Badge>
          </div>

          <div v-if="dueMistakes.length" class="divide-y">
            <div v-for="m in dueMistakes" :key="m.id" class="flex items-center gap-3 px-4 py-2.5">
              <div class="min-w-0 flex-1">
                <p class="truncate text-sm">
                  {{ prettyType(m.questionType) }}
                  <span v-if="m.topic" class="text-muted-foreground"> · {{ m.topic }}</span>
                </p>
                <p class="truncate text-xs text-muted-foreground">
                  You: <b>{{ m.yourAnswer || '—' }}</b> · Correct: <b>{{ m.correctAnswer }}</b>
                  · seen {{ m.timesSeen }}×
                  <span v-if="m.mistakeType"> · {{ prettyType(m.mistakeType) }}</span>
                </p>
              </div>
              <Button size="sm" variant="outline" class="h-7" @click="reviewMistake(m.id, false)">
                Reviewed
              </Button>
              <Button size="sm" class="h-7" @click="reviewMistake(m.id, true)">Resolve</Button>
            </div>
          </div>
          <p v-else class="px-4 py-8 text-center text-sm text-muted-foreground">
            Nothing due — come back after the next review date.
          </p>
        </div>
      </div>
    </div>
  </div>
</template>
