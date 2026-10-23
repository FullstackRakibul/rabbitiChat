import { ref, watch } from 'vue'

const STORAGE_KEY = 'rabbitic-theme'

// Storage can be blocked (a private window, cleared site data): the theme then
// simply isn't remembered. The Ministry of Truth's public shell shares this
// composable, and must render without storage (docs/TRUTH_PROMPT.md §4).
function initial() {
  try {
    const saved = localStorage.getItem(STORAGE_KEY)
    if (saved === 'light' || saved === 'dark') return saved
  } catch { /* storage blocked */ }
  // A page may name its own default (truth.html is dark by default); otherwise the OS preference.
  const preset = document.documentElement.dataset.defaultTheme
  if (preset === 'light' || preset === 'dark') return preset
  return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
}

// Module-level so every caller shares one source of truth.
const theme = ref(initial())

function apply(value) {
  document.documentElement.classList.toggle('dark', value === 'dark')
}

apply(theme.value)
watch(theme, (value) => {
  apply(value)
  try { localStorage.setItem(STORAGE_KEY, value) } catch { /* storage blocked */ }
})

export function useTheme() {
  return {
    theme,
    toggleTheme: () => { theme.value = theme.value === 'dark' ? 'light' : 'dark' },
  }
}
