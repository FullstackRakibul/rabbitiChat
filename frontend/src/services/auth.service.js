import { http } from './http.js'
import { setSession, clearSession } from './session.service.js'

/**
 * Log in or auto-register. Returns { username, token, created }.
 * A new username creates an account; an existing one checks the secret.
 * authError:false so a 401 surfaces as "wrong secret", not a session bounce.
 */
export async function login(username, secret) {
  const data = await http.post('/auth/login', { username, secret }, { authError: false })
  setSession(data.username, data.token)
  return data
}

/**
 * Change the signed-in user's secret. The server rotates the session token —
 * every other session of this account is signed out — and this one keeps going
 * on the new token. A wrong current secret is a 403 with the server's message.
 */
export async function changeSecret(currentSecret, newSecret) {
  const data = await http.post('/auth/secret', { currentSecret, newSecret })
  setSession(data.username, data.token)
  return data
}

export function logout() {
  clearSession()
}
