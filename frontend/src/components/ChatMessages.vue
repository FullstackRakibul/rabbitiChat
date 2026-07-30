<script setup>
import { cn } from '@/lib/utils'
import { renderMarkdown } from '@/lib/markdown'

defineProps({
  messages: { type: Array, default: () => [] },
  busy: { type: Boolean, default: false },
  assistantLabel: { type: String, default: 'Assistant' },
})
</script>

<template>
  <div class="flex flex-col gap-4">
    <template v-for="(m, i) in messages" :key="i">
      <!-- Agent activity card (workspace ingestion / extraction events) -->
      <div
        v-if="m.role === 'event'"
        class="mx-auto flex w-full max-w-2xl items-center gap-2 rounded-lg border bg-muted/40 px-3 py-2 text-xs text-muted-foreground"
      >
        <span
          :class="cn(
            'h-1.5 w-1.5 shrink-0 rounded-full',
            m.meta === 'fail' ? 'bg-destructive'
              : m.meta === 'done' ? 'bg-foreground'
                : 'animate-pulse bg-muted-foreground',
          )"
        />
        <span>{{ m.content }}</span>
      </div>

      <!-- Chat bubble -->
      <div
        v-else
        :class="cn('flex w-full', m.role === 'user' ? 'justify-end' : 'justify-start')"
      >
        <div
          :class="cn(
            'max-w-[85%] rounded-xl px-4 py-3 text-sm leading-relaxed shadow-sm sm:max-w-[75%]',
            m.role === 'user'
              ? 'bg-primary text-primary-foreground'
              : 'border bg-card text-card-foreground',
          )"
        >
          <div
            v-if="m.role === 'assistant'"
            class="mb-1.5 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground"
          >
            {{ assistantLabel }}
          </div>

          <!-- Assistant output is sanitized markdown; user text stays plain. -->
          <div v-if="m.role === 'assistant'" class="md" v-html="renderMarkdown(m.content)" />
          <p v-else class="whitespace-pre-wrap wrap-break-word">
            {{ m.content }}
          </p>

          <span
            v-if="busy && i === messages.length - 1 && m.role === 'assistant'"
            class="ml-0.5 inline-block animate-pulse"
          >▋</span>
        </div>
      </div>
    </template>
  </div>
</template>
