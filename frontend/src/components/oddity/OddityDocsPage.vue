<script setup>
/**
 * /oddity/docs — the gateway's documentation for callers, with no login.
 *
 * Rendered by App.vue outside the login gate, the way a public note is. It makes
 * no API call: the caller part of the docs needs nothing from the server, and the
 * base URL shown is the address this page was opened on.
 */
import { Moon, Satellite, Sun } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import { useTheme } from '@/composables/useTheme'
import OddityDocs from '@/components/oddity/OddityDocs.vue'

const { theme, toggleTheme } = useTheme()
</script>

<template>
  <div class="min-h-screen bg-background text-foreground">
    <header class="sticky top-0 z-10 border-b bg-background/95 backdrop-blur">
      <div class="mx-auto flex max-w-4xl items-center gap-2 px-4 py-3">
        <Satellite class="h-5 w-5 shrink-0" aria-hidden="true" />
        <span class="font-semibold">Oddity</span>
        <span class="truncate text-sm text-muted-foreground">· মস্তিস্ক the ai</span>
        <Button
          variant="ghost"
          size="icon"
          class="ml-auto shrink-0"
          :aria-label="theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'"
          @click="toggleTheme"
        >
          <Sun v-if="theme === 'dark'" class="h-4 w-4" />
          <Moon v-else class="h-4 w-4" />
        </Button>
      </div>
    </header>
    <main class="px-4 py-6">
      <OddityDocs />
    </main>
  </div>
</template>
