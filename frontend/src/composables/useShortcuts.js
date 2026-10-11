// Global keyboard shortcuts.
//
// Built on VueUse's useMagicKeys for the modifier combos, plus a small sequence
// tracker for the "g then c" style jumps that magic-keys doesn't model.
//
// Typing guard: while focus is in a text field, only the explicitly text-scoped
// shortcuts fire (Cmd/Ctrl+Enter to send, Esc to dismiss). Everything else —
// the palette, the sidebar toggle, the g-sequences — stays out of the way so
// typing "g" in a message never navigates.

import { onBeforeUnmount, onMounted, ref } from 'vue'
import { useMagicKeys, whenever } from '@vueuse/core'

/** True when the event target is a text field or anything contenteditable. */
export function isTypingTarget(el) {
  if (!el) return false
  const tag = el.tagName
  if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return true
  return el.isContentEditable === true
}

function typingNow() {
  return typeof document !== 'undefined' && isTypingTarget(document.activeElement)
}

/**
 * Wires the app's shortcuts. Every handler is optional.
 *
 *   onPalette   Cmd/Ctrl+K      open the command palette
 *   onSidebar   Cmd/Ctrl+B      toggle the active tab's sidebar
 *   onSubmit    Cmd/Ctrl+Enter  primary action of the active tab (fires while typing)
 *   onEscape    Esc             close the top-most overlay (fires while typing)
 *   onJump      g then c/w/d/t  jump to a tab, by tab id
 *   onLauncher  g then m        open the module launcher
 *   enabled     ref/getter — shortcuts are inert when it returns false (e.g. login)
 */
export function useShortcuts({
  onPalette, onSidebar, onSubmit, onEscape, onJump, onLauncher, enabled,
} = {}) {
  const isEnabled = () => (typeof enabled === 'function'
    ? enabled()
    : enabled === undefined || enabled.value !== false)

  // preventDefault on the combos the browser also claims (Ctrl+K, Ctrl+B).
  const keys = useMagicKeys({
    passive: false,
    onEventFired(e) {
      if (e.type !== 'keydown' || !isEnabled()) return
      const mod = e.ctrlKey || e.metaKey
      if (!mod) return
      const k = e.key.toLowerCase()
      if ((k === 'k' && onPalette) || (k === 'b' && onSidebar && !typingNow())) {
        e.preventDefault()
      }
    },
  })

  // ---- Modifier combos ----

  // Cmd+K / Ctrl+K — the palette is reachable even mid-message, since it is how
  // you leave the message; the guard would only trap the user.
  whenever(keys['Meta+K'], () => { if (isEnabled()) onPalette?.() })
  whenever(keys['Ctrl+K'], () => { if (isEnabled()) onPalette?.() })

  // Cmd+B / Ctrl+B — layout toggle, suppressed while typing.
  whenever(keys['Meta+B'], () => { if (isEnabled() && !typingNow()) onSidebar?.() })
  whenever(keys['Ctrl+B'], () => { if (isEnabled() && !typingNow()) onSidebar?.() })

  // Cmd/Ctrl+Enter — deliberately text-scoped: it exists to submit what you
  // are typing, so it must fire inside the composer.
  whenever(keys['Meta+Enter'], () => { if (isEnabled()) onSubmit?.() })
  whenever(keys['Ctrl+Enter'], () => { if (isEnabled()) onSubmit?.() })

  // ---- "g then <key>" sequences ----
  // Tracked by hand: a leading "g" arms a short window in which the next key
  // selects a tab. Never armed while typing.

  const JUMPS = { c: 'chat', w: 'workspace', d: 'docs', t: 'todo' }
  const armed = ref(false)
  let armedTimer = null

  function disarm() {
    armed.value = false
    clearTimeout(armedTimer)
  }

  function onKeydown(e) {
    if (!isEnabled()) return

    if (e.key === 'Escape') {
      disarm()
      onEscape?.()            // text-scoped: Esc works from inside a field too
      return
    }

    if (typingNow()) return
    if (e.ctrlKey || e.metaKey || e.altKey) return

    const k = e.key.toLowerCase()

    if (armed.value) {
      disarm()
      // "m" opens the launcher rather than a tab, so it is not in JUMPS.
      if (k === 'm' && onLauncher) {
        e.preventDefault()
        onLauncher()
        return
      }
      const tab = JUMPS[k]
      if (tab) {
        e.preventDefault()
        onJump?.(tab)
      }
      return
    }

    if (k === 'g') {
      armed.value = true
      clearTimeout(armedTimer)
      armedTimer = setTimeout(disarm, 1200)   // the sequence expires quietly
    }
  }

  onMounted(() => window.addEventListener('keydown', onKeydown))
  onBeforeUnmount(() => {
    window.removeEventListener('keydown', onKeydown)
    clearTimeout(armedTimer)
  })

  return { sequenceArmed: armed }
}
