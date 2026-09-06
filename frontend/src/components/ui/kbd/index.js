export { default as Kbd } from './Kbd.vue'

// Detected once at module load — the platform doesn't change mid-session.
// navigator.platform is deprecated but still the most reliable Mac signal
// across browsers; userAgent is the fallback.
export const isMac = typeof navigator !== 'undefined'
  && /mac|iphone|ipad|ipod/i.test(navigator.platform || navigator.userAgent || '')

/** Display symbols for keys that have a conventional glyph. */
export const SYMBOLS = {
  meta: '⌘',
  cmd: '⌘',
  command: '⌘',
  ctrl: 'Ctrl',
  control: 'Ctrl',
  alt: isMac ? '⌥' : 'Alt',
  option: '⌥',
  shift: '⇧',
  enter: '↵',
  return: '↵',
  esc: 'Esc',
  escape: 'Esc',
  tab: '⇥',
  space: 'Space',
  backspace: '⌫',
  delete: '⌦',
  up: '↑',
  down: '↓',
  left: '←',
  right: '→',
}

/** "mod" spelled for the current platform — for building label strings. */
export const MOD_LABEL = isMac ? '⌘' : 'Ctrl'
