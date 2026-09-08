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
 *
 * The response is NDJSON — one tagged frame per line — because a reasoning
 * model's scratch work and a tool's progress have to stay distinguishable from
 * the answer itself, which concatenated text cannot express.
 *
 * @param {object} args
 *   { conversationId, source, model, message, webSearch, think, activeSourceIds }
 *   - source selects which backend answers ("local" | "cloud"); omitted means local.
 *   - webSearch grounds this one turn on live web results (per-message opt-in).
 *   - activeSourceIds are the ContextSource ids prepended to the system prompt.
 * @param {{
 *   onContent?: (text: string) => void,
 *   onThinking?: (text: string) => void,
 *   onTool?: (tool: {tool: string, status: string, detail?: string}) => void,
 *   onError?: (message: string) => void,
 * }} handlers
 * @param {AbortSignal} signal
 * @returns {Promise<number>} the conversation id (new or existing)
 */
export async function streamChat(
  { conversationId, source, model, message, webSearch = false, think = false, activeSourceIds = [] },
  handlers,
  signal,
) {
  // Frames can be split across network chunks, so hold the tail until its newline.
  let buffer = ''

  const consume = (line) => {
    const trimmed = line.trim()
    if (!trimmed) return

    let frame
    try {
      frame = JSON.parse(trimmed)
    } catch {
      return // partial or keep-alive line
    }

    switch (frame.type) {
      case 'content': handlers.onContent?.(frame.text ?? ''); break
      case 'thinking': handlers.onThinking?.(frame.text ?? ''); break
      case 'tool': handlers.onTool?.(frame); break
      case 'error': handlers.onError?.(frame.message ?? 'The model failed.'); break
      // 'done' carries no UI-visible payload the caller doesn't already know.
    }
  }

  const res = await http.stream(
    '/chat',
    { conversationId, source, model, message, webSearch, think, activeSourceIds },
    (chunk) => {
      buffer += chunk
      const lines = buffer.split('\n')
      buffer = lines.pop() ?? ''   // the last piece may be half a frame
      lines.forEach(consume)
    },
    signal,
  )

  consume(buffer) // a final frame with no trailing newline

  return Number(res.headers.get('X-Conversation-Id')) || conversationId
}
