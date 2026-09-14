// Small animation helpers for the 5Lakhs tab.
//
// Every one of these checks prefers-reduced-motion and degrades to "just set the
// final value". Motion here is decoration on a joke feature; it should never be
// the reason someone gets a headache using the app.

import { onBeforeUnmount, ref } from 'vue'

/** True when the visitor has asked the OS for less motion. */
export function prefersReducedMotion() {
  return typeof window !== 'undefined'
    && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true
}

/**
 * Counts from 0 to `target` for a stat tile.
 *
 * Returns a ref that lands exactly on the target — the final frame is assigned
 * rather than interpolated, so a count never stops at 499,998.
 */
export function useCountUp(target = 0, duration = 700) {
  const value = ref(0)
  let frame = null

  function run(to = target) {
    cancelAnimationFrame(frame)

    if (prefersReducedMotion() || duration <= 0) {
      value.value = to
      return
    }

    const from = value.value
    const start = performance.now()

    const step = (now) => {
      const t = Math.min(1, (now - start) / duration)
      // easeOutCubic: quick to start, settles gently.
      const eased = 1 - (1 - t) ** 3
      value.value = Math.round(from + (to - from) * eased)
      if (t < 1) frame = requestAnimationFrame(step)
      else value.value = to
    }

    frame = requestAnimationFrame(step)
  }

  onBeforeUnmount(() => cancelAnimationFrame(frame))

  return { value, run }
}

/**
 * A one-shot flag for "something just happened" flourishes — a flash on the
 * preview after a download, say. Auto-clears so the caller doesn't have to.
 */
export function usePulse(ms = 900) {
  const active = ref(false)
  let timer = null

  function pulse() {
    if (prefersReducedMotion()) return
    clearTimeout(timer)
    active.value = true
    timer = setTimeout(() => { active.value = false }, ms)
  }

  onBeforeUnmount(() => clearTimeout(timer))

  return { active, pulse }
}
