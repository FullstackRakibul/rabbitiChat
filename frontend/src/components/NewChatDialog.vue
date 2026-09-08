<script setup>
import { computed } from 'vue'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { Checkbox } from '@/components/ui/checkbox'
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from '@/components/ui/dialog'

/**
 * Pick the context set for a new conversation.
 *
 * The old "Context" dropdown (General / Code / Research…) is gone: it was a
 * label that changed nothing about the request. Context is now the sources
 * themselves, which really are prepended to the system prompt.
 */
const props = defineProps({
  open: { type: Boolean, default: false },
  sources: { type: Array, default: () => [] },
  /** Ids that will be switched on for the new chat. */
  activeSourceIds: { type: Array, default: () => [] },
})

const emit = defineEmits(['update:open', 'update:activeSourceIds', 'start'])

const openValue = computed({
  get: () => props.open,
  set: (v) => emit('update:open', v),
})

function isOn(id) {
  return props.activeSourceIds.includes(id)
}

function toggle(id, on) {
  emit('update:activeSourceIds', on
    ? [...new Set([...props.activeSourceIds, id])]
    : props.activeSourceIds.filter((x) => x !== id))
}
</script>

<template>
  <Dialog v-model:open="openValue">
    <DialogContent class="sm:max-w-md">
      <DialogHeader>
        <DialogTitle>Start a new chat</DialogTitle>
        <DialogDescription>
          Pick the sources this conversation should have in mind. Each one is prepended
          to the system prompt.
        </DialogDescription>
      </DialogHeader>

      <div class="flex flex-col gap-2">
        <Label>Sources</Label>
        <div
          v-if="sources.length"
          class="flex max-h-48 flex-col gap-0.5 overflow-y-auto rounded-md border p-1"
        >
          <label
            v-for="s in sources"
            :key="s.id"
            class="flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-accent"
          >
            <Checkbox :model-value="isOn(s.id)" @update:model-value="toggle(s.id, $event)" />
            <span class="flex-1 truncate">{{ s.name }}</span>
          </label>
        </div>
        <p v-else class="rounded-md border px-3 py-4 text-center text-xs text-muted-foreground">
          No sources yet — add one in the Sources tab to give chats standing context.
        </p>
      </div>

      <DialogFooter>
        <Button variant="outline" @click="emit('update:open', false)">
          Cancel
        </Button>
        <Button @click="emit('start')">
          Start Chat
        </Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
</template>
