<script setup>
import { ref, onMounted, nextTick } from 'vue'
import {
  getModels, listConversations, getConversation,
  deleteConversation, streamChat,
} from './lib/api.js'

const models = ref([])
const model = ref('')
const conversations = ref([])
const currentId = ref(null)      // null = unsaved new chat
const messages = ref([])         // { role, content }
const input = ref('')
const busy = ref(false)
const status = ref('connecting…')
const error = ref('')
let controller = null

const scroller = ref(null)
function scrollDown() {
  nextTick(() => {
    if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight
  })
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
  try { conversations.value = await listConversations() } catch {}
}

async function openConversation(id) {
  const convo = await getConversation(id)
  currentId.value = convo.id
  messages.value = convo.messages.map((m) => ({ role: m.role, content: m.content }))
  scrollDown()
}

function newChat() {
  currentId.value = null
  messages.value = []
  error.value = ''
}

async function removeConversation(id) {
  await deleteConversation(id)
  if (id === currentId.value) newChat()
  refreshConversations()
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
    if (e.name !== 'AbortError') {
      error.value = e.message
      reply.content = reply.content || '⚠ ' + e.message
    }
  } finally {
    busy.value = false
    controller = null
  }
}

onMounted(() => {
  refreshModels()
  refreshConversations()
})
</script>

<template>
  <div class="app">
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
    </aside>

    <!-- Main -->
    <main class="main">
      <header class="head">
        <select v-model="model">
          <option v-for="m in models" :key="m" :value="m">{{ m }}</option>
        </select>
        <span class="status">{{ status }}</span>
      </header>

      <div class="messages" ref="scroller">
        <p v-if="!messages.length" class="hello">Pick a model and say hi 👋</p>
        <div v-for="(m, i) in messages" :key="i" class="row" :class="m.role">
          <div class="bubble" :class="m.role">
            {{ m.content }}<span v-if="busy && i === messages.length - 1 && m.role === 'assistant'" class="caret">▋</span>
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
* { box-sizing: border-box; }
body { margin: 0; font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif; }
.app { display: flex; height: 100vh; background: #0e1116; color: #e6eaf0; }

.sidebar { width: 250px; flex: 0 0 auto; background: #151b24; border-right: 1px solid #283341;
  display: flex; flex-direction: column; padding: 14px; }
.brand { font-weight: 700; letter-spacing: .5px; color: #4dd0c4; margin-bottom: 14px; }
.newbtn { width: 100%; padding: 9px; background: #1b232e; color: #e6eaf0; border: 1px solid #283341;
  border-radius: 8px; cursor: pointer; margin-bottom: 12px; }
.newbtn:hover { border-color: #4dd0c4; }
.threads { overflow-y: auto; flex: 1; }
.thread { display: flex; align-items: center; justify-content: space-between; gap: 6px;
  padding: 9px 10px; border-radius: 8px; cursor: pointer; color: #c2cad6; }
.thread:hover { background: #1b232e; }
.thread.active { background: #1f3a3a; color: #fff; }
.thread .t { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.del { background: none; border: 0; color: #6b7787; cursor: pointer; opacity: 0; }
.thread:hover .del { opacity: 1; }
.empty-list { color: #6b7787; font-size: 13px; padding: 6px 10px; }

.main { flex: 1; display: flex; flex-direction: column; min-width: 0; }
.head { display: flex; align-items: center; gap: 12px; padding: 12px 18px;
  border-bottom: 1px solid #283341; }
.head select { background: #1b232e; color: #e6eaf0; border: 1px solid #283341;
  border-radius: 8px; padding: 7px 10px; }
.status { color: #8a97a8; font-size: 13px; }

.messages { flex: 1; overflow-y: auto; padding: 22px; }
.hello { color: #6b7787; text-align: center; margin-top: 18vh; }
.row { display: flex; margin-bottom: 16px; }
.row.user { justify-content: flex-end; }
.bubble { max-width: 75%; padding: 11px 15px; border-radius: 14px; line-height: 1.55;
  white-space: pre-wrap; word-break: break-word; }
.bubble.assistant { background: #151b24; border: 1px solid #283341; border-top-left-radius: 4px; }
.bubble.user { background: #1f3a3a; border: 1px solid #2a5d58; border-top-right-radius: 4px; }
.caret { color: #4dd0c4; }

.error { color: #e2685f; padding: 0 22px 8px; font-size: 13px; }

.composer { display: flex; gap: 10px; padding: 14px 18px; border-top: 1px solid #283341; }
.composer input { flex: 1; background: #1b232e; color: #e6eaf0; border: 1px solid #283341;
  border-radius: 10px; padding: 12px 14px; font-size: 15px; }
.send { padding: 0 20px; background: #4dd0c4; color: #07211f; border: 0; border-radius: 10px;
  font-weight: 700; cursor: pointer; }
.send.stop { background: #e2685f; color: #fff; }
</style>
