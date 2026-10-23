// The telescreen theme's state (docs/TRUTH_PROMPT.md §3A): serious mode, the
// current view's mood and mascot pose, and the meme picker.
//
// Serious mode has three sources. The visitor's own switch (remembered) adds
// html.serious, and truth.css then hides every meme and effect and stops every
// animation. prefers-reduced-motion stops every animation in CSS alone. And a
// view turns serious by itself while any item is under active attack: the hero
// loses its glitch, its crack and its memes, and the mascot is worried.

import { reactive, ref, watch } from 'vue'
import defaultMemes from '../content/memes.default.json'
import { resolveDoc } from './content-docs.js'

const KEY = 'truth-serious'

function readChoice() {
  try { return localStorage.getItem(KEY) === '1' } catch { return false }
}

/** The visitor's switch. */
export const seriousChoice = ref(readChoice())

function apply(on) { document.documentElement.classList.toggle('serious', on) }
apply(seriousChoice.value)
watch(seriousChoice, (on) => {
  apply(on)
  try { localStorage.setItem(KEY, on ? '1' : '0') } catch { /* storage blocked */ }
})

/** The memes in use: the admin's published list, else the built-in one. */
export const memes = ref(defaultMemes.memes)
export function applyMemes(override) {
  memes.value = resolveDoc(defaultMemes, override, 'memes')
  view.memeRev++
}

/**
 * One page view. `mood` chooses memes (alert while anything is urgent, relief when
 * everything is patched, calm otherwise); `pose` is the mascot's; `autoSerious`
 * is the active-attack rule; `footerSlot` is off on pages that carry a meme of
 * their own (404, empty, backend down), so two never share a screen.
 */
export const view = reactive({ id: 0, mood: 'calm', pose: 'calm', autoSerious: false, footerSlot: true, memeRev: 0 })

let used = new Set()

export function newView() {
  view.id++
  view.mood = 'calm'
  view.pose = 'calm'
  view.autoSerious = false
  view.footerSlot = true
  used = new Set()
}

const URGENT = ['active_attack', 'patch_scheduled']
const SETTLED = ['patched', 'resolved', 'blocked']

/** Sets the view from an issue's items. */
export function setIssueState(items) {
  const attack = items.some((i) => i.status === 'active_attack')
  const urgent = items.some((i) => URGENT.includes(i.status))
  const settled = items.length > 0 && items.every((i) => SETTLED.includes(i.status))
  view.autoSerious = attack
  view.mood = urgent ? 'alert' : settled ? 'relief' : 'calm'
  view.pose = attack ? 'worried' : urgent ? 'alert' : settled ? 'relieved' : 'calm'
}

/**
 * A meme for a slot: active, placed there, of the view's mood (any mood for the
 * 404, empty and backend-down slots, which have no news to fit), never one this
 * view has shown. Chosen at random by weight; null when none fits.
 */
export function pickMeme(slot, { anyMood = false, random = Math.random } = {}) {
  const pool = memes.value.filter((m) => m.active && m.placement?.includes(slot) && (anyMood || m.mood === view.mood) && !used.has(m.id))
  if (!pool.length) return null
  let r = random() * pool.reduce((n, m) => n + (m.weight || 1), 0)
  const meme = pool.find((m) => (r -= m.weight || 1) < 0) ?? pool.at(-1)
  used.add(meme.id)
  return meme
}

/** The crack draws itself once a session, never again (§3A.3). */
export function crackOnce() {
  try {
    if (sessionStorage.getItem('truth-cracked')) return false
    sessionStorage.setItem('truth-cracked', '1')
    return true
  } catch { return false }
}
