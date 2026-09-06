import { http } from './http.js'

// Embedding models are installed alongside chat models but can't answer chat
// requests ("does not support chat"), so they never belong in the model picker.
const EMBEDDING_MODELS = /embed|bge-|gte-|e5-/i

/** List installed Ollama chat model names (proxied through the backend). */
export async function getModels() {
  const data = await http.get('/models')
  return (data.models || [])
    .map((m) => m.name)
    .filter((name) => !EMBEDDING_MODELS.test(name))
}

/**
 * Send a message and stream the reply.
 * @param {object} args - { conversationId, model, message, webSearch }
 *   webSearch grounds this one turn on live web results (per-message opt-in).
 * @param {(chunk: string) => void} onToken - called for each streamed token
 * @param {AbortSignal} signal
 * @returns {Promise<number>} the conversation id (new or existing)
 */
export async function streamChat(
  { conversationId, model, message, webSearch = false }, onToken, signal,
) {
  const res = await http.stream(
    '/chat', { conversationId, model, message, webSearch }, onToken, signal,
  )
  return Number(res.headers.get('X-Conversation-Id')) || conversationId
}
