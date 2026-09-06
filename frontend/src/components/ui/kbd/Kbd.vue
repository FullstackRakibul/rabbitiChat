<script setup>
import { computed } from 'vue'
import { cn } from '@/lib/utils'
import { isMac, SYMBOLS } from '.'

const props = defineProps({
  /**
   * The shortcut to render. Either a combo string ("Ctrl + B", "mod+k") or the
   * key alone ("Esc"). Separators accepted: "+", " then ", " ".
   */
  keys: { type: [String, Array], default: '' },
  /** 'sm' suits inline hints inside buttons; 'default' suits menus and lists. */
  size: { type: String, default: 'default' },
  class: { type: null, default: '' },
})

// "mod" renders as ⌘ on a Mac and Ctrl everywhere else, so one declaration
// serves both platforms.
function symbolFor(key) {
  const k = key.trim()
  const lower = k.toLowerCase()
  if (lower === 'mod') return isMac ? SYMBOLS.meta : 'Ctrl'
  if (SYMBOLS[lower]) return SYMBOLS[lower]
  // Single letters read better capitalised; longer names keep their casing.
  return k.length === 1 ? k.toUpperCase() : k
}

/**
 * Splits a combo into caps and the literal separators between them, so
 * "Ctrl + B" renders two caps joined by "+", and "g then c" keeps the word.
 */
const parts = computed(() => {
  if (Array.isArray(props.keys)) {
    return props.keys.flatMap((k, i) =>
      i === 0 ? [{ cap: symbolFor(k) }] : [{ sep: '+' }, { cap: symbolFor(k) }])
  }

  const raw = String(props.keys).trim()
  if (!raw) return []

  const tokens = raw.split(/(\s+then\s+|\s*\+\s*|\s+)/i).filter(t => t.trim())
  return tokens.map((t) => {
    const trimmed = t.trim()
    if (trimmed === '+') return { sep: '+' }
    if (/^then$/i.test(trimmed)) return { sep: 'then' }
    return { cap: symbolFor(trimmed) }
  })
})

const capClass = computed(() => cn(
  'inline-flex select-none items-center justify-center rounded-md border border-border bg-muted font-mono font-medium text-muted-foreground shadow-sm',
  props.size === 'sm'
    ? 'h-[18px] min-w-[18px] px-1 text-[10px]'
    : 'h-5 min-w-[20px] px-1.5 text-[11px]',
))
</script>

<template>
  <span :class="cn('inline-flex items-center gap-1 align-middle', props.class)">
    <template v-for="(p, i) in parts" :key="i">
      <kbd v-if="p.cap" :class="capClass">{{ p.cap }}</kbd>
      <span
        v-else
        :class="cn(
          'text-muted-foreground/70',
          size === 'sm' ? 'text-[10px]' : 'text-[11px]',
        )"
      >{{ p.sep }}</span>
    </template>
  </span>
</template>
