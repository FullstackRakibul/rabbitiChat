<!--
  Full CRUD over context sources, for whichever `kind` it is given.

  The Sources tab mounts it with kind="source" and the Workspace tab with
  kind="buyer". They are the same editor over the same endpoints because they
  are the same mechanism — a buyer profile is a connected source that happens to
  describe a buyer.
-->
<script setup>
import { computed, onMounted, ref, watch } from 'vue'
import draggable from 'vuedraggable'
import { GripVertical, Pencil, Plus, Search, Trash2, X } from '@lucide/vue'
import {
  listContextSources, createContextSource, updateContextSource,
  deleteContextSource, reorderContextSources,
} from '@/services/index.js'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Switch } from '@/components/ui/switch'
import { Textarea } from '@/components/ui/textarea'

const props = defineProps({
  /** "source" | "buyer" — decides which rows this instance owns. */
  kind: { type: String, default: 'source' },
  title: { type: String, default: 'Connected Sources' },
  description: {
    type: String,
    default: 'Active sources are prepended to the system prompt of every chat that has them switched on.',
  },
  addLabel: { type: String, default: 'Add source' },
  contentPlaceholder: {
    type: String,
    default: 'Paste the text you want the model to have in mind — guidelines, product facts, tone rules…',
  },
})

/** Emitted whenever the server list changes, so the parent can refresh its copy. */
const emit = defineEmits(['changed'])

const items = ref([])
const loading = ref(true)
const error = ref('')
const query = ref('')

// The editor is one form reused for add and edit; editingId decides which.
const editingId = ref(null)   // null = closed, 0 = adding, >0 = editing that row
const draftName = ref('')
const draftContent = ref('')
const saving = ref(false)

const filtered = computed(() => {
  const q = query.value.trim().toLowerCase()
  if (!q) return items.value
  return items.value.filter((s) => s.name.toLowerCase().includes(q))
})

const activeCount = computed(() => items.value.filter((s) => s.isActive).length)

// Dragging reorders the real list, so it is disabled while a search filters it —
// otherwise the dropped position would refer to a list the user cannot see.
const canReorder = computed(() => !query.value.trim())

const draggableItems = computed({
  get: () => items.value,
  set: (value) => {
    items.value = value
    persistOrder()
  },
})

async function refresh() {
  loading.value = true
  error.value = ''
  try {
    items.value = await listContextSources(props.kind)
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

function openAdd() {
  editingId.value = 0
  draftName.value = ''
  draftContent.value = ''
}

function openEdit(item) {
  editingId.value = item.id
  draftName.value = item.name
  draftContent.value = item.content
}

function closeEditor() {
  editingId.value = null
  draftName.value = ''
  draftContent.value = ''
}

async function save() {
  const name = draftName.value.trim()
  if (!name || saving.value) return

  saving.value = true
  error.value = ''
  try {
    if (editingId.value === 0) {
      await createContextSource({ name, kind: props.kind, content: draftContent.value })
    } else {
      await updateContextSource(editingId.value, { name, content: draftContent.value })
    }
    closeEditor()
    await refresh()
    emit('changed', items.value)
  } catch (e) {
    error.value = e.message
  } finally {
    saving.value = false
  }
}

async function toggle(item, isActive) {
  const previous = item.isActive
  item.isActive = isActive          // optimistic: the switch must not lag the click
  try {
    await updateContextSource(item.id, { isActive })
    emit('changed', items.value)
  } catch (e) {
    item.isActive = previous        // the server refused; put the switch back
    error.value = e.message
  }
}

async function remove(item) {
  if (!confirm(`Delete “${item.name}”? This cannot be undone.`)) return
  error.value = ''
  try {
    await deleteContextSource(item.id)
    if (editingId.value === item.id) closeEditor()
    await refresh()
    emit('changed', items.value)
  } catch (e) {
    error.value = e.message
  }
}

async function persistOrder() {
  try {
    await reorderContextSources(items.value.map((s) => s.id))
    emit('changed', items.value)
  } catch (e) {
    error.value = e.message
    await refresh()   // the order on screen is now a lie; take the server's
  }
}

watch(() => props.kind, refresh)
onMounted(refresh)

defineExpose({ refresh })
</script>

<template>
  <div class="mx-auto flex w-full max-w-2xl flex-col gap-4">
    <div class="flex items-start justify-between gap-3">
      <div class="min-w-0">
        <h2 class="text-base font-semibold">{{ title }}</h2>
        <p class="mt-0.5 text-xs leading-relaxed text-muted-foreground">{{ description }}</p>
      </div>
      <Button size="sm" class="h-8 shrink-0" @click="openAdd">
        <Plus class="h-4 w-4" />
        {{ addLabel }}
      </Button>
    </div>

    <p v-if="error" class="rounded-lg border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive">
      {{ error }}
    </p>

    <!-- Add / edit form -->
    <div v-if="editingId !== null" class="flex flex-col gap-2 rounded-xl border bg-card p-3">
      <div class="flex items-center justify-between gap-2">
        <p class="text-sm font-medium">
          {{ editingId === 0 ? addLabel : 'Edit' }}
        </p>
        <Button size="icon" variant="ghost" class="h-7 w-7" title="Cancel" @click="closeEditor">
          <X class="h-4 w-4" />
        </Button>
      </div>

      <Input v-model="draftName" placeholder="Name" class="h-9" @keydown.enter="save" />
      <Textarea v-model="draftContent" :placeholder="contentPlaceholder" class="min-h-32 text-sm" />

      <div class="flex items-center justify-end gap-2">
        <Button variant="ghost" size="sm" class="h-8" @click="closeEditor">Cancel</Button>
        <Button size="sm" class="h-8" :disabled="!draftName.trim() || saving" @click="save">
          {{ saving ? 'Saving…' : 'Save' }}
        </Button>
      </div>
    </div>

    <div v-if="items.length" class="relative">
      <Search class="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
      <Input v-model="query" placeholder="Search…" class="pl-9" />
    </div>

    <p v-if="loading" class="py-6 text-center text-sm text-muted-foreground">Loading…</p>

    <template v-else>
      <!-- Reorderable list. The handle keeps the switch and buttons clickable. -->
      <draggable
        v-if="filtered.length"
        v-model="draggableItems"
        item-key="id"
        handle=".cs-handle"
        :disabled="!canReorder"
        class="flex flex-col gap-1.5"
      >
        <template #item="{ element: s }">
          <div class="flex items-center gap-2 rounded-lg border bg-card p-3">
            <GripVertical
              :class="cn(
                'h-4 w-4 shrink-0 text-muted-foreground',
                canReorder ? 'cs-handle cursor-grab active:cursor-grabbing' : 'opacity-30',
              )"
            />

            <div class="min-w-0 flex-1">
              <p class="truncate text-sm font-medium">{{ s.name }}</p>
              <p v-if="s.content" class="truncate text-xs text-muted-foreground">{{ s.content }}</p>
              <p v-else class="text-xs italic text-muted-foreground">No content yet</p>
            </div>

            <Button size="icon" variant="ghost" class="h-7 w-7 shrink-0" title="Edit" @click="openEdit(s)">
              <Pencil class="h-3.5 w-3.5" />
            </Button>
            <Button size="icon" variant="ghost" class="h-7 w-7 shrink-0" title="Delete" @click="remove(s)">
              <Trash2 class="h-3.5 w-3.5" />
            </Button>

            <Switch
              :model-value="s.isActive"
              :title="s.isActive ? 'Active' : 'Off'"
              @update:model-value="toggle(s, $event)"
            />
          </div>
        </template>
      </draggable>

      <p v-else-if="items.length" class="py-6 text-center text-sm text-muted-foreground">
        Nothing matches “{{ query }}”.
      </p>

      <p v-else class="py-6 text-center text-sm text-muted-foreground">
        No {{ kind === 'buyer' ? 'profiles' : 'sources' }} yet — add one to give the model standing context.
      </p>

      <p v-if="items.length" class="text-xs text-muted-foreground">
        {{ activeCount }} of {{ items.length }} active
        <span v-if="!canReorder"> · clear the search to reorder</span>
      </p>
    </template>
  </div>
</template>
