<script setup>
import { TabsContent as RekaTabsContent } from 'reka-ui'
import { cn } from '@/lib/utils'

const props = defineProps({
  value: { type: null, required: true },
  class: { type: null, default: '' },
})
</script>

<template>
  <RekaTabsContent
    :value="value"
    :class="cn(
      'ring-offset-background focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2',
      props.class,
    )"
  >
    <slot />
  </RekaTabsContent>
</template>
