<script setup>
// A technology's neutral icon (docs/TRUTH_PROMPT.md §3): its one or two letters
// on a muted square made from its hue. Never a vendor logo. The colours come
// from truth.css, so the same mark reads at AA contrast in both themes.
defineProps({
  tech: { type: Object, required: true },
  size: { type: Number, default: 40 },
})
</script>

<template>
  <svg :width="size" :height="size" viewBox="0 0 40 40" aria-hidden="true" focusable="false" translate="no"
    class="flex-none" :style="{ '--hue': tech.hue }">
    <rect class="lettermark-bg" width="40" height="40" rx="10" />
    <text class="lettermark-fg" x="20" y="21" text-anchor="middle" dominant-baseline="middle"
      font-family="ui-sans-serif, system-ui, sans-serif" font-weight="700" :font-size="tech.monogram.length > 1 ? 15 : 18">{{ tech.monogram }}</text>
  </svg>
</template>
