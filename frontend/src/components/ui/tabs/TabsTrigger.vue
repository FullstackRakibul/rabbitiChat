<script setup>
import { TabsTrigger as RekaTabsTrigger } from 'reka-ui'
import { cn } from '@/lib/utils'

const props = defineProps({
  value: { type: null, required: true },
  disabled: { type: Boolean, default: false },
  class: { type: null, default: '' },
})
</script>

<template>
  <RekaTabsTrigger
    :value="value"
    :disabled="disabled"
    :class="cn(
      'inline-flex items-center justify-center gap-1.5 whitespace-nowrap rounded-md px-3 py-1 text-sm font-medium ring-offset-background transition-all focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:pointer-events-none disabled:opacity-50 data-[state=active]:bg-background data-[state=active]:text-foreground data-[state=active]:shadow [&_svg]:size-4 [&_svg]:shrink-0',
      props.class,
    )"
  >
    <slot />
  </RekaTabsTrigger>
</template>
