<script setup>
import { ref, computed, onMounted, nextTick } from 'vue'
import { marked } from 'marked'
import DOMPurify from 'dompurify'
import {
  getModels, listConversations, getConversation,
  deleteConversation, streamChat,
  login, getToken, getUsername, clearSession, AuthError,
} from './lib/api.js'

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

// ─────────────────────────────────────────────────────────────
// UI-ONLY ADDITIONS (per spec — no existing logic touched)
// ─────────────────────────────────────────────────────────────

// (1) Group conversations by recency for the sidebar. Robust to the API
//     not returning any timestamp: in that case everything falls into a
//     single "Your conversations" group and no "Last 7 Days" header shows.
const SEVEN_DAYS_MS = 7 * 24 * 60 * 60 * 1000
function conversationTime(c) {
  const raw = c.updated_at ?? c.updatedAt ?? c.created_at ?? c.createdAt ?? c.date ?? null
  if (raw == null) return null
  const ms = typeof raw === 'number' ? (raw < 1e12 ? raw * 1000 : raw) : Date.parse(raw)
  return Number.isNaN(ms) ? null : ms
}
const groupedConversations = computed(() => {
  const now = Date.now()
  const older = []   // > 7 days  → "Your conversations"
  const recent = []  // ≤ 7 days  → "Last 7 Days"
  let anyDated = false
  for (const c of conversations.value) {
    const ms = conversationTime(c)
    if (ms != null) anyDated = true
    if (ms != null && now - ms <= SEVEN_DAYS_MS) recent.push(c)
    else older.push(c)
  }
  // Fallback: no timestamps at all → one flat list, no date split.
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

// Mobile sidebar drawer (UI only). Closed by default; toggled from the header.
const sidebarOpen = ref(false)
function closeSidebar() {
  sidebarOpen.value = false
}

// Title shown in the main header: first user message, or a friendly default.
const chatTitle = computed(() => {
  const first = messages.value.find((m) => m.role === 'user')
  return first?.content?.trim() || 'New Chat'
})

// (2) Clear All — deletes every conversation via the existing API, then resets.
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
  await Promise.all([refreshModels(), refreshConversations()])
}

onMounted(() => {
  if (loggedIn.value) bootstrap()
})
</script>

<template>
  <!-- Login gate -->
  <div v-if="!loggedIn" class="gate">
    <form class="card" @submit.prevent="doLogin">
      <div class="gate-brand">CHAT&nbsp;A.I<span>+</span></div>
      <p class="sub">Sign in to your chats</p>
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

    <!-- Sidebar -->
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

      <div class="newrow">
        <button class="newbtn" @click="newChat(); closeSidebar()">
          <svg viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="currentColor" stroke-width="2.2"
            stroke-linecap="round">
            <path d="M12 5v14M5 12h14" />
          </svg>
          New chat
        </button>
        <button class="searchbtn" title="Search" aria-label="Search">
          <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.2"
            stroke-linecap="round">
            <circle cx="11" cy="11" r="7" />
            <path d="M21 21l-4-4" />
          </svg>
        </button>
      </div>

      <div class="convo-head">
        <span class="convo-label">Your conversations</span>
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
              <button class="tact" title="Rename" @click.stop>
                <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.8"
                  stroke-linecap="round" stroke-linejoin="round">
                  <path d="M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z" />
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
        <div class="settings-row">
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

    <!-- Main -->
    <main class="main">
      <header class="head">
        <button class="menubtn" title="Menu" aria-label="Open menu" @click="sidebarOpen = true">
          <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2"
            stroke-linecap="round">
            <path d="M4 7h16M4 12h16M4 17h16" />
          </svg>
        </button>
        <h1 class="chat-title">{{ chatTitle }}</h1>
        <div class="head-right">
          <span class="status" :title="status">
            <span class="led" :class="{ on: connected }"></span>
          </span>
          <div class="model-select">
            <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.8">
              <circle cx="12" cy="12" r="3" />
              <path
                d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.6 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.6a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9c.14.65.65 1.15 1.31 1.34H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
            </svg>
            <select v-model="model" title="Model">
              <option v-for="m in models" :key="m" :value="m">{{ m }}</option>
            </select>
          </div>
        </div>
      </header>

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
    </main>

    <!-- Decorative upgrade tab (visual only) -->
    <!-- <div class="upgrade" aria-hidden="true"><span>Upgrade to Pro</span></div> -->
  </div>
</template>

<style>
:root {
  --bg: #eef1fb;
  --panel: #ffffff;
  --border: #e9ebf3;
  --text: #14161f;
  --muted: #9aa1b4;
  --accent: #5b64f4;
  --accent-soft: #eceefe;
  --accent-ink: #4149d6;
  --user-bg: #eceefe;
  --user-border: #dce0fb;
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
  box-shadow: 0 20px 50px rgba(50, 50, 110, .10);
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
  background: #f7f8fd;
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
  padding: 16px;
  gap: 16px;
  position: relative;
}

/* ---- Sidebar ---- */
.sidebar {
  width: 288px;
  flex: 0 0 auto;
  background: var(--panel);
  border-radius: 22px;
  display: flex;
  flex-direction: column;
  padding: 22px 18px 18px;
  box-shadow: 0 10px 40px rgba(50, 55, 120, .06);
}

.side-top {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
}

.brand {
  font-weight: 800;
  font-size: 22px;
  letter-spacing: 1.5px;
  color: var(--text);
  padding: 0 6px 18px;
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
  background: #f4f5fb;
}

.backdrop {
  display: none;
}

.brand span {
  color: var(--accent);
}

.newrow {
  display: flex;
  gap: 10px;
  margin-bottom: 22px;
}

.newbtn {
  flex: 1;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 13px;
  background: var(--accent);
  color: #fff;
  border: 0;
  border-radius: 14px;
  font-size: 14.5px;
  font-weight: 600;
  cursor: pointer;
  box-shadow: 0 8px 18px rgba(91, 100, 244, .28);
}

.newbtn:hover {
  background: #4c55ee;
}

.searchbtn {
  flex: 0 0 auto;
  width: 46px;
  border-radius: 14px;
  background: #14161f;
  color: #fff;
  border: 0;
  cursor: pointer;
  display: grid;
  place-items: center;
}

.searchbtn:hover {
  background: #262a3a;
}

.convo-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 6px 6px;
}

.convo-label {
  color: var(--muted);
  font-size: 12.5px;
  font-weight: 500;
}

.clearall {
  background: none;
  border: 0;
  color: var(--accent);
  font-size: 12.5px;
  font-weight: 600;
  cursor: pointer;
  padding: 4px 2px;
}

.clearall:hover {
  color: var(--accent-ink);
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
  padding: 11px 12px;
  border-radius: 13px;
  cursor: pointer;
  color: #3a4152;
  position: relative;
}

.thread:hover {
  background: #f4f5fb;
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
  background: #cfd3e2;
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
  padding: 11px 14px;
  border: 1px solid var(--border);
  border-radius: 16px;
}

.settings-row {
  cursor: pointer;
}

.settings-row:hover {
  background: #f7f8fd;
}

.gear {
  display: grid;
  place-items: center;
  width: 30px;
  height: 30px;
  border-radius: 50%;
  background: #f2f3f9;
  color: #3a4152;
  flex: 0 0 auto;
}

.settings-label {
  font-size: 14px;
  font-weight: 500;
  color: #3a4152;
}

.avatar {
  width: 32px;
  height: 32px;
  border-radius: 50%;
  background: var(--accent);
  color: #fff;
  display: grid;
  place-items: center;
  font-size: 12px;
  font-weight: 700;
  flex: 0 0 auto;
  background-image: linear-gradient(135deg, #6b73f6, #8b63f0);
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

/* ---- Main ---- */
.main {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-width: 0;
  background: var(--panel);
  border-radius: 22px;
  box-shadow: 0 10px 40px rgba(50, 55, 120, .06);
  overflow: hidden;
}

.head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 18px 26px;
  border-bottom: 1px solid var(--border);
}

.chat-title {
  margin: 0;
  font-size: 17px;
  font-weight: 700;
  color: var(--text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.head-right {
  display: flex;
  align-items: center;
  gap: 12px;
  flex: 0 0 auto;
}

.status {
  display: inline-flex;
  align-items: center;
}

.led {
  width: 9px;
  height: 9px;
  border-radius: 50%;
  background: #cfd3e2;
  display: inline-block;
}

.led.on {
  background: #22c55e;
  box-shadow: 0 0 0 3px rgba(34, 197, 94, .16);
}

.model-select {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  padding: 7px 10px 7px 12px;
  background: #f5f6fb;
  border: 1px solid var(--border);
  border-radius: 12px;
  color: var(--muted);
}

.model-select select {
  background: none;
  border: 0;
  color: var(--text);
  font-size: 13px;
  font-weight: 500;
  cursor: pointer;
  outline: none;
  max-width: 150px;
}

.messages {
  flex: 1;
  overflow-y: auto;
  padding: 28px 32px;
}

.hello {
  color: var(--muted);
  text-align: center;
  margin-top: 18vh;
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
  box-shadow: 0 4px 16px rgba(50, 55, 120, .05);
}

.bubble.user {
  background: var(--user-bg);
  border: 1px solid var(--user-border);
  border-top-right-radius: 5px;
  color: #2b2f52;
  white-space: pre-wrap;
}

.who {
  font-size: 12px;
  font-weight: 700;
  color: var(--accent);
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
  background: #14161f;
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
  background: #f2f3f9;
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
  color: var(--accent);
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
  padding: 0 32px 8px;
  font-size: 13px;
}

/* ---- Composer ---- */
.composer {
  padding: 16px 32px 26px;
}

.composer-inner {
  display: flex;
  align-items: center;
  gap: 12px;
  background: #fff;
  border: 1px solid var(--border);
  border-radius: 999px;
  padding: 8px 8px 8px 18px;
  box-shadow: 0 8px 30px rgba(50, 55, 120, .10);
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
  color: #b3b8ca;
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
  box-shadow: 0 6px 16px rgba(91, 100, 244, .35);
}

.send:hover {
  background: #4c55ee;
}

.send.stop {
  background: var(--danger);
  box-shadow: 0 6px 16px rgba(226, 104, 95, .35);
}

/* Decorative upgrade tab */
.upgrade {
  position: absolute;
  right: 0;
  top: 50%;
  transform: translateY(-50%);
  background: var(--accent);
  border-radius: 12px 0 0 12px;
  padding: 20px 8px;
  box-shadow: 0 8px 20px rgba(91, 100, 244, .3);
}

.upgrade span {
  writing-mode: vertical-rl;
  transform: rotate(180deg);
  color: #fff;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: .5px;
}

/* ---- Responsive ---- */

/* Tablet: keep the sidebar but slim things down */
@media (max-width: 1024px) {
  .sidebar {
    width: 248px;
  }

  .bubble {
    max-width: 84%;
  }

  .messages {
    padding: 22px 20px;
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
    border-radius: 0 22px 22px 0;
    transform: translateX(-104%);
    transition: transform .25s ease;
    box-shadow: 0 10px 40px rgba(20, 22, 31, .25);
  }

  .sidebar.open {
    transform: translateX(0);
  }

  .backdrop {
    display: block;
    position: fixed;
    inset: 0;
    z-index: 30;
    background: rgba(20, 22, 31, .45);
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
    border-radius: 16px;
  }

  .head {
    padding: 12px 14px;
    gap: 8px;
  }

  .chat-title {
    font-size: 15px;
    flex: 1;
    min-width: 0;
  }

  .head-right {
    gap: 8px;
  }

  .model-select {
    padding: 6px 8px 6px 10px;
  }

  .model-select select {
    max-width: 110px;
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

  .upgrade {
    display: none;
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

  .model-select select {
    max-width: 84px;
  }

  .bubble {
    font-size: 14px;
  }
}



</style>