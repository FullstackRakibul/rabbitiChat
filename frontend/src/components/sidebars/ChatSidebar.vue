<script setup>
/**
 * Chat sidebar: new-chat action, connected sources, and the conversation list.
 *
 * This is the previous AppSidebar's content, unchanged in behaviour — the
 * brand row and the drawer chrome now live in AppShell, so this component is
 * purely the Chat tab's own panel.
 */
import { computed } from 'vue'
import { LogOut, MessageSquare, Plus, Search, Settings, Trash2 } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Checkbox } from '@/components/ui/checkbox'
import { Separator } from '@/components/ui/separator'
import { Kbd } from '@/components/ui/kbd'

const props = defineProps({
  username: { type: String, default: '' },
  initials: { type: String, default: '?' },
  sources: { type: Array, default: () => [] },
  sourceSearch: { type: String, default: '' },
  filteredSources: { type: Array, default: () => [] },
  activeSourcesCount: { type: Number, default: 0 },
  groupedConversations: { type: Array, default: () => [] },
  conversationCount: { type: Number, default: 0 },
  currentId: { type: [String, Number], default: null },
})

const emit = defineEmits([
  'update:sourceSearch', 'new-chat', 'open-conversation',
  'delete-conversation', 'clear-all', 'logout', 'open-settings',
])

const searchModel = computed({
  get: () => props.sourceSearch,
  set: (v) => emit('update:sourceSearch', v),
})
</script>

<template>
  <div class="flex min-h-0 flex-1 flex-col">
    <div class="p-3">
      <Button class="w-full justify-start gap-2" title="Start a new chat" @click="emit('new-chat')">
        <Plus />
        <span class="flex-1 text-left">New Chat</span>
        <Kbd keys="mod + K" size="sm" class="opacity-80" />
      </Button>
    </div>

    <Separator />

    <!-- Connected sources -->
    <div class="shrink-0 p-3">
      <p class="mb-2 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
        Connected sources
      </p>

      <div class="relative mb-2">
        <Search
          class="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
        <Input v-model="searchModel" placeholder="Search sources…" class="h-8 pl-8 text-xs" />
      </div>

      <div class="max-h-36 space-y-0.5 overflow-y-auto">
        <label v-for="s in filteredSources" :key="s.id"
          class="flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-accent">
          <Checkbox v-model="s.active" />
          <span class="flex-1 truncate">{{ s.name }}</span>
          <span :class="cn('h-1.5 w-1.5 rounded-full', s.active ? 'bg-foreground' : 'bg-muted-foreground/30')" />
        </label>
        <p v-if="!filteredSources.length" class="px-2 py-1.5 text-xs text-muted-foreground">
          No sources match.
        </p>
      </div>

      <p class="mt-2 text-[11px] text-muted-foreground">
        {{ activeSourcesCount }} of {{ sources.length }} active
      </p>
    </div>

    <Separator />

    <!-- Conversations -->
    <div class="flex items-center justify-between px-3 py-2">
      <p class="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
        Your conversations
      </p>
      <Button variant="ghost" size="sm" class="h-6 px-2 text-[11px]" @click="emit('clear-all')">
        Clear All
      </Button>
    </div>

    <div class="min-h-0 flex-1 overflow-y-auto px-2 pb-2">
      <template v-for="g in groupedConversations" :key="g.key">
        <p v-if="g.label" class="px-2 py-1.5 text-[11px] font-medium text-muted-foreground">
          {{ g.label }}
        </p>
        <div v-for="c in g.items" :key="c.id" :class="cn(
          'group flex cursor-pointer items-center gap-2 rounded-md px-2 py-2 text-sm hover:bg-accent',
          c.id === currentId && 'bg-accent font-medium',
        )" @click="emit('open-conversation', c.id)">
          <MessageSquare class="h-4 w-4 shrink-0 text-muted-foreground" />
          <span class="flex-1 truncate">{{ c.title }}</span>
          <button
            class="shrink-0 rounded p-1 text-muted-foreground opacity-0 transition hover:text-destructive focus:opacity-100 group-hover:opacity-100"
            title="Delete" @click.stop="emit('delete-conversation', c.id)">
            <Trash2 class="h-3.5 w-3.5" />
          </button>
        </div>
      </template>

      <p v-if="!conversationCount" class="px-2 py-1.5 text-xs text-muted-foreground">
        No conversations yet.
      </p>
    </div>

    <Separator />

    <!-- Bottom: settings + account -->
    <div class="shrink-0 p-3">
      <Button variant="ghost" class="mb-2 w-full justify-start" @click="emit('open-settings')">
        <Settings />
        Settings
      </Button>

      <div class="flex items-center gap-2 rounded-md border px-2 py-2">
        <span
          class="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-primary text-[11px] font-semibold text-primary-foreground">
          {{ initials }}
        </span>
        <span class="flex-1 truncate text-sm">{{ username }}</span>
        <Button variant="ghost" size="icon" class="h-7 w-7" title="Log out" @click="emit('logout')">
          <LogOut class="h-3.5 w-3.5" />
        </Button>
      </div>
    </div>
  </div>
</template>
