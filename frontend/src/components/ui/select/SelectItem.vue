<script setup>
// Aliased: an SFC's filename registers as a self-referencing component name,
// so an unaliased <SelectItem> in the template would resolve to this file.
import {
  SelectItem as RekaSelectItem,
  SelectItemIndicator,
  SelectItemText,
} from 'reka-ui'
import { Check } from '@lucide/vue'
import { cn } from '@/lib/utils'

const props = defineProps({
  value: { type: null, required: true },
  disabled: { type: Boolean, default: false },
  class: { type: null, default: '' },
})
</script>

<template>
  <RekaSelectItem
    :value="value"
    :disabled="disabled"
    :class="cn(
      'relative flex w-full cursor-default select-none items-center rounded-sm py-1.5 pl-2 pr-8 text-sm outline-none focus:bg-accent focus:text-accent-foreground data-[highlighted]:bg-accent data-[highlighted]:text-accent-foreground data-[disabled]:pointer-events-none data-[disabled]:opacity-50',
      props.class,
    )"
  >
    <span class="absolute right-2 flex h-3.5 w-3.5 items-center justify-center">
      <SelectItemIndicator>
        <Check class="h-4 w-4" />
      </SelectItemIndicator>
    </span>
    <SelectItemText>
      <slot />
    </SelectItemText>
  </RekaSelectItem>
</template>
