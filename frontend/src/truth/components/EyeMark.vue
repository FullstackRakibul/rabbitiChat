<script setup>
// The brand mark (§3A.3): a round, friendly eye. An original drawing.
defineProps({ size: { type: Number, default: 28 } })
</script>

<template>
  <svg :width="size" :height="size" viewBox="0 0 32 32" aria-hidden="true" focusable="false" class="flex-none">
    <circle cx="16" cy="16" r="15" class="eye-ring" />
    <path d="M5 16q11-10 22 0q-11 10-22 0z" class="eye-white" />
    <circle cx="16" cy="16" r="4.6" class="eye-iris" />
    <circle cx="17.6" cy="14.4" r="1.5" class="eye-glint" />
  </svg>
</template>
