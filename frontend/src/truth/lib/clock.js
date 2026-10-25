// The server's clock (docs/TRUTH_PROMPT.md §4, G2.6). A countdown must be right
// even when the visitor's own clock is hours off, so the page asks the server for
// its time once (/time is never cached) and keeps the difference. Every "how long
// until" reads serverNow(), never Date.now() alone. A minute tick drives the countdowns.

import { ref } from 'vue'

const offset = ref(0)        // server time − this device's time, in ms
export const tick = ref(0)   // bumps every 30 s

let synced = null
export function syncClock() {
  synced ??= (async () => {
    try {
      const sent = Date.now()
      const res = await fetch('/truth-api/time', { cache: 'no-store' })
      const { serverNow } = await res.json()
      const back = Date.now()
      offset.value = Date.parse(serverNow) - (sent + back) / 2
      tick.value++
    } catch { /* offline: the device's clock it is */ }
  })()
  return synced
}

let timer = null
export function startTicking() {
  timer ??= setInterval(() => { tick.value++ }, 30_000)
}

export function serverNow() {
  void tick.value
  return Date.now() + offset.value
}
