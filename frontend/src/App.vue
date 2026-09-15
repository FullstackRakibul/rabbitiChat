<script setup>
import { ref, computed, onMounted, nextTick } from 'vue'
import {
  FileText, Globe, LayoutDashboard, ListTodo, MessageSquare, PanelLeft,
  Plug, Plus, Settings, Brain, Upload, Table, BadgeDollarSign, ShieldCheck
} from '@lucide/vue'
import {
  listModels, listConversations, getConversation,
  deleteConversation, streamChat,
  uploadDocument, listDocuments, deleteDocument, workspaceChat,
  login, getToken, getUsername, clearSession, AuthError,
  listContextSources, myTabs
} from './services/index.js'
import { environment } from './environments/environment.js'
import { useTheme } from '@/composables/useTheme'
import { useShortcuts } from '@/composables/useShortcuts'
import { Tabs, TabsContent } from '@/components/ui/tabs'
import LoginGate from '@/components/LoginGate.vue'
import AppShell from '@/components/AppShell.vue'
import ChatSidebar from '@/components/sidebars/ChatSidebar.vue'
import WorkspaceSidebar from '@/components/sidebars/WorkspaceSidebar.vue'
import CommandPalette from '@/components/CommandPalette.vue'
import NewChatDialog from '@/components/NewChatDialog.vue'
import ExtractionReview from '@/components/ExtractionReview.vue'
import DashboardTab from '@/components/tabs/DashboardTab.vue'
import ChatTab from '@/components/tabs/ChatTab.vue'
import WorkspaceTab from '@/components/tabs/WorkspaceTab.vue'
import DocumentsTab from '@/components/tabs/DocumentsTab.vue'
import TodoTab from '@/components/tabs/TodoTab.vue'
import SourcesTab from '@/components/tabs/SourcesTab.vue'
import TableTab from '@/components/tabs/TableTab.vue'
import CheckGenerator from '@/components/fivelakhs/CheckGenerator.vue'
import SearchTab from '@/components/tabs/SearchTab.vue'
import SettingsTab from '@/components/tabs/SettingsTab.vue'
import RbacTab from '@/components/tabs/RbacTab.vue'

const { theme, toggleTheme } = useTheme()

// ---- Session ----
const token = ref(getToken())
const username = ref(getUsername())
const loggedIn = computed(() => !!token.value)
const initials = computed(() =>
  (username.value || '?').trim().slice(0, 2).toUpperCase())

// Login form
const loginUser = ref('')
const loginSecret = ref('')
const loginBusy = ref(false)
const loginError = ref('')

async function doLogin() {
  if (loginBusy.value) return
  loginError.value = ''
  if (!loginUser.value.trim() || !loginSecret.value) {
    loginError.value = 'Enter a username and secret.'
    return
  }
  loginBusy.value = true
  try {
    const res = await login(loginUser.value.trim(), loginSecret.value)
    token.value = getToken()
    username.value = res.username
    loginSecret.value = ''
    await bootstrap()
  } catch (e) {
    loginError.value = e.message
  } finally {
    loginBusy.value = false
  }
}

function logout() {
  clearSession()
  token.value = ''
  username.value = ''
  conversations.value = []
  messages.value = []
  currentId.value = null

  // Drop the permission set too, so the next account never inherits the previous
  // one's tabs for even a frame.
  allowedTabs.value = null
  myRole.value = ''
  isSystemAdmin.value = false
  myUserId.value = 0
}

// If the server rejects our token mid-session, drop to the login gate.
function handleAuthError(e) {
  if (e instanceof AuthError) {
    logout()
    loginError.value = e.message
    return true
  }
  return false
}

// ---- Chat state ----
const models = ref([])          // names, for the pickers
const modelRecords = ref([])    // full records, for capabilities + context length
const model = ref('')
// Which source answers the next send. The picker owns the choice (and persists
// it); App just carries it to the API.
const modelSource = ref('local')
const conversations = ref([])
const currentId = ref(null)      // null = unsaved new chat
const messages = ref([])         // { role, content }
const input = ref('')
const busy = ref(false)
const status = ref('connecting…')
const connected = computed(() => status.value === 'connected')
const error = ref('')
let controller = null

/**
 * The chosen model's own record. Everything capability-gated reads from here,
 * so an unknown model simply offers no extra controls rather than guessing.
 */
const selectedModel = computed(() =>
  modelRecords.value.find(
    (m) => m.name === model.value && m.source === modelSource.value,
  ) ?? modelRecords.value.find((m) => m.name === model.value) ?? null)

const scroller = ref(null)
function scrollDown() {
  nextTick(() => {
    if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight
  })
}

async function refreshModels() {
  try {
    // The full records, not just names: the composer gates Think and Search on
    // each model's declared capabilities, and shows its real context window.
    modelRecords.value = await listModels()
    models.value = modelRecords.value.map((m) => m.name)

    if (models.value.length) {
      if (!models.value.includes(model.value)) model.value = models.value[0]
      status.value = 'connected'
    } else {
      status.value = 'no models — run: ollama pull llama3'
    }
  } catch {
    status.value = "can't reach backend / Ollama"
  }
}

async function refreshConversations() {
  try {
    conversations.value = await listConversations()
  } catch (e) {
    if (!handleAuthError(e)) { /* non-auth list failure: keep current list */ }
  }
}

async function openConversation(id) {
  try {
    const convo = await getConversation(id)
    currentId.value = convo.id
    messages.value = convo.messages.map((m) => ({
      role: m.role, content: m.content, webSearchUsed: m.webSearchUsed,
    }))

    // Restore the context set this chat was last used with, so reopening it
    // resumes with the same sources switched on rather than silently none.
    activeSourceIds.value = parseSourceIds(convo.activeSourceIds)

    activeTab.value = 'chat'
    closeSidebar()
    scrollDown()
  } catch (e) {
    if (!handleAuthError(e)) error.value = e.message
  }
}

function newChat() {
  currentId.value = null
  messages.value = []
  error.value = ''
  // A new chat starts from each source's own default switch.
  activeSourceIds.value = sources.value.filter((s) => s.isActive).map((s) => s.id)
}

/** The server stores the set as a comma list; drop anything unparsable. */
function parseSourceIds(csv) {
  if (!csv) return []
  return String(csv)
    .split(',')
    .map((x) => Number(x.trim()))
    .filter((n) => Number.isInteger(n) && n > 0)
}

async function removeConversation(id) {
  try {
    await deleteConversation(id)
    if (id === currentId.value) newChat()
    refreshConversations()
  } catch (e) {
    if (!handleAuthError(e)) error.value = e.message
  }
}

// Group conversations by recency for the sidebar (robust to missing dates).
const SEVEN_DAYS_MS = 7 * 24 * 60 * 60 * 1000
function conversationTime(c) {
  const raw = c.updated_at ?? c.updatedAt ?? c.created_at ?? c.createdAt ?? c.date ?? null
  if (raw == null) return null
  const ms = typeof raw === 'number' ? (raw < 1e12 ? raw * 1000 : raw) : Date.parse(raw)
  return Number.isNaN(ms) ? null : ms
}
const groupedConversations = computed(() => {
  const now = Date.now()
  const older = []
  const recent = []
  let anyDated = false
  for (const c of conversations.value) {
    const ms = conversationTime(c)
    if (ms != null) anyDated = true
    if (ms != null && now - ms <= SEVEN_DAYS_MS) recent.push(c)
    else older.push(c)
  }
  if (!anyDated) {
    return conversations.value.length
      ? [{ key: 'all', label: '', items: conversations.value }]
      : []
  }
  const groups = []
  if (older.length) groups.push({ key: 'older', label: '', items: older })
  if (recent.length) groups.push({ key: 'recent', label: 'Last 7 Days', items: recent })
  return groups
})

// Two independent things: the desktop collapse (Cmd/Ctrl+B) and the mobile
// drawer. Only Chat and Workspace render a sidebar at all.
const sidebarCollapsed = ref(false)
const mobileSidebarOpen = ref(false)
function closeSidebar() {
  mobileSidebarOpen.value = false
}

/** Tabs that fill the shell's sidebar slot; every other tab is full-bleed. */
const SIDEBAR_TABS = ['chat', 'workspace']
const tabHasSidebar = computed(() => SIDEBAR_TABS.includes(activeTab.value))

// Clear All — deletes every conversation via the existing API, then resets.
async function clearAllConversations() {
  if (!conversations.value.length) return
  if (!confirm('Delete all conversations? This cannot be undone.')) return
  try {
    for (const c of conversations.value) {
      await deleteConversation(c.id)
    }
    await refreshConversations()
    newChat()
  } catch (e) {
    if (!handleAuthError(e)) error.value = e.message
  }
}

async function send() {
  if (busy.value) { controller?.abort(); return }
  const text = input.value.trim()
  if (!text || !model.value) return

  error.value = ''
  messages.value.push({ role: 'user', content: text })
  input.value = ''
  scrollDown()

  // Capture and clear the toggle up-front: it applies to this message only.
  const useWebSearch = webSearch.value
  webSearch.value = false

  // webSearchUsed starts false and is set only if the server actually grounded
  // the answer — the toggle is a request, not a result.
  const reply = {
    role: 'assistant', content: '', thinking: '', thinkingDone: false,
    tools: [], webSearchUsed: false,
  }
  messages.value.push(reply)

  busy.value = true
  controller = new AbortController()
  try {
    const id = await streamChat(
      {
        conversationId: currentId.value,
        source: modelSource.value,
        model: model.value,
        message: text,
        webSearch: useWebSearch,
        think: think.value,
        activeSourceIds: activeSourceIds.value,
      },
      {
        onContent: (chunk) => {
          // The first content token ends the reasoning phase, which is what
          // collapses the thinking block.
          if (reply.thinking && !reply.thinkingDone) reply.thinkingDone = true
          reply.content += chunk
          scrollDown()
        },
        onThinking: (chunk) => { reply.thinking += chunk; scrollDown() },
        onTool: (t) => {
          // One card per tool, updated in place as its status changes.
          const existing = reply.tools.find((x) => x.tool === t.tool && x.detail === t.detail)
          if (existing) existing.status = t.status
          else reply.tools.push({ tool: t.tool, status: t.status, detail: t.detail })
          if (t.tool === 'web_search' && t.status === 'done') reply.webSearchUsed = true
          scrollDown()
        },
        onError: (message) => { error.value = message },
      },
      controller.signal,
    )
    if (currentId.value == null) {       // first turn just created a conversation
      currentId.value = id
      refreshConversations()
    }
  } catch (e) {
    if (handleAuthError(e)) return
    if (e.name !== 'AbortError') {
      error.value = e.message
      reply.content = reply.content || '⚠ ' + e.message
    }
  } finally {
    // Whatever streamed before a Stop is kept — the partial reply is still the
    // model's answer, and the server persisted it too.
    reply.thinkingDone = true
    busy.value = false
    controller = null
  }
}

// Load everything that requires a session.
//
// Permissions come first and alone: the calls below belong to tabs this user may
// not have, and the server answers 403 for those. Knowing the tab set first lets
// us skip exactly those calls instead of firing them and swallowing the refusals.
async function bootstrap() {
  await refreshPermissions()

  const may = (tab) => allowedTabs.value?.includes(tab)

  await Promise.all([
    refreshModels(),
    refreshConversations(),
    may('docs') || may('workspace') ? refreshDocuments() : Promise.resolve(),
    may('sources') ? refreshSources() : Promise.resolve(),
  ])
}

onMounted(() => {
  if (loggedIn.value) bootstrap()
})

// ─────────────────────────────────────────────────────────────
// WORKSPACE ADDITIONS (UI-only local state — no API changes)
// ─────────────────────────────────────────────────────────────

// Tab state
const activeTab = ref('dashboard') // dashboard | chat | workspace | docs | todo | search | jsonto | sources | settings | rbac

/**
 * Every tab this build can render, with the id the backend knows it by. Which of
 * these a given user actually sees is decided by the server (see `allowedTabs`),
 * never here — there is deliberately no per-role list in the frontend, so
 * changing someone's access needs no release.
 */
const ALL_TABS = [
  { id: 'dashboard', label: 'Dashboard', icon: LayoutDashboard },
  { id: 'chat', label: 'Chat', icon: MessageSquare },
  { id: 'workspace', label: 'Workspace', icon: Brain },
  { id: 'docs', label: 'Documents', icon: FileText },
  { id: 'todo', label: 'To-Do', icon: ListTodo },
  { id: 'search', label: 'Search', icon: Globe },
  { id: 'jsonto', label: 'JSON to ...', icon: Table },
  { id: 'fivelakhs', label: '5Lakhs', icon: BadgeDollarSign },
  { id: 'sources', label: 'Sources', icon: Plug },
  { id: 'settings', label: 'Settings', icon: Settings },
  { id: 'rbac', label: 'Access', icon: ShieldCheck },
]

// ---- Permissions ----
// What /api/me/tabs said this user may see. Null until it answers: the tab bar
// stays empty for that moment rather than flashing tabs the user may not have.
const allowedTabs = ref(null)
const myRole = ref('')
const isSystemAdmin = ref(false)
const myUserId = ref(0)

/** The tabs actually rendered — the catalogue filtered by the server's answer. */
const tabs = computed(() => {
  if (allowedTabs.value === null) return []
  return ALL_TABS.filter((t) => allowedTabs.value.includes(t.id))
})

async function refreshPermissions() {
  try {
    const me = await myTabs()
    allowedTabs.value = me.tabs
    myRole.value = me.role
    isSystemAdmin.value = me.isSystemAdmin
    myUserId.value = me.id

    // If the current tab is no longer permitted — a revoked grant, or a stale
    // tab restored from a previous session — fall back to the first one that is.
    if (!me.tabs.includes(activeTab.value)) {
      activeTab.value = me.tabs.includes('dashboard') ? 'dashboard' : (me.tabs[0] ?? 'dashboard')
    }
  } catch (e) {
    if (!handleAuthError(e)) {
      // Permissions unknown: show nothing rather than guessing a tab set. The
      // APIs would refuse anyway, so a guess could only mislead.
      allowedTabs.value = []
    }
  }
}

// Per-message web grounding for the next chat send. Resets after each send —
// it is never sticky for a conversation or profile.
const webSearch = ref(false)

// Ask reasoning-capable models to show their work. Sticky, unlike webSearch:
// it is a display preference, not a per-message action.
const think = ref(false)

// "Ask about these results" hands the query to the composer with search armed.
function askFromSearch(query) {
  input.value = query
  webSearch.value = true
  activeTab.value = 'chat'
}

// To-Do — todorabbit owns its own server-backed state; nothing to wire here.

// ---- Documents (PO ingestion) ----
const documents = ref([])
const docFile = ref(null)          // File chosen in the picker
const docBuyer = ref('')
const docUploading = ref(false)
const docError = ref('')
const docNotice = ref('')
const docsTab = ref(null)          // child component, for clearing its file input

function onDocPicked(file) {
  docFile.value = file
  docError.value = ''
  docNotice.value = ''
}

async function refreshDocuments() {
  try {
    documents.value = await listDocuments()
  } catch (e) {
    if (!handleAuthError(e)) docError.value = e.message
  }
}

async function doUpload() {
  if (docUploading.value || !docFile.value) return
  docError.value = ''
  docNotice.value = ''
  docUploading.value = true
  try {
    const res = await uploadDocument(docFile.value, docBuyer.value.trim())
    docNotice.value = `“${res.fileName}” ingested — ${res.chunks} chunks indexed.`
    docFile.value = null
    docsTab.value?.reset()
    await refreshDocuments()
  } catch (e) {
    if (!handleAuthError(e)) docError.value = e.message
  } finally {
    docUploading.value = false
  }
}

async function removeDocument(id) {
  if (!confirm('Delete this document and its indexed chunks?')) return
  try {
    await deleteDocument(id)
    await refreshDocuments()
  } catch (e) {
    if (!handleAuthError(e)) docError.value = e.message
  }
}

// ---- Extraction review overlay (the human-in-the-loop gate) ----
const reviewDoc = ref(null)          // { id, fileName } | null

function openReview(doc) {
  reviewDoc.value = { id: doc.id, fileName: doc.fileName }
}
function extractDoc(id) {
  const d = documents.value.find(x => x.id === id)
  if (d) openReview({ id: d.id, fileName: d.fileName })
}
function onReviewConfirmed() {
  refreshDocuments()
}

// ---- Workspace (agentic PO chat) ----
// Buyer profiles are local for now; they become backend entities in the next module.
const buyerProfiles = ['Kohls', 'Walmart', 'Target', 'H&M', 'Generic']
const wsBuyer = ref('Kohls')
const wsFile = ref(null)
const wsDoc = ref(null)            // last ingested document for this session
const wsUploading = ref(false)
const wsMessages = ref([])         // { role: 'user'|'assistant'|'event', content, meta? }
const wsInput = ref('')
const wsBusy = ref(false)
const wsError = ref('')
const wsExtracting = ref(false)
const wsTab = ref(null)            // child component, for clearing its file input
const wsScroller = ref(null)
let wsController = null

function wsScrollDown() {
  nextTick(() => {
    if (wsScroller.value) wsScroller.value.scrollTop = wsScroller.value.scrollHeight
  })
}

function wsEvent(content, meta = '') {
  wsMessages.value.push({ role: 'event', content, meta })
  wsScrollDown()
}

function onWsFilePicked(file) {
  wsFile.value = file
  wsError.value = ''
}

async function wsUpload() {
  if (wsUploading.value || !wsFile.value) return
  wsError.value = ''
  wsUploading.value = true
  wsEvent(`Uploading “${wsFile.value.name}” for buyer profile ${wsBuyer.value}…`, 'ingest')
  try {
    const res = await uploadDocument(wsFile.value, wsBuyer.value)
    wsDoc.value = res
    wsEvent(`Parsed, chunked and embedded — ${res.chunks} chunks indexed and ready for retrieval.`, 'done')
    wsFile.value = null
    wsTab.value?.reset()
    refreshDocuments()
  } catch (e) {
    if (handleAuthError(e)) return
    wsError.value = e.message
    wsEvent(`Ingestion failed: ${e.message}`, 'fail')
  } finally {
    wsUploading.value = false
  }
}

// RAG chat: the backend grounds the answer on the pinned document's chunks
// (or a vector search over all my chunks when nothing is pinned). Stateless —
// workspace turns are never saved to Conversations.
async function wsSend() {
  if (wsBusy.value) { wsController?.abort(); return }
  const text = wsInput.value.trim()
  if (!text || !model.value) return

  wsError.value = ''
  wsMessages.value.push({ role: 'user', content: text })
  wsInput.value = ''
  wsScrollDown()

  const reply = { role: 'assistant', content: '' }
  wsMessages.value.push(reply)

  wsBusy.value = true
  wsController = new AbortController()
  try {
    await workspaceChat(
      { documentId: wsDoc.value?.id ?? null, model: model.value, message: text },
      (chunk) => { reply.content += chunk; wsScrollDown() },
      wsController.signal,
    )
  } catch (e) {
    if (handleAuthError(e)) return
    if (e.name !== 'AbortError') {
      wsError.value = e.message
      reply.content = reply.content || '⚠ ' + e.message
    }
  } finally {
    wsBusy.value = false
    wsController = null
  }
}

// Extraction opens the review overlay: detect → parse → validate, then a human
// confirms (or corrects) before anything is accepted.
function wsExtract() {
  if (!wsDoc.value) return
  wsEvent(`Opened extraction review for “${wsDoc.value.fileName}”.`, 'ingest')
  openReview(wsDoc.value)
}

// Pin an already-ingested document and restore its saved thread (chat + extractions).
async function wsOpenDocument(d) {
  wsController?.abort()
  wsDoc.value = { id: d.id, fileName: d.fileName, chunks: d.chunks }
  if (d.buyer) wsBuyer.value = d.buyer
  wsMessages.value = []
  wsError.value = ''
  activeTab.value = 'workspace'

  if (d.conversationId) {
    try {
      const convo = await getConversation(d.conversationId)
      wsMessages.value = convo.messages.map((m) => ({ role: m.role, content: m.content }))
      wsScrollDown()
      return
    } catch (e) {
      if (handleAuthError(e)) return
    }
  }
  wsEvent(`Pinned “${d.fileName}” — ${d.chunks} chunks ready for retrieval.`, 'done')
}

function wsNewChat() {
  wsController?.abort()
  wsMessages.value = []
  wsDoc.value = null
  wsFile.value = null
  wsError.value = ''
  wsTab.value?.reset()
}

// ---- Connected sources ----
// Server-backed: the same ContextSource rows the Sources tab edits and the
// backend prepends to the system prompt. `activeSourceIds` is per-conversation.
const sources = ref([])
const activeSourceIds = ref([])
const sourceSearch = ref('')
const sourcesTab = ref(null)

const filteredSources = computed(() => {
  const q = sourceSearch.value.trim().toLowerCase()
  return q ? sources.value.filter(s => s.name.toLowerCase().includes(q)) : sources.value
})
const activeSources = computed(() =>
  sources.value.filter(s => activeSourceIds.value.includes(s.id)))
const activeSourcesCount = computed(() => activeSources.value.length)

async function refreshSources() {
  try {
    sources.value = await listContextSources('source')

    // A source deleted elsewhere must not linger in this chat's active set.
    const live = new Set(sources.value.map((s) => s.id))
    activeSourceIds.value = activeSourceIds.value.filter((id) => live.has(id))

    // An untouched new chat follows each source's own default switch.
    if (currentId.value == null && !activeSourceIds.value.length) {
      activeSourceIds.value = sources.value.filter((s) => s.isActive).map((s) => s.id)
    }
  } catch (e) {
    if (!handleAuthError(e)) { /* keep whatever list we have */ }
  }
}

/** The Sources tab saved something — take its list rather than re-fetching. */
function onSourcesChanged(list) {
  sources.value = list
  const live = new Set(list.map((s) => s.id))
  activeSourceIds.value = activeSourceIds.value.filter((id) => live.has(id))
}

// Chat creation flow: pick context + sources, then start a fresh chat.
const showNewChat = ref(false)
function openNewChat() {
  // Reset to each source's own default before the dialog shows them, so the
  // checkboxes describe the chat about to be started, not the previous one.
  newChat()
  refreshSources()
  showNewChat.value = true
  closeSidebar()
}
function startNewChat() {
  // newChat() already ran when the dialog opened; re-running it would discard
  // the source choices just made in it.
  currentId.value = null
  messages.value = []
  error.value = ''
  showNewChat.value = false
  activeTab.value = 'chat'
}

// ─────────────────────────────────────────────────────────────
// SHELL: command palette + keyboard shortcuts
// ─────────────────────────────────────────────────────────────

const paletteOpen = ref(false)

/** Sidebar document pick — reuses the tab's own restore-the-thread flow. */
function wsPickDocument(doc) {
  wsOpenDocument(doc)
  closeSidebar()
}

/** Cmd/Ctrl+B — only meaningful where a sidebar exists. */
function toggleSidebar() {
  if (!tabHasSidebar.value) return          // no-op on full-bleed tabs
  if (window.matchMedia('(max-width: 767px)').matches) {
    mobileSidebarOpen.value = !mobileSidebarOpen.value
  } else {
    sidebarCollapsed.value = !sidebarCollapsed.value
  }
}

/** Cmd/Ctrl+Enter — the primary action of whichever tab is showing. */
function primaryAction() {
  if (activeTab.value === 'chat') send()
  else if (activeTab.value === 'workspace') wsSend()
  else if (activeTab.value === 'dashboard') openNewChat()
}

function goTab(id) {
  // Keyboard jumps (g-then-c, and friends) bypass the tab bar, so the permission
  // check has to live here too rather than only in what gets rendered.
  if (allowedTabs.value && !allowedTabs.value.includes(id)) return
  activeTab.value = id
  closeSidebar()
}

// Palette rows. Tab navigation first, then the actions worth reaching by name.
//
// `tabs` is a computed, so script scope needs .value — the template gets auto
// unwrapping, script scope does not. Iterating the permission-filtered list is
// deliberate: the palette offers only tabs this user may actually open, which
// matches the guard in goTab().
const commands = computed(() => [
  ...tabs.value.map(t => ({
    id: `tab:${t.id}`,
    label: t.label,
    group: 'Go to',
    icon: t.icon,
    keys: { chat: 'g then c', workspace: 'g then w', docs: 'g then d', todo: 'g then t' }[t.id],
    run: () => goTab(t.id),
  })),
  {
    id: 'action:new-chat', label: 'New chat', group: 'Actions', icon: Plus,
    run: () => openNewChat(),
  },
  {
    id: 'action:upload', label: 'Upload a purchase order', group: 'Actions', icon: Upload,
    run: () => goTab('workspace'),
  },
  {
    id: 'action:sidebar', label: 'Toggle sidebar', group: 'Actions', icon: PanelLeft,
    keys: 'mod + B', run: () => toggleSidebar(),
  },
  {
    id: 'action:theme',
    label: theme.value === 'dark' ? 'Switch to light theme' : 'Switch to dark theme',
    group: 'Actions', icon: Settings, run: () => toggleTheme(),
  },
])

function runCommand(cmd) {
  cmd.run?.()
}

useShortcuts({
  enabled: () => loggedIn.value,
  onPalette: () => { paletteOpen.value = !paletteOpen.value },
  onSidebar: toggleSidebar,
  onSubmit: primaryAction,
  onEscape: () => {
    if (paletteOpen.value) paletteOpen.value = false
    else if (showNewChat.value) showNewChat.value = false
    else if (mobileSidebarOpen.value) mobileSidebarOpen.value = false
  },
  onJump: goTab,
})
</script>

<template>
  <!-- Login gate -->
  <LoginGate v-if="!loggedIn" v-model:username="loginUser" v-model:secret="loginSecret" :busy="loginBusy"
    :error="loginError" @submit="doLogin" />

  <!-- App. Tabs is the outermost element so the header's tab bar and the
       panels below share one root; unmount-on-hide=false keeps every panel
       alive across switches, so a streaming reply survives navigating away. -->
  <Tabs v-else v-model="activeTab" :unmount-on-hide="false" class="contents">
    <AppShell :tabs="tabs" :theme="theme" :connected="connected" :status="status"
      :has-sidebar="tabHasSidebar" :sidebar-open="!sidebarCollapsed"
      v-model:mobile-open="mobileSidebarOpen"
      @toggle-theme="toggleTheme" @open-palette="paletteOpen = true">
      <!-- Sidebar slot: filled only by the tabs that have one. -->
      <template v-if="tabHasSidebar" #sidebar>
        <ChatSidebar v-show="activeTab === 'chat'" v-model:source-search="sourceSearch"
          v-model:active-source-ids="activeSourceIds" :username="username"
          :initials="initials" :sources="sources" :filtered-sources="filteredSources"
          :active-sources-count="activeSourcesCount" :grouped-conversations="groupedConversations"
          :conversation-count="conversations.length" :current-id="currentId" @new-chat="openNewChat"
          @open-conversation="openConversation" @delete-conversation="removeConversation"
          @clear-all="clearAllConversations" @logout="logout"
          @open-settings="activeTab = 'settings'; closeSidebar()" />

        <WorkspaceSidebar v-show="activeTab === 'workspace'" :documents="documents" :active-doc="wsDoc"
          :messages="wsMessages" :busy="wsBusy" @new-session="wsNewChat" @open-document="wsPickDocument"
          @go-documents="activeTab = 'docs'; closeSidebar()" />
      </template>

      <!-- Panels -->
      <TabsContent value="dashboard" class="mt-0 min-h-0 flex-1 data-[state=inactive]:hidden">
        <DashboardTab :username="username" @navigate="goTab" @new-chat="openNewChat"
          @open-palette="paletteOpen = true" />
      </TabsContent>

      <TabsContent value="chat" class="mt-0 min-h-0 flex-1 data-[state=inactive]:hidden">
        <ChatTab v-model:model="model" v-model:source="modelSource" v-model:input="input"
          v-model:web-search="webSearch" v-model:think="think" v-model:active-source-ids="activeSourceIds"
          :models="models" :selected-model="selectedModel" :messages="messages" :busy="busy" :error="error"
          :sources="sources" @send="send" @new-chat="openNewChat"
          @manage-sources="goTab('sources')" @scroller="el => scroller = el" />
      </TabsContent>

      <TabsContent value="workspace" class="mt-0 min-h-0 flex-1 data-[state=inactive]:hidden">
        <WorkspaceTab ref="wsTab" v-model:buyer="wsBuyer" v-model:model="model" v-model:input="wsInput"
          :buyer-profiles="buyerProfiles" :models="models" :doc="wsDoc" :file="wsFile" :messages="wsMessages"
          :busy="wsBusy" :uploading="wsUploading" :extracting="wsExtracting" :error="wsError"
          @file-picked="onWsFilePicked" @upload="wsUpload" @extract="wsExtract" @send="wsSend" @new-chat="wsNewChat"
          @scroller="el => wsScroller = el" />
      </TabsContent>

      <TabsContent value="docs" class="mt-0 min-h-0 flex-1 data-[state=inactive]:hidden">
        <DocumentsTab ref="docsTab" v-model:buyer="docBuyer" :documents="documents" :file="docFile"
          :uploading="docUploading" :error="docError" :notice="docNotice" @file-picked="onDocPicked"
          @upload="doUpload" @delete="removeDocument" @review="extractDoc" />
      </TabsContent>

      <TabsContent value="todo" class="mt-0 min-h-0 flex-1 data-[state=inactive]:hidden">
        <TodoTab />
      </TabsContent>

      <TabsContent value="search" class="mt-0 min-h-0 flex-1 data-[state=inactive]:hidden">
        <SearchTab @ask="askFromSearch" />
      </TabsContent>

      <TabsContent value="jsonto" class="mt-0 min-h-0 flex-1 data-[state=inactive]:hidden">
        <TableTab />
      </TabsContent>

      <TabsContent value="fivelakhs" class="mt-0 min-h-0 flex-1 data-[state=inactive]:hidden">
        <CheckGenerator />
      </TabsContent>

      <TabsContent value="sources" class="mt-0 min-h-0 flex-1 data-[state=inactive]:hidden">
        <SourcesTab ref="sourcesTab" @changed="onSourcesChanged" />
      </TabsContent>

      <!-- Access control. Rendered only for a SystemAdmin — and the admin APIs
           refuse anyone else regardless, so this is convenience, not the control. -->
      <TabsContent v-if="isSystemAdmin" value="rbac"
        class="mt-0 min-h-0 flex-1 data-[state=inactive]:hidden">
        <RbacTab :current-user-id="myUserId" />
      </TabsContent>

      <TabsContent value="settings" class="mt-0 min-h-0 flex-1 data-[state=inactive]:hidden">
        <SettingsTab v-model:model="model" :username="username" :initials="initials"
          :status="status" :connected="connected" :api-base-url="environment.apiBaseUrl" :models="models"
          @logout="logout" />
      </TabsContent>
    </AppShell>

    <CommandPalette v-model:open="paletteOpen" :commands="commands" @run="runCommand" />

    <NewChatDialog v-model:open="showNewChat" v-model:active-source-ids="activeSourceIds"
      :sources="sources" @start="startNewChat" />

    <!-- Human-in-the-loop extraction review -->
    <ExtractionReview v-if="reviewDoc" :doc="reviewDoc" :model="model"
      @close="reviewDoc = null" @confirmed="onReviewConfirmed" />
  </Tabs>
</template>
