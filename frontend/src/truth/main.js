// The Ministry of Truth's public shell — its own Vite entry (truth.html), sharing
// nothing with the admin app but Vue and the theme (docs/TRUTH_PROMPT.md §4).
import { createApp } from 'vue'
import './truth.css'
import App from './App.vue'
import { i18n } from './lib/i18n.js'
import { initLang } from './lib/lang.js'
import { initRouter } from './lib/router.js'

initLang()
initRouter()

// truth.html's own first paint (the header and "loading") goes to the screen
// before Vue replaces it: one frame, then mount. The fonts only the rendered page
// uses — display faces, the log's mono — start after that paint, not before it.
requestAnimationFrame(() => setTimeout(() => createApp(App).use(i18n).mount('#app')))

// The typewriter face decorates nicknames and stamps only: it comes once the page has
// drawn its heading (the largest paint) and the browser is idle, so its file never
// competes with the content. Until then they are set in the mono face the log already uses.
const idle = window.requestIdleCallback ?? ((f) => setTimeout(f, 200))
let fontsLate = false
const lateFonts = () => {
  if (fontsLate) return
  fontsLate = true
  // After the faces the heading and text are drawn in have arrived.
  const ready = Promise.race([document.fonts?.ready ?? Promise.resolve(), new Promise((r) => setTimeout(r, 5000))])
  ready.then(() => idle(() => document.documentElement.classList.add('fonts-late'), { timeout: 3000 }))
}
const whenHeading = () => {
  if (document.querySelector('#app h1')) return lateFonts()
  const seen = new MutationObserver(() => { if (document.querySelector('#app h1')) { seen.disconnect(); lateFonts() } })
  seen.observe(document.getElementById('app'), { childList: true, subtree: true })
  setTimeout(() => { seen.disconnect(); lateFonts() }, 8000)   // a page that never draws one
}
if (document.readyState === 'complete') whenHeading()
else window.addEventListener('load', whenHeading, { once: true })
