<script setup>
import { Globe, Loader, Search, TriangleAlert } from '@lucide/vue'
import { cn } from '@/lib/utils'
import MarkdownBody from '@/components/MarkdownBody.vue'
import ThinkingBlock from '@/components/ThinkingBlock.vue'

/**
 * A message is { role, content, ... }. Assistant messages may additionally carry:
 *   thinking      — reasoning text, when the model streamed any
 *   thinkingDone  — reasoning finished (so the block can collapse)
 *   tools         — [{ tool, status, detail }] status cards
 *   webSearchUsed — grounded on live results
 *
 * These are all absent unless the stream actually produced them, which is what
 * keeps a non-reasoning model from rendering an empty box.
 */
defineProps({
  messages: { type: Array, default: () => [] },
  busy: { type: Boolean, default: false },
  assistantLabel: { type: String, default: 'Assistant' },
})

const TOOL_LABELS = {
  web_search: { running: 'Searching the web…', done: 'Searched the web', failed: 'Web search failed' },
}

function toolLabel(t) {
  return TOOL_LABELS[t.tool]?.[t.status] ?? `${t.tool} — ${t.status}`
}
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
        :class="cn('flex w-full gap-2.5', m.role === 'user' ? 'justify-end' : 'justify-start')"
      >
        <!-- Assistant avatar -->
        <div
          v-if="m.role === 'assistant'"
          class="mt-0.5 flex h-7 w-7 shrink-0 items-center justify-center rounded-full border bg-muted text-[10px] font-semibold uppercase text-muted-foreground"
          aria-hidden="true"
        >
          {{ assistantLabel.slice(0, 2) }}
        </div>

        <div
          :class="cn(
            'min-w-0 max-w-[85%] rounded-xl px-4 py-3 text-sm leading-relaxed shadow-sm sm:max-w-[75%]',
            m.role === 'user'
              ? 'bg-primary text-primary-foreground'
              : 'border bg-card text-card-foreground',
          )"
        >
          <div
            v-if="m.role === 'assistant'"
            class="mb-1.5 flex items-center gap-1.5 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground"
          >
            {{ assistantLabel }}
            <span
              v-if="m.webSearchUsed"
              class="inline-flex items-center gap-1 rounded border px-1.5 py-px text-[10px] normal-case text-muted-foreground"
              title="Answered using live web results"
            >
              <Globe class="h-2.5 w-2.5" />
              web
            </span>
          </div>

          <!-- Reasoning, only when the model actually sent some. -->
          <ThinkingBlock
            v-if="m.role === 'assistant' && m.thinking"
            :content="m.thinking"
            :streaming="!m.thinkingDone"
            :answered="!!m.content"
          />

          <!-- Tool activity, in the order the model requested it. -->
          <div v-if="m.role === 'assistant' && m.tools?.length" class="mb-2 flex flex-col gap-1">
            <div
              v-for="(t, ti) in m.tools"
              :key="ti"
              class="flex items-center gap-2 rounded-lg border bg-muted/40 px-2.5 py-1.5 text-xs text-muted-foreground"
            >
              <Loader v-if="t.status === 'running'" class="h-3.5 w-3.5 shrink-0 animate-spin" />
              <TriangleAlert v-else-if="t.status === 'failed'" class="h-3.5 w-3.5 shrink-0 text-destructive" />
              <Search v-else class="h-3.5 w-3.5 shrink-0" />

              <span class="font-medium">{{ toolLabel(t) }}</span>
              <span v-if="t.detail" class="min-w-0 truncate italic opacity-80">“{{ t.detail }}”</span>
            </div>
          </div>

          <!-- Assistant output is sanitized markdown; user text stays plain. -->
          <MarkdownBody v-if="m.role === 'assistant' && m.content" :content="m.content" />
          <p v-else-if="m.role !== 'assistant'" class="whitespace-pre-wrap wrap-break-word">
            {{ m.content }}
          </p>

          <!-- Idle → thinking: dots until the first token of anything arrives. -->
          <span
            v-if="busy && i === messages.length - 1 && m.role === 'assistant'
              && !m.content && !m.thinking && !m.tools?.length"
            class="inline-flex items-center gap-1 text-muted-foreground"
            aria-label="Waiting for the model"
          >
            <span class="h-1.5 w-1.5 animate-bounce rounded-full bg-current [animation-delay:-0.3s]" />
            <span class="h-1.5 w-1.5 animate-bounce rounded-full bg-current [animation-delay:-0.15s]" />
            <span class="h-1.5 w-1.5 animate-bounce rounded-full bg-current" />
          </span>

          <!-- Streaming cursor, once text is actually flowing. -->
          <span
            v-else-if="busy && i === messages.length - 1 && m.role === 'assistant' && m.content"
            class="ml-0.5 inline-block animate-pulse"
          >▋</span>
        </div>
      </div>
    </template>
  </div>
</template>
