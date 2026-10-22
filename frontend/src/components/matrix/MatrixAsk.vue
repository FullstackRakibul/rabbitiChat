<script setup>
/**
 * ম্যাট্রিক্স — প্রশ্ন Ask, on a dataset's Report page, or across every current dataset in
 * the Ask section (datasetId null) — docs/MATRIX_PROMPT.md §3.9.
 *
 * A question goes plan → MatrixQuery → answer on the server; here it streams. The
 * status line shows within a second, the answer word by word. Under each answer:
 * its citations as chips ([R2] opens that group's rows highlighted, [C3] a report-card
 * line's rows, [KPI: …] its definition), a chart when a result is a breakdown (with
 * "Add to dashboard"), and the Data used panel — the queries in plain words, the
 * datasets and report dates, the row counts. Conversations are saved per dataset.
 */
import { computed, nextTick, onMounted, reactive, ref, watch } from 'vue'
import { AlertTriangle, ChevronDown, LayoutDashboard, Loader2, MessageSquarePlus, Send, Trash2 } from '@lucide/vue'
import {
  createMatrixDashboard, deleteMatrixConversation, getMatrixConversation, getMatrixDashboard, listMatrixConversations,
  listMatrixDashboards, saveMatrixDashboard, streamMatrixChat, warmMatrixAsk, warmMatrixAskAll,
} from '@/services/index.js'
import { Button } from '@/components/ui/button'
import MatrixChart from '@/components/matrix/MatrixChart.vue'
import { renderChecked } from '@/lib/matrix-text.js'
import { formatDate, lakh } from '@/lib/matrix-format.js'

const props = defineProps({
  /** The dataset asked about; null: every current dataset (the Ask section). */
  datasetId: { type: Number, default: null },
  /** action:matrix.dashboards.edit — "Add to dashboard". */
  canEditDashboards: { type: Boolean, default: false },
})
const emit = defineEmits(['drill', 'definition'])

const MY_CHARTS = 'আমার চার্ট · My charts'
const conversations = ref([])
const conversationId = ref(null)
const messages = ref([])
const question = ref('')
const sending = ref(false)
const notice = ref('')
const error = ref('')
const scroller = ref(null)

/** A saved or streaming answer, in one shape. */
function answerOf(content, meta = {}) {
  return reactive({
    role: 'assistant', content, status: '', streaming: false,
    results: meta.results ?? [], cardLabels: meta.cardLabels ?? [], kpi: meta.kpi ?? null, kind: meta.plan?.kind ?? meta.kind ?? null,
    formulas: meta.formulas ?? [], unverified: meta.unverified ?? [], unknown: meta.unknownCitations ?? [],
    firstWordMs: meta.firstWordMs ?? null, model: meta.model ?? null, repaired: !!meta.repaired, refusal: meta.refusal ?? null,
  })
}

async function loadConversations() {
  try { conversations.value = (await listMatrixConversations(props.datasetId)).conversations } catch (e) { error.value = e.message }
}

async function open(id) {
  conversationId.value = id
  error.value = ''
  if (!id) { messages.value = []; return }
  try {
    const list = await getMatrixConversation(id)
    messages.value = list.map((m) => (m.role === 'user' ? { role: 'user', content: m.content } : answerOf(m.content, m.meta ?? {})))
    scrollDown()
  } catch (e) { error.value = e.message }
}

async function remove() {
  if (!conversationId.value) return
  await deleteMatrixConversation(conversationId.value).catch((e) => { error.value = e.message })
  await open(null)
  await loadConversations()
}

function scrollDown() { nextTick(() => { if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight }) }

async function send() {
  const text = question.value.trim()
  if (!text || sending.value) return
  question.value = ''
  error.value = ''
  notice.value = ''
  sending.value = true
  messages.value.push({ role: 'user', content: text })
  const a = answerOf('')
  a.streaming = true
  messages.value.push(a)
  scrollDown()
  try {
    conversationId.value = await streamMatrixChat({ conversationId: conversationId.value, message: text, datasetId: props.datasetId }, {
      onStatus: (s) => { a.status = s },
      onContext: (c) => { a.results = c.results ?? []; a.cardLabels = c.cardLabels ?? []; a.kpi = c.kpi; a.kind = c.kind; a.model = c.model; a.repaired = c.repaired },
      onContent: (t) => { a.content += t; a.status = ''; scrollDown() },
      onFormula: (f) => { a.formulas.push(f) },
      onUnverified: (u) => { a.unverified = u.amounts ?? []; a.unknown = u.citations ?? [] },
      onError: (m) => { error.value = m },
      onDone: (d) => { a.firstWordMs = d.firstWordMs },
    })
    await loadConversations()
  } catch (e) {
    error.value = e.message
    if (!a.content) messages.value.splice(messages.value.indexOf(a), 1)
  } finally {
    a.streaming = false
    a.status = ''
    sending.value = false
  }
}

// ---- citations ----
const rowsOf = (a) => a.results.flatMap((r) => r.rows.map((row) => ({ ...row, datasetId: r.datasets?.[0]?.id })))
function known(a, label) {
  if (label === 'Card') return true
  if (/^R\d+$/.test(label)) return rowsOf(a).some((r) => r.label === label)
  if (/^C\d+$/.test(label)) return a.cardLabels.some((c) => c.label === label)
  return label.startsWith('KPI:')
}
function html(a) {
  return renderChecked(a.content, { formulas: a.formulas, unverified: a.unverified, unknown: a.unknown, known: (l) => known(a, l) })
}
function onChip(a, e) {
  const label = e.target.closest?.('[data-cite]')?.getAttribute('data-cite')
  if (!label) return
  if (/^R\d+$/.test(label)) {
    const row = rowsOf(a).find((r) => r.label === label)
    if (row?.cite) emit('drill', { datasetId: row.datasetId ?? props.datasetId, cite: row.cite })
  } else if (/^C\d+$/.test(label)) {
    const line = a.cardLabels.find((c) => c.label === label)
    if (line?.cite) emit('drill', { datasetId: props.datasetId, cite: line.cite })
  } else if (label.startsWith('KPI:')) {
    emit('definition', a.kpi ?? label.slice(4).trim())
  }
}

// ---- charts: a breakdown with one or two group fields ----
function chartable(r) {
  return !r.error && r.rows.length >= 2 && r.groupBy.length >= 1 && r.groupBy.length <= 2 && r.measures.length >= 1
    && r.rows.every((x) => r.measures.every((m) => x.values[m] === null || typeof x.values[m] === 'number'))
}
const chartRows = (r) => r.rows.map((x) => ({ ...x.values, _cite: x.cite }))
function onChartDrill(r, d) { emit('drill', { datasetId: r.datasets?.[0]?.id ?? props.datasetId, cite: d.cite }) }

async function addToDashboard(r) {
  notice.value = ''
  try {
    const mine = (await listMatrixDashboards()).dashboards.find((d) => !d.auto && d.name === MY_CHARTS && d.canEdit)
    const dash = mine ? await getMatrixDashboard(mine.id) : await createMatrixDashboard({ name: MY_CHARTS, shared: false })
    const layout = JSON.parse(JSON.stringify(dash.layout?.widgets ? dash.layout : { widgets: [] }))
    const bottom = Math.max(0, ...layout.widgets.map((w) => w.y + w.h))
    const title = r.plain.length > 110 ? r.plain.slice(0, 110) + '…' : r.plain
    layout.widgets.push({ id: `ask-${Date.now().toString(36)}`, type: r.groupBy.length === 2 ? 'stacked-bar' : 'bar',
      title: { en: title, bn: title }, query: r.query, x: 0, y: bottom, w: 12, h: 4, options: {} })
    await saveMatrixDashboard(dash.id, { name: dash.name, layout })
    notice.value = `${MY_CHARTS}: ${dash.id}`
  } catch (e) { error.value = e.message }
}

const placeholder = computed(() => 'যেমন: ইউনিট অনুযায়ী জনবল কত? · e.g. Which line has the fewest operators?')

/** The model reads this dataset's question prefix while the person reads the page (the server limits it to once per 10 minutes). */
function warm() { (props.datasetId ? warmMatrixAsk(props.datasetId) : warmMatrixAskAll()).catch(() => { /* a warm-up that fails only means a slower first answer */ }) }

watch(() => props.datasetId, async () => { warm(); await open(null); await loadConversations() })
onMounted(() => { warm(); loadConversations() })
</script>

<template>
  <section class="flex min-w-0 flex-col rounded-lg border bg-card" data-ask :data-ask-dataset="datasetId">
    <header class="flex flex-wrap items-center gap-2 border-b px-3 py-2">
      <h4 class="flex-1 text-sm font-semibold">প্রশ্ন <span class="font-normal text-muted-foreground">{{ datasetId ? 'Ask about this report' : 'Ask across every current dataset' }}</span></h4>
      <select v-if="conversations.length" class="h-7 max-w-[14rem] rounded-md border bg-background px-1 text-xs" :value="conversationId ?? ''"
        aria-label="Conversation" data-ask-conversations @change="open(Number($event.target.value) || null)">
        <option value="">নতুন প্রশ্ন · New conversation</option>
        <option v-for="c in conversations" :key="c.id" :value="c.id">{{ c.title }}</option>
      </select>
      <button v-if="conversationId" type="button" class="rounded p-1 text-muted-foreground hover:bg-accent hover:text-foreground" title="New conversation"
        aria-label="New conversation" @click="open(null)"><MessageSquarePlus class="size-4" /></button>
      <button v-if="conversationId" type="button" class="rounded p-1 text-muted-foreground hover:bg-destructive/10 hover:text-destructive" title="Delete"
        aria-label="Delete conversation" data-ask-delete @click="remove"><Trash2 class="size-4" /></button>
    </header>

    <div v-if="messages.length" ref="scroller" class="flex max-h-[34rem] flex-col gap-3 overflow-y-auto px-3 py-3" data-ask-messages>
      <template v-for="(m, i) in messages" :key="i">
        <p v-if="m.role === 'user'" class="ml-auto max-w-[85%] rounded-lg bg-secondary px-3 py-2 text-sm text-secondary-foreground" data-ask-question>{{ m.content }}</p>

        <article v-else class="flex min-w-0 flex-col gap-2 text-sm" data-ask-answer :data-plan="m.kind || undefined">
          <p v-if="m.status && !m.content" class="flex items-center gap-2 text-muted-foreground" role="status" data-ask-status>
            <Loader2 class="size-4 animate-spin" /> {{ m.status }}
          </p>
          <!-- eslint-disable-next-line vue/no-v-html -- sanitized by renderMarkdown; chips are ours -->
          <div v-if="m.content" class="md leading-relaxed" data-ask-text v-html="html(m)" @click="onChip(m, $event)" />
          <p v-if="!m.streaming && (m.unverified.length || m.unknown.length)" class="flex items-start gap-1.5 text-xs text-destructive" data-ask-unverified>
            <AlertTriangle class="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
            <span><template v-if="m.unverified.length">{{ m.unverified.length }} number(s) not found in the results: {{ m.unverified.join(', ') }}. </template>
              <template v-if="m.unknown.length">Unknown source: {{ m.unknown.join(', ') }}.</template></span>
          </p>

          <!-- a chart for each breakdown -->
          <div v-for="(r, k) in m.results.filter(chartable)" :key="k" class="rounded-md border p-2" data-ask-chart>
            <div class="h-56">
              <MatrixChart :type="r.groupBy.length === 2 ? 'stacked-bar' : 'bar'" :rows="chartRows(r)" :group-by="r.groupBy" :measures="r.measures.slice(0, r.groupBy.length === 2 ? 1 : 3)"
                :label-of="(f, v) => (v === null || v === undefined ? '(none)' : /line_no$/.test(f) ? `Line ${v}` : String(v))" :title="r.plain"
                @drill="(d) => onChartDrill(r, d)" />
            </div>
            <Button v-if="canEditDashboards && !m.streaming" size="sm" variant="ghost" class="mt-1 h-7" data-add-to-dashboard @click="addToDashboard(r)">
              <LayoutDashboard class="size-3.5" /> ড্যাশবোর্ডে যোগ <span class="text-xs opacity-70">Add to dashboard</span>
            </Button>
          </div>

          <!-- what the answer used -->
          <details v-if="!m.streaming && (m.results.length || m.kpi || m.kind)" class="rounded-md border bg-muted/30 px-2 py-1.5 text-xs" data-data-used>
            <summary class="flex cursor-pointer list-none items-center gap-1 text-muted-foreground">
              <ChevronDown class="size-3.5" aria-hidden="true" /> ব্যবহৃত ডেটা <span class="opacity-70">Data used</span>
              <span v-if="m.firstWordMs" class="ml-auto opacity-70">first word {{ (m.firstWordMs / 1000).toFixed(1) }} s · {{ m.model }}</span>
            </summary>
            <ul class="mt-1.5 space-y-1.5">
              <li v-if="m.kind" class="text-muted-foreground">Plan: {{ m.kind }}<span v-if="m.repaired"> (after one repair round)</span><span v-if="m.kpi"> · KPI {{ m.kpi }}</span></li>
              <li v-for="(r, k) in m.results" :key="k" data-data-used-query>
                <span class="font-medium">{{ k + 1 }}.</span> {{ r.error ? `Not run: ${r.error}` : r.plain }}
                <span v-if="!r.error" class="text-muted-foreground">
                  · {{ r.datasets.map((d) => `${d.kind} ${formatDate(d.reportDate)}`).join(', ') }} · {{ lakh(r.matched) }} rows matched · {{ r.rows.length }} result row(s)
                  <template v-if="r.rows.length"> ({{ r.rows[0].label }}–{{ r.rows[r.rows.length - 1].label }})</template>
                </span>
              </li>
            </ul>
          </details>
        </article>
      </template>
    </div>

    <p v-if="error" class="mx-3 mt-2 rounded-md border border-destructive/40 bg-destructive/10 p-2 text-xs text-destructive">{{ error }}</p>
    <p v-if="notice" class="mx-3 mt-2 text-xs text-muted-foreground" role="status" data-ask-notice>যোগ হয়েছে · Added to {{ notice }}</p>

    <form class="flex items-end gap-2 p-3" @submit.prevent="send">
      <textarea v-model="question" rows="1" :placeholder="placeholder" :disabled="sending"
        class="min-h-9 flex-1 resize-y rounded-md border bg-background px-2.5 py-2 text-sm placeholder:text-muted-foreground"
        aria-label="Question" data-ask-input @keydown.enter.exact.prevent="send" />
      <Button type="submit" size="sm" :disabled="sending || !question.trim()" data-ask-send>
        <Loader2 v-if="sending" class="size-4 animate-spin" /><Send v-else class="size-4" />
      </Button>
    </form>
  </section>
</template>
