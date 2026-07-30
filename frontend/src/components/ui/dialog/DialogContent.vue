<script setup>
import {
  DialogClose,
  DialogContent as RekaDialogContent,
  DialogOverlay,
  DialogPortal,
} from 'reka-ui'
import { X } from '@lucide/vue'
import { cn } from '@/lib/utils'

const props = defineProps({
  class: { type: null, default: '' },
  showClose: { type: Boolean, default: true },
})

// Reka's interaction events (escapeKeyDown, interactOutside, …) reach the
// caller through fallthrough attrs, so no explicit emits declaration.
</script>

<template>
  <DialogPortal>
    <DialogOverlay
      class="fixed inset-0 z-50 bg-black/80 data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0"
    />
    <RekaDialogContent
      :class="cn(
        'fixed left-1/2 top-1/2 z-50 grid w-full max-w-lg -translate-x-1/2 -translate-y-1/2 gap-4 border bg-background p-6 shadow-lg duration-200 data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95 sm:rounded-lg',
        props.class,
      )"
    >
      <slot />

      <DialogClose
        v-if="showClose"
        class="absolute right-4 top-4 rounded-sm opacity-70 ring-offset-background transition-opacity hover:opacity-100 focus:outline-none focus:ring-2 focus:ring-ring focus:ring-offset-2 disabled:pointer-events-none"
      >
        <X class="h-4 w-4" />
        <span class="sr-only">Close</span>
      </DialogClose>
    </RekaDialogContent>
  </DialogPortal>
</template>
