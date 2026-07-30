<script setup>
import { cn } from '@/lib/utils'
import { badgeVariants } from '.'

const props = defineProps({
  variant: { type: String, default: 'default' },
  class: { type: null, default: '' },
})
</script>

<template>
  <div :class="cn(badgeVariants({ variant }), props.class)">
    <slot />
  </div>
</template>
