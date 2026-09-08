<script setup>
import { computed, ref } from 'vue'
import { Layers, Search } from '@lucide/vue'
import { PopoverContent, PopoverPortal, PopoverRoot, PopoverTrigger } from 'reka-ui'
import { cn } from '@/lib/utils'
import { Input } from '@/components/ui/input'
import { Switch } from '@/components/ui/switch'

/**
 * The compact source picker that lives in the composer. It only toggles which
 * of the user's existing sources apply to THIS conversation — creating and
 * editing them is the Sources tab's job, so there is one editor, not two.
 */
const props = defineProps({
  /** Every source of kind "source" the user owns. */
  sources: { type: Array, default: () => [] },
  /** Ids active for this conversation. */
  activeIds: { type: Array, default: () => [] },
  disabled: { type: Boolean, default: false },
})

const emit = defineEmits(['update:activeIds', 'manage'])

const query = ref('')

const filtered = computed(() => {
  const q = query.value.trim().toLowerCase()
  if (!q) return props.sources
  return props.sources.filter((s) => s.name.toLowerCase().includes(q))
})

const activeCount = computed(() =>
  props.sources.filter((s) => props.activeIds.includes(s.id)).length)

function isActive(id) {
  return props.activeIds.includes(id)
}

function toggle(id, on) {
  const next = on
    ? [...new Set([...props.activeIds, id])]
    : props.activeIds.filter((x) => x !== id)
  emit('update:activeIds', next)
}
</script>

<template>
  <PopoverRoot>
    <PopoverTrigger as-child>
      <button
        type="button"
        :disabled="disabled"
        :title="`${activeCount} of ${sources.length} sources active`"
        :class="cn(
          'inline-flex h-8 shrink-0 items-center gap-1.5 rounded-full border px-3 text-xs font-medium transition-colors',
          'hover:bg-accent hover:text-accent-foreground disabled:cursor-not-allowed disabled:opacity-50',
          activeCount > 0 && 'border-primary/30 bg-primary/10 text-primary hover:bg-primary/15 hover:text-primary',
        )"
      >
        <Layers class="h-3.5 w-3.5" />
        Sources
        <span v-if="activeCount" class="tabular-nums">{{ activeCount }}</span>
      </button>
    </PopoverTrigger>

    <PopoverPortal>
      <PopoverContent
        side="top"
        align="start"
        :side-offset="8"
        class="z-50 w-80 rounded-xl border bg-popover p-3 text-popover-foreground shadow-lg outline-none"
      >
        <div class="mb-2 flex items-baseline justify-between gap-2">
          <p class="text-sm font-semibold">Connected sources</p>
          <span class="text-xs tabular-nums text-muted-foreground">
            {{ activeCount }} of {{ sources.length }} active
          </span>
        </div>

        <p class="mb-3 text-xs leading-relaxed text-muted-foreground">
          Active sources are prepended to this conversation's system prompt, in order.
        </p>

        <div v-if="sources.length" class="relative mb-2">
          <Search class="pointer-events-none absolute left-2.5 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
          <Input v-model="query" placeholder="Search sources…" class="h-8 pl-8 text-xs" />
        </div>

        <div v-if="filtered.length" class="flex max-h-64 flex-col gap-1 overflow-y-auto">
          <label
            v-for="s in filtered"
            :key="s.id"
            class="flex cursor-pointer items-center gap-2.5 rounded-lg px-2 py-1.5 hover:bg-accent"
          >
            <span class="min-w-0 flex-1 truncate text-sm">{{ s.name }}</span>
            <Switch
              :model-value="isActive(s.id)"
              @update:model-value="toggle(s.id, $event)"
            />
          </label>
        </div>

        <p v-else-if="sources.length" class="py-4 text-center text-xs text-muted-foreground">
          No sources match “{{ query }}”.
        </p>

        <p v-else class="py-4 text-center text-xs text-muted-foreground">
          No sources yet.
        </p>

        <button
          type="button"
          class="mt-2 w-full rounded-lg border px-2 py-1.5 text-xs font-medium hover:bg-accent"
          @click="emit('manage')"
        >
          Manage sources
        </button>
      </PopoverContent>
    </PopoverPortal>
  </PopoverRoot>
</template>
