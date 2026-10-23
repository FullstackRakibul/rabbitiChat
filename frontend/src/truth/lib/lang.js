// The language switch (docs/TRUTH_PROMPT.md §3): ?lang= first, so a shared link
// keeps its language; then the visitor's own choice; then the browser language;
// Bangla when none of those says. Changing it re-renders in place — no reload —
// and writes the URL and the choice back.

import { computed } from 'vue'
import { i18n } from './i18n.js'

const KEY = 'truth-lang'
const LANGS = ['bn', 'en']

function initial() {
  const fromUrl = new URLSearchParams(location.search).get('lang')
  if (LANGS.includes(fromUrl)) return fromUrl
  try {
    const saved = localStorage.getItem(KEY)
    if (LANGS.includes(saved)) return saved
  } catch { /* storage blocked */ }
  for (const l of navigator.languages ?? [navigator.language]) {
    const code = String(l).toLowerCase()
    if (code.startsWith('bn')) return 'bn'
    if (code.startsWith('en')) return 'en'
  }
  return 'bn'
}

export const lang = computed(() => i18n.global.locale.value)

/** The current URL with this language in it — what the share button and the links carry. */
export function withLang(url, l = lang.value) {
  const u = new URL(url, location.origin)
  u.searchParams.set('lang', l)
  return u.pathname + u.search + u.hash
}

export function setLang(l, { remember = true } = {}) {
  if (!LANGS.includes(l)) return
  i18n.global.locale.value = l
  document.documentElement.lang = l
  if (remember) {
    try { localStorage.setItem(KEY, l) } catch { /* storage blocked */ }
  }
  const next = withLang(location.href, l)
  if (next !== location.pathname + location.search + location.hash) history.replaceState(history.state, '', next)
}

export function initLang() {
  setLang(initial(), { remember: false })
}
