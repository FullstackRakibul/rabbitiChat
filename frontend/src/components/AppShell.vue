<script setup>
/**
 * The application shell: a persistent header above a body with an optional
 * left sidebar.
 *
 * The header lives here and is never unmounted, so switching tabs never
 * rebuilds the brand, tab bar, or theme toggle. The `sidebar` slot is filled
 * only by tabs that need one (Chat, Workspace); when nothing is passed the
 * body stretches full-bleed.
 */
import { computed } from 'vue'
import { Menu, Moon, Sun, X } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Kbd } from '@/components/ui/kbd'
import { TabsList, TabsTrigger } from '@/components/ui/tabs'

const props = defineProps({
  tabs: { type: Array, default: () => [] },
  theme: { type: String, default: 'light' },
  connected: { type: Boolean, default: false },
  status: { type: String, default: '' },
  /**
   * Whether the active tab has a sidebar at all. Passed explicitly rather than
   * inferred from the slot: a `v-if` on the caller's <template #sidebar> still
   * provides a slot function, so slot presence can't answer this.
   */
  hasSidebar: { type: Boolean, default: false },
  /** Whether that sidebar is expanded — only meaningful when hasSidebar. */
  sidebarOpen: { type: Boolean, default: true },
  /** Mobile drawer state, separate from the desktop collapse above. */
  mobileOpen: { type: Boolean, default: false },
})

const emit = defineEmits([
  'update:sidebarOpen', 'update:mobileOpen', 'toggle-theme', 'open-palette',
])

const showSidebar = computed(() => props.hasSidebar)
</script>

<template>
  <div class="flex h-screen flex-col overflow-hidden bg-background text-foreground">
    <!-- Persistent header — mounted once, outlives every tab switch. -->
    <header class="flex h-14 shrink-0 items-center gap-2 border-b px-3 sm:px-4">
      <Button
        v-if="showSidebar"
        variant="ghost"
        size="icon"
        class="md:hidden"
        aria-label="Open menu"
        @click="emit('update:mobileOpen', true)"
      >
        <Menu />
      </Button>

      <span class="mr-1 hidden truncate text-lg font-semibold tracking-tight sm:inline">
        মস্তিস্ক the ai
      </span>

      <TabsList class="h-9 overflow-x-auto">
        <TabsTrigger v-for="t in tabs" :key="t.id" :value="t.id">
          <component :is="t.icon" />
          <span class="hidden sm:inline">{{ t.label }}</span>
        </TabsTrigger>
      </TabsList>

      <div class="ml-auto flex items-center gap-2">
        <button
          class="hidden items-center gap-2 rounded-md border px-2 py-1.5 text-xs text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground lg:flex"
          title="Open command palette"
          @click="emit('open-palette')"
        >
          <span>Search…</span>
          <Kbd keys="mod + K" size="sm" />
        </button>

        <span
          class="hidden items-center gap-1.5 text-xs text-muted-foreground sm:flex"
          :title="status"
        >
          <span :class="cn(
            'h-2 w-2 rounded-full',
            connected ? 'bg-emerald-500' : 'bg-muted-foreground/40',
          )" />
        </span>

        <Button
          variant="ghost"
          size="icon"
          :title="theme === 'dark' ? 'Switch to light' : 'Switch to dark'"
          @click="emit('toggle-theme')"
        >
          <Moon v-if="theme === 'dark'" />
          <Sun v-else />
          <span class="sr-only">Toggle theme</span>
        </Button>
      </div>
    </header>

    <!-- Body: optional sidebar + content -->
    <div class="flex min-h-0 flex-1">
      <template v-if="showSidebar">
        <!-- Mobile backdrop -->
        <div
          v-show="mobileOpen"
          class="fixed inset-0 z-40 bg-black/50 md:hidden"
          @click="emit('update:mobileOpen', false)"
        />

        <aside :class="cn(
          'fixed inset-y-0 left-0 z-50 flex w-72 flex-col border-r bg-card transition-transform duration-200 md:static md:z-auto',
          mobileOpen ? 'translate-x-0' : '-translate-x-full',
          // On desktop the drawer transform is irrelevant; sidebarOpen decides
          // whether the column exists at all.
          sidebarOpen ? 'md:translate-x-0' : 'md:hidden',
        )">
          <div class="flex h-14 shrink-0 items-center justify-between gap-2 border-b px-4 md:hidden">
            <span class="truncate text-lg font-semibold tracking-tight">মস্তিস্ক the ai</span>
            <Button
              variant="ghost"
              size="icon"
              aria-label="Close menu"
              @click="emit('update:mobileOpen', false)"
            >
              <X />
            </Button>
          </div>

          <slot name="sidebar" />
        </aside>
      </template>

      <main class="flex min-w-0 flex-1 flex-col">
        <slot />
      </main>
    </div>
  </div>
</template>
