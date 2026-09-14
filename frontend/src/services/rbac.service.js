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
