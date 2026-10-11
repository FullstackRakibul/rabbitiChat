<script setup>
/**
 * Oddity — the Ground Control tab for the Ollama-compatible gateway.
 *
 * Sub-navigation only: each board is its own component under components/oddity/.
 * A board is mounted the first time its entry is chosen and only hidden after,
 * so it fetches on first open — never when the tab mounts — and keeps its state
 * across switches. Boards not built yet say so.
 */
import { ref, watch } from 'vue'
import { Activity, BookOpen, KeyRound, LayoutDashboard, SlidersHorizontal, UserCog } from '@lucide/vue'
import OddityDocs from '@/components/oddity/OddityDocs.vue'
import OddityKeys from '@/components/oddity/OddityKeys.vue'
import OddityRequests from '@/components/oddity/OddityRequests.vue'
import OdditySettings from '@/components/oddity/OdditySettings.vue'

/** @type {import('vue').Ref<'overview' | 'requests' | 'keys' | 'profiles' | 'settings' | 'docs'>} */
const view = ref('overview')

const views = [
  { id: 'overview', label: 'Overview', icon: LayoutDashboard, blurb: 'Request, error and token analytics.' },
  { id: 'requests', label: 'Requests', icon: Activity, board: OddityRequests },
  { id: 'keys', label: 'Keys', icon: KeyRound, board: OddityKeys },
  { id: 'profiles', label: 'Profiles', icon: UserCog, blurb: 'System prompts, sources and pinned sampling.' },
  { id: 'settings', label: 'Settings', icon: SlidersHorizontal, board: OdditySettings },
  // The same docs as the public /oddity/docs page, plus the live settings and the Ground Control API.
  { id: 'docs', label: 'Docs', icon: BookOpen, board: OddityDocs, props: { admin: true } },
]

/** Boards opened at least once. Mounted from then on; v-show hides the rest. */
const opened = ref(new Set())
watch(view, (id) => { opened.value = new Set(opened.value).add(id) }, { immediate: true })
</script>

<template>
  <div class="flex h-full flex-col">
    <!-- Sub-navigation — one entry per board. -->
    <div
      class="flex shrink-0 items-center gap-1 overflow-x-auto border-b bg-background/95 px-4 py-2 backdrop-blur sm:px-6"
    >
      <button
        v-for="v in views"
        :key="v.id"
        :class="[
          'inline-flex shrink-0 items-center gap-1.5 rounded-md px-3 py-1.5 text-sm font-medium transition-colors',
          'focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring',
          view === v.id
            ? 'bg-primary text-primary-foreground shadow-sm'
            : 'text-muted-foreground hover:bg-muted hover:text-foreground',
        ]"
        :aria-current="view === v.id ? 'page' : undefined"
        @click="view = v.id"
      >
        <component :is="v.icon" class="h-3.5 w-3.5" />
        {{ v.label }}
      </button>
    </div>

    <div class="flex-1 overflow-y-auto p-4 sm:p-6">
      <template v-for="v in views" :key="v.id">
        <template v-if="v.board">
          <component :is="v.board" v-if="opened.has(v.id)" v-show="view === v.id" v-bind="v.props ?? {}" />
        </template>
        <section v-else-if="view === v.id" class="mx-auto w-full max-w-2xl rounded-lg border bg-card p-6">
          <h2 class="flex items-center gap-2 text-lg font-semibold tracking-tight">
            <component :is="v.icon" class="h-5 w-5" />
            {{ v.label }}
          </h2>
          <p class="mt-1 text-sm text-muted-foreground">{{ v.blurb }}</p>
          <p class="mt-4 rounded-md bg-muted px-3 py-2 text-sm text-muted-foreground">
            Not built yet.
          </p>
        </section>
      </template>
    </div>
  </div>
</template>
