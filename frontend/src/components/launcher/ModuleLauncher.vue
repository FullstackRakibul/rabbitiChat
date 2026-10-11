<script setup>
/**
 * Module launcher — a popover holding LauncherPanel, opened by whatever the
 * `trigger` slot renders (the header's module chip, or its grid button).
 *
 * Built on reka's Popover directly, as SourcesPopover is: it supplies Esc to
 * close, focus return to the trigger, collision handling and aria-expanded. On
 * open, focus goes to the open module's tile instead of the panel itself.
 */
import { computed, nextTick, ref, toRef } from 'vue'
import { PopoverContent, PopoverPortal, PopoverRoot, PopoverTrigger } from 'reka-ui'
import LauncherPanel from '@/components/launcher/LauncherPanel.vue'
import { useLauncherFavourites } from '@/composables/useLauncherFavourites'

const props = defineProps({
  /** The modules this user may open: [{ id, label, icon, group }]. */
  tabs: { type: Array, default: () => [] },
  activeTab: { type: String, default: '' },
  /** The signed-in user's id; 0 until known. Keys the favourites. */
  userId: { type: Number, default: 0 },
  /** Which edge of the trigger the panel lines up with. */
  align: { type: String, default: 'end' },
})

const emit = defineEmits(['launch'])

const open = ref(false)
const panel = ref(null)
const { favourites, toggle } = useLauncherFavourites(toRef(props, 'userId'), computed(() => props.tabs))

function onOpenAutoFocus(e) {
  e.preventDefault()
  nextTick(() => panel.value?.focusInitial())
}

function launch(id) {
  open.value = false
  emit('launch', id)
}

defineExpose({ show: () => { open.value = true } })
</script>

<template>
  <PopoverRoot v-model:open="open">
    <PopoverTrigger as-child>
      <slot name="trigger" :open="open" />
    </PopoverTrigger>
    <PopoverPortal>
      <PopoverContent
        :align="align"
        :side-offset="8"
        :collision-padding="8"
        aria-label="Modules"
        class="z-50 max-h-[min(40rem,calc(100dvh-5rem))] w-[min(28rem,calc(100vw-1rem))] overflow-y-auto overscroll-contain rounded-2xl border bg-popover p-3 text-popover-foreground shadow-lg outline-none data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=closed]:zoom-out-95 data-[state=open]:animate-in data-[state=open]:fade-in-0 data-[state=open]:zoom-in-95 motion-reduce:animate-none!"
        @open-auto-focus="onOpenAutoFocus"
      >
        <LauncherPanel
          ref="panel"
          :tabs="tabs"
          :active-tab="activeTab"
          :favourites="favourites"
          @launch="launch"
          @toggle-favourite="toggle"
        />
      </PopoverContent>
    </PopoverPortal>
  </PopoverRoot>
</template>
