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

export function logout() {
  clearSession()
}
