<script setup>
/**
 * Command palette (Cmd/Ctrl+K).
 *
 * Built on the existing Reka dialog, which supplies the focus trap, the
 * scroll lock and Escape-to-close. Arrow keys move the highlight, Enter runs
 * the highlighted command, and typing filters the list.
 */
import { computed, nextTick, ref, watch } from 'vue'
import { Search } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Dialog, DialogContent, DialogTitle, DialogDescription } from '@/components/ui/dialog'
import { Kbd } from '@/components/ui/kbd'

const props = defineProps({
  open: { type: Boolean, default: false },
  /** [{ id, label, hint, group, keys?, icon? }] */
  commands: { type: Array, default: () => [] },
})

const emit = defineEmits(['update:open', 'run'])

const query = ref('')
const activeIndex = ref(0)
const inputEl = ref(null)
const listEl = ref(null)

const isOpen = computed({
  get: () => props.open,
  set: (v) => emit('update:open', v),
})

const filtered = computed(() => {
  const q = query.value.trim().toLowerCase()
  if (!q) return props.commands
  return props.commands.filter(c =>
    c.label.toLowerCase().includes(q)
    || (c.group || '').toLowerCase().includes(q)
    || (c.hint || '').toLowerCase().includes(q))
})

// Group headings, in first-seen order.
const groups = computed(() => {
  const out = []
  for (const cmd of filtered.value) {
    const name = cmd.group || 'Actions'
    let g = out.find(x => x.name === name)
    if (!g) { g = { name, items: [] }; out.push(g) }
    g.items.push(cmd)
  }
  return out
})

/** Flat order, so arrow keys cross group boundaries naturally. */
const flat = computed(() => groups.value.flatMap(g => g.items))

watch(() => props.open, (open) => {
  if (!open) return
  query.value = ''
  activeIndex.value = 0
  nextTick(() => inputEl.value?.focus())
})

// A narrowed list can leave the highlight past the end.
watch(filtered, () => { activeIndex.value = 0 })

function move(delta) {
  const n = flat.value.length
  if (!n) return
  activeIndex.value = (activeIndex.value + delta + n) % n
  nextTick(() => {
    listEl.value?.querySelector('[data-active="true"]')
      ?.scrollIntoView({ block: 'nearest' })
  })
}

function run(cmd) {
  if (!cmd) return
  emit('update:open', false)
  emit('run', cmd)
}
</script>

<template>
  <Dialog v-model:open="isOpen">
    <DialogContent
      class="max-w-xl gap-0 overflow-hidden p-0"
      :show-close="false"
    >
      <DialogTitle class="sr-only">
        Command palette
      </DialogTitle>
      <DialogDescription class="sr-only">
        Search for a tab or an action. Use the arrow keys to browse and Enter to run.
      </DialogDescription>

      <!-- Search field. DialogContent renders a fragment, so the key handlers
           live here — on a real element that can receive them. -->
      <div
        class="flex items-center gap-2 border-b px-3"
        @keydown.down.prevent="move(1)"
        @keydown.up.prevent="move(-1)"
        @keydown.enter.prevent="run(flat[activeIndex])"
      >
        <Search class="h-4 w-4 shrink-0 text-muted-foreground" />
        <input
          ref="inputEl"
          v-model="query"
          class="h-12 w-full bg-transparent text-sm outline-none placeholder:text-muted-foreground"
          placeholder="Search tabs and actions…"
          aria-label="Search commands"
        >
        <Kbd keys="Esc" size="sm" class="shrink-0" />
      </div>

      <!-- Results -->
      <div ref="listEl" class="max-h-80 overflow-y-auto p-2">
        <template v-for="g in groups" :key="g.name">
          <p class="px-2 py-1.5 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
            {{ g.name }}
          </p>

          <button
            v-for="cmd in g.items"
            :key="cmd.id"
            :data-active="flat[activeIndex]?.id === cmd.id"
            :class="cn(
              'flex w-full items-center gap-2.5 rounded-md px-2 py-2 text-left text-sm transition-colors',
              flat[activeIndex]?.id === cmd.id
                ? 'bg-accent text-accent-foreground'
                : 'hover:bg-accent/50',
            )"
            @click="run(cmd)"
            @mousemove="activeIndex = flat.findIndex(c => c.id === cmd.id)"
          >
            <component :is="cmd.icon" v-if="cmd.icon" class="h-4 w-4 shrink-0 text-muted-foreground" />
            <span class="flex-1 truncate">{{ cmd.label }}</span>
            <Kbd v-if="cmd.keys" :keys="cmd.keys" size="sm" class="shrink-0" />
          </button>
        </template>

        <p v-if="!flat.length" class="px-2 py-6 text-center text-sm text-muted-foreground">
          No matching commands.
        </p>
      </div>

      <!-- Footer legend -->
      <div class="flex items-center gap-3 border-t px-3 py-2 text-[11px] text-muted-foreground">
        <span class="flex items-center gap-1"><Kbd keys="up" size="sm" /><Kbd keys="down" size="sm" /> browse</span>
        <span class="flex items-center gap-1"><Kbd keys="enter" size="sm" /> run</span>
        <span class="ml-auto flex items-center gap-1"><Kbd keys="g then c" size="sm" /> jump</span>
      </div>
    </DialogContent>
  </Dialog>
</template>
