<script setup>
/**
 * Dashboard — the landing surface for Mostisko.
 *
 * An introduction, not an analytics screen. The only numbers here are real
 * counts read back from the API; anything that fails to load stays "—" rather
 * than falling back to a zero that would read as fact.
 */
import { computed, onMounted, ref } from 'vue'
import {
  ArrowRight, Brain, FileText, Globe, LayoutGrid, ListTodo,
  MessageSquare, Plus, Upload,
} from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Kbd } from '@/components/ui/kbd'
import { listConversations, listDocuments, listBoards } from '@/services/index.js'

const props = defineProps({
  username: { type: String, default: '' },
  /**
   * The tabs this user may open, as the server answered. null while unknown.
   *
   * The dashboard advertises other modules, so it has to respect the same list
   * the tab bar and the router guard use: offering a card for a tab the guard
   * will refuse is a dead end, and for a restricted account it also discloses
   * what the deployment runs.
   */
  allowedTabs: { type: Array, default: null },
})

const emit = defineEmits(['navigate', 'new-chat', 'open-palette'])

// null = still loading or unavailable; rendered as "—", never as 0.
const stats = ref({ conversations: null, documents: null, boards: null })

onMounted(async () => {
  // Settled independently: one failing endpoint must not blank the others.
  const [conv, docs, boards] = await Promise.allSettled([
    listConversations(), listDocuments(), listBoards(),
  ])
  stats.value = {
    conversations: conv.status === 'fulfilled' ? conv.value.length : null,
    documents: docs.status === 'fulfilled' ? docs.value.length : null,
    boards: boards.status === 'fulfilled' ? boards.value.length : null,
  }
})

const show = (n) => (n === null ? '—' : String(n))

const statChips = [
  { key: 'conversations', label: 'Conversations', icon: MessageSquare },
  { key: 'documents', label: 'Documents', icon: FileText },
  { key: 'boards', label: 'Boards', icon: LayoutGrid },
]

const allCapabilities = [
  {
    title: 'Chat',
    body: 'Ask questions against your connected sources, with optional live web grounding.',
    icon: MessageSquare,
    tab: 'chat',
  },
  {
    title: 'Workspace',
    body: 'Upload a purchase order and extract its fields, with a review step before anything is trusted.',
    icon: Brain,
    tab: 'workspace',
  },
  {
    title: 'To-Do',
    body: 'A Kanban board with columns you define, drag-and-drop, and multi-line cards.',
    icon: ListTodo,
    tab: 'todo',
  },
  {
    title: 'Search',
    body: 'Search the web and carry any result straight into a grounded conversation.',
    icon: Globe,
    tab: 'search',
  },
]

const allActions = [
  // New chat opens the chat tab, so it is gated on it like any other entry.
  { label: 'New chat', icon: Plus, keys: 'mod + K', event: 'new-chat', tab: 'chat' },
  { label: 'Upload a PO', icon: Upload, keys: 'g then w', tab: 'workspace' },
  { label: 'Open the board', icon: ListTodo, keys: 'g then t', tab: 'todo' },
  { label: 'Browse documents', icon: FileText, keys: 'g then d', tab: 'docs' },
]

// Unknown permissions (null) show nothing rather than guessing: the same
// choice the shell makes, and a guess could only mislead.
const may = (tab) => !tab || (props.allowedTabs ?? []).includes(tab)

const capabilities = computed(() => allCapabilities.filter((c) => may(c.tab)))
const actions = computed(() => allActions.filter((a) => may(a.tab)))

function runAction(a) {
  if (a.event === 'new-chat') emit('new-chat')
  else if (a.tab) emit('navigate', a.tab)
}
</script>

<template>
  <div class="h-full overflow-y-auto">
    <div class="mx-auto flex min-h-full w-full max-w-4xl flex-col gap-10 px-6 py-12">
      <!-- Hero -->
      <header class="flex flex-col items-start gap-4">
        <span class="flex h-11 w-11 items-center justify-center rounded-xl border bg-card shadow-sm">
          <Brain class="h-5 w-5" />
        </span>

        <div>
          <h1 class="text-3xl font-semibold tracking-tight">
            মস্তিস্ক <span class="text-muted-foreground">the ai</span>
          </h1>
          <p class="mt-2 text-base text-muted-foreground">
            One workspace for the documents, questions and work in front of you.
          </p>
        </div>

        <p class="max-w-2xl text-sm leading-relaxed text-muted-foreground">
          {{ username ? `Welcome back, ${username}.` : 'Welcome.' }}
          Your tools for the documents, questions and work in front of you —
          each one listed below.
        </p>

        <div class="flex flex-wrap items-center gap-2 pt-1">
          <Button v-if="may('chat')" class="gap-1.5" @click="emit('new-chat')">
            <Plus class="h-4 w-4" />
            New chat
          </Button>
          <Button variant="outline" class="gap-2" @click="emit('open-palette')">
            Command palette
            <Kbd keys="mod + K" size="sm" />
          </Button>
        </div>
      </header>

      <!-- Real counts only -->
      <section class="flex flex-wrap gap-3">
        <div
          v-for="c in statChips"
          :key="c.key"
          class="flex min-w-[9rem] flex-1 items-center gap-3 rounded-lg border bg-card px-4 py-3"
        >
          <component :is="c.icon" class="h-4 w-4 shrink-0 text-muted-foreground" />
          <div class="min-w-0">
            <p class="text-xl font-semibold tabular-nums leading-none">
              {{ show(stats[c.key]) }}
            </p>
            <p class="mt-1 truncate text-xs text-muted-foreground">
              {{ c.label }}
            </p>
          </div>
        </div>
      </section>

      <!-- What this workspace does -->
      <section>
        <h2 class="mb-3 text-sm font-medium text-foreground">
          What you can do here
        </h2>
        <div class="grid gap-3 sm:grid-cols-2">
          <button
            v-for="c in capabilities"
            :key="c.tab"
            class="group flex flex-col items-start gap-2 rounded-lg border bg-card p-4 text-left transition-colors hover:border-foreground/20 hover:bg-accent/40"
            @click="emit('navigate', c.tab)"
          >
            <span class="flex items-center gap-2 text-sm font-medium">
              <component :is="c.icon" class="h-4 w-4 text-muted-foreground" />
              {{ c.title }}
              <ArrowRight
                class="h-3.5 w-3.5 -translate-x-1 text-muted-foreground opacity-0 transition-all group-hover:translate-x-0 group-hover:opacity-100"
              />
            </span>
            <span class="text-xs leading-relaxed text-muted-foreground">
              {{ c.body }}
            </span>
          </button>
        </div>
      </section>

      <!-- Quick actions -->
      <section>
        <h2 class="mb-3 text-sm font-medium text-foreground">
          Quick actions
        </h2>
        <div class="divide-y overflow-hidden rounded-lg border bg-card">
          <button
            v-for="a in actions"
            :key="a.label"
            class="flex w-full items-center gap-3 px-4 py-3 text-left text-sm transition-colors hover:bg-accent/40"
            @click="runAction(a)"
          >
            <component :is="a.icon" class="h-4 w-4 shrink-0 text-muted-foreground" />
            <span class="flex-1">{{ a.label }}</span>
            <Kbd :keys="a.keys" size="sm" />
          </button>
        </div>
      </section>

      <!-- Attribution -->
      <footer class="mt-auto border-t pt-6 text-center">
        <p class="text-xs text-muted-foreground">
          A creation of Mostisko, the AI.
        </p>
      </footer>
    </div>
  </div>
</template>
