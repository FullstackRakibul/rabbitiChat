// Module routing — one URL per module.
//
// Each tab in the shell is a route: /dashboard, /chat, /ielts, /5lakhs, … so a
// module can be deep-linked, bookmarked, refreshed, and reached with the browser
// back/forward buttons.
//
// Why the routes carry no components: the shell keeps every module mounted at
// once (a streaming chat reply has to survive navigating away and back), so the
// router's job here is to own the *identity* of the active module, not to swap
// component trees. The shell reads `route.params`/`route.name` and shows the
// matching panel. That keeps routing and the keep-everything-mounted guarantee
// from fighting each other — see the note on KEEP_MOUNTED below.
//
// Permission enforcement lives in the guard at the bottom. It is a convenience
// for the user (no 404 flash, no dead tab), never the control: every module's
// API independently answers 403 for a role that lacks it.

import { createRouter, createWebHistory } from 'vue-router'

/**
 * The tab key ↔ path map. The KEY IS THE CONTRACT with the backend
 * (TabCatalog.All) — these strings must match `/api/me/tabs` exactly, so they
 * are written once here and imported everywhere rather than retyped.
 *
 * `path` is separate from `key` only where a key cannot be a clean URL segment:
 * "5lakhs" starts with a digit, which is a fine path but reads better as-is, and
 * "jsonto" gets the friendlier /json-converter.
 */
export const MODULE_ROUTES = [
  { key: 'dashboard', path: '/dashboard' },
  { key: 'chat', path: '/chat' },
  // Workspace Studio. The `docs` key still exists on the server, where it gates
  // /api/documents and /api/buyer-profiles, but it no longer has a route of its
  // own: Documents and Workspace are one tab now, and /documents redirects into
  // its Library section below.
  { key: 'workspace', path: '/workspace' },
  // মানব সম্পদ (HR Assistant).
  { key: 'hr', path: '/hr' },
  { key: 'matrix', path: '/matrix' },
  // এইচআর ওয়ার্কস্পেস · HRIS Workspace (docs/HRIS_WORKSPACE_PROMPT.md).
  { key: 'hris', path: '/hris' },
  // The staff side. Not /truth: that is the public page (truth.html), on Vercel and on the LAN.
  { key: 'truth', path: '/ministry-of-truth' },
  { key: 'todo', path: '/todo' },
  { key: 'search', path: '/search' },
  { key: 'jsonto', path: '/json-converter' },
  { key: '5lakhs', path: '/5lakhs' },
  { key: 'ielts', path: '/ielts' },
  { key: 'component-splitter', path: '/component-splitter' },
  { key: 'sources', path: '/sources' },
  { key: 'settings', path: '/settings' },
  { key: 'rbac', path: '/access' },
  { key: 'notes', path: '/notes' },
  { key: 'logs', path: '/logs' },
  { key: 'oddity', path: '/oddity' },
]

/** Tab key → URL path. */
export function pathForTab(key) {
  return MODULE_ROUTES.find((r) => r.key === key)?.path ?? '/dashboard'
}

/** URL path → tab key, or null when the path names no module. */
export function tabForPath(path) {
  return MODULE_ROUTES.find((r) => r.path === path)?.key ?? null
}

/**
 * Routes are name-only placeholders: the shell renders the panels, so a route
 * needs no component of its own. `component` is still required by vue-router,
 * so each gets the same empty render function.
 */
const Passthrough = { render: () => null }

export const router = createRouter({
  // Real paths rather than hashes — the backend serves index.html for any
  // unmatched GET (SPA fallback), so /ielts survives a hard refresh.
  history: createWebHistory(),
  routes: [
    { path: '/', redirect: '/dashboard' },
    // Documents merged into Workspace Studio; old links and bookmarks still work.
    { path: '/documents', redirect: '/workspace#library' },
    ...MODULE_ROUTES.map((r) => ({
      path: r.path,
      name: r.key,
      component: Passthrough,
      meta: { tab: r.key },
    })),
    { path: '/notes/:id', name: 'notes-detail', component: Passthrough, meta: { tab: 'notes' } },
    { path: '/n/:token', name: 'public-note', component: Passthrough },
    // The gateway's caller docs. No meta.tab, so the guard lets anyone through;
    // App.vue renders it outside the login gate.
    { path: '/oddity/docs', name: 'oddity-docs', component: Passthrough },
    // Anything else lands on the dashboard rather than showing a dead screen.
    { path: '/:pathMatch(.*)*', redirect: '/dashboard' },
  ],
  scrollBehavior: () => ({ top: 0 }),
})

/**
 * The permission gate.
 *
 * The guard needs the user's effective tabs, which arrive asynchronously from
 * /api/me/tabs. Rather than have the router fetch them (and duplicate the
 * shell's state), the shell installs a resolver here once it knows them.
 *
 * Until a resolver is installed — the first paint, before login — every route is
 * allowed through: the shell is showing the login gate at that point anyway, and
 * blocking here would only produce a redirect the user never sees.
 */
let resolvePermissions = null

/**
 * @param {() => { tabs: string[] | null, fallback: string }} resolver
 *   `tabs` null means "not known yet" (allow through); an array is authoritative.
 *   `fallback` is where a forbidden route should land — the user's own default.
 */
export function useRoutePermissions(resolver) {
  resolvePermissions = resolver
}

router.beforeEach((to) => {
  if (!resolvePermissions) return true

  const { tabs, fallback } = resolvePermissions()
  if (!tabs) return true                       // permissions not loaded yet

  // Allow public note endpoint to pass without checking tabs
  if (to.name === 'public-note') return true

  const wanted = to.meta?.tab
  if (!wanted) return true                     // redirects and the catch-all

  if (tabs.includes(wanted)) return true

  // Forbidden: send them to a module they do have, so they never see a 404
  // flash or an empty panel. Returning a path (not calling next()) makes this a
  // redirect rather than a second navigation.
  const target = pathForTab(tabs.includes(fallback) ? fallback : (tabs[0] ?? 'dashboard'))
  return target === to.path ? false : target
})
