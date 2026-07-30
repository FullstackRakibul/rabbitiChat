<script setup>
import { DialogDescription as RekaDialogDescription } from 'reka-ui'
import { cn } from '@/lib/utils'

const props = defineProps({ class: { type: null, default: '' } })
</script>

<template>
  <RekaDialogDescription :class="cn('text-sm text-muted-foreground', props.class)">
    <slot />
  </RekaDialogDescription>
</template>
