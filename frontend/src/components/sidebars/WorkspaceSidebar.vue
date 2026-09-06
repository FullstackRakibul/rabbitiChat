<script setup>
/**
 * Workspace sidebar: the ingested documents, and the current extraction session.
 *
 * Selecting a document pins it as the workspace's active document — the same
 * action the Workspace tab's own picker performs.
 */
import { Brain, FileText, Plus } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Separator } from '@/components/ui/separator'
import { Kbd } from '@/components/ui/kbd'

const props = defineProps({
  documents: { type: Array, default: () => [] },
  /** The document pinned in the current workspace session, if any. */
  activeDoc: { type: Object, default: null },
  /** Transcript of the running session — used for its message count only. */
  messages: { type: Array, default: () => [] },
  busy: { type: Boolean, default: false },
})

const emit = defineEmits(['new-session', 'open-document', 'go-documents'])

const statusDot = {
  ready: 'bg-emerald-500',
  processing: 'bg-amber-500',
  failed: 'bg-destructive',
}
</script>

<template>
  <div class="flex min-h-0 flex-1 flex-col">
    <div class="p-3">
      <Button class="w-full justify-start gap-2" title="Start a new workspace session" @click="emit('new-session')">
        <Plus />
        <span class="flex-1 text-left">New session</span>
      </Button>
    </div>

    <Separator />

    <!-- Current session -->
    <div class="shrink-0 p-3">
      <p class="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
        Current session
      </p>

      <div v-if="activeDoc" class="rounded-md border bg-card px-2.5 py-2">
        <div class="flex items-start gap-2">
          <Brain class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
          <div class="min-w-0 flex-1">
            <p class="truncate text-sm font-medium" :title="activeDoc.fileName">
              {{ activeDoc.fileName }}
            </p>
            <p class="mt-0.5 text-[11px] text-muted-foreground">
              {{ messages.length }} {{ messages.length === 1 ? 'entry' : 'entries' }}
              <span v-if="busy"> · working…</span>
            </p>
          </div>
        </div>
      </div>

      <p v-else class="rounded-md border border-dashed px-2.5 py-3 text-xs text-muted-foreground">
        No document pinned yet — upload one to start extracting.
      </p>
    </div>

    <Separator />

    <!-- Documents -->
    <div class="flex items-center justify-between px-3 py-2">
      <p class="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
        Documents
      </p>
      <Button variant="ghost" size="sm" class="h-6 px-2 text-[11px]" @click="emit('go-documents')">
        View all
      </Button>
    </div>

    <div class="min-h-0 flex-1 overflow-y-auto px-2 pb-3">
      <div
        v-for="d in documents"
        :key="d.id"
        :class="cn(
          'group flex cursor-pointer items-center gap-2 rounded-md px-2 py-2 text-sm hover:bg-accent',
          activeDoc && d.id === activeDoc.id && 'bg-accent font-medium',
        )"
        :title="d.fileName"
        @click="emit('open-document', d)"
      >
        <FileText class="h-4 w-4 shrink-0 text-muted-foreground" />
        <span class="min-w-0 flex-1 truncate">{{ d.fileName }}</span>
        <span
          :class="cn('h-1.5 w-1.5 shrink-0 rounded-full', statusDot[d.status] || 'bg-muted-foreground/30')"
          :title="d.status"
        />
      </div>

      <p v-if="!documents.length" class="px-2 py-1.5 text-xs text-muted-foreground">
        No documents yet.
      </p>
    </div>

    <Separator />

    <div class="shrink-0 px-3 py-2.5 text-[11px] text-muted-foreground">
      <span class="flex items-center gap-1.5">
        <Kbd keys="mod + B" size="sm" /> toggle this panel
      </span>
    </div>
  </div>
</template>
