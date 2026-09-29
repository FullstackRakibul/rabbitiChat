<script setup>
/**
 * DocCallout — a variant-based callout box for documentation.
 *
 * Three variants:
 *  - `tip`      — emerald accent, positive guidance
 *  - `security` — amber accent, trust/safety notes
 *  - `note`     — indigo accent, general information
 */
import { computed } from 'vue'
import { Lightbulb, ShieldCheck, Info } from '@lucide/vue'

/** @type {{ variant: 'tip' | 'security' | 'note', title?: string }} */
const props = defineProps({
  variant: {
    type: String,
    default: 'note',
    validator: (v) => ['tip', 'security', 'note'].includes(v),
  },
  title: { type: String, default: '' },
})

/** @type {import('vue').ComputedRef<{ icon: object, border: string, bg: string, label: string, iconClass: string }>} */
const config = computed(() => {
  const variants = {
    tip: {
      icon: Lightbulb,
      border: 'border-l-emerald-500',
      bg: 'bg-emerald-500/5 dark:bg-emerald-500/10',
      label: 'Tip',
      iconClass: 'text-emerald-500',
    },
    security: {
      icon: ShieldCheck,
      border: 'border-l-amber-500',
      bg: 'bg-amber-500/5 dark:bg-amber-500/10',
      label: 'Security Note',
      iconClass: 'text-amber-500',
    },
    note: {
      icon: Info,
      border: 'border-l-indigo-400 dark:border-l-indigo-500',
      bg: 'bg-indigo-500/5 dark:bg-indigo-500/10',
      label: 'Note',
      iconClass: 'text-indigo-400 dark:text-indigo-500',
    },
  }
  return variants[props.variant] ?? variants.note
})
</script>

<template>
  <div :class="[
    'rounded-lg border border-l-4 p-4 transition-colors',
    config.border,
    config.bg,
  ]" role="note">
    <div class="mb-2 flex items-center gap-2 text-sm font-semibold text-foreground">
      <component :is="config.icon" :class="['h-4 w-4 shrink-0', config.iconClass]" />
      <span>{{ title || config.label }}</span>
    </div>
    <div class="text-sm leading-relaxed text-muted-foreground">
      <slot />
    </div>
  </div>
</template>