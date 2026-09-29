<script setup>
/**
 * DocSection — a reusable section wrapper for documentation pages.
 *
 * Renders a `<section>` element with the given `id` (the scroll-spy / anchor
 * target) and an `<h2>` heading. Content goes in the default slot.
 *
 * `scroll-mt-20` offsets the anchor so the heading isn't hidden under any
 * sticky header when clicking a sidebar link.
 */
const props = defineProps({
  /** Unique section ID — used as the scroll anchor target. */
  id: { type: String, required: true },
  /** Section heading text. */
  title: { type: String, required: true },
})
</script>

<template>
  <section :id="id" class="scroll-mt-20">
    <div class="mb-6 flex items-center gap-3">
      <div class="h-px flex-1 bg-border" />
      <h2 class="shrink-0 text-xl font-bold tracking-tight text-foreground sm:text-2xl">
        {{ title }}
      </h2>
      <div class="h-px flex-1 bg-border" />
    </div>
    <div class="space-y-6">
      <slot />
    </div>
  </section>
</template>
