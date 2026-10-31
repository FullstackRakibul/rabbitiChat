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
      <div role="group" :aria-label="t('lang.label')" class="lang-pill flex shrink-0">
        <button type="button" data-testid="lang-bn" lang="bn" :aria-pressed="lang === 'bn'"
          class="lang-bn-label lang-seg" :class="lang === 'bn' ? 'font-semibold text-bg' : 'font-medium text-muted'"
          @click="setLang('bn')"><span :class="{ 'bg-fg': lang === 'bn' }">{{ t('lang.bn') }}</span></button>
        <button type="button" data-testid="lang-en" lang="en" :aria-pressed="lang === 'en'"
          class="lang-seg" :class="lang === 'en' ? 'font-semibold text-bg' : 'font-medium text-muted'"
          @click="setLang('en')"><span :class="{ 'bg-fg': lang === 'en' }">{{ t('lang.en') }}</span></button>
      </div>
      <button type="button" data-testid="theme" class="theme-btn grid size-11 shrink-0 place-items-center text-muted hover:text-fg"
        :aria-label="t(theme === 'dark' ? 'theme.toLight' : 'theme.toDark')" @click="toggleTheme">
        <span class="grid place-items-center rounded-full">
          <Sun v-if="theme === 'dark'" class="size-5" aria-hidden="true" />
          <Moon v-else class="size-5" aria-hidden="true" />
        </span>
      </button>
    </div>
  </header>
</template>

<style scoped>
/* The language pill and the theme button are drawn 32 px tall (28 px below 360 px wide), but each button's
   own box stays 44 px tall so the touch target is ≥ 44 px; only the inner span and the ::before are visible. */
.lang-pill { --pill: 2rem; position: relative; align-items: center; padding-inline: 3px; }
.lang-pill::before { content: ''; position: absolute; inset-inline: 0; top: 50%; height: var(--pill); transform: translateY(-50%);
  border: 1px solid var(--color-line, currentColor); border-radius: 9999px; pointer-events: none; }
.lang-seg { position: relative; display: grid; place-items: center; height: 2.75rem; min-width: 3rem; font-size: 13px; line-height: 1; border-radius: 9999px; }
.lang-seg > span { display: grid; place-items: center; height: calc(var(--pill) - 6px); min-width: 3rem; padding-inline: .75rem; border-radius: 9999px; }
.theme-btn { border-radius: 9999px; }
.theme-btn > span { width: 2rem; height: 2rem; }
/* The focus ring goes round what is drawn, not the invisible 44 px box. */
.lang-seg:focus-visible, .theme-btn:focus-visible { outline: none; }
.lang-seg:focus-visible > span, .theme-btn:focus-visible > span { outline: 3px solid var(--accent); outline-offset: 2px; }
@media (max-width: 359px) { .lang-pill { --pill: 1.75rem; } .lang-seg > span { padding-inline: .5rem; } }
</style>
