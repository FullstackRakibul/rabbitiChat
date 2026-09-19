<script setup>
/**
 * DocModule — renders a module's documentation in three consistent registers:
 *
 *  1. PURPOSE  — what it's for, what problem it solves (indigo dot)
 *  2. USER GUIDE — concrete steps a user follows (emerald dot, numbered)
 *  3. TECHNICAL — stack, key decisions, code snippets (amber dot)
 *
 * Every module in the app is documented through this same component, so the
 * reader always knows where to look for "what does it do / how do I use it /
 * how is it built".
 *
 * @typedef {import('./sections-data.js').ModuleDef} ModuleDef
 */
import DocCallout from './DocCallout.vue'
import DocCodeBlock from './DocCodeBlock.vue'

const props = defineProps({
  /** The module data object — see ModuleDef in sections-data.js. */
  module: { type: Object, required: true },
  /** When true, renders a sub-heading for use inside a grouped section. */
  showTitle: { type: Boolean, default: false },
  /** When true, uses tighter spacing for brief module entries. */
  compact: { type: Boolean, default: false },
})
</script>

<template>
  <div :class="compact ? 'space-y-3' : 'space-y-5'">
    <!-- Optional sub-heading for grouped/brief modules -->
    <h3
      v-if="showTitle"
      class="text-lg font-semibold tracking-tight text-foreground"
    >
      {{ module.title }}
    </h3>

    <!-- ① PURPOSE -->
    <div class="rounded-lg border border-border bg-card p-4 sm:p-5">
      <h4
        class="mb-3 flex items-center gap-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground"
      >
        <span class="h-2 w-2 shrink-0 rounded-full bg-indigo-500" />
        Purpose
      </h4>
      <p class="text-sm leading-relaxed text-foreground/90">
        {{ module.purpose }}
      </p>
    </div>

    <!-- ② USER GUIDE -->
    <div class="rounded-lg border border-border bg-card p-4 sm:p-5">
      <h4
        class="mb-3 flex items-center gap-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground"
      >
        <span class="h-2 w-2 shrink-0 rounded-full bg-emerald-500" />
        User Guide
      </h4>
      <ol class="space-y-2.5 text-sm">
        <li
          v-for="(step, i) in module.guide"
          :key="i"
          class="flex gap-3"
        >
          <span
            class="mt-0.5 flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-muted text-[10px] font-bold text-muted-foreground"
          >
            {{ i + 1 }}
          </span>
          <div class="min-w-0">
            <span class="font-medium text-foreground">{{ step.step }}</span>
            <span
              v-if="step.detail"
              class="text-muted-foreground"
            >
              — {{ step.detail }}
            </span>
          </div>
        </li>
      </ol>
    </div>

    <!-- ③ TECHNICAL -->
    <div class="rounded-lg border border-border bg-card p-4 sm:p-5">
      <h4
        class="mb-3 flex items-center gap-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground"
      >
        <span class="h-2 w-2 shrink-0 rounded-full bg-amber-500" />
        Technical
      </h4>

      <!-- Stack chips -->
      <div v-if="module.stack?.length" class="mb-4 flex flex-wrap gap-1.5">
        <span
          v-for="tech in module.stack"
          :key="tech"
          class="rounded-full bg-muted px-2.5 py-0.5 text-[11px] font-medium text-muted-foreground"
        >
          {{ tech }}
        </span>
      </div>

      <!-- Key engineering decisions / callouts -->
      <div v-if="module.decisions?.length" class="mb-4 space-y-3">
        <DocCallout
          v-for="(note, i) in module.decisions"
          :key="i"
          :variant="note.variant || 'note'"
          :title="note.title"
        >
          {{ note.detail }}
        </DocCallout>
      </div>

      <!-- Code snippets -->
      <div v-if="module.snippets?.length" class="space-y-3">
        <DocCodeBlock
          v-for="(s, i) in module.snippets"
          :key="i"
          :filename="s.filename"
          :language="s.language"
          :code="s.code"
        />
      </div>

      <!-- Fallback when no technical detail is provided -->
      <p
        v-if="!module.stack?.length && !module.decisions?.length && !module.snippets?.length"
        class="text-sm italic text-muted-foreground"
      >
        Uses the shared application stack (Vue 3, .NET 8 API, SQLite).
      </p>
    </div>
  </div>
</template>
