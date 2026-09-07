import { http } from './http.js'
import { listModels } from './model.service.js'

/**
 * Chat model names for the simple (flat) picker.
 *
 * The backend now filters embedding models and returns tagged entries, so this
 * just flattens the merged list. Use listModelsGrouped() when the picker needs
 * the local/cloud split.
 */
export async function getModels() {
  const models = await listModels()
  return models.map((m) => m.name)
}

/**
 * Send a message and stream the reply.
 * @param {object} args - { conversationId, source, model, message, webSearch }
 *   source selects which backend answers ("local" | "cloud"); omitted means local.
 *   webSearch grounds this one turn on live web results (per-message opt-in).
 * @param {(chunk: string) => void} onToken - called for each streamed token
 * @param {AbortSignal} signal
 * @returns {Promise<number>} the conversation id (new or existing)
 */
export async function streamChat(
  { conversationId, source, model, message, webSearch = false }, onToken, signal,
) {
  const res = await http.stream(
    '/chat', { conversationId, source, model, message, webSearch }, onToken, signal,
  )
  return Number(res.headers.get('X-Conversation-Id')) || conversationId
}
