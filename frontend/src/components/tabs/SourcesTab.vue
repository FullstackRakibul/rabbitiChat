<script setup>
import { ref } from 'vue'
import ContextSourceManager from '@/components/ContextSourceManager.vue'

/**
 * Connected sources — the full editor. The composer's popover only toggles
 * these; everything that creates or changes one happens here.
 */
const emit = defineEmits(['changed'])

const manager = ref(null)

// Lets the parent re-read the list after a change made elsewhere.
defineExpose({ refresh: () => manager.value?.refresh() })
</script>

<template>
  <div class="h-full overflow-y-auto p-4 sm:p-6">
    <ContextSourceManager
      ref="manager"
      kind="source"
      title="Connected Sources"
      description="Each active source is prepended to the system prompt of the chats you switch it on for, in the order shown."
      add-label="Add source"
      @changed="emit('changed', $event)"
    />
  </div>
</template>
