// The public shell's routes (docs/TRUTH_PROMPT.md §3). A few lines instead of
// vue-router: five paths, history API, and the bundle budget (G1.5).
//
//   /truth             the current issue
//   /truth/about       how we verify (and the privacy promise)
//   /truth/glossary    the Newspeak Dictionary
//   /truth/:slug       one published issue
//   anything else      Room 101
//   /truth/guides      every fix guide
//   /truth/guide/:slug one fix guide

import { reactive } from 'vue'

export const route = reactive({ name: 'home', slug: null })

function parse() {
  const parts = location.pathname.replace(/\/+$/, '').split('/').filter(Boolean)
  if (parts[0] !== 'truth' || parts.length === 1) return { name: 'home', slug: null }
  if (parts.length === 2 && parts[1] === 'about') return { name: 'about', slug: null }
  if (parts.length === 2 && parts[1] === 'glossary') return { name: 'glossary', slug: null }
  if (parts.length === 2 && parts[1] === 'guides') return { name: 'guides', slug: null }
  if (parts.length === 3 && parts[1] === 'guide' && /^[a-z0-9][a-z0-9-]{0,59}$/.test(parts[2])) return { name: 'guide', slug: parts[2] }
  if (parts.length === 2 && /^[a-z0-9][a-z0-9-]{0,39}$/.test(parts[1]) && parts[1] !== 'guide') return { name: 'issue', slug: parts[1] }
  return { name: 'notFound', slug: null }
}

function sync() { Object.assign(route, parse()) }

export function navigate(href) {
  const url = new URL(href, location.origin)
  if (url.pathname + url.search === location.pathname + location.search) {
    if (url.hash) document.getElementById(url.hash.slice(1))?.scrollIntoView()
    return
  }
  history.pushState(null, '', url.pathname + url.search + url.hash)
  sync()
  if (!url.hash) window.scrollTo(0, 0)
}

export function initRouter() {
  sync()
  window.addEventListener('popstate', sync)
}
