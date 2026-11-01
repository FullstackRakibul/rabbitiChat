// Access → Models — /api/admin/rbac/model-access. Who may run which model in Chat, HR and Jiji.
//
// Rules are the server's: these calls read and replace a role's or a user's rules. A grant is
// { module, providerKey, modelId, maxMultiplier }: providerKey "*" = every Settings provider,
// modelId "*" = every model the provider offers, maxMultiplier caps "*" by cost. A user's rules
// only add to their role's.

import { http } from './http.js'

const BASE = '/admin/rbac/model-access'

export const MODEL_MODULES = [
  { key: 'chat', label: 'Chat' },
  { key: 'hr', label: 'মানব সম্পদ · HR' },
  { key: 'jiji', label: 'Jiji' },
]

/** → { sources: [{ key, label, paid, models: [{ name, multiplier }] }], defaults: { hr: { source, model } }, roles } */
export function getModelAccessCatalog() {
  return http.get(`${BASE}/catalog`)
}

/** → { grants, locked: { chat, hr, jiji }, cap } */
export function getRoleModelRules(role) {
  return http.get(`${BASE}/roles/${encodeURIComponent(role)}`)
}

export function saveRoleModelRules(role, rules) {
  return http.put(`${BASE}/roles/${encodeURIComponent(role)}`, rules)
}

/** → { userId, username, role, user: rules, roleRules: rules, effectiveCap, usedThisMonth } */
export function getUserModelRules(id) {
  return http.get(`${BASE}/users/${id}`)
}

export function saveUserModelRules(id, rules) {
  return http.put(`${BASE}/users/${id}`, rules)
}

/** A module's default model: what a locked user gets. → the catalog. */
export function setModuleDefault(module, source, model) {
  return http.put(`${BASE}/defaults/${module}`, { source, model })
}

/** The server's matching, for showing what a set of grants covers. */
export function grantCovers(g, source, model) {
  const providerOk = g.providerKey === '*' ? source.paid : g.providerKey.toLowerCase() === source.key.toLowerCase()
  if (!providerOk) return false
  if (g.modelId !== '*' && g.modelId.toLowerCase() !== model.name.toLowerCase()) return false
  if (g.maxMultiplier != null && source.paid && (model.multiplier ?? 1) > g.maxMultiplier + 1e-9) return false
  return true
}
