<script setup>
// The header: the site's name, the language switch (always visible, §3) and the theme.
import { useI18n } from '../lib/i18n.js'
import { Moon, Sun } from '@lucide/vue'
import { useTheme } from '@/composables/useTheme.js'
import Link from './Link.vue'
import EyeMark from './EyeMark.vue'
import { lang, setLang } from '../lib/lang.js'

const { t } = useI18n()
const { theme, toggleTheme } = useTheme()
</script>

<template>
  <header class="border-b border-line bg-surface">
    <div class="mx-auto flex max-w-3xl items-center gap-2 px-4">
      <Link to="/truth" class="flex min-h-11 min-w-0 flex-1 items-center gap-2 py-1 text-fg no-underline">
        <EyeMark :size="28" />
        <span class="flex min-w-0 flex-col">
          <span class="truncate text-base font-semibold leading-tight">{{ t('site.name') }}</span>
          <span class="truncate text-sm leading-tight text-muted">{{ t('site.series') }}</span>
        </span>
      </Link>
      <div role="group" :aria-label="t('lang.label')" class="flex rounded-full border border-line p-0.5">
        <button type="button" data-testid="lang-bn" lang="bn" :aria-pressed="lang === 'bn'"
          class="lang-bn-label min-h-11 min-w-11 rounded-full px-3 text-sm font-semibold"
          :class="lang === 'bn' ? 'bg-fg text-bg' : 'text-muted'" @click="setLang('bn')">{{ t('lang.bn') }}</button>
        <button type="button" data-testid="lang-en" lang="en" :aria-pressed="lang === 'en'"
          class="min-h-11 min-w-11 rounded-full px-3 text-sm font-semibold"
          :class="lang === 'en' ? 'bg-fg text-bg' : 'text-muted'" @click="setLang('en')">{{ t('lang.en') }}</button>
      </div>
      <button type="button" data-testid="theme" class="grid size-11 place-items-center rounded-full text-muted hover:text-fg"
        :aria-label="t(theme === 'dark' ? 'theme.toLight' : 'theme.toDark')" @click="toggleTheme">
        <Sun v-if="theme === 'dark'" class="size-5" aria-hidden="true" />
        <Moon v-else class="size-5" aria-hidden="true" />
      </button>
    </div>
  </header>
</template>
