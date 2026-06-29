// All backend calls go through here. In dev, /api is proxied to the .NET app by Vite.
const BASE = '/api';

const TOKEN_KEY = 'rabbitic.token';
const USER_KEY = 'rabbitic.username';

// ---- Session ----
export function getToken() {
  return localStorage.getItem(TOKEN_KEY) || '';
}
export function getUsername() {
  return localStorage.getItem(USER_KEY) || '';
}
function setSession(username, token) {
  localStorage.setItem(TOKEN_KEY, token);
  localStorage.setItem(USER_KEY, username);
}
export function clearSession() {
  localStorage.removeItem(TOKEN_KEY);
  localStorage.removeItem(USER_KEY);
}

// Attach the bearer token to a fetch headers object.
function authHeaders(extra = {}) {
  const token = getToken();
  return token ? { ...extra, Authorization: `Bearer ${token}` } : extra;
}

// Thrown when the server rejects our token — the UI uses this to bounce to login.
export class AuthError extends Error {
  constructor(message = 'Session expired. Please log in again.') {
    super(message);
    this.name = 'AuthError';
  }
}

// ---- Auth ----
/**
 * Log in or auto-register. Returns { username, token, created }.
 * A new username creates an account; an existing one checks the secret.
 */
export async function login(username, secret) {
  const res = await fetch(`${BASE}/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, secret }),
  });
  if (!res.ok) {
    const data = await res.json().catch(() => ({}));
    throw new Error(data.error || `Login failed (${res.status})`);
  }
  const data = await res.json();
  setSession(data.username, data.token);
  return data;
}

// ---- Data ----
export async function getModels() {
  const res = await fetch(`${BASE}/models`);
  if (!res.ok) throw new Error('Could not load models');
  const data = await res.json();
  return (data.models || []).map((m) => m.name);
}

export async function listConversations() {
  const res = await fetch(`${BASE}/conversations`, { headers: authHeaders() });
  if (res.status === 401) throw new AuthError();
  if (!res.ok) throw new Error('Could not load conversations');
  return res.json();
}

export async function getConversation(id) {
  const res = await fetch(`${BASE}/conversations/${id}`, { headers: authHeaders() });
  if (res.status === 401) throw new AuthError();
  if (!res.ok) throw new Error('Could not load conversation');
  return res.json();
}

export async function deleteConversation(id) {
  const res = await fetch(`${BASE}/conversations/${id}`, {
    method: 'DELETE',
    headers: authHeaders(),
  });
  if (res.status === 401) throw new AuthError();
}

/**
 * Send a message and stream the reply.
 * @param {object} args - { conversationId, model, message }
 * @param {(chunk: string) => void} onToken - called for each streamed token
 * @param {AbortSignal} signal
 * @returns {Promise<number>} the conversation id (new or existing)
 */
export async function streamChat({ conversationId, model, message }, onToken, signal) {
  const res = await fetch(`${BASE}/chat`, {
    method: 'POST',
    headers: authHeaders({ 'Content-Type': 'application/json' }),
    body: JSON.stringify({ conversationId, model, message }),
    signal,
  });

  if (res.status === 401) throw new AuthError();
  if (!res.ok) {
    const text = await res.text().catch(() => '');
    throw new Error(text || `Request failed (${res.status})`);
  }

  const convoId = Number(res.headers.get('X-Conversation-Id')) || conversationId;

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    onToken(decoder.decode(value, { stream: true }));
  }
  return convoId;
}
