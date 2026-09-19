<script setup>
/**
 * DocApiExample — shows an HTTP method + path with a REQUEST/RESPONSE badge,
 * embedding a DocCodeBlock for the payload.
 */
import DocCodeBlock from './DocCodeBlock.vue'

const props = defineProps({
  /** HTTP method: GET, POST, PUT, DELETE, PATCH */
  method: { type: String, default: 'GET' },
  /** API endpoint path. */
  path: { type: String, default: '' },
  /** Whether this shows the request or the response body. */
  type: {
    type: String,
    default: 'request',
    validator: (v) => ['request', 'response'].includes(v),
  },
  /** The code content (JSON, etc.). */
  code: { type: String, default: '' },
  /** Language label for the code block. */
  language: { type: String, default: 'json' },
})

/** @type {Record<string, string>} */
const methodColors = {
  GET: 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400 ring-emerald-500/20',
  POST: 'bg-blue-500/10 text-blue-600 dark:text-blue-400 ring-blue-500/20',
  PUT: 'bg-amber-500/10 text-amber-600 dark:text-amber-400 ring-amber-500/20',
  DELETE: 'bg-red-500/10 text-red-600 dark:text-red-400 ring-red-500/20',
  PATCH: 'bg-purple-500/10 text-purple-600 dark:text-purple-400 ring-purple-500/20',
}
</script>

<template>
  <div class="space-y-2">
    <!-- Endpoint + badge row -->
    <div class="flex flex-wrap items-center gap-2 text-sm">
      <span
        :class="[
          'inline-flex rounded-md px-2 py-0.5 text-xs font-bold uppercase ring-1 ring-inset',
          methodColors[method] || methodColors.GET,
        ]"
      >
        {{ method }}
      </span>
      <code class="font-mono text-sm text-foreground/80">{{ path }}</code>
      <span
        :class="[
          'ml-auto inline-flex rounded-full px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide',
          type === 'request'
            ? 'bg-slate-500/10 text-slate-600 dark:text-slate-400'
            : 'bg-indigo-500/10 text-indigo-600 dark:text-indigo-400',
        ]"
      >
        {{ type }}
      </span>
    </div>

    <!-- Code block -->
    <DocCodeBlock :language="language" :code="code" />
  </div>
</template>
