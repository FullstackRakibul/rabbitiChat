<script setup>
import { ref, computed, onMounted, nextTick } from 'vue'
import { marked } from 'marked'
import DOMPurify from 'dompurify'
import {
  getModels, listConversations, getConversation,
  deleteConversation, streamChat,
  uploadDocument, listDocuments, deleteDocument, extractDocument, getExtraction, workspaceChat,
  login, getToken, getUsername, clearSession, AuthError,
} from './services/index.js'
import { environment } from './environments/environment.js'

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
const models = ref([])
const model = ref('')
const conversations = ref([])
const currentId = ref(null)      // null = unsaved new chat
const messages = ref([])         // { role, content }
const input = ref('')
const busy = ref(false)
const status = ref('connecting…')
const connected = computed(() => status.value === 'connected')
const error = ref('')
let controller = null

const scroller = ref(null)
function scrollDown() {
  nextTick(() => {
    if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight
  })
}

// ---- Markdown ----
marked.setOptions({ breaks: true, gfm: true })
function renderMarkdown(text) {
  return DOMPurify.sanitize(marked.parse(text || ''))
}

async function refreshModels() {
  try {
    models.value = await getModels()
    if (models.value.length) {
      model.value = models.value[0]
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
    messages.value = convo.messages.map((m) => ({ role: m.role, content: m.content }))
    activeTab.value = 'chat'
    scrollDown()
  } catch (e) {
    if (!handleAuthError(e)) error.value = e.message
  }
}

function newChat() {
  currentId.value = null
  messages.value = []
  error.value = ''
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

// Mobile sidebar drawer. Closed by default; toggled from the tab bar.
const sidebarOpen = ref(false)
function closeSidebar() {
  sidebarOpen.value = false
}

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

  const reply = { role: 'assistant', content: '' }
  messages.value.push(reply)

  busy.value = true
  controller = new AbortController()
  try {
    const id = await streamChat(
      { conversationId: currentId.value, model: model.value, message: text },
      (chunk) => { reply.content += chunk; scrollDown() },
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
    busy.value = false
    controller = null
  }
}

// Load everything that requires a session.
async function bootstrap() {
  await Promise.all([refreshModels(), refreshConversations(), refreshDocuments()])
}

onMounted(() => {
  if (loggedIn.value) bootstrap()
})

// ─────────────────────────────────────────────────────────────
// WORKSPACE ADDITIONS (UI-only local state — no API changes)
// ─────────────────────────────────────────────────────────────

// Tab state
const activeTab = ref('chat') // 'chat' | 'workspace' | 'docs' | 'todo' | 'sources' | 'settings'
const tabs = [
  { id: 'chat', label: 'Chat' },
  { id: 'workspace', label: 'Workspace' },
  { id: 'docs', label: 'Documents' },
  { id: 'todo', label: 'To-Do' },
  { id: 'sources', label: 'Sources' },
  { id: 'settings', label: 'Settings' },
]

// To-Do List (local only)
const tasks = ref([])
const newTask = ref('')
const filterTodo = ref('all') // 'all' | 'active' | 'completed'

function addTask() {
  if (!newTask.value.trim()) return
  tasks.value.push({ id: Date.now(), text: newTask.value.trim(), done: false })
  newTask.value = ''
}

function toggleTask(id) {
  const task = tasks.value.find(t => t.id === id)
  if (task) task.done = !task.done
}

function deleteTask(id) {
  tasks.value = tasks.value.filter(t => t.id !== id)
}

const filteredTasks = computed(() => {
  if (filterTodo.value === 'active') return tasks.value.filter(t => !t.done)
  if (filterTodo.value === 'completed') return tasks.value.filter(t => t.done)
  return tasks.value
})
const activeTaskCount = computed(() => tasks.value.filter(t => !t.done).length)
const doneTaskCount = computed(() => tasks.value.filter(t => t.done).length)

// ---- Documents (PO ingestion) ----
const documents = ref([])
const docFile = ref(null)          // File chosen in the picker
const docBuyer = ref('')
const docUploading = ref(false)
const docError = ref('')
const docNotice = ref('')
const docInput = ref(null)         // <input type=file> element

function onDocPicked(e) {
  docFile.value = e.target.files?.[0] ?? null
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
    if (docInput.value) docInput.value.value = ''
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
    if (docResultId.value === id) docResult.value = null
    await refreshDocuments()
  } catch (e) {
    if (!handleAuthError(e)) docError.value = e.message
  }
}

// Structured extraction from the Documents tab — result shown inline below the row.
const docExtractingId = ref(null)
const docResultId = ref(null)
const docResult = ref(null)

async function extractDoc(id) {
  if (docExtractingId.value || !model.value) return
  docError.value = ''
  docNotice.value = ''
  docExtractingId.value = id
  if (docResultId.value !== id) docResult.value = null
  try {
    const res = await extractDocument(id, model.value)
    docResultId.value = id
    docResult.value = res
    docNotice.value = 'Extraction saved to the document and its workspace thread.'
    refreshDocuments()
    refreshConversations()
  } catch (e) {
    if (!handleAuthError(e)) docError.value = e.message
  } finally {
    docExtractingId.value = null
  }
}

// Show the JSON already stored on the server — no model run needed.
async function showStoredExtraction(id) {
  docError.value = ''
  docNotice.value = ''
  if (docResultId.value === id && docResult.value) { docResult.value = null; return } // toggle off
  try {
    const res = await getExtraction(id)
    if (!res) { docNotice.value = 'No stored extraction yet — run Extract JSON.'; return }
    docResultId.value = id
    docResult.value = res
  } catch (e) {
    if (!handleAuthError(e)) docError.value = e.message
  }
}

function copyDocResult() {
  if (!docResult.value) return
  navigator.clipboard.writeText(JSON.stringify(docResult.value.extracted, null, 2))
  docNotice.value = 'JSON copied to clipboard.'
}

// ---- Workspace (agentic PO chat) ----
// Buyer profiles are local for now; they become backend entities in the next module.
const buyerProfiles = ['Kohls', 'Walmart', 'Target', 'H&M', 'Generic']
const wsBuyer = ref('Kohls')
const wsFile = ref(null)
const wsDoc = ref(null)            // last ingested document for this session
const wsUploading = ref(false)
const wsDragging = ref(false)
const wsMessages = ref([])         // { role: 'user'|'assistant'|'event', content, meta? }
const wsInput = ref('')
const wsBusy = ref(false)
const wsError = ref('')
const wsExtracting = ref(false)
const wsFileInput = ref(null)
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

function onWsFilePicked(e) {
  wsFile.value = e.target.files?.[0] ?? null
  wsError.value = ''
}

function onWsDrop(e) {
  wsDragging.value = false
  const f = e.dataTransfer?.files?.[0]
  if (f) { wsFile.value = f; wsError.value = '' }
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
    if (wsFileInput.value) wsFileInput.value.value = ''
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

// One-click structured extraction of the pinned PO. The backend stores the JSON on
// the document AND appends it to the workspace conversation, so it survives reloads.
async function wsExtract() {
  if (wsExtracting.value || !wsDoc.value || !model.value) return
  wsError.value = ''
  wsExtracting.value = true
  wsEvent(`Extracting structured JSON from “${wsDoc.value.fileName}”…`, 'ingest')
  try {
    const res = await extractDocument(wsDoc.value.id, model.value)
    wsEvent(`Extraction complete — saved to this workspace thread. Sources: ${res.sources.join(', ')}`, 'done')
    wsMessages.value.push({
      role: 'assistant',
      content: '```json\n' + JSON.stringify(res.extracted, null, 2) + '\n```',
    })
    wsScrollDown()
    refreshDocuments()
    refreshConversations()
  } catch (e) {
    if (handleAuthError(e)) return
    wsError.value = e.message
    wsEvent(`Extraction failed: ${e.message}`, 'fail')
  } finally {
    wsExtracting.value = false
  }
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
  if (wsFileInput.value) wsFileInput.value.value = ''
}

// Context selector (local only)
const context = ref('General')
const contexts = ['General', 'Code', 'Research', 'Creative', 'Custom']

// Connected sources (local only)
const sources = ref([
  { id: 1, name: 'Seekingalpha', active: true },
  { id: 2, name: 'CNBC', active: true },
  { id: 3, name: 'Calbudgetcenter', active: false },
])
const sourceSearch = ref('')
const filteredSources = computed(() => {
  const q = sourceSearch.value.trim().toLowerCase()
  return q ? sources.value.filter(s => s.name.toLowerCase().includes(q)) : sources.value
})
const activeSources = computed(() => sources.value.filter(s => s.active))
const activeSourcesCount = computed(() => activeSources.value.length)

// Metrics (derived placeholders)
const topicsCount = computed(() => messages.value.filter(m => m.role === 'user').length)
const ideasCount = computed(() => messages.value.filter(m => m.role === 'assistant').length)
const docsCount = computed(() => conversations.value.length)

// Chat creation flow: pick context + sources, then start a fresh chat.
const showNewChat = ref(false)
function openNewChat() {
  showNewChat.value = true
  closeSidebar()
}
function startNewChat() {
  newChat()
  showNewChat.value = false
  activeTab.value = 'chat'
}
</script>

<template>
  <!-- Login gate -->
  <div v-if="!loggedIn" class="gate">
    <form class="card" @submit.prevent="doLogin">
      <div class="gate-brand">CHAT&nbsp;A.I<span>+</span></div>
      <p class="sub">Sign in to your workspace</p>
      <input v-model="loginUser" placeholder="Username" autocomplete="username" />
      <input v-model="loginSecret" type="password" placeholder="Secret" autocomplete="current-password" />
      <button type="submit" :disabled="loginBusy">{{ loginBusy ? 'Signing in…' : 'Continue' }}</button>
      <p v-if="loginError" class="gate-err">{{ loginError }}</p>
      <p class="hint">A new username creates an account. Your secret is never sent in plain text after hashing on the
        server.</p>
    </form>
  </div>

  <!-- App -->
  <div v-else class="app">
    <!-- Backdrop behind the mobile drawer -->
    <div class="backdrop" :class="{ show: sidebarOpen }" @click="closeSidebar"></div>

    <!-- Sources + conversations sidebar -->
    <aside class="sidebar" :class="{ open: sidebarOpen }">
      <div class="side-top">
        <div class="brand">মস্তিস্ক the&nbsp;ai<span>+</span></div>
        <button class="closebtn" title="Close menu" aria-label="Close menu" @click="closeSidebar">
          <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2.2"
            stroke-linecap="round">
            <path d="M6 6l12 12M18 6L6 18" />
          </svg>
        </button>
      </div>

      <button class="newbtn" @click="openNewChat">
        <svg viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="currentColor" stroke-width="2.2"
          stroke-linecap="round">
          <path d="M12 5v14M5 12h14" />
        </svg>
        New Chat
      </button>

      <!-- Connected sources -->
      <div class="src-section">
        <span class="section-label">Connected sources</span>
        <div class="src-search">
          <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2.2"
            stroke-linecap="round">
            <circle cx="11" cy="11" r="7" />
            <path d="M21 21l-4-4" />
          </svg>
          <input v-model="sourceSearch" placeholder="Search sources..." />
        </div>
        <div class="src-list">
          <label v-for="s in filteredSources" :key="s.id" class="src-row">
            <input type="checkbox" v-model="s.active" />
            <span class="src-name">{{ s.name }}</span>
            <span class="src-dot" :class="{ on: s.active }"></span>
          </label>
          <p v-if="!filteredSources.length" class="empty-list">No sources match.</p>
        </div>
        <p class="src-count">{{ activeSourcesCount }} of {{ sources.length }} active</p>
      </div>

      <!-- Conversations -->
      <div class="convo-head">
        <span class="section-label">Your conversations</span>
        <button class="clearall" @click="clearAllConversations">Clear All</button>
      </div>

      <div class="threads">
        <template v-for="g in groupedConversations" :key="g.key">
          <div v-if="g.label" class="group-label">{{ g.label }}</div>
          <div v-for="c in g.items" :key="c.id" class="thread" :class="{ active: c.id === currentId }"
            @click="openConversation(c.id); closeSidebar()">
            <svg class="tico" viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor"
              stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">
              <path
                d="M21 11.5a8.38 8.38 0 0 1-8.5 8.5 8.5 8.5 0 0 1-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 0 1-.9-3.8A8.5 8.5 0 0 1 12.5 3 8.38 8.38 0 0 1 21 11.5z" />
            </svg>
            <span class="t">{{ c.title }}</span>
            <span class="thread-actions">
              <button class="tact" title="Delete" @click.stop="removeConversation(c.id)">
                <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.8"
                  stroke-linecap="round" stroke-linejoin="round">
                  <path d="M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6" />
                </svg>
              </button>
              <span class="tdot" :class="{ on: c.id === currentId }"></span>
            </span>
          </div>
        </template>
        <p v-if="!conversations.length" class="empty-list">No conversations yet.</p>
      </div>

      <!-- Bottom -->
      <div class="side-bottom">
        <div class="settings-row" @click="activeTab = 'settings'; closeSidebar()">
          <span class="gear">
            <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.8">
              <circle cx="12" cy="12" r="3" />
              <path
                d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.6 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.6a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9c.14.65.65 1.15 1.31 1.34H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
            </svg>
          </span>
          <span class="settings-label">Settings</span>
        </div>
        <div class="userchip">
          <span class="avatar">{{ initials }}</span>
          <span class="uname">{{ username }}</span>
          <button class="logout" title="Log out" @click="logout">
            <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.8"
              stroke-linecap="round" stroke-linejoin="round">
              <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9" />
            </svg>
          </button>
        </div>
      </div>
    </aside>

    <!-- Main workspace -->
    <main class="main">
      <!-- Tab bar -->
      <div class="tabbar">
        <button class="menubtn" title="Menu" aria-label="Open menu" @click="sidebarOpen = true">
          <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2"
            stroke-linecap="round">
            <path d="M4 7h16M4 12h16M4 17h16" />
          </svg>
        </button>
        <div class="brand-mini">CHAT&nbsp;A.I<span>+</span></div>
        <nav class="tabs">
          <button v-for="t in tabs" :key="t.id" class="tab" :class="{ active: activeTab === t.id }"
            @click="activeTab = t.id">
            <svg v-if="t.id === 'settings'" viewBox="0 0 24 24" width="14" height="14" fill="none"
              stroke="currentColor" stroke-width="1.8">
              <circle cx="12" cy="12" r="3" />
              <path
                d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.6 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.6a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9c.14.65.65 1.15 1.31 1.34H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
            </svg>
            <span>{{ t.label }}</span>
          </button>
        </nav>
        <span class="status" :title="status">
          <span class="led" :class="{ on: connected }"></span>
        </span>
      </div>

      <!-- ── Chat tab ── -->
      <section v-show="activeTab === 'chat'" class="workspace">
        <div class="ws-head">
          <div class="ws-controls">
            <div class="select-pill" title="Model">
              <span class="select-label">Model</span>
              <select v-model="model">
                <option v-for="m in models" :key="m" :value="m">{{ m }}</option>
              </select>
            </div>
            <div class="select-pill" title="Context">
              <span class="select-label">Context</span>
              <select v-model="context">
                <option v-for="c in contexts" :key="c" :value="c">{{ c }}</option>
              </select>
            </div>
            <button class="newchat-mini" @click="openNewChat">
              <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2.2"
                stroke-linecap="round">
                <path d="M12 5v14M5 12h14" />
              </svg>
              New Chat
            </button>
          </div>
          <div class="metrics">
            <span class="metric topics">📊 Topics identified <b>{{ topicsCount }}</b></span>
            <span class="metric ideas">💡 Ideas generated <b>{{ ideasCount }}</b></span>
            <span class="metric docs">📄 Documents parsed <b>{{ docsCount }}</b></span>
          </div>
        </div>

        <div class="messages" ref="scroller">
          <p v-if="!messages.length" class="hello">Pick a model and say hi 👋</p>
          <div v-for="(m, i) in messages" :key="i" class="row" :class="m.role">
            <div class="bubble" :class="m.role">
              <div v-if="m.role === 'assistant'" class="who">CHAT A.I<span>+</span></div>
              <!-- Assistant: rendered + sanitized markdown. User: plain text. -->
              <div v-if="m.role === 'assistant'" class="md" v-html="renderMarkdown(m.content)"></div>
              <template v-else>{{ m.content }}</template>
              <span v-if="busy && i === messages.length - 1 && m.role === 'assistant'" class="caret">▋</span>
            </div>
          </div>
          <!-- Sources footer: which connected sources contributed -->
          <div v-if="messages.length && !busy && activeSources.length" class="src-footer">
            <span class="src-footer-label">Sources</span>
            <span v-for="s in activeSources" :key="s.id" class="chip">{{ s.name }}</span>
          </div>
        </div>

        <p v-if="error" class="error">{{ error }}</p>

        <footer class="composer">
          <div class="composer-inner">
            <span class="composer-ico">
              <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.7"
                stroke-linecap="round" stroke-linejoin="round">
                <path d="M12 2a7 7 0 0 0-4 12.7V17a2 2 0 0 0 2 2h4a2 2 0 0 0 2-2v-2.3A7 7 0 0 0 12 2z" />
                <path d="M9 21h6" />
              </svg>
            </span>
            <input v-model="input" placeholder="What's in your mind?..." @keydown.enter.exact.prevent="send" />
            <button class="send" :class="{ stop: busy }" :title="busy ? 'Stop' : 'Send'" @click="send">
              <svg v-if="!busy" viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor"
                stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <path d="M22 2 11 13M22 2l-7 20-4-9-9-4 20-7z" />
              </svg>
              <svg v-else viewBox="0 0 24 24" width="16" height="16" fill="currentColor">
                <rect x="6" y="6" width="12" height="12" rx="2" />
              </svg>
            </button>
          </div>
        </footer>
      </section>

      <!-- ── Workspace tab (agentic PO chat) ── -->
      <section v-show="activeTab === 'workspace'" class="workspace">
        <div class="ws-head">
          <div class="ws-controls">
            <div class="select-pill" title="Buyer profile">
              <span class="select-label">Buyer</span>
              <select v-model="wsBuyer">
                <option v-for="b in buyerProfiles" :key="b" :value="b">{{ b }}</option>
              </select>
            </div>
            <div class="select-pill" title="Model">
              <span class="select-label">Model</span>
              <select v-model="model">
                <option v-for="m in models" :key="m" :value="m">{{ m }}</option>
              </select>
            </div>
            <span v-if="wsDoc" class="chip doc-chip" :title="wsDoc.fileName">
              📎 {{ wsDoc.fileName }} · {{ wsDoc.chunks }} chunks
            </span>
            <button v-if="wsDoc" class="newchat-mini" :disabled="wsExtracting" @click="wsExtract">
              {{ wsExtracting ? 'Extracting…' : '{ } Extract JSON' }}
            </button>
            <button class="newchat-mini" @click="wsNewChat">
              <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2.2"
                stroke-linecap="round">
                <path d="M12 5v14M5 12h14" />
              </svg>
              New Chat
            </button>
          </div>
        </div>

        <div class="messages" ref="wsScroller">
          <!-- Empty-state hero: guides the buyer → upload → ask flow -->
          <div v-if="!wsMessages.length" class="ws-hero">
            <div class="ws-hero-icon">🧠</div>
            <h3>Agentic PO Workspace</h3>
            <p>Pick a buyer profile, drop in a purchase order, then ask anything about it.</p>
            <div class="ws-steps">
              <span class="ws-step"><b>1</b> Buyer: {{ wsBuyer }}</span>
              <span class="ws-step" :class="{ done: wsDoc }"><b>2</b> {{ wsDoc ? wsDoc.fileName : 'Upload the PO' }}</span>
              <span class="ws-step"><b>3</b> Ask & extract</span>
            </div>
          </div>

          <template v-for="(m, i) in wsMessages" :key="i">
            <!-- Agent activity cards -->
            <div v-if="m.role === 'event'" class="ws-event" :class="m.meta">
              <span class="ws-event-dot"></span>
              <span>{{ m.content }}</span>
            </div>
            <!-- Normal chat bubbles -->
            <div v-else class="row" :class="m.role">
              <div class="bubble" :class="m.role">
                <div v-if="m.role === 'assistant'" class="who">WORKSPACE AGENT<span>+</span></div>
                <div v-if="m.role === 'assistant'" class="md" v-html="renderMarkdown(m.content)"></div>
                <template v-else>{{ m.content }}</template>
                <span v-if="wsBusy && i === wsMessages.length - 1 && m.role === 'assistant'" class="caret">▋</span>
              </div>
            </div>
          </template>
        </div>

        <p v-if="wsError" class="error">{{ wsError }}</p>

        <!-- Upload strip: drag & drop or pick, then ingest -->
        <div class="ws-upload" :class="{ dragging: wsDragging }" @dragover.prevent="wsDragging = true"
          @dragleave.prevent="wsDragging = false" @drop.prevent="onWsDrop">
          <input ref="wsFileInput" type="file" accept=".pdf,.xlsx,.txt,.csv" class="ws-file-input"
            @change="onWsFilePicked" />
          <button class="btn-ghost ws-attach" @click="wsFileInput?.click()">
            📎 {{ wsFile ? wsFile.name : 'Attach PO (PDF, XLSX, TXT, CSV) — or drop it here' }}
          </button>
          <button v-if="wsFile" class="btn-accent" :disabled="wsUploading" @click="wsUpload">
            {{ wsUploading ? 'Ingesting…' : `Ingest for ${wsBuyer}` }}
          </button>
        </div>

        <footer class="composer">
          <div class="composer-inner">
            <span class="composer-ico">
              <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.7"
                stroke-linecap="round" stroke-linejoin="round">
                <path d="M12 2a7 7 0 0 0-4 12.7V17a2 2 0 0 0 2 2h4a2 2 0 0 0 2-2v-2.3A7 7 0 0 0 12 2z" />
                <path d="M9 21h6" />
              </svg>
            </span>
            <input v-model="wsInput" :placeholder="wsDoc ? `Ask about ${wsDoc.fileName}…` : 'Ask the workspace agent…'"
              @keydown.enter.exact.prevent="wsSend" />
            <button class="send" :class="{ stop: wsBusy }" :title="wsBusy ? 'Stop' : 'Send'" @click="wsSend">
              <svg v-if="!wsBusy" viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor"
                stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                <path d="M22 2 11 13M22 2l-7 20-4-9-9-4 20-7z" />
              </svg>
              <svg v-else viewBox="0 0 24 24" width="16" height="16" fill="currentColor">
                <rect x="6" y="6" width="12" height="12" rx="2" />
              </svg>
            </button>
          </div>
        </footer>
      </section>

      <!-- ── Documents tab (PO upload + ingestion) ── -->
      <section v-show="activeTab === 'docs'" class="panel">
        <div class="panel-card wide">
          <h2>Documents</h2>
          <p class="panel-sub">Upload a PO / order file (PDF, XLSX, TXT, CSV). It's parsed, chunked and embedded for
            retrieval.</p>

          <form class="doc-upload" @submit.prevent="doUpload">
            <input ref="docInput" type="file" accept=".pdf,.xlsx,.txt,.csv" @change="onDocPicked" />
            <input v-model="docBuyer" class="doc-buyer" placeholder="Buyer (e.g. Kohls) — optional" />
            <button type="submit" class="btn-accent" :disabled="docUploading || !docFile">
              {{ docUploading ? 'Ingesting…' : 'Upload' }}
            </button>
          </form>

          <p v-if="docNotice" class="doc-notice">{{ docNotice }}</p>
          <p v-if="docError" class="gate-err">{{ docError }}</p>

          <ul class="doc-list">
            <template v-for="d in documents" :key="d.id">
              <li class="doc-row">
                <span class="doc-status" :class="d.status">{{ d.status }}</span>
                <span class="doc-name" :title="d.fileName">{{ d.fileName }}</span>
                <span v-if="d.buyer" class="chip">{{ d.buyer }}</span>
                <span class="doc-meta">{{ d.chunks }} chunks</span>
                <button v-if="d.hasExtraction" class="doc-extract stored" title="Show the stored JSON"
                  @click="showStoredExtraction(d.id)">
                  ✓ Saved JSON
                </button>
                <button class="doc-extract" :disabled="d.status !== 'ready' || docExtractingId === d.id"
                  :title="d.status === 'ready' ? `Extract JSON using ${model}` : 'Document is not ready'"
                  @click="extractDoc(d.id)">
                  {{ docExtractingId === d.id ? 'Extracting…' : (d.hasExtraction ? '{ } Re-extract' : '{ } Extract JSON') }}
                </button>
                <button class="doc-extract" title="Open in Workspace" @click="wsOpenDocument(d)">Workspace →</button>
                <button class="task-del" title="Delete" @click="removeDocument(d.id)">✕</button>
              </li>
              <!-- Extraction result, inline under its row -->
              <li v-if="docResultId === d.id && docResult" class="doc-result">
                <div class="doc-result-head">
                  <span class="section-label">
                    Extracted JSON
                    <template v-if="docResult.sources.length"> · {{ docResult.sources.join(', ') }}</template>
                    <template v-if="docResult.extractedAt"> · saved {{ new Date(docResult.extractedAt).toLocaleString() }}</template>
                  </span>
                  <button class="btn-ghost doc-copy" @click="copyDocResult">Copy</button>
                </div>
                <pre>{{ JSON.stringify(docResult.extracted, null, 2) }}</pre>
              </li>
            </template>
          </ul>
          <p v-if="!documents.length" class="empty-list">No documents yet — upload your first PO above.</p>
        </div>
      </section>

      <!-- ── To-Do tab ── -->
      <section v-show="activeTab === 'todo'" class="panel">
        <div class="panel-card">
          <h2>To-Do List</h2>
          <p class="panel-sub">Quick tasks for this session — stored locally, not synced.</p>

          <form class="todo-add" @submit.prevent="addTask">
            <input v-model="newTask" placeholder="Add a task..." />
            <button type="submit" class="btn-accent">Add</button>
          </form>

          <div class="seg-filters">
            <button v-for="f in ['all', 'active', 'completed']" :key="f" class="seg"
              :class="{ active: filterTodo === f }" @click="filterTodo = f">
              {{ f[0].toUpperCase() + f.slice(1) }}
            </button>
          </div>

          <ul class="todo-list">
            <li v-for="t in filteredTasks" :key="t.id" class="task" :class="{ done: t.done }">
              <label class="task-main">
                <input type="checkbox" :checked="t.done" @change="toggleTask(t.id)" />
                <span class="task-text">{{ t.text }}</span>
              </label>
              <button class="task-del" title="Delete" @click="deleteTask(t.id)">✕</button>
            </li>
          </ul>

          <p v-if="!filteredTasks.length" class="empty-list">
            {{ tasks.length ? 'Nothing here for this filter.' : 'No tasks yet — add one above.' }}
          </p>
          <p v-if="tasks.length" class="todo-count">
            {{ activeTaskCount }} active · {{ doneTaskCount }} completed
          </p>
        </div>
      </section>

      <!-- ── Sources tab ── -->
      <section v-show="activeTab === 'sources'" class="panel">
        <div class="panel-card">
          <h2>Connected Sources</h2>
          <p class="panel-sub">Toggle which sources feed your chats. Local state only for now.</p>

          <div class="src-search big">
            <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.2"
              stroke-linecap="round">
              <circle cx="11" cy="11" r="7" />
              <path d="M21 21l-4-4" />
            </svg>
            <input v-model="sourceSearch" placeholder="Search sources..." />
          </div>

          <div class="src-list big">
            <label v-for="s in filteredSources" :key="s.id" class="src-row big">
              <input type="checkbox" v-model="s.active" />
              <span class="src-name">{{ s.name }}</span>
              <span class="src-state" :class="{ on: s.active }">{{ s.active ? 'Active' : 'Off' }}</span>
            </label>
          </div>

          <p v-if="!filteredSources.length" class="empty-list">No sources match your search.</p>
          <p class="todo-count">{{ activeSourcesCount }} of {{ sources.length }} sources active</p>
        </div>
      </section>

      <!-- ── Settings tab ── -->
      <section v-show="activeTab === 'settings'" class="panel">
        <div class="panel-card">
          <h2>Settings</h2>
          <p class="panel-sub">Session and workspace defaults.</p>

          <div class="set-group">
            <span class="section-label">Account</span>
            <div class="set-row">
              <span class="avatar">{{ initials }}</span>
              <span class="uname">{{ username }}</span>
              <button class="btn-ghost danger" @click="logout">Log out</button>
            </div>
          </div>

          <div class="set-group">
            <span class="section-label">Connection</span>
            <div class="set-row">
              <span class="led" :class="{ on: connected }"></span>
              <span class="set-text">{{ status }}</span>
              <span class="set-hint">API: {{ environment.apiBaseUrl }}</span>
            </div>
          </div>

          <div class="set-group">
            <span class="section-label">Defaults</span>
            <div class="set-row">
              <div class="select-pill">
                <span class="select-label">Model</span>
                <select v-model="model">
                  <option v-for="m in models" :key="m" :value="m">{{ m }}</option>
                </select>
              </div>
              <div class="select-pill">
                <span class="select-label">Context</span>
                <select v-model="context">
                  <option v-for="c in contexts" :key="c" :value="c">{{ c }}</option>
                </select>
              </div>
            </div>
          </div>
        </div>
      </section>
    </main>

    <!-- New chat modal: pick context + sources before starting -->
    <div v-if="showNewChat" class="modal-backdrop" @click.self="showNewChat = false">
      <div class="modal">
        <h3>Start a new chat</h3>
        <label class="modal-label">Context</label>
        <select v-model="context" class="modal-select">
          <option v-for="c in contexts" :key="c" :value="c">{{ c }}</option>
        </select>
        <label class="modal-label">Sources</label>
        <div class="modal-sources">
          <label v-for="s in sources" :key="s.id" class="src-row">
            <input type="checkbox" v-model="s.active" />
            <span class="src-name">{{ s.name }}</span>
          </label>
        </div>
        <div class="modal-actions">
          <button class="btn-ghost" @click="showNewChat = false">Cancel</button>
          <button class="btn-accent" @click="startNewChat">Start Chat</button>
        </div>
      </div>
    </div>
  </div>
</template>

<style>
:root {
  --bg: #eef2f4;
  --panel: #ffffff;
  --border: #e5eaee;
  --text: #13202a;
  --muted: #8b98a5;
  --accent: #14B8A6;
  --accent-ink: #0D9488;
  --accent-soft: #e3f6f3;
  --user-bg: #e3f6f3;
  --user-border: #c9ede7;
  --assistant-bg: #ffffff;
  --danger: #e2685f;
}

* {
  box-sizing: border-box;
}

body {
  margin: 0;
  font-family: "Anek Bangla", system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
  font-optical-sizing: auto;
  font-variation-settings: "wdth" 100;
  background: var(--bg);
  color: var(--text);
}

button,
input,
select,
textarea {
  font-family: inherit;
}

/* ---- Login gate ---- */
.gate {
  height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--bg);
}

.gate .card {
  width: 340px;
  background: var(--panel);
  border: 1px solid var(--border);
  border-radius: 20px;
  padding: 32px;
  display: flex;
  flex-direction: column;
  gap: 10px;
  box-shadow: 0 20px 50px rgba(13, 148, 136, .08);
}

.gate-brand {
  font-weight: 800;
  font-size: 24px;
  letter-spacing: 1px;
  color: var(--text);
  margin-bottom: 4px;
}

.gate-brand span {
  color: var(--accent);
}

.gate .sub {
  margin: -2px 0 8px;
  color: var(--muted);
  font-size: 14px;
}

.gate input {
  padding: 12px 14px;
  border: 1px solid var(--border);
  border-radius: 12px;
  font-size: 15px;
  background: #f6f9f9;
}

.gate input:focus {
  outline: none;
  border-color: var(--accent);
  background: #fff;
}

.gate button {
  padding: 12px;
  background: var(--accent);
  color: #fff;
  border: 0;
  border-radius: 12px;
  font-weight: 700;
  cursor: pointer;
}

.gate button:hover {
  background: var(--accent-ink);
}

.gate button:disabled {
  opacity: .6;
  cursor: default;
}

.gate-err {
  color: var(--danger);
  font-size: 13px;
  margin: 2px 0 0;
}

.hint {
  color: var(--muted);
  font-size: 12px;
  line-height: 1.5;
  margin: 6px 0 0;
}

/* ---- App shell ---- */
.app {
  display: flex;
  height: 100vh;
  background: var(--bg);
  color: var(--text);
  padding: 14px;
  gap: 14px;
  position: relative;
}

/* ---- Sidebar ---- */
.sidebar {
  width: 292px;
  flex: 0 0 auto;
  background: var(--panel);
  border-radius: 20px;
  display: flex;
  flex-direction: column;
  padding: 20px 16px 16px;
  box-shadow: 0 10px 40px rgba(13, 148, 136, .05);
}

.side-top {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
}

.brand {
  font-weight: 800;
  font-size: 21px;
  letter-spacing: 1.2px;
  color: var(--text);
  padding: 0 6px 14px;
}

.brand span {
  color: var(--accent);
}

/* Mobile-only controls (hidden on desktop) */
.closebtn,
.menubtn {
  display: none;
  background: none;
  border: 0;
  color: var(--muted);
  cursor: pointer;
  padding: 6px;
  border-radius: 10px;
  place-items: center;
}

.closebtn:hover,
.menubtn:hover {
  color: var(--text);
  background: #f2f6f6;
}

.backdrop {
  display: none;
}

.newbtn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 12px;
  background: var(--accent);
  color: #fff;
  border: 0;
  border-radius: 14px;
  font-size: 14.5px;
  font-weight: 600;
  cursor: pointer;
  box-shadow: 0 8px 18px rgba(20, 184, 166, .28);
  margin-bottom: 16px;
}

.newbtn:hover {
  background: var(--accent-ink);
}

.section-label {
  color: var(--muted);
  font-size: 12.5px;
  font-weight: 500;
}

/* ---- Connected sources (sidebar) ---- */
.src-section {
  border: 1px solid var(--border);
  border-radius: 14px;
  padding: 10px 10px 8px;
  margin-bottom: 14px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.src-search {
  display: flex;
  align-items: center;
  gap: 8px;
  background: #f4f8f8;
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 7px 10px;
  color: var(--muted);
}

.src-search input {
  flex: 1;
  border: 0;
  background: none;
  font-size: 13.5px;
  color: var(--text);
  min-width: 0;
}

.src-search input:focus {
  outline: none;
}

.src-list {
  display: flex;
  flex-direction: column;
  max-height: 132px;
  overflow-y: auto;
}

.src-row {
  display: flex;
  align-items: center;
  gap: 9px;
  padding: 7px 6px;
  border-radius: 9px;
  cursor: pointer;
  font-size: 13.5px;
  color: #33424e;
}

.src-row:hover {
  background: #f2f7f6;
}

.src-row input[type="checkbox"] {
  accent-color: var(--accent);
  width: 15px;
  height: 15px;
  cursor: pointer;
}

.src-name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.src-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: #cfd8de;
  flex: 0 0 auto;
}

.src-dot.on {
  background: var(--accent);
}

.src-count {
  margin: 0;
  padding: 0 6px 2px;
  color: var(--muted);
  font-size: 12px;
}

.convo-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 6px 6px;
}

.clearall {
  background: none;
  border: 0;
  color: var(--accent-ink);
  font-size: 12.5px;
  font-weight: 600;
  cursor: pointer;
  padding: 4px 2px;
}

.clearall:hover {
  text-decoration: underline;
}

.threads {
  overflow-y: auto;
  flex: 1;
  margin: 4px -4px 0;
  padding: 4px;
}

.group-label {
  color: var(--muted);
  font-size: 12px;
  font-weight: 500;
  padding: 14px 8px 6px;
  border-top: 1px solid var(--border);
  margin-top: 8px;
}

.thread {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 12px;
  border-radius: 12px;
  cursor: pointer;
  color: #33424e;
  position: relative;
}

.thread:hover {
  background: #f2f7f6;
}

.thread.active {
  background: var(--accent-soft);
  color: var(--accent-ink);
  font-weight: 600;
}

.tico {
  flex: 0 0 auto;
  opacity: .8;
}

.thread .t {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 14px;
}

.thread-actions {
  display: flex;
  align-items: center;
  gap: 4px;
  opacity: 0;
}

.thread:hover .thread-actions,
.thread.active .thread-actions {
  opacity: 1;
}

.tact {
  background: none;
  border: 0;
  color: var(--muted);
  cursor: pointer;
  padding: 3px;
  display: grid;
  place-items: center;
  border-radius: 7px;
}

.tact:hover {
  color: var(--text);
  background: rgba(0, 0, 0, .05);
}

.tdot {
  width: 9px;
  height: 9px;
  border-radius: 50%;
  background: #cfd8de;
  flex: 0 0 auto;
  margin-left: 2px;
}

.tdot.on {
  background: var(--accent);
}

.empty-list {
  color: var(--muted);
  font-size: 13px;
  padding: 10px;
  text-align: center;
}

/* Bottom */
.side-bottom {
  margin-top: 12px;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.settings-row,
.userchip {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 14px;
  border: 1px solid var(--border);
  border-radius: 15px;
}

.settings-row {
  cursor: pointer;
}

.settings-row:hover {
  background: #f4f8f8;
}

.gear {
  display: grid;
  place-items: center;
  width: 30px;
  height: 30px;
  border-radius: 50%;
  background: #eef4f3;
  color: #33424e;
  flex: 0 0 auto;
}

.settings-label {
  font-size: 14px;
  font-weight: 500;
  color: #33424e;
}

.avatar {
  width: 32px;
  height: 32px;
  border-radius: 50%;
  color: #fff;
  display: grid;
  place-items: center;
  font-size: 12px;
  font-weight: 700;
  flex: 0 0 auto;
  background-image: linear-gradient(135deg, #14B8A6, #0e9f8e);
}

.uname {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 14px;
  font-weight: 600;
}

.logout {
  background: none;
  border: 0;
  color: var(--muted);
  cursor: pointer;
  display: grid;
  place-items: center;
  padding: 4px;
}

.logout:hover {
  color: var(--danger);
}

/* ---- Main / tab bar ---- */
.main {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  background: var(--panel);
  border-radius: 20px;
  box-shadow: 0 10px 40px rgba(13, 148, 136, .05);
  overflow: hidden;
}

.tabbar {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 12px 20px;
  border-bottom: 1px solid var(--border);
}

.brand-mini {
  font-weight: 800;
  font-size: 16px;
  letter-spacing: .8px;
  white-space: nowrap;
}

.brand-mini span {
  color: var(--accent);
}

.tabs {
  display: flex;
  align-items: center;
  gap: 4px;
  flex: 1;
  min-width: 0;
  overflow-x: auto;
}

.tab {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 8px 14px;
  border: 0;
  background: none;
  border-radius: 10px;
  font-size: 13.5px;
  font-weight: 500;
  color: var(--muted);
  cursor: pointer;
  white-space: nowrap;
}

.tab:hover {
  color: var(--text);
  background: #f2f7f6;
}

.tab.active {
  background: var(--accent-soft);
  color: var(--accent-ink);
  font-weight: 700;
}

.status {
  display: inline-flex;
  align-items: center;
}

.led {
  width: 9px;
  height: 9px;
  border-radius: 50%;
  background: #cfd8de;
  display: inline-block;
  flex: 0 0 auto;
}

.led.on {
  background: #22c55e;
  box-shadow: 0 0 0 3px rgba(34, 197, 94, .16);
}

/* ---- Workspace (chat tab) ---- */
.workspace {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.ws-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 10px;
  padding: 12px 20px;
  border-bottom: 1px solid var(--border);
  background: #fbfdfd;
}

.ws-controls {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 10px;
}

.select-pill {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  padding: 6px 10px 6px 12px;
  background: #f4f8f8;
  border: 1px solid var(--border);
  border-radius: 11px;
}

.select-label {
  color: var(--muted);
  font-size: 12px;
  font-weight: 600;
}

.select-pill select {
  background: none;
  border: 0;
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  outline: none;
  max-width: 150px;
}

.newchat-mini {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 8px 14px;
  background: var(--accent);
  color: #fff;
  border: 0;
  border-radius: 11px;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
}

.newchat-mini:hover {
  background: var(--accent-ink);
}

.metrics {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
}

.metric {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 11px;
  border-radius: 999px;
  font-size: 12.5px;
  font-weight: 500;
  border: 1px solid transparent;
}

.metric b {
  font-weight: 800;
}

.metric.topics {
  background: var(--accent-soft);
  color: var(--accent-ink);
  border-color: #c9ede7;
}

.metric.ideas {
  background: #fdf4e3;
  color: #a97913;
  border-color: #f4e3bd;
}

.metric.docs {
  background: #e9effc;
  color: #3b64c4;
  border-color: #d4dff7;
}

/* ---- Messages ---- */
.messages {
  flex: 1;
  overflow-y: auto;
  padding: 26px 30px;
}

.hello {
  color: var(--muted);
  text-align: center;
  margin-top: 16vh;
}

.row {
  display: flex;
  margin-bottom: 18px;
}

.row.user {
  justify-content: flex-end;
}

.bubble {
  max-width: 74%;
  padding: 14px 18px;
  border-radius: 18px;
  line-height: 1.6;
  word-break: break-word;
  font-size: 14.5px;
}

.bubble.assistant {
  background: var(--assistant-bg);
  border: 1px solid var(--border);
  border-top-left-radius: 5px;
  box-shadow: 0 4px 16px rgba(13, 148, 136, .05);
}

.bubble.user {
  background: var(--user-bg);
  border: 1px solid var(--user-border);
  border-top-right-radius: 5px;
  color: #114b44;
  white-space: pre-wrap;
}

.who {
  font-size: 12px;
  font-weight: 700;
  color: var(--accent-ink);
  letter-spacing: .3px;
  margin-bottom: 7px;
}

.who span {
  margin-left: 1px;
}

.caret {
  color: var(--accent);
  animation: blink 1s steps(1) infinite;
}

@keyframes blink {
  50% {
    opacity: 0;
  }
}

/* Sources footer under the response */
.src-footer {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 7px;
  padding: 2px 4px 8px;
}

.src-footer-label {
  color: var(--muted);
  font-size: 12.5px;
  font-weight: 600;
  margin-right: 2px;
}

.chip {
  display: inline-flex;
  align-items: center;
  padding: 4px 11px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: #fff;
  font-size: 12.5px;
  font-weight: 500;
  color: #33424e;
}

/* Markdown rendering inside assistant bubbles */
.md> :first-child {
  margin-top: 0;
}

.md> :last-child {
  margin-bottom: 0;
}

.md p {
  margin: 0 0 10px;
}

.md pre {
  background: #13202a;
  color: #e6eef7;
  padding: 12px 14px;
  border-radius: 12px;
  overflow-x: auto;
  font-size: 13.5px;
  line-height: 1.5;
}

.md code {
  font-family: "SFMono-Regular", Consolas, "Liberation Mono", Menlo, monospace;
  font-size: 0.92em;
}

.md :not(pre)>code {
  background: #eef4f3;
  padding: 1px 6px;
  border-radius: 6px;
}

.md pre code {
  background: none;
  padding: 0;
}

.md ul,
.md ol {
  margin: 0 0 10px;
  padding-left: 22px;
}

.md li {
  margin: 3px 0;
}

.md a {
  color: var(--accent-ink);
}

.md table {
  border-collapse: collapse;
  margin: 0 0 10px;
}

.md th,
.md td {
  border: 1px solid var(--border);
  padding: 6px 10px;
}

.md blockquote {
  margin: 0 0 10px;
  padding-left: 12px;
  border-left: 3px solid var(--border);
  color: var(--muted);
}

.error {
  color: var(--danger);
  padding: 0 30px 8px;
  font-size: 13px;
}

/* ---- Composer ---- */
.composer {
  padding: 14px 30px 24px;
}

.composer-inner {
  display: flex;
  align-items: center;
  gap: 12px;
  background: #fff;
  border: 1px solid var(--border);
  border-radius: 999px;
  padding: 8px 8px 8px 18px;
  box-shadow: 0 8px 30px rgba(13, 148, 136, .08);
  max-width: 760px;
  margin: 0 auto;
}

.composer-ico {
  color: var(--accent);
  display: grid;
  place-items: center;
  flex: 0 0 auto;
}

.composer input {
  flex: 1;
  background: none;
  border: 0;
  color: var(--text);
  font-size: 15px;
  padding: 8px 0;
}

.composer input:focus {
  outline: none;
}

.composer input::placeholder {
  color: #a9b6bf;
}

.send {
  width: 44px;
  height: 44px;
  flex: 0 0 auto;
  background: var(--accent);
  color: #fff;
  border: 0;
  border-radius: 50%;
  cursor: pointer;
  display: grid;
  place-items: center;
  box-shadow: 0 6px 16px rgba(20, 184, 166, .35);
}

.send:hover {
  background: var(--accent-ink);
}

.send.stop {
  background: var(--danger);
  box-shadow: 0 6px 16px rgba(226, 104, 95, .35);
}

/* ---- Generic panels (To-Do / Sources / Settings tabs) ---- */
.panel {
  flex: 1;
  overflow-y: auto;
  padding: 26px 20px;
  min-height: 0;
}

.panel-card {
  max-width: 640px;
  margin: 0 auto;
  background: #fff;
  border: 1px solid var(--border);
  border-radius: 18px;
  padding: 26px 28px;
  box-shadow: 0 8px 30px rgba(13, 148, 136, .05);
}

.panel-card h2 {
  margin: 0 0 4px;
  font-size: 20px;
}

.panel-sub {
  margin: 0 0 18px;
  color: var(--muted);
  font-size: 13.5px;
}

.btn-accent {
  padding: 10px 18px;
  background: var(--accent);
  color: #fff;
  border: 0;
  border-radius: 11px;
  font-weight: 700;
  font-size: 13.5px;
  cursor: pointer;
}

.btn-accent:hover {
  background: var(--accent-ink);
}

.btn-ghost {
  padding: 10px 18px;
  background: none;
  border: 1px solid var(--border);
  border-radius: 11px;
  font-weight: 600;
  font-size: 13.5px;
  color: var(--text);
  cursor: pointer;
}

.btn-ghost:hover {
  background: #f2f7f6;
}

.btn-ghost.danger {
  color: var(--danger);
  border-color: #f2d4d1;
}

.btn-ghost.danger:hover {
  background: #fdf1f0;
}

/* ---- Workspace (agentic PO chat) ---- */
.ws-hero {
  max-width: 460px;
  margin: 10vh auto 0;
  text-align: center;
  color: var(--muted);
}

.ws-hero-icon {
  font-size: 42px;
  margin-bottom: 6px;
}

.ws-hero h3 {
  margin: 0 0 6px;
  color: var(--text);
  font-size: 19px;
}

.ws-hero p {
  margin: 0 0 18px;
  font-size: 14px;
}

.ws-steps {
  display: flex;
  justify-content: center;
  flex-wrap: wrap;
  gap: 8px;
}

.ws-step {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  padding: 7px 13px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: #fff;
  font-size: 12.5px;
  font-weight: 500;
  color: #33424e;
  max-width: 220px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ws-step b {
  display: grid;
  place-items: center;
  width: 18px;
  height: 18px;
  border-radius: 50%;
  background: var(--accent-soft);
  color: var(--accent-ink);
  font-size: 11px;
  flex: 0 0 auto;
}

.ws-step.done {
  border-color: #c9ede7;
  background: var(--accent-soft);
  color: var(--accent-ink);
}

/* Agent activity cards in the thread */
.ws-event {
  display: flex;
  align-items: center;
  gap: 9px;
  margin: 0 auto 14px;
  padding: 8px 14px;
  max-width: 560px;
  border: 1px dashed var(--border);
  border-radius: 12px;
  background: #fbfdfd;
  color: var(--muted);
  font-size: 13px;
}

.ws-event-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: #cfd8de;
  flex: 0 0 auto;
}

.ws-event.ingest .ws-event-dot {
  background: #f0b429;
  animation: blink 1s steps(1) infinite;
}

.ws-event.done {
  border-color: #c9ede7;
  color: var(--accent-ink);
}

.ws-event.done .ws-event-dot {
  background: var(--accent);
}

.ws-event.fail {
  border-color: #f2d4d1;
  color: var(--danger);
}

.ws-event.fail .ws-event-dot {
  background: var(--danger);
}

/* Upload strip above the composer */
.ws-upload {
  display: flex;
  align-items: center;
  gap: 10px;
  max-width: 760px;
  margin: 0 auto;
  padding: 0 30px;
  width: 100%;
}

.ws-upload.dragging .ws-attach {
  border-color: var(--accent);
  background: var(--accent-soft);
  color: var(--accent-ink);
}

.ws-file-input {
  display: none;
}

.ws-attach {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  text-align: left;
  border-style: dashed;
  color: var(--muted);
  font-weight: 500;
}

.doc-chip {
  max-width: 240px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  background: var(--accent-soft);
  border-color: #c9ede7;
  color: var(--accent-ink);
}

/* Documents */
.doc-upload {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  align-items: center;
  margin-bottom: 14px;
}

.doc-upload input[type="file"] {
  flex: 1 1 220px;
  min-width: 0;
  font-size: 13px;
  color: var(--muted);
}

.doc-buyer {
  flex: 1 1 180px;
  padding: 10px 14px;
  border: 1px solid var(--border);
  border-radius: 11px;
  font-size: 14px;
  background: #f6f9f9;
  min-width: 0;
}

.doc-buyer:focus {
  outline: none;
  border-color: var(--accent);
  background: #fff;
}

.doc-notice {
  color: var(--accent-ink);
  font-size: 13px;
  margin: 0 0 10px;
}

.doc-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.doc-row {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: 12px;
}

.doc-row:hover {
  background: #fafcfc;
}

.doc-row:hover .task-del {
  opacity: 1;
}

.doc-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 14px;
}

.doc-meta {
  color: var(--muted);
  font-size: 12.5px;
  white-space: nowrap;
}

.doc-status {
  font-size: 11.5px;
  font-weight: 700;
  border-radius: 999px;
  padding: 3px 10px;
  text-transform: capitalize;
  background: #f0f5f4;
  color: var(--muted);
}

.doc-status.ready {
  background: var(--accent-soft);
  color: var(--accent-ink);
}

.doc-status.failed {
  background: #fdf1f0;
  color: var(--danger);
}

.doc-extract {
  padding: 6px 12px;
  border: 1px solid var(--border);
  border-radius: 9px;
  background: #fff;
  font-size: 12.5px;
  font-weight: 600;
  color: var(--accent-ink);
  cursor: pointer;
  white-space: nowrap;
  flex: 0 0 auto;
}

.doc-extract:hover:not(:disabled) {
  background: var(--accent-soft);
  border-color: #c9ede7;
}

.doc-extract:disabled {
  color: var(--muted);
  cursor: default;
  opacity: .6;
}

.doc-extract.stored {
  background: var(--accent-soft);
  border-color: #c9ede7;
}

/* Extraction result block under its document row */
.doc-result {
  border: 1px solid var(--border);
  border-radius: 12px;
  padding: 12px 14px;
  background: #fbfdfd;
}

.doc-result-head {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 8px;
}

.doc-result-head .section-label {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.doc-copy {
  padding: 5px 12px;
  font-size: 12.5px;
  flex: 0 0 auto;
}

.doc-result pre {
  margin: 0;
  max-height: 340px;
  overflow: auto;
  background: #13202a;
  color: #e6eef7;
  padding: 12px 14px;
  border-radius: 10px;
  font-family: "SFMono-Regular", Consolas, "Liberation Mono", Menlo, monospace;
  font-size: 12.5px;
  line-height: 1.5;
}

/* The Documents panel carries JSON, so give it more room than the other panels. */
.panel-card.wide {
  max-width: 860px;
}

/* To-Do */
.todo-add {
  display: flex;
  gap: 10px;
  margin-bottom: 14px;
}

.todo-add input {
  flex: 1;
  padding: 11px 14px;
  border: 1px solid var(--border);
  border-radius: 11px;
  font-size: 14px;
  background: #f6f9f9;
  min-width: 0;
}

.todo-add input:focus {
  outline: none;
  border-color: var(--accent);
  background: #fff;
}

.seg-filters {
  display: inline-flex;
  gap: 2px;
  background: #f0f5f4;
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 3px;
  margin-bottom: 12px;
}

.seg {
  padding: 6px 14px;
  border: 0;
  background: none;
  border-radius: 8px;
  font-size: 12.5px;
  font-weight: 600;
  color: var(--muted);
  cursor: pointer;
}

.seg.active {
  background: #fff;
  color: var(--accent-ink);
  box-shadow: 0 1px 4px rgba(0, 0, 0, .07);
}

.todo-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.task {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: 12px;
}

.task:hover {
  background: #fafcfc;
}

.task-main {
  display: flex;
  align-items: center;
  gap: 10px;
  flex: 1;
  min-width: 0;
  cursor: pointer;
}

.task-main input[type="checkbox"] {
  accent-color: var(--accent);
  width: 16px;
  height: 16px;
  flex: 0 0 auto;
  cursor: pointer;
}

.task-text {
  font-size: 14px;
  overflow-wrap: anywhere;
}

.task.done .task-text {
  text-decoration: line-through;
  color: var(--muted);
}

.task-del {
  background: none;
  border: 0;
  color: var(--muted);
  cursor: pointer;
  font-size: 13px;
  padding: 4px 7px;
  border-radius: 7px;
  opacity: 0;
}

.task:hover .task-del {
  opacity: 1;
}

.task-del:hover {
  color: var(--danger);
  background: #fdf1f0;
}

.todo-count {
  margin: 12px 0 0;
  color: var(--muted);
  font-size: 12.5px;
}

/* Sources tab (bigger rows) */
.src-search.big {
  margin-bottom: 12px;
}

.src-list.big {
  max-height: none;
  gap: 6px;
}

.src-row.big {
  border: 1px solid var(--border);
  border-radius: 12px;
  padding: 12px 14px;
  font-size: 14px;
}

.src-state {
  font-size: 12px;
  font-weight: 700;
  color: var(--muted);
  background: #f0f5f4;
  border-radius: 999px;
  padding: 3px 10px;
}

.src-state.on {
  color: var(--accent-ink);
  background: var(--accent-soft);
}

/* Settings tab */
.set-group {
  margin-bottom: 18px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.set-row {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 12px;
  padding: 12px 14px;
  border: 1px solid var(--border);
  border-radius: 13px;
}

.set-text {
  font-size: 14px;
  font-weight: 500;
}

.set-hint {
  margin-left: auto;
  color: var(--muted);
  font-size: 12.5px;
}

.set-row .uname {
  flex: 0 1 auto;
}

.set-row .btn-ghost {
  margin-left: auto;
}

/* ---- New chat modal ---- */
.modal-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(19, 32, 42, .45);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 60;
}

.modal {
  width: min(92vw, 380px);
  background: #fff;
  border-radius: 18px;
  padding: 24px;
  box-shadow: 0 24px 60px rgba(0, 0, 0, .2);
}

.modal h3 {
  margin: 0 0 14px;
  font-size: 17px;
}

.modal-label {
  display: block;
  color: var(--muted);
  font-size: 12.5px;
  font-weight: 600;
  margin: 10px 0 6px;
}

.modal-select {
  width: 100%;
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: 11px;
  font-size: 14px;
  background: #f6f9f9;
  outline: none;
}

.modal-select:focus {
  border-color: var(--accent);
  background: #fff;
}

.modal-sources {
  display: flex;
  flex-direction: column;
  border: 1px solid var(--border);
  border-radius: 11px;
  padding: 4px;
  max-height: 160px;
  overflow-y: auto;
}

.modal-actions {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
  margin-top: 18px;
}

/* ---- Responsive ---- */

/* Tablet: keep the sidebar but slim things down */
@media (max-width: 1024px) {
  .sidebar {
    width: 252px;
  }

  .bubble {
    max-width: 84%;
  }

  .messages {
    padding: 22px 20px;
  }

  .metrics {
    order: 3;
    width: 100%;
  }
}

/* Mobile: sidebar becomes an off-canvas drawer behind a hamburger button */
@media (max-width: 768px) {
  .app {
    padding: 10px;
    gap: 0;
  }

  .sidebar {
    position: fixed;
    inset: 0 auto 0 0;
    width: min(88vw, 320px);
    height: 100%;
    z-index: 40;
    border-radius: 0 20px 20px 0;
    transform: translateX(-104%);
    transition: transform .25s ease;
    box-shadow: 0 10px 40px rgba(19, 32, 42, .25);
  }

  .sidebar.open {
    transform: translateX(0);
  }

  .backdrop {
    display: block;
    position: fixed;
    inset: 0;
    z-index: 30;
    background: rgba(19, 32, 42, .45);
    opacity: 0;
    pointer-events: none;
    transition: opacity .25s ease;
  }

  .backdrop.show {
    opacity: 1;
    pointer-events: auto;
  }

  .closebtn,
  .menubtn {
    display: grid;
  }

  .main {
    border-radius: 14px;
  }

  .tabbar {
    padding: 10px 12px;
    gap: 8px;
  }

  .brand-mini {
    display: none;
  }

  .ws-head {
    padding: 10px 12px;
  }

  .select-pill select {
    max-width: 104px;
    font-size: 12.5px;
  }

  .messages {
    padding: 16px 12px;
  }

  .bubble {
    max-width: 92%;
    padding: 12px 14px;
  }

  .error {
    padding: 0 16px 8px;
  }

  .composer {
    padding: 10px 12px 14px;
  }

  .composer-inner {
    gap: 8px;
    padding: 6px 6px 6px 14px;
  }

  .send {
    width: 40px;
    height: 40px;
  }

  .panel {
    padding: 14px 10px;
  }

  .panel-card {
    padding: 18px 16px;
  }

  .gate .card {
    width: min(92vw, 340px);
    padding: 26px 22px;
  }
}

/* Small phones */
@media (max-width: 420px) {
  .composer-ico {
    display: none;
  }

  .bubble {
    font-size: 14px;
  }

  .tab span {
    font-size: 12.5px;
  }

  .tab {
    padding: 7px 10px;
  }
}
</style>
