<script setup>
import { computed } from 'vue'
import { Trash2 } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Checkbox } from '@/components/ui/checkbox'
import {
  Card, CardContent, CardDescription, CardHeader, CardTitle,
} from '@/components/ui/card'

const props = defineProps({
  tasks: { type: Array, default: () => [] },
  filteredTasks: { type: Array, default: () => [] },
  newTask: { type: String, default: '' },
  filter: { type: String, default: 'all' },
  activeCount: { type: Number, default: 0 },
  doneCount: { type: Number, default: 0 },
})

const emit = defineEmits(['update:newTask', 'update:filter', 'add', 'toggle', 'delete'])

const newTaskValue = computed({
  get: () => props.newTask,
  set: (v) => emit('update:newTask', v),
})

const filters = ['all', 'active', 'completed']
</script>

<template>
  <div class="h-full overflow-y-auto p-4 sm:p-6">
    <Card class="mx-auto w-full max-w-2xl">
      <CardHeader>
        <CardTitle>To-Do List</CardTitle>
        <CardDescription>
          Quick tasks for this session — stored locally, not synced.
        </CardDescription>
      </CardHeader>

      <CardContent class="flex flex-col gap-4">
        <form class="flex gap-2" @submit.prevent="emit('add')">
          <Input v-model="newTaskValue" placeholder="Add a task…" class="flex-1" />
          <Button type="submit">
            Add
          </Button>
        </form>

        <div class="inline-flex w-fit items-center gap-1 rounded-lg bg-muted p-1">
          <button
            v-for="f in filters"
            :key="f"
            :class="cn(
              'rounded-md px-3 py-1 text-sm font-medium capitalize transition-colors',
              filter === f
                ? 'bg-background text-foreground shadow'
                : 'text-muted-foreground hover:text-foreground',
            )"
            @click="emit('update:filter', f)"
          >
            {{ f }}
          </button>
        </div>

        <ul v-if="filteredTasks.length" class="flex flex-col gap-1.5">
          <li
            v-for="t in filteredTasks"
            :key="t.id"
            class="group flex items-center gap-3 rounded-lg border p-3"
          >
            <Checkbox :model-value="t.done" @update:model-value="emit('toggle', t.id)" />
            <span
              :class="cn(
                'flex-1 text-sm',
                t.done && 'text-muted-foreground line-through',
              )"
            >
              {{ t.text }}
            </span>
            <Button
              variant="ghost"
              size="icon"
              class="h-7 w-7 text-muted-foreground opacity-0 transition hover:text-destructive focus:opacity-100 group-hover:opacity-100"
              title="Delete"
              @click="emit('delete', t.id)"
            >
              <Trash2 class="h-3.5 w-3.5" />
            </Button>
          </li>
        </ul>

        <p v-else class="py-6 text-center text-sm text-muted-foreground">
          {{ tasks.length ? 'Nothing here for this filter.' : 'No tasks yet — add one above.' }}
        </p>

        <p v-if="tasks.length" class="text-xs text-muted-foreground">
          {{ activeCount }} active · {{ doneCount }} completed
        </p>
      </CardContent>
    </Card>
  </div>
</template>
