// Base HTTP client. Every backend call goes through here so the base URL,
// auth header, JSON handling and error mapping live in exactly one place.
import { environment } from '../environments/environment.js'
import { getToken } from './session.service.js'

// Thrown when the server rejects our token — the UI uses this to bounce to login.
export class AuthError extends Error {
  constructor(message = 'Session expired. Please log in again.') {
    super(message)
    this.name = 'AuthError'
  }
}

// Any other non-2xx response, with the server's message when it sent one.
export class HttpError extends Error {
  constructor(message, status) {
    super(message)
    this.name = 'HttpError'
    this.status = status
  }
}

// ---- Access revision ----
// Every API response carries X-Access-Rev, the server's access-policy revision.
// When it moves, someone's role preset, overrides or role changed: the shell
// re-reads /api/me/access so its menus match what the server will now allow,
// on the user's next call rather than their next sign-in.
let accessRev = null
const accessListeners = new Set()

/** Calls fn() whenever a response shows the access revision has moved. Returns an unsubscribe. */
export function onAccessChange(fn) {
  accessListeners.add(fn)
  return () => accessListeners.delete(fn)
}

function noteAccessRev(res) {
  const rev = res.headers?.get?.('X-Access-Rev')
  if (!rev) return
  const moved = accessRev !== null && rev !== accessRev
  accessRev = rev
  if (moved) for (const fn of accessListeners) fn(rev)
}

function buildUrl(path) {
  return `${environment.apiBaseUrl}${path}`
}

function authHeaders(extra = {}) {
  const token = getToken()
  return token ? { ...extra, Authorization: `Bearer ${token}` } : extra
}

async function errorMessage(res) {
  try {
    const data = await res.clone().json()
    if (data && data.error) return data.error
  } catch { /* not JSON */ }
  try {
    return await res.text()
  } catch {
    return ''
  }
}

/**
 * Core request. Options:
 *  - method, body (JSON-serialized), signal
 *  - authError (default true): map 401 to AuthError. Login turns this off
 *    because its 401 means "wrong secret", not "session expired".
 */
async function request(path, { method = 'GET', body, signal, authError = true } = {}) {
  const res = await fetch(buildUrl(path), {
    method,
    headers: authHeaders(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
    body: body !== undefined ? JSON.stringify(body) : undefined,
    signal,
  })
  noteAccessRev(res)

  if (res.status === 401 && authError) throw new AuthError()
  if (!res.ok) {
    const err = new HttpError((await errorMessage(res)) || `Request failed (${res.status})`, res.status)
    // The JSON body when there is one: a refusal can carry more than its message (the Ministry of Truth's checklist problems).
    try { err.data = await res.clone().json() } catch { err.data = null }
    throw err
  }
  return res
}

export const http = {
  async get(path, options) {
    return (await request(path, options)).json()
  },

  /** Raw Response — for endpoints that may answer 204 (no body to parse). */
  async raw(path, options) {
    return request(path, options)
  },

  async post(path, body, options) {
    return (await request(path, { ...options, method: 'POST', body })).json()
  },

  async put(path, body, options) {
    return (await request(path, { ...options, method: 'PUT', body })).json()
  },

  /**
   * POST multipart/form-data (file uploads). Pass a FormData instance;
   * the browser sets the Content-Type boundary itself.
   */
  async postForm(path, formData, { signal } = {}) {
    const res = await fetch(buildUrl(path), {
      method: 'POST',
      headers: authHeaders(),
      body: formData,
      signal,
    })
    noteAccessRev(res)
    if (res.status === 401) throw new AuthError()
    if (!res.ok) throw new HttpError((await errorMessage(res)) || `Upload failed (${res.status})`, res.status)
    return res.json()
  },

  async del(path, options) {
    await request(path, { ...options, method: 'DELETE' })
  },

  /**
   * A call outside the app's own /api — the Oddity gateway — with the caller's own
   * headers and NO session token: the gateway takes its own key, and an app session
   * must never reach it. Resolves with the raw Response for every status, so the
   * API explorer can show an error exactly as it came back.
   */
  async external(url, { method = 'GET', headers = {}, body, signal } = {}) {
    return fetch(url, { method, headers, body, signal })
  },

  /**
   * POST and stream the raw response body. Calls onChunk with each decoded
   * text chunk; resolves with the Response (headers stay readable) once done.
   */
  async stream(path, body, onChunk, signal) {
    const res = await request(path, { method: 'POST', body, signal })
    const reader = res.body.getReader()
    const decoder = new TextDecoder()
    while (true) {
      const { value, done } = await reader.read()
      if (done) break
      onChunk(decoder.decode(value, { stream: true }))
    }
    return res
  },
}
