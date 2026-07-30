<script setup>
import {
  SelectContent as RekaSelectContent,
  SelectPortal,
  SelectViewport,
} from 'reka-ui'
import { cn } from '@/lib/utils'

const props = defineProps({
  class: { type: null, default: '' },
  position: { type: String, default: 'popper' },
})
</script>

<template>
  <SelectPortal>
    <RekaSelectContent
      :position="props.position"
      :side-offset="4"
      :class="cn(
        'relative z-50 max-h-96 min-w-32 overflow-hidden rounded-md border bg-popover text-popover-foreground shadow-md data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95',
        props.position === 'popper' && 'data-[side=bottom]:translate-y-1 data-[side=top]:-translate-y-1',
        props.class,
      )"
    >
      <!-- Only the *width* is tied to the trigger; constraining the height here
           would collapse the list to a single row. -->
      <SelectViewport
        :class="cn(
          'p-1',
          props.position === 'popper' && 'w-full min-w-(--reka-select-trigger-width)',
        )"
      >
        <slot />
      </SelectViewport>
    </RekaSelectContent>
  </SelectPortal>
</template>
