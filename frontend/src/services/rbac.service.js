// RBAC — roles and per-tab permissions.
//
// Two audiences, same http.js plumbing: myTabs() is what the shell calls on
// login to learn which tabs to render, and everything else is the admin surface,
// which answers 403 for anyone who is not a SystemAdmin.

import { http } from './http.js'

/**
 * The signed-in user's own role and effective tabs:
 *   { id, username, role, isSystemAdmin, tabs: ['chat', …] }
 *
 * The backend is the source of truth — the shell renders this list rather than
 * deciding per-role tab sets of its own, so a permission change takes effect on
 * the next load without a frontend release.
 */
export function myTabs() {
  return http.get('/me/tabs')
}

// ---- Admin surface (SystemAdmin only) ----

/**
 * One page of users matching a username substring.
 * Returns { items: [{ id, username, role, tabCount, createdAt }], total, page, pageSize }.
 */
export function searchUsers({ search = '', page = 1, pageSize = 20 } = {}) {
  const params = new URLSearchParams()
  if (search) params.set('search', search)
  params.set('page', String(page))
  params.set('pageSize', String(pageSize))
  return http.get(`/admin/rbac/users?${params}`)
}

/**
 * One user in detail:
 *   { id, username, role, createdAt, effectiveTabs, roleDefaultTabs, grantedTabs }
 *
 * The three lists are separate because the UI needs to tell them apart: role
 * defaults render checked and locked, grants render toggleable.
 */
export function getUser(id) {
  return http.get(`/admin/rbac/users/${id}`)
}

/** Set a user's role. 403 on self-edit, 409 when demoting the last admin. */
export function setRole(id, role) {
  return http.put(`/admin/rbac/users/${id}/role`, { role })
}

/**
 * Replace a user's explicit grants — this is a REPLACE, not a merge, so pass the
 * complete set. Keys already covered by the role baseline are dropped server-side.
 */
export function setTabs(id, tabKeys) {
  return http.put(`/admin/rbac/users/${id}/tabs`, { tabKeys })
}

/** The tab catalogue for the permission checklist: [{ key, label }]. */
export function listTabs() {
  return http.get('/admin/rbac/tabs')
}

/**
 * Every role, least to most privileged, with the tabs it gives before any grant:
 *   [{ key, label, defaultTabs: ['dashboard', …] }]
 *
 * The role picker previews a change from this, so the frontend never keeps its
 * own copy of the role baselines.
 */
export function listRoles() {
  return http.get('/admin/rbac/roles')
}

/**
 * Which models each role may run, plus the installed local models to choose
 * from: { policies: [{ role, allowedModels, defaultModel, locked, editable,
 * updatedAtUtc }], installedModels: [...] }.
 *
 * An empty allowedModels means no restriction — that asymmetry is the server's,
 * and the editor shows it as "No restriction" rather than as an empty list.
 */
export function listModelPolicies() {
  return http.get('/admin/rbac/model-policies')
}

/**
 * Replaces one role's policy. An empty list clears the restriction.
 *
 * Every rule about what is valid lives on the server; this just posts the draft
 * and lets the refusal come back worded there.
 */
export function setModelPolicy(role, allowedModels, defaultModel) {
  return http.put(`/admin/rbac/model-policies/${encodeURIComponent(role)}`, {
    allowedModels, defaultModel,
  })
}
