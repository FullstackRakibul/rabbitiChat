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
