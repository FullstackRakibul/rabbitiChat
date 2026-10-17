<script setup>
/**
 * প্রশ্ন (Ask) — the HR chat (docs/HR_MODULE_PROMPT.md §3.3, §3.4).
 *
 * At the top, instant search: the hybrid search with no model, so HR can find
 * where something is written in under a second, and "Ask about this" to start a
 * question scoped to that item. Below, the chat: a status line appears at once
 * ("খুঁজছি… N টি অংশ পাওয়া গেছে"), then the answer streams word by word (the
 * reply object is reactive(), as in the main chat). Every citation opens the
 * cited item at its lines; every amount the server calculated from an inline
 * formula shows its formula on hover or tap; a "Context used" panel lists exactly
 * what was sent; and any amount the number check could not trace, or formula it
 * could not calculate, is shown under the answer. The HR conversation list sits
 * at the side, a drawer on phones.
 *
 * Nothing here decides what is allowed or what counts as verified — the server
 * does, and this component shows its answer.
 */
import { computed, nextTick, onMounted, reactive, ref } from 'vue'
import {
  BookOpen, Calculator, ChevronDown, Loader2, MessageSquare, PanelLeft, Plus, Search, Send, Square, Trash2, TriangleAlert, X,
} from '@lucide/vue'
import {
  listHrLibrary, listHrConversations, getHrConversation, deleteHrConversation, searchHr, streamHrChat,
} from '@/services/hr.service.js'
import MarkdownBody from '@/components/MarkdownBody.vue'
import { Button } from '@/components/ui/button'
import HrItemViewer from '@/components/hr/HrItemViewer.vue'
import { escapeHtml, withFormulas } from '@/lib/hr-text'

/** A Rulebook citation was clicked: the tab opens that page at that version ({ pageId | title, version }). */
const emit = defineEmits(['open-rule'])

const EXAMPLES = [
  'Schedule-A কর্মী ৭ বছর ৮ মাস চাকরি করে ইস্তফা দিলে সার্ভিস বেনিফিট কীভাবে হিসাব হয়?',
  'Which attendance codes count as worked days for earned leave?',
  'Basic is 12,000, 7 years 8 months, 250 fraction days, resignation in April 2026, Non-Management. What is the service benefit?',
]

const items = ref([])            // library items, for "Search in" and for opening citations
const conversations = ref([])
const currentId = ref(null)
const drawer = ref(false)        // the conversation list on phones

const messages = ref([])
const input = ref('')
const busy = ref(false)
const error = ref('')
let controller = null

const scope = ref([])            // "Search in": item ids; empty = the whole library
const scopeOpen = ref(false)

const query = ref('')
const results = ref([])
const searching = ref(false)
const searchMs = ref(null)
let searchTimer = null

const viewerOpen = ref(false)
const viewerTarget = ref(null)

const scroller = ref(null)
const composer = ref(null)

const activeItems = computed(() => items.value.filter((i) => i.isActive))
const scopeLabel = computed(() => scope.value.length
  ? `${scope.value.length} টি আইটেম · ${scope.value.length} item${scope.value.length > 1 ? 's' : ''}`
  : 'পুরো লাইব্রেরি · Whole library')

onMounted(async () => {
  try { items.value = (await listHrLibrary()).items ?? [] } catch { /* the chat still works */ }
  await refreshConversations()
})

async function refreshConversations() {
  try { conversations.value = await listHrConversations() } catch (e) { error.value = e.message }
}

async function openConversation(id) {
  drawer.value = false
  if (busy.value) return
  error.value = ''
  try {
    const c = await getHrConversation(id)
    currentId.value = c.id
    messages.value = c.messages.map((m) => reactive({
      role: m.role, content: m.content,
      context: m.meta?.context ?? null,
      formulas: m.meta?.formulas ?? [],
      unverified: m.meta?.unverified ?? [],
      unverifiedFormulas: m.meta?.unverifiedFormulas ?? [],
      shownFormula: null,
    }))
    scrollDown()
  } catch (e) { error.value = e.message }
}

function newConversation() {
  if (busy.value) return
  currentId.value = null
  messages.value = []
  error.value = ''
  drawer.value = false
}

async function removeConversation(id) {
  if (!confirm('এই কথোপকথন মুছে ফেলবেন? / Delete this conversation?')) return
  await deleteHrConversation(id)
  if (currentId.value === id) newConversation()
  await refreshConversations()
}

function scrollDown() {
  nextTick(() => { if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight })
}

async function send(text = input.value) {
  if (busy.value) { controller?.abort(); return }
  const question = text.trim()
  if (!question) return
  error.value = ''
  input.value = ''
  messages.value.push({ role: 'user', content: question })

  // reactive(), not a plain object: the stream writes to `reply` directly, and a
  // write to the plain object behind the array's proxy is invisible to Vue — the
  // answer would sit behind the "…" until the stream ended.
  const reply = reactive({
    role: 'assistant', content: '', status: '', context: null, formulas: [],
    unverified: [], unverifiedFormulas: [], shownFormula: null, error: '',
  })
  messages.value.push(reply)
  scrollDown()

  busy.value = true
  controller = new AbortController()
  try {
    const id = await streamHrChat({ conversationId: currentId.value, message: question, items: scope.value }, {
      onStatus: (text) => { reply.status = text },
      onContext: (c) => { reply.context = c },
      onContent: (chunk) => { reply.content += chunk; scrollDown() },
      onFormula: (f) => { reply.formulas.push(f) },
      onUnverified: (amounts, formulas) => { reply.unverified = amounts; reply.unverifiedFormulas = formulas },
      onError: (message) => { reply.error = message },
    }, controller.signal)
    if (currentId.value == null) {
      currentId.value = id
      refreshConversations()
    }
  } catch (e) {
    if (e.name !== 'AbortError') reply.error = e.message
  } finally {
    busy.value = false
    controller = null
  }
}

function onKeydown(event) {
  if (event.key !== 'Enter' || event.shiftKey || event.isComposing) return
  event.preventDefault()
  send()
}

// ---- instant search ----

function onQuery() {
  clearTimeout(searchTimer)
  const q = query.value.trim()
  if (!q) { results.value = []; searchMs.value = null; return }
  searchTimer = setTimeout(async () => {
    searching.value = true
    try {
      const res = await searchHr(q, scope.value)
      if (query.value.trim() === q) { results.value = res.hits ?? []; searchMs.value = res.tookMs }
    } catch (e) { error.value = e.message } finally { searching.value = false }
  }, 250)
}

function askAbout(hit) {
  scope.value = [hit.itemId]
  input.value = query.value.trim() ? `${query.value.trim()} — ` : ''
  results.value = []
  nextTick(() => composer.value?.focus())
}

// ---- rendering ----

/** "…«match»…" → highlighted, with everything else escaped. */
const snippetHtml = (s) => escapeHtml(s ?? '').replace(/«/g, '<mark class="rounded-sm bg-primary/15 px-0.5 text-foreground">').replace(/»/g, '</mark>')

/**
 * The answer's markdown with every [citation] made a button that opens the cited
 * place. MarkdownBody renders it through the same sanitizer as the main chat,
 * which keeps <button>, <span> and data-* attributes.
 */
function withCites(content) {
  return (content || '').replace(/\[([^[\]\n]{2,200})\](?!\()/g, (all, label) => label === 'calculate'
    // Not a place in the library: the amount came from the calculator, shown above the answer.
    ? `<span title="Worked out with the calculate tool" class="mx-0.5 rounded border border-dashed px-1 py-px align-baseline font-mono text-[11px] text-muted-foreground">calculate</span>`
    : `<button type="button" data-cite="${escapeHtml(label)}" class="mx-0.5 rounded border bg-muted/60 px-1 py-px align-baseline font-mono text-[11px] text-foreground hover:bg-accent">${escapeHtml(label)}</button>`)
}

/** A citation → the item and place to open, from what this answer was sent, then from the library by name. */
function resolveCite(message, label) {
  // A Rulebook page is cited as "Rulebook: <Title> v<N>" (its `cite`) and opens the page
  // at that version (docs/HR_RULEBOOK_PROMPT.md §3.5); anything else by its SourceRef.
  const rule = message.context?.excerpts?.find((e) => e.cite === label && e.rulePageId)
  if (rule) return { rule: { pageId: rule.rulePageId, version: rule.ruleVersion } }
  const named = /^Rulebook: (.+) v(\d+)$/.exec(label)
  if (named) return { rule: { title: named[1], version: Number(named[2]) } }
  const excerpt = message.context?.excerpts?.find((e) => e.cite === label || e.sourceRef === label)
  if (excerpt) return { itemId: excerpt.itemId, label, lineFrom: excerpt.lineFrom, lineTo: excerpt.lineTo }

  const known = [...(message.context?.always ?? []).map((a) => ({ id: a.itemId, name: a.name })), ...items.value.map((i) => ({ id: i.id, name: i.name }))]
  const item = known.filter((k) => label === k.name || label.startsWith(`${k.name} `)).sort((a, b) => b.name.length - a.name.length)[0]
  if (!item) return null
  const rest = label.slice(item.name.length).trim()
  const lines = /L(\d+)-(\d+)$/.exec(rest)
  return {
    itemId: item.id, label,
    lineFrom: lines ? +lines[1] : null, lineTo: lines ? +lines[2] : null,
    section: rest.replace(/^§\s*/, '').replace(/\s*L\d+-\d+$/, '') || null,
  }
}

function onAnswerClick(message, event) {
  const formula = event.target.closest?.('[data-formula]')
  if (formula) {
    const i = Number(formula.dataset.formula)
    message.shownFormula = message.shownFormula === i ? null : i
    return
  }
  const el = event.target.closest?.('[data-cite]')
  if (!el) return
  const target = resolveCite(message, el.dataset.cite)
  if (target?.rule) emit('open-rule', target.rule)
  else if (target) openViewer(target)
}

function openViewer(target) {
  viewerTarget.value = target
  viewerOpen.value = true
}

const formatAmount = (n) => `৳${Number(n).toLocaleString('en-IN')}`
const contextTokens = (c) => c ? c.tokens.system + c.tokens.excerpts + c.tokens.history + c.tokens.question : 0
</script>

<template>
  <div class="flex h-full min-h-0">
    <!-- Conversation list: a column on wider screens, a drawer on phones. -->
    <div
      v-if="drawer"
      class="fixed inset-0 z-40 bg-background/70 backdrop-blur-sm md:hidden"
      @click.self="drawer = false"
    />
    <aside
      :class="[
        'z-50 flex w-64 shrink-0 flex-col border-r bg-background',
        drawer ? 'fixed inset-y-0 left-0 shadow-xl md:static md:shadow-none' : 'hidden md:flex',
      ]"
      aria-label="HR conversations drawer"
    >
      <div class="flex items-center gap-2 border-b p-2">
        <Button size="sm" variant="outline" class="h-8 flex-1" :disabled="busy" @click="newConversation">
          <Plus class="h-3.5 w-3.5" /> নতুন প্রশ্ন <span class="text-xs text-muted-foreground">New</span>
        </Button>
        <Button size="icon" variant="ghost" class="h-8 w-8 md:hidden" aria-label="Close" @click="drawer = false">
          <X class="h-4 w-4" />
        </Button>
      </div>
      <ul class="min-h-0 flex-1 overflow-y-auto p-1">
        <li v-for="c in conversations" :key="c.id" class="group flex items-center">
          <button
            type="button"
            :class="['min-w-0 flex-1 truncate rounded-md px-2 py-1.5 text-left text-sm',
              c.id === currentId ? 'bg-secondary font-medium' : 'text-muted-foreground hover:bg-accent hover:text-foreground']"
            @click="openConversation(c.id)"
          >
            {{ c.title }}
          </button>
          <button
            type="button"
            class="hidden h-7 w-7 shrink-0 items-center justify-center rounded text-muted-foreground hover:text-destructive group-hover:flex"
            aria-label="Delete conversation"
            @click="removeConversation(c.id)"
          >
            <Trash2 class="h-3.5 w-3.5" />
          </button>
        </li>
        <li v-if="!conversations.length" class="px-2 py-6 text-center text-xs text-muted-foreground">
          এখনও কোনো প্রশ্ন নেই <br>No questions yet
        </li>
      </ul>
    </aside>

    <div class="flex min-w-0 flex-1 flex-col">
      <!-- Instant search: no model, under a second. -->
      <div class="shrink-0 border-b p-2 sm:p-3">
        <div class="mx-auto flex w-full max-w-3xl items-center gap-2">
          <Button size="icon" variant="ghost" class="h-9 w-9 shrink-0 md:hidden" aria-label="Conversations" @click="drawer = true">
            <PanelLeft class="h-4 w-4" />
          </Button>
          <label class="relative min-w-0 flex-1">
            <Search class="pointer-events-none absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <input
              v-model="query"
              type="search"
              class="h-9 w-full rounded-md border bg-background pl-8 pr-3 text-sm outline-none focus:ring-1 focus:ring-ring"
              placeholder="লাইব্রেরিতে খুঁজুন · Search the library (EXL, gratuity, unit 27…)"
              @input="onQuery"
            >
          </label>
          <Loader2 v-if="searching" class="h-4 w-4 shrink-0 animate-spin text-muted-foreground" />
        </div>

        <div v-if="results.length" class="mx-auto mt-2 w-full max-w-3xl">
          <p class="mb-1 text-xs text-muted-foreground">{{ results.length }} ফলাফল · results in {{ searchMs }} ms</p>
          <ol class="max-h-72 space-y-1.5 overflow-y-auto">
            <li v-for="h in results" :key="h.chunkId" class="rounded-md border bg-card p-2">
              <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
                <button type="button" class="min-w-0 truncate text-left font-mono text-xs font-medium hover:underline"
                  @click="openViewer({ itemId: h.itemId, label: h.sourceRef, lineFrom: h.lineFrom, lineTo: h.lineTo })">
                  {{ h.sourceRef }}
                </button>
                <span class="text-[11px] text-muted-foreground">{{ h.itemName }}</span>
                <Button size="sm" variant="ghost" class="ml-auto h-7 text-xs" @click="askAbout(h)">
                  <MessageSquare class="h-3.5 w-3.5" /> এ নিয়ে প্রশ্ন <span class="text-muted-foreground">Ask about this</span>
                </Button>
              </div>
              <!-- eslint-disable-next-line vue/no-v-html -- escaped above; only <mark> is added -->
              <p class="mt-1 line-clamp-3 text-xs text-muted-foreground" v-html="snippetHtml(h.snippet)" />
            </li>
          </ol>
        </div>
      </div>

      <!-- The conversation -->
      <div ref="scroller" class="min-h-0 flex-1 overflow-y-auto px-3 py-4 sm:px-6">
        <div class="mx-auto flex w-full max-w-3xl flex-col gap-4">
          <div v-if="!messages.length" class="py-10 text-center">
            <BookOpen class="mx-auto h-6 w-6 text-muted-foreground" />
            <p class="mt-2 text-sm font-medium">HR লাইব্রেরি থেকে উত্তর, উৎসসহ</p>
            <p class="text-xs text-muted-foreground">Answers come only from the HR library, each statement with its source.</p>
            <div class="mt-4 flex flex-col items-center gap-2">
              <button v-for="q in EXAMPLES" :key="q" type="button"
                class="max-w-full rounded-full border px-3 py-1.5 text-left text-xs text-muted-foreground hover:bg-accent hover:text-foreground"
                @click="send(q)">
                {{ q }}
              </button>
            </div>
          </div>

          <template v-for="(m, i) in messages" :key="i">
            <div v-if="m.role === 'user'" class="ml-auto max-w-[85%] whitespace-pre-wrap rounded-2xl bg-primary px-3.5 py-2 text-sm text-primary-foreground">
              {{ m.content }}
            </div>

            <div v-else class="flex min-w-0 flex-col gap-2" data-role="assistant">
              <div v-if="m.content" class="min-w-0 wrap-break-word text-sm" @click="onAnswerClick(m, $event)">
                <MarkdownBody :content="withCites(withFormulas(m.content, m.formulas))" />
              </div>

              <!-- Until the first word: what the server is doing, within a second of asking. -->
              <span v-if="busy && i === messages.length - 1 && !m.content && !m.error"
                class="inline-flex items-center gap-2 text-sm text-muted-foreground" aria-label="Waiting for the model" data-status>
                <Loader2 class="h-3.5 w-3.5 animate-spin" />
                {{ m.status || 'খুঁজছি…' }}
              </span>

              <!-- The formula behind a tapped amount (hover shows it too). -->
              <p v-if="m.shownFormula != null && m.formulas[m.shownFormula]"
                class="flex items-start gap-1.5 rounded-md border bg-muted/40 px-3 py-1.5 font-mono text-xs" data-formula-line>
                <Calculator class="mt-px h-3.5 w-3.5 shrink-0 text-muted-foreground" />
                <span class="min-w-0 wrap-break-word">
                  <span class="font-sans text-muted-foreground">সূত্র · Formula:</span>
                  {{ m.formulas[m.shownFormula].expression }}
                  <template v-if="m.formulas[m.shownFormula].ok"> = {{ m.formulas[m.shownFormula].display }}</template>
                  <template v-else> — {{ m.formulas[m.shownFormula].error }}</template>
                </span>
              </p>

              <p v-if="m.error" class="rounded-md border border-destructive/40 bg-destructive/5 px-3 py-2 text-sm text-destructive">{{ m.error }}</p>

              <!-- The number check's verdict, in the answer's own place. -->
              <p v-if="m.unverified?.length || m.unverifiedFormulas?.length" class="flex items-start gap-1.5 rounded-md border border-destructive/60 bg-destructive/5 px-3 py-2 text-xs text-destructive dark:bg-destructive/30 dark:text-foreground">
                <TriangleAlert class="mt-px h-3.5 w-3.5 shrink-0" />
                <span>
                  এই সংখ্যাগুলো যাচাই করা যায়নি / Not verified:
                  {{ [...(m.unverified ?? []).map(formatAmount), ...(m.unverifiedFormulas ?? []).map((x) => `গণনা করা যায়নি (could not calculate): ${x}`)].join(', ') }}
                </span>
              </p>

              <!-- Context used: exactly what this answer was given. -->
              <details v-if="m.context" class="rounded-md border text-xs">
                <summary class="cursor-pointer select-none px-3 py-1.5 text-muted-foreground">
                  ব্যবহৃত প্রসঙ্গ · Context used — {{ m.context.excerpts.length }} excerpts, {{ contextTokens(m.context).toLocaleString() }} tokens
                </summary>
                <div class="space-y-2 border-t px-3 py-2">
                  <div>
                    <p class="font-medium">সবসময় · Always sent</p>
                    <ul class="mt-1 space-y-0.5">
                      <li v-for="a in m.context.always" :key="a.itemId" class="flex justify-between gap-2">
                        <button type="button" class="truncate text-left hover:underline" @click="openViewer({ itemId: a.itemId, label: a.name })">{{ a.name }} v{{ a.version }}</button>
                        <span class="shrink-0 text-muted-foreground">{{ a.tokens.toLocaleString() }}</span>
                      </li>
                    </ul>
                  </div>
                  <div>
                    <p class="font-medium">অংশ · Excerpts for this question</p>
                    <ul class="mt-1 space-y-0.5">
                      <li v-for="e in m.context.excerpts" :key="e.chunkId" class="flex justify-between gap-2">
                        <button type="button" class="truncate text-left font-mono hover:underline"
                          @click="openViewer({ itemId: e.itemId, label: e.sourceRef, lineFrom: e.lineFrom, lineTo: e.lineTo })">{{ e.sourceRef }}</button>
                        <span class="shrink-0 text-muted-foreground">{{ e.tokens.toLocaleString() }}</span>
                      </li>
                      <li v-if="!m.context.excerpts.length" class="text-muted-foreground">None matched.</li>
                    </ul>
                  </div>
                  <p class="text-muted-foreground">
                    {{ m.context.historyTurns }} earlier turns sent<template v-if="m.context.historyDropped">, {{ m.context.historyDropped }} oldest left out</template>
                    · system {{ m.context.tokens.system.toLocaleString() }} · window {{ m.context.tokens.window.toLocaleString() }}
                    <template v-if="m.context.scope?.length"> · scoped to {{ m.context.scope.length }} item(s)</template>
                    · {{ m.context.model }}
                  </p>
                </div>
              </details>
            </div>
          </template>

          <p v-if="error" class="text-sm text-destructive">{{ error }}</p>
        </div>
      </div>

      <!-- Composer, with the "Search in" picker -->
      <div class="shrink-0 border-t p-2 sm:p-3">
        <div class="relative mx-auto w-full max-w-3xl">
          <div v-if="scopeOpen" class="absolute bottom-full left-0 z-30 mb-2 max-h-72 w-full overflow-y-auto rounded-lg border bg-popover p-2 shadow-lg sm:w-96">
            <div class="mb-1 flex items-center justify-between">
              <p class="text-xs font-medium">খুঁজুন যেখানে · Search in</p>
              <button type="button" class="text-xs text-muted-foreground hover:text-foreground" @click="scope = []">সব · All</button>
            </div>
            <label v-for="it in activeItems" :key="it.id" class="flex items-center gap-2 rounded px-1 py-1 text-xs hover:bg-accent">
              <input v-model="scope" type="checkbox" :value="it.id" class="h-3.5 w-3.5 accent-primary">
              <span class="min-w-0 truncate">{{ it.name }}</span>
              <span class="ml-auto shrink-0 text-muted-foreground">{{ it.mode }}</span>
            </label>
          </div>

          <div class="flex flex-col gap-2 rounded-2xl border bg-card p-2 shadow-sm focus-within:ring-1 focus-within:ring-ring">
            <textarea
              ref="composer"
              v-model="input"
              rows="2"
              class="max-h-40 w-full resize-none bg-transparent px-2 py-1.5 text-sm outline-none placeholder:text-muted-foreground"
              placeholder="প্রশ্ন লিখুন · Ask in Bengali or English…"
              @keydown="onKeydown"
            />
            <div class="flex items-center gap-2">
              <button type="button"
                class="flex h-8 min-w-0 items-center gap-1.5 rounded-full border px-3 text-xs text-muted-foreground hover:bg-accent hover:text-foreground"
                @click="scopeOpen = !scopeOpen">
                <BookOpen class="h-3.5 w-3.5 shrink-0" />
                <span class="truncate">Search in: {{ scopeLabel }}</span>
                <ChevronDown class="h-3.5 w-3.5 shrink-0" />
              </button>
              <Button size="icon" class="ml-auto h-9 w-9 shrink-0 rounded-full" :variant="busy ? 'destructive' : 'default'"
                :disabled="!busy && !input.trim()" :aria-label="busy ? 'Stop' : 'Send'" @click="send()">
                <Square v-if="busy" class="h-3.5 w-3.5" />
                <Send v-else class="h-4 w-4" />
              </Button>
            </div>
          </div>
        </div>
      </div>
    </div>

    <HrItemViewer v-model:open="viewerOpen" :target="viewerTarget" />
  </div>
</template>
