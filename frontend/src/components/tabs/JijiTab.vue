<script setup>
/**
 * Jiji — notes, books and thoughts into English YouTube Shorts (docs/JIJI_PLAN.md).
 *
 * Sub-navigation only, like Oddity: each board is its own component under
 * components/jiji/, mounted the first time its entry is chosen and only hidden
 * after, so it fetches on first open and keeps its state across switches.
 * Queue holds the items and their script editor; Settings shows what is configured.
 */
import { ref, watch } from 'vue'
import { LayoutList, Settings2 } from '@lucide/vue'
import JijiSettings from '@/components/jiji/JijiSettings.vue'
import QueueBoard from '@/components/jiji/QueueBoard.vue'

/** @type {import('vue').Ref<'queue' | 'settings'>} */
const view = ref('queue')

const views = [
  { id: 'queue', label: 'Queue', icon: LayoutList, board: QueueBoard },
  { id: 'settings', label: 'Settings', icon: Settings2, board: JijiSettings },
]

/** Boards opened at least once. Mounted from then on; v-show hides the rest. */
const opened = ref(new Set())
watch(view, (id) => { opened.value = new Set(opened.value).add(id) }, { immediate: true })
</script>

<template>
  <div class="flex h-full flex-col" data-jiji>
    <div class="flex shrink-0 items-center gap-1 overflow-x-auto border-b bg-background/95 px-4 py-2 backdrop-blur sm:px-6">
      <button
        v-for="v in views"
        :key="v.id"
        type="button"
        :data-jiji-tab="v.id"
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
          <component :is="v.board" v-if="opened.has(v.id)" v-show="view === v.id" />
        </template>
        <section v-else-if="view === v.id" class="mx-auto w-full max-w-2xl rounded-lg border bg-card p-6">
          <h2 class="flex items-center gap-2 text-lg font-semibold tracking-tight">
            <component :is="v.icon" class="h-5 w-5" />
            {{ v.label }}
          </h2>
          <p class="mt-1 text-sm text-muted-foreground">{{ v.blurb }}</p>
          <p class="mt-4 rounded-md bg-muted px-3 py-2 text-sm text-muted-foreground">Not built yet.</p>
        </section>
      </template>
    </div>
  </div>
</template>
