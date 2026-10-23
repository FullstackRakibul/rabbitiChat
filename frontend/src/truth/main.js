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

// The typewriter face decorates nicknames and stamps only: it comes after the page
// has loaded, and until then they are set in the mono face the log already uses.
const lateFonts = () => setTimeout(() => document.documentElement.classList.add('fonts-late'), 0)
if (document.readyState === 'complete') lateFonts()
else window.addEventListener('load', lateFonts, { once: true })
