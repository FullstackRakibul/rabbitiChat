// Web search. The upstream key lives only in backend configuration — these calls
// go to our own proxy, which attaches it server-side.
import { http } from './http.js'

/**
 * Run a web search.
 * @param {string} query
 * @param {number} maxResults - clamped to 1..10 by the backend
 * @param {AbortSignal} [signal]
 * @returns {Promise<Array<{title: string, url: string, content: string}>>}
 */
export async function search(query, maxResults = 5, signal) {
  const data = await http.post('/search', { query, maxResults }, { signal })
  return data.results || []
}

/**
 * Fetch one page's readable content (already truncated server-side).
 * @param {string} url - absolute http(s)
 * @param {AbortSignal} [signal]
 * @returns {Promise<{title: string, content: string, truncated: boolean}>}
 */
export async function fetchPage(url, signal) {
  return http.post('/search/fetch', { url }, { signal })
}

/**
 * Whether web search is usable right now, as judged server-side.
 * Never carries the key — only a status the UI can render.
 * @returns {Promise<{ok: boolean, status: string, detail: string}>}
 */
export async function health() {
  try {
    return await http.get('/search/health')
  } catch (e) {
    // 503 = not configured; the backend answers with the same shape, but http.js
    // throws on non-2xx, so map it back to a status the UI understands.
    if (e.status === 503) {
      return { ok: false, status: 'not_configured', detail: 'Web search is not configured on the server.' }
    }
    throw e
  }
}

// ---- History ----------------------------------------------------------------
// Query text and usage metadata only. Results are never stored server-side, so
// "reuse" is always a re-run through search() above, never a cache read.

/**
 * The current user's remembered queries, newest run first.
 * @param {boolean} [deleted] - true lists soft-deleted rows instead of active ones
 * @returns {Promise<Array<{id, query, title, maxResults, runCount, lastRunAt, createdAt, isDeleted}>>}
 */
export function listHistory(deleted = false) {
  return http.get(deleted ? '/search/history?deleted=true' : '/search/history')
}

/**
 * Rename one entry. An empty title clears it, so the row shows its query again.
 * @param {number} id
 * @param {string} title
 */
export function renameHistory(id, title) {
  return http.put(`/search/history/${id}`, { title })
}

/**
 * Soft-delete one entry — the row survives, so restoreHistory() is a real undo.
 * @param {number} id
 */
export function deleteHistory(id) {
  return http.raw(`/search/history/${id}`, { method: 'DELETE' }).then((r) => r.json())
}

/** @param {number} id */
export function restoreHistory(id) {
  return http.post(`/search/history/${id}/restore`)
}
