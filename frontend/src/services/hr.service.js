// মানব সম্পদ (HR Assistant) — the HR library's endpoints (docs/HR_MODULE_PROMPT.md §3.2).
// Everything under /api/hr is gated by the `hr` tab on the server; who may change
// the library is decided there too, and reported back as `canEdit`.
import { http } from './http.js'

/**
 * Every library item, plus whether the caller may change the library:
 * { items: [{ id, name, hrType, mode, version, approvedBy, approvedAt, isActive,
 * sortOrder, chars, tokens, chunkCount, embeddedCount, updatedAt, canEdit }],
 * canEdit, types, modes, uploadExtensions }.
 */
export function listHrLibrary() {
  return http.get('/hr/library')
}

/** One item, with its content. */
export function getHrItem(id) {
  return http.get(`/hr/library/${id}`)
}

/** A `search` item's chunks: [{ sourceRef, section, lineFrom, lineTo, chars, tokens, embedded, text }]. */
export function getHrChunks(id) {
  return http.get(`/hr/library/${id}/chunks`)
}

/** Add by pasting text. Mode defaults by type on the server (procedure → search). */
export function createHrItem({ name, hrType, mode, content, approvedBy }) {
  return http.post('/hr/library', { name, hrType, mode, content, approvedBy })
}

/**
 * Add a .md, .txt, .sql or .csv file. The server reads it, honouring a byte-order
 * mark (some procedure exports are UTF-16), and infers name and type from the file.
 */
export function uploadHrFile(file) {
  const form = new FormData()
  form.append('file', file)
  return http.postForm('/hr/library/upload', form)
}

/**
 * Change an item. Only the fields given change; approvedBy '' clears the approval.
 * A content change raises the version and replaces the item's chunks.
 */
export function updateHrItem(id, changes) {
  return http.put(`/hr/library/${id}`, changes)
}

export function deleteHrItem(id) {
  return http.del(`/hr/library/${id}`)
}

/** Retry the chunks the embedding model could not take earlier. */
export function embedHrPending() {
  return http.post('/hr/library/embed', {})
}

/** Add one or many files in one request; each is answered on its own: { results: [{ file, ok, item, error }] }. */
export function uploadHrFiles(files, fields = {}) {
  const form = new FormData()
  for (const f of files) form.append('file', f)
  for (const [k, v] of Object.entries(fields)) if (v) form.append(k, v)
  return http.postForm('/hr/library/upload', form)
}

/** Queue an item whose embedding failed again. */
export function retryHrItem(id) {
  return http.post(`/hr/library/${id}/retry`, {})
}

// ───────────────────────── Ask ─────────────────────────

/**
 * Instant hybrid search, no model: the top 10 chunks with SourceRef, item name
 * and a snippet whose matches are wrapped in « ». `items` limits it to those ids.
 */
export function searchHr(q, items = []) {
  const scope = items.length ? `&items=${items.join(',')}` : ''
  return http.get(`/hr/search?q=${encodeURIComponent(q)}${scope}`)
}

export function listHrConversations() {
  return http.get('/hr/conversations')
}

/** One HR conversation: { id, title, messages: [{ role, content, meta: { context, formulas, unverified, unverifiedFormulas } }] }. */
export function getHrConversation(id) {
  return http.get(`/hr/conversations/${id}`)
}

export function deleteHrConversation(id) {
  return http.del(`/hr/conversations/${id}`)
}

/**
 * Ask the HR assistant; the answer streams. Frames: `status` (the line shown until
 * the first word), `context` (what was sent), `content`, `formula` (where a
 * calculated amount sits in the answer, and its formula), `error`, `unverified`
 * (amounts the number check could not trace, and formulas that could not be
 * calculated), `done`. `items` is the "Search in" scope; empty means the whole
 * library. Resolves with the conversation id.
 */
export async function streamHrChat({ conversationId, message, items = [], model }, handlers, signal) {
  let buffer = ''
  const consume = (line) => {
    const trimmed = line.trim()
    if (!trimmed) return
    let frame
    try { frame = JSON.parse(trimmed) } catch { return }
    switch (frame.type) {
      case 'status': handlers.onStatus?.(frame.text ?? ''); break
      case 'context': handlers.onContext?.(frame); break
      case 'content': handlers.onContent?.(frame.text ?? ''); break
      case 'formula': handlers.onFormula?.(frame); break
      case 'error': handlers.onError?.(frame.message ?? 'The model failed.'); break
      case 'unverified': handlers.onUnverified?.(frame.amounts ?? [], frame.formulas ?? [], frame.citations ?? []); break
    }
  }
  const res = await http.stream('/hr/chat', { conversationId, message, items, model }, (chunk) => {
    buffer += chunk
    const lines = buffer.split('\n')
    buffer = lines.pop() ?? ''
    lines.forEach(consume)
  }, signal)
  consume(buffer)
  return Number(res.headers.get('X-Conversation-Id')) || conversationId
}

// ───────────────────────── Rulebook (নিয়মাবলি) ─────────────────────────
// docs/HR_RULEBOOK_PROMPT.md §3. Who may write, and who may see a draft, is the
// server's decision; it says `canEdit`, and answers 403 to anything else.

/**
 * The pages this user may see: { pages: [{ id, title, category, language, status,
 * currentVersion, confirmedVersion, confirmedBy, confirmedAtUtc, libraryItemId,
 * draftInProgress, canEdit }], canEdit, categories: [{ key, bn, en }], languages, statuses }.
 * `q` searches titles and text.
 */
export function listRulebook({ q = '', category = '', status = '' } = {}) {
  const query = [
    q && `q=${encodeURIComponent(q)}`,
    category && `category=${encodeURIComponent(category)}`,
    status && `status=${encodeURIComponent(status)}`,
  ].filter(Boolean).join('&')
  return http.get(`/hr/rulebook${query ? `?${query}` : ''}`)
}

/**
 * One page at a version (default: the one this user opens), with
 * `version: { version, kind, changeNote, author, atUtc, body, display, formulas }`.
 */
export function getRulePage(id, version) {
  return http.get(`/hr/rulebook/${id}${version ? `?version=${version}` : ''}`)
}

/** Every version, newest last: [{ version, kind, changeNote, author, atUtc }]. */
export function listRuleVersions(id) {
  return http.get(`/hr/rulebook/${id}/versions`)
}

export function createRulePage({ title, category, language, body, note }) {
  return http.post('/hr/rulebook', { title, category, language, body, note })
}

/** A save is always a new version. `baseVersion` is where the editor started: a newer page answers 409. */
export function saveRulePage(id, { title, category, language, body, note, baseVersion }) {
  return http.put(`/hr/rulebook/${id}`, { title, category, language, body, note, baseVersion })
}

export function confirmRulePage(id, note) {
  return http.post(`/hr/rulebook/${id}/confirm`, { note })
}

export function retireRulePage(id, note) {
  return http.post(`/hr/rulebook/${id}/retire`, { note })
}

/** A new draft from an old version. */
export function restoreRuleVersion(id, version, note) {
  return http.post(`/hr/rulebook/${id}/restore`, { version, note })
}

/** Saves a file the server sends, under the name it gives. */
async function saveDownload(path, fallbackName) {
  const res = await http.raw(path)
  const blob = await res.blob()
  const disposition = res.headers.get('content-disposition') ?? ''
  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition)?.[1]
  const name = encoded ? decodeURIComponent(encoded) : /filename="?([^";]+)"?/i.exec(disposition)?.[1] ?? fallbackName
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = name
  a.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

/** One version as Markdown, under its header line. */
export function downloadRulePage(id, version) {
  return saveDownload(`/hr/rulebook/${id}/download${version ? `?version=${version}` : ''}`, `rulebook-${id}.md`)
}

/** Every confirmed page in one Markdown file, grouped by category. */
export function exportRulebook() {
  return saveDownload('/hr/rulebook/export', 'rulebook-confirmed.md')
}

/**
 * Works a formula out exactly as a [[= … ]] in a page is (the server's one
 * evaluator): { ok, result, display: '৳42,000', error }. The Rulebook editor's
 * preview calls it once per formula.
 */
export function calculateHr(expression) {
  return http.post('/hr/calculate', { expression, inline: true })
}

/** The number check before Confirm: { version, generatedVersion, unverified: [77777], failedFormulas }. */
export function checkRulePage(id, version) {
  return http.get(`/hr/rulebook/${id}/check${version ? `?version=${version}` : ''}`)
}

/**
 * Drafts a page from the Library; the draft streams. Frames: `status` (the line
 * shown until the first word), `context` (what was sent), `content` (the model's
 * words), `error`, then `draft` — the page as saved: { pageId, version, body,
 * unverified, formulas }. `pageId` adds a version to that page; without it a new
 * page is made from title, category and language. `keywords`: English words HR
 * adds to the search; the Library's glossary adds the English of Bengali terms itself.
 */
export async function streamRulebookDraft({ pageId, title, category, language, items = [], instruction, keywords }, handlers, signal) {
  let buffer = ''
  const consume = (line) => {
    const trimmed = line.trim()
    if (!trimmed) return
    let frame
    try { frame = JSON.parse(trimmed) } catch { return }
    switch (frame.type) {
      case 'status': handlers.onStatus?.(frame.text ?? ''); break
      case 'context': handlers.onContext?.(frame); break
      case 'content': handlers.onContent?.(frame.text ?? ''); break
      case 'error': handlers.onError?.(frame.message ?? 'The model failed.'); break
      case 'draft': handlers.onDraft?.(frame); break
    }
  }
  await http.stream('/hr/rulebook/generate', { pageId, title, category, language, items, instruction, keywords }, (chunk) => {
    buffer += chunk
    const lines = buffer.split('\n')
    buffer = lines.pop() ?? ''
    lines.forEach(consume)
  }, signal)
  consume(buffer)
}
