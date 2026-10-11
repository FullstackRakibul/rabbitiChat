<script setup>
/**
 * The application shell: a persistent header above a body with an optional
 * left sidebar.
 *
 * The header lives here and is never unmounted, so switching modules never
 * rebuilds the brand, the module launcher or the theme toggle. Navigation is the
 * Google-apps pattern: a chip names the open module, and it and the grid button
 * both open the launcher — every module the user may open, as tiles. The
 * `sidebar` slot is filled only by tabs that need one (Chat, Workspace); when
 * nothing is passed the body stretches full-bleed.
 */
import { computed, ref } from 'vue'
import { ChevronDown, Grip, Menu, Moon, Sun, X } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Kbd } from '@/components/ui/kbd'
import ModuleLauncher from '@/components/launcher/ModuleLauncher.vue'

const props = defineProps({
  /** The modules this user may open: [{ id, label, icon, group }]. */
  tabs: { type: Array, default: () => [] },
  /** The open module's id — the chip names it and the launcher marks it. */
  activeTab: { type: String, default: '' },
  /** The signed-in user's id (0 until known): keys their launcher favourites. */
  userId: { type: Number, default: 0 },
  /** Permissions still loading: the chip shows a placeholder, never a guess. */
  loading: { type: Boolean, default: false },
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
  'update:sidebarOpen', 'update:mobileOpen', 'toggle-theme', 'open-palette', 'navigate',
])

const showSidebar = computed(() => props.hasSidebar)

const current = computed(() => props.tabs.find((t) => t.id === props.activeTab) ?? null)

/** The grid button's launcher: what "g then m" and the palette open. */
const gridLauncher = ref(null)

defineExpose({ openLauncher: () => gridLauncher.value?.show() })
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

      <span class="mr-1 hidden shrink-0 truncate text-lg font-semibold tracking-tight sm:inline">
        মস্তিস্ক the ai
      </span>

      <!-- The open module, named — and one of the two ways into the launcher. -->
      <div v-if="loading" class="h-9 w-36 shrink-0 animate-pulse rounded-full bg-muted" aria-hidden="true" />
      <ModuleLauncher
        v-else-if="tabs.length"
        :tabs="tabs"
        :active-tab="activeTab"
        :user-id="userId"
        align="start"
        @launch="emit('navigate', $event)"
      >
        <template #trigger>
          <button
            type="button"
            data-testid="module-chip"
            class="inline-flex h-9 min-w-0 max-w-56 items-center gap-2 rounded-full border bg-muted/50 pl-1.5 pr-2.5 text-sm font-medium transition-colors hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            :aria-label="current ? `${current.label}: open the module launcher` : 'Open the module launcher'"
          >
            <span class="grid size-6 shrink-0 place-items-center rounded-full bg-primary text-primary-foreground">
              <component :is="current.icon" v-if="current" class="size-3.5" :stroke-width="2" aria-hidden="true" />
            </span>
            <span class="truncate">{{ current?.label ?? 'Modules' }}</span>
            <ChevronDown class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
          </button>
        </template>
      </ModuleLauncher>

      <div class="ml-auto flex shrink-0 items-center gap-2">
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

        <ModuleLauncher
          v-if="!loading && tabs.length"
          ref="gridLauncher"
          :tabs="tabs"
          :active-tab="activeTab"
          :user-id="userId"
          align="end"
          @launch="emit('navigate', $event)"
        >
          <template #trigger>
            <button
              type="button"
              data-testid="launcher-button"
              class="inline-flex size-9 shrink-0 items-center justify-center rounded-md transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
              title="Modules (g then m)"
              aria-label="Open the module launcher"
            >
              <Grip class="size-5" aria-hidden="true" />
            </button>
          </template>
        </ModuleLauncher>
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
