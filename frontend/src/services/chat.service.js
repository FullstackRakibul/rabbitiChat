import { http } from './http.js'

/** List installed Ollama model names (proxied through the backend). */
export async function getModels() {
  const data = await http.get('/models')
  return (data.models || []).map((m) => m.name)
}

/**
 * Send a message and stream the reply.
 * @param {object} args - { conversationId, model, message }
 * @param {(chunk: string) => void} onToken - called for each streamed token
 * @param {AbortSignal} signal
 * @returns {Promise<number>} the conversation id (new or existing)
 */
export async function streamChat({ conversationId, model, message }, onToken, signal) {
  const res = await http.stream('/chat', { conversationId, model, message }, onToken, signal)
  return Number(res.headers.get('X-Conversation-Id')) || conversationId
}
