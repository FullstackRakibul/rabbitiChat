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
      <div class="brand">RabbitIC</div>
      <p class="sub">Sign in to your chats</p>
      <input v-model="loginUser" placeholder="Username" autocomplete="username" />
      <input v-model="loginSecret" type="password" placeholder="Secret" autocomplete="current-password" />
      <button type="submit" :disabled="loginBusy">{{ loginBusy ? 'Signing in…' : 'Continue' }}</button>
      <p v-if="loginError" class="gate-err">{{ loginError }}</p>
      <p class="hint">A new username creates an account. Your secret is never sent in plain text after hashing on the server.</p>
    </form>
  </div>

  <!-- App -->
  <div v-else class="app">
    <!-- Sidebar -->
    <aside class="sidebar">
      <div class="brand">RabbitIC</div>
      <button class="newbtn" @click="newChat">+ New chat</button>
      <div class="threads">
        <div
          v-for="c in conversations"
          :key="c.id"
          class="thread"
          :class="{ active: c.id === currentId }"
          @click="openConversation(c.id)"
        >
          <span class="t">{{ c.title }}</span>
          <button class="del" title="Delete" @click.stop="removeConversation(c.id)">✕</button>
        </div>
        <p v-if="!conversations.length" class="empty-list">No conversations yet.</p>
      </div>

      <!-- User chip -->
      <div class="userchip">
        <span class="avatar">{{ initials }}</span>
        <span class="uname">{{ username }}</span>
        <button class="logout" title="Log out" @click="logout">Log out</button>
      </div>
    </aside>

    <!-- Main -->
    <main class="main">
      <header class="head">
        <select v-model="model">
          <option v-for="m in models" :key="m" :value="m">{{ m }}</option>
        </select>
        <span class="status">
          <span class="led" :class="{ on: connected }"></span>{{ status }}
        </span>
      </header>

      <div class="messages" ref="scroller">
        <p v-if="!messages.length" class="hello">Pick a model and say hi 👋</p>
        <div v-for="(m, i) in messages" :key="i" class="row" :class="m.role">
          <div class="bubble" :class="m.role">
            <!-- Assistant: rendered + sanitized markdown. User: plain text. -->
            <div
              v-if="m.role === 'assistant'"
              class="md"
              v-html="renderMarkdown(m.content)"
            ></div>
            <template v-else>{{ m.content }}</template>
            <span
              v-if="busy && i === messages.length - 1 && m.role === 'assistant'"
              class="caret"
            >▋</span>
          </div>
        </div>
      </div>

      <p v-if="error" class="error">{{ error }}</p>

      <footer class="composer">
        <input
          v-model="input"
          placeholder="Type a message…"
          @keydown.enter.exact.prevent="send"
        />
        <button class="send" :class="{ stop: busy }" @click="send">
          {{ busy ? 'Stop' : 'Send' }}
        </button>
      </footer>
    </main>
  </div>
</template>

<style>
:root {
  --bg: #ffffff;
  --panel: #f6f7f9;
  --border: #e5e8ec;
  --text: #1f2733;
  --muted: #6b7787;
  --accent: #14b8a6;
  --accent-ink: #053b35;
  --user-bg: #e8faf7;
  --user-border: #b7ece4;
  --danger: #e2685f;
}
* { box-sizing: border-box; }
body { margin: 0; font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif;
  background: var(--bg); color: var(--text); }

/* ---- Login gate ---- */
.gate { height: 100vh; display: flex; align-items: center; justify-content: center; background: var(--panel); }
.gate .card { width: 320px; background: var(--bg); border: 1px solid var(--border); border-radius: 16px;
  padding: 28px; display: flex; flex-direction: column; gap: 10px; box-shadow: 0 6px 24px rgba(20,30,50,.06); }
.gate .sub { margin: -4px 0 6px; color: var(--muted); font-size: 14px; }
.gate input { padding: 11px 13px; border: 1px solid var(--border); border-radius: 10px; font-size: 15px; }
.gate input:focus { outline: none; border-color: var(--accent); }
.gate button { padding: 11px; background: var(--accent); color: #fff; border: 0; border-radius: 10px;
  font-weight: 700; cursor: pointer; }
.gate button:disabled { opacity: .6; cursor: default; }
.gate-err { color: var(--danger); font-size: 13px; margin: 2px 0 0; }
.hint { color: var(--muted); font-size: 12px; line-height: 1.5; margin: 6px 0 0; }

/* ---- App shell ---- */
.app { display: flex; height: 100vh; background: var(--bg); color: var(--text); }

.sidebar { width: 260px; flex: 0 0 auto; background: var(--panel); border-right: 1px solid var(--border);
  display: flex; flex-direction: column; padding: 14px; }
.brand { font-weight: 700; letter-spacing: .5px; color: var(--accent); margin-bottom: 14px; }
.newbtn { width: 100%; padding: 9px; background: var(--bg); color: var(--text); border: 1px solid var(--border);
  border-radius: 10px; cursor: pointer; margin-bottom: 12px; }
.newbtn:hover { border-color: var(--accent); }
.threads { overflow-y: auto; flex: 1; }
.thread { display: flex; align-items: center; justify-content: space-between; gap: 6px;
  padding: 9px 10px; border-radius: 10px; cursor: pointer; color: #45505f; }
.thread:hover { background: #eef0f3; }
.thread.active { background: #e3f7f4; color: var(--accent-ink); }
.thread .t { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.del { background: none; border: 0; color: var(--muted); cursor: pointer; opacity: 0; }
.thread:hover .del { opacity: 1; }
.empty-list { color: var(--muted); font-size: 13px; padding: 6px 10px; }

/* User chip */
.userchip { display: flex; align-items: center; gap: 9px; padding: 10px 8px 4px;
  border-top: 1px solid var(--border); margin-top: 8px; }
.avatar { width: 30px; height: 30px; border-radius: 50%; background: var(--accent); color: #fff;
  display: grid; place-items: center; font-size: 12px; font-weight: 700; flex: 0 0 auto; }
.uname { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 14px; }
.logout { background: none; border: 0; color: var(--muted); cursor: pointer; font-size: 13px; }
.logout:hover { color: var(--danger); }

.main { flex: 1; display: flex; flex-direction: column; min-width: 0; }
.head { display: flex; align-items: center; gap: 12px; padding: 12px 18px; border-bottom: 1px solid var(--border); }
.head select { background: var(--bg); color: var(--text); border: 1px solid var(--border);
  border-radius: 10px; padding: 7px 10px; }
.status { color: var(--muted); font-size: 13px; display: inline-flex; align-items: center; gap: 7px; }
.led { width: 8px; height: 8px; border-radius: 50%; background: #c7ced8; display: inline-block; }
.led.on { background: var(--accent); box-shadow: 0 0 0 3px rgba(20,184,166,.18); }

.messages { flex: 1; overflow-y: auto; padding: 22px; }
.hello { color: var(--muted); text-align: center; margin-top: 18vh; }
.row { display: flex; margin-bottom: 16px; }
.row.user { justify-content: flex-end; }
.bubble { max-width: 75%; padding: 11px 15px; border-radius: 14px; line-height: 1.55; word-break: break-word; }
.bubble.assistant { background: var(--panel); border: 1px solid var(--border); border-top-left-radius: 4px; }
.bubble.user { background: var(--user-bg); border: 1px solid var(--user-border); border-top-right-radius: 4px;
  white-space: pre-wrap; }
.caret { color: var(--accent); }

/* Markdown rendering inside assistant bubbles */
.md > :first-child { margin-top: 0; }
.md > :last-child { margin-bottom: 0; }
.md p { margin: 0 0 10px; }
.md pre { background: #0f1720; color: #e6eef7; padding: 12px 14px; border-radius: 10px;
  overflow-x: auto; font-size: 13.5px; line-height: 1.5; }
.md code { font-family: "SFMono-Regular", Consolas, "Liberation Mono", Menlo, monospace; font-size: 0.92em; }
.md :not(pre) > code { background: #eef0f3; padding: 1px 6px; border-radius: 6px; }
.md pre code { background: none; padding: 0; }
.md ul, .md ol { margin: 0 0 10px; padding-left: 22px; }
.md li { margin: 3px 0; }
.md a { color: var(--accent); }
.md table { border-collapse: collapse; margin: 0 0 10px; }
.md th, .md td { border: 1px solid var(--border); padding: 6px 10px; }
.md blockquote { margin: 0 0 10px; padding-left: 12px; border-left: 3px solid var(--border); color: var(--muted); }

.error { color: var(--danger); padding: 0 22px 8px; font-size: 13px; }

.composer { display: flex; gap: 10px; padding: 14px 18px; border-top: 1px solid var(--border); }
.composer input { flex: 1; background: var(--bg); color: var(--text); border: 1px solid var(--border);
  border-radius: 12px; padding: 12px 14px; font-size: 15px; }
.composer input:focus { outline: none; border-color: var(--accent); }
.send { padding: 0 20px; background: var(--accent); color: #fff; border: 0; border-radius: 12px;
  font-weight: 700; cursor: pointer; }
.send.stop { background: var(--danger); }
</style>
