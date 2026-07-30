<script setup>
import { computed } from 'vue'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { Checkbox } from '@/components/ui/checkbox'
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from '@/components/ui/dialog'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'

const props = defineProps({
  open: { type: Boolean, default: false },
  contexts: { type: Array, default: () => [] },
  context: { type: String, default: '' },
  sources: { type: Array, default: () => [] },
})

const emit = defineEmits(['update:open', 'update:context', 'start'])

const openValue = computed({
  get: () => props.open,
  set: (v) => emit('update:open', v),
})
const contextValue = computed({
  get: () => props.context,
  set: (v) => emit('update:context', v),
})
</script>

<template>
  <Dialog v-model:open="openValue">
    <DialogContent class="sm:max-w-md">
      <DialogHeader>
        <DialogTitle>Start a new chat</DialogTitle>
        <DialogDescription>
          Choose a context and which sources should feed this conversation.
        </DialogDescription>
      </DialogHeader>

      <div class="flex flex-col gap-4">
        <div class="flex flex-col gap-2">
          <Label>Context</Label>
          <Select v-model="contextValue">
            <SelectTrigger>
              <SelectValue placeholder="Context" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem v-for="c in contexts" :key="c" :value="c">
                {{ c }}
              </SelectItem>
            </SelectContent>
          </Select>
        </div>

        <div class="flex flex-col gap-2">
          <Label>Sources</Label>
          <div class="flex max-h-48 flex-col gap-0.5 overflow-y-auto rounded-md border p-1">
            <label
              v-for="s in sources"
              :key="s.id"
              class="flex cursor-pointer items-center gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-accent"
            >
              <Checkbox v-model="s.active" />
              <span class="flex-1 truncate">{{ s.name }}</span>
            </label>
          </div>
        </div>
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
