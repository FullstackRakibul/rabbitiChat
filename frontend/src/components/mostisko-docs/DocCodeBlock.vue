<script setup>
/**
 * DocCodeBlock — a formatted code block with a header bar and copy-to-clipboard.
 *
 * Pass code via the `code` prop (for data-driven rendering and copy support)
 * or via the default slot (DOM text content is read for the copy operation).
 */
import { ref } from 'vue'
import { Copy, Check } from '@lucide/vue'

const props = defineProps({
  /** Optional filename shown in the header bar. */
  filename: { type: String, default: '' },
  /** Optional language label shown in the header bar (if no filename). */
  language: { type: String, default: '' },
  /** Code content — preferred over slot for reliable copy. */
  code: { type: String, default: '' },
})

const copied = ref(false)
const codeEl = ref(null)

async function copyCode() {
  try {
    const text = props.code || codeEl.value?.textContent || ''
    await navigator.clipboard.writeText(text.trim())
    copied.value = true
    setTimeout(() => { copied.value = false }, 2000)
  } catch {
    /* clipboard API unavailable or denied — silent fail */
  }
}

const headerLabel = /** @type {string} */ (props.filename || props.language)
</script>

<template>
  <div class="overflow-hidden rounded-lg border border-border bg-muted/30 dark:bg-muted/50">
    <!-- Header bar -->
    <div
      class="flex items-center justify-between border-b border-border bg-muted/60 px-4 py-2"
    >
      <span class="truncate text-xs font-medium text-muted-foreground">
        {{ headerLabel || 'Code' }}
      </span>
      <button
        class="inline-flex items-center gap-1.5 rounded-md px-2 py-1 text-xs text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
        :title="copied ? 'Copied!' : 'Copy to clipboard'"
        @click="copyCode"
      >
        <component :is="copied ? Check : Copy" class="h-3.5 w-3.5" />
        {{ copied ? 'Copied' : 'Copy' }}
      </button>
    </div>

    <!-- Code body -->
    <pre
      ref="codeEl"
      class="overflow-x-auto p-4 text-[13px] leading-relaxed"
    ><code class="font-mono">{{ code }}<slot v-if="!code" /></code></pre>
  </div>
</template>
