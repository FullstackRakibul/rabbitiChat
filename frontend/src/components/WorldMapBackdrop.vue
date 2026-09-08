<script setup>
/**
 * Decorative dotted-world-map backdrop.
 *
 * Purely presentational: it sits behind the search UI, never receives pointer
 * events, and is hidden from assistive tech. The map is loaded as raw markup
 * (Vite's `?raw`) rather than as an <img> so its shapes inherit `currentColor`
 * and therefore recolor with the theme — an <img> would be locked to whatever
 * colour was baked into the file.
 *
 * The asset is local. If it has not been added yet the component renders
 * nothing at all rather than reaching for a remote image.
 */
import { computed } from 'vue'

// eslint-disable-next-line import/no-unresolved
const modules = import.meta.glob('@/assets/world-dots.svg', { query: '?raw', import: 'default', eager: true })

/**
 * Strip anything that would let a decorative asset act: scripts, event handlers,
 * external references. The file is ours, but it is still markup being inlined,
 * and a backdrop has no business executing anything.
 */
function sanitize(markup) {
  return markup
    .replace(/<\?xml[\s\S]*?\?>/gi, '')
    .replace(/<!DOCTYPE[\s\S]*?>/gi, '')
    .replace(/<script[\s\S]*?<\/script>/gi, '')
    .replace(/\son\w+\s*=\s*("[^"]*"|'[^']*'|[^\s>]+)/gi, '')
    // Let the theme drive the colour: drop baked-in fills/strokes so the
    // wrapper's `currentColor` applies.
    .replace(/\s(fill|stroke)\s*=\s*("(?!none")[^"]*"|'(?!none')[^']*')/gi, '')
    .replace(/<svg\b/i, '<svg preserveAspectRatio="xMidYMid slice"')
}

const markup = computed(() => {
  const raw = Object.values(modules)[0]
  return raw ? sanitize(raw) : null
})
</script>

<template>
  <!-- aria-hidden + pointer-events-none: decoration only, never in the tab order,
       never intercepting a click meant for the search bar underneath. -->
  <div
    v-if="markup"
    class="world-map pointer-events-none absolute inset-0 z-0 select-none overflow-hidden text-foreground opacity-[0.06] dark:opacity-[0.05]"
    aria-hidden="true"
    v-html="markup"
  />
</template>

<style scoped>
/* Radial fade so the map dissolves toward the edges instead of ending in a hard
   rectangle and competing with the content in front of it. */
.world-map {
  -webkit-mask-image: radial-gradient(ellipse 75% 85% at 50% 40%, #000 35%, transparent 100%);
  mask-image: radial-gradient(ellipse 75% 85% at 50% 40%, #000 35%, transparent 100%);
}

/* The inlined SVG fills the backdrop and takes its colour from the wrapper. */
.world-map :deep(svg) {
  width: 100%;
  height: 100%;
  display: block;
  fill: currentColor;
  color: inherit;
}
</style>
