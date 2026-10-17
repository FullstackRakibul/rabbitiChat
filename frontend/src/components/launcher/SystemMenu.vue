<script setup>
/**
 * The header's System menu — the grid button's panel: the System modules this
 * user may open, then who is signed in and a one-click log out.
 *
 * The items are the `tabs` the shell already has (the server's answer), filtered
 * to the System group, so there is no second list to keep in step. A user with
 * no System tabs still gets the user block: logging out is always one click away.
 *
 * Built on reka's Popover, as ModuleLauncher is: Esc, outside click, focus return
 * and aria-expanded come with it. Plain buttons inside, so Tab walks the items
 * and Enter opens one. On phones the panel spans the screen width.
 *
 * Colours are theme tokens only.
 */
import { computed, ref } from 'vue'
import { LogOut } from '@lucide/vue'
import { PopoverContent, PopoverPortal, PopoverRoot, PopoverTrigger } from 'reka-ui'
import { Button } from '@/components/ui/button'

const props = defineProps({
  /** The modules this user may open: [{ id, label, icon, group }]. */
  tabs: { type: Array, default: () => [] },
  activeTab: { type: String, default: '' },
  username: { type: String, default: '' },
  /** The role as the server labels it, e.g. "hr-assistant"; empty until known. */
  role: { type: String, default: '' },
})

const emit = defineEmits(['launch', 'logout'])

const open = ref(false)

const items = computed(() => props.tabs.filter((t) => t.group === 'System'))

const initials = computed(() => (props.username || '?').trim().slice(0, 2).toUpperCase())

function launch(id) {
  open.value = false
  emit('launch', id)
}

function logout() {
  open.value = false
  emit('logout')
}
</script>

<template>
  <PopoverRoot v-model:open="open">
    <PopoverTrigger as-child>
      <slot name="trigger" :open="open" />
    </PopoverTrigger>
    <PopoverPortal>
      <PopoverContent
        align="end"
        :side-offset="8"
        :collision-padding="8"
        aria-label="System"
        data-testid="system-menu"
        class="z-50 max-h-[calc(100dvh-5rem)] w-[calc(100vw-1rem)] overflow-y-auto overscroll-contain rounded-2xl border bg-popover p-2 text-popover-foreground shadow-lg outline-none data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=closed]:zoom-out-95 data-[state=open]:animate-in data-[state=open]:fade-in-0 data-[state=open]:zoom-in-95 motion-reduce:animate-none! sm:w-72"
      >
        <section v-if="items.length" aria-labelledby="system-menu-title">
          <h3
            id="system-menu-title"
            class="px-2 pb-1 pt-1.5 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground"
          >
            System
          </h3>
          <ul class="grid gap-0.5">
            <li v-for="t in items" :key="t.id">
              <button
                type="button"
                :data-tab-key="t.id"
                :aria-current="t.id === activeTab ? 'page' : undefined"
                class="flex w-full items-center gap-3 rounded-lg px-2 py-2 text-left text-sm transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring aria-[current=page]:bg-accent aria-[current=page]:font-medium"
                @click="launch(t.id)"
              >
                <component :is="t.icon" class="size-4 shrink-0 text-muted-foreground" :stroke-width="1.75" aria-hidden="true" />
                <span class="truncate">{{ t.label }}</span>
              </button>
            </li>
          </ul>
        </section>

        <div
          :class="['flex items-center gap-3 px-2 pb-1', items.length ? 'mt-2 border-t pt-3' : 'pt-1']"
          data-testid="system-menu-user"
        >
          <span
            class="grid size-9 shrink-0 place-items-center rounded-full bg-muted text-xs font-semibold text-foreground"
            aria-hidden="true"
          >{{ initials }}</span>
          <div class="min-w-0 flex-1">
            <p class="truncate text-sm font-medium">{{ username || 'Signed in' }}</p>
            <p v-if="role" class="truncate text-xs text-muted-foreground">{{ role }}</p>
          </div>
        </div>
        <div class="px-2 pb-1 pt-2">
          <Button
            variant="destructive"
            size="sm"
            class="w-full"
            data-testid="logout-button"
            @click="logout"
          >
            <LogOut aria-hidden="true" />
            লগ আউট · Log out
          </Button>
        </div>
      </PopoverContent>
    </PopoverPortal>
  </PopoverRoot>
</template>
