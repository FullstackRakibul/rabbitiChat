<script setup>
import { computed } from 'vue'
import { Search } from '@lucide/vue'
import { Input } from '@/components/ui/input'
import { Switch } from '@/components/ui/switch'
import {
  Card, CardContent, CardDescription, CardHeader, CardTitle,
} from '@/components/ui/card'

const props = defineProps({
  sources: { type: Array, default: () => [] },
  filteredSources: { type: Array, default: () => [] },
  search: { type: String, default: '' },
  activeCount: { type: Number, default: 0 },
})

const emit = defineEmits(['update:search'])

const searchValue = computed({
  get: () => props.search,
  set: (v) => emit('update:search', v),
})
</script>

<template>
  <div class="h-full overflow-y-auto p-4 sm:p-6">
    <Card class="mx-auto w-full max-w-2xl">
      <CardHeader>
        <CardTitle>Connected Sources</CardTitle>
        <CardDescription>
          Toggle which sources feed your chats. Local state only for now.
        </CardDescription>
      </CardHeader>

      <CardContent class="flex flex-col gap-4">
        <div class="relative">
          <Search class="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
          <Input v-model="searchValue" placeholder="Search sources…" class="pl-9" />
        </div>

        <div v-if="filteredSources.length" class="flex flex-col gap-1.5">
          <label
            v-for="s in filteredSources"
            :key="s.id"
            class="flex cursor-pointer items-center gap-3 rounded-lg border p-3"
          >
            <span class="flex-1 text-sm font-medium">{{ s.name }}</span>
            <span class="text-xs text-muted-foreground">{{ s.active ? 'Active' : 'Off' }}</span>
            <Switch v-model="s.active" />
          </label>
        </div>

        <p v-else class="py-6 text-center text-sm text-muted-foreground">
          No sources match your search.
        </p>

        <p class="text-xs text-muted-foreground">
          {{ activeCount }} of {{ sources.length }} sources active
        </p>
      </CardContent>
    </Card>
  </div>
</template>
