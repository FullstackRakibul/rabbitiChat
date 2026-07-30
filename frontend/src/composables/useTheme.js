import { ref, watch } from 'vue'

const STORAGE_KEY = 'rabbitic-theme'

function initial() {
  const saved = localStorage.getItem(STORAGE_KEY)
  if (saved === 'light' || saved === 'dark') return saved
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
  localStorage.setItem(STORAGE_KEY, value)
})

export function useTheme() {
  return {
    theme,
    toggleTheme: () => { theme.value = theme.value === 'dark' ? 'light' : 'dark' },
  }
}
