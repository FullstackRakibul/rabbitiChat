<script setup>
/**
 * Model providers — the catalog of one provider, from its GET /models, with the
 * shortlist: Allowed models appear in every picker; Tools marks those Chat may
 * send tools. Nothing is offered until it is ticked, so an expensive model cannot
 * be picked by accident. Prices are shown as the provider sends them.
 */
import { computed, onMounted, ref } from 'vue'
import { Loader2, RefreshCw, Search } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Checkbox } from '@/components/ui/checkbox'
import { getModelProviderCatalog, refreshModelProviderCatalog, saveModelProviderModels } from '@/services/model-providers.service.js'

const props = defineProps({
  provider: { type: Object, required: true },
})
const emit = defineEmits(['saved'])

const catalog = ref(null)
const allowed = ref(new Set())
const tools = ref(new Set())
const query = ref('')
const onlyTicked = ref(false)
const busy = ref('')
const error = ref('')
const done = ref('')

function take(c) {
  catalog.value = c
  allowed.value = new Set(c.allowed)
  tools.value = new Set(c.toolCapable)
}

async function load() {
  error.value = ''
  try { take(await getModelProviderCatalog(props.provider.id)) } catch (e) { error.value = e.message }
}

async function refresh() {
  busy.value = 'refresh'; error.value = ''; done.value = ''
  try { take(await refreshModelProviderCatalog(props.provider.id)) } catch (e) { error.value = e.message } finally { busy.value = '' }
}

async function save() {
  busy.value = 'save'; error.value = ''; done.value = ''
  try {
    take(await saveModelProviderModels(props.provider.id, [...allowed.value], [...tools.value].filter((t) => allowed.value.has(t))))
    done.value = `Saved. ${allowed.value.size} model${allowed.value.size === 1 ? '' : 's'} now in the pickers.`
    emit('saved')
  } catch (e) { error.value = e.message } finally { busy.value = '' }
}

onMounted(load)

function setAllowed(id, on) {
  const next = new Set(allowed.value)
  if (on) next.add(id)
  else {
    next.delete(id)
    const t = new Set(tools.value); t.delete(id); tools.value = t
  }
  allowed.value = next
}

function setTools(id, on) {
  const next = new Set(tools.value)
  if (on) { next.add(id); setAllowed(id, true) } else next.delete(id)
  tools.value = next
}

/** Models with no catalog entry (a server without GET /models) still show, as typed. */
const rows = computed(() => {
  const list = catalog.value?.models || []
  const known = new Set(list.map((m) => m.id))
  const extra = [...allowed.value].filter((id) => !known.has(id)).map((id) => ({ id }))
  const q = query.value.trim().toLowerCase()
  return [...extra, ...list].filter((m) =>
    (!onlyTicked.value || allowed.value.has(m.id))
    && (!q || m.id.toLowerCase().includes(q) || (m.name || '').toLowerCase().includes(q)))
})

const dirty = computed(() => {
  const c = catalog.value
  if (!c) return false
  const same = (a, b) => a.size === b.length && b.every((x) => a.has(x))
  return !same(allowed.value, c.allowed) || !same(tools.value, c.toolCapable)
})

const hasPrices = computed(() => (catalog.value?.models || []).some((m) => m.inputPrice || m.outputPrice))
</script>

<template>
  <div class="flex flex-col gap-3" data-provider-models>
    <div class="flex flex-wrap items-center gap-2">
      <div class="relative min-w-48 flex-1">
        <Search class="pointer-events-none absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" />
        <Input v-model="query" placeholder="Search models" class="h-9 pl-8" />
      </div>
      <label class="flex items-center gap-2 text-xs text-muted-foreground">
        <Checkbox v-model="onlyTicked" /> Ticked only
      </label>
      <Button variant="outline" size="sm" :disabled="!!busy" @click="refresh">
        <Loader2 v-if="busy === 'refresh'" class="h-4 w-4 animate-spin" /><RefreshCw v-else class="h-4 w-4" /> Refresh catalog
      </Button>
    </div>

    <p class="text-xs text-muted-foreground">
      <template v-if="catalog?.fetchedAt">{{ catalog.models.length }} models, fetched {{ new Date(catalog.fetchedAt).toLocaleString() }}.</template>
      <template v-else-if="catalog">No catalog yet — Refresh catalog (or Test) reads it from the provider.</template>
      {{ allowed.size }} allowed.
    </p>

    <div class="max-h-88 overflow-y-auto rounded-md border">
      <table class="w-full text-sm">
        <thead class="sticky top-0 bg-muted/80 text-xs text-muted-foreground backdrop-blur">
          <tr>
            <th class="px-2 py-1.5 text-left font-medium">Model</th>
            <th v-if="hasPrices" class="px-2 py-1.5 text-right font-medium">Input</th>
            <th v-if="hasPrices" class="px-2 py-1.5 text-right font-medium">Output</th>
            <th class="w-16 px-2 py-1.5 text-center font-medium">Allowed</th>
            <th class="w-14 px-2 py-1.5 text-center font-medium">Tools</th>
          </tr>
        </thead>
        <tbody class="divide-y">
          <tr v-for="m in rows" :key="m.id" :class="allowed.has(m.id) ? 'bg-primary/5' : ''">
            <td class="px-2 py-1.5">
              <div class="font-mono text-xs">{{ m.id }}</div>
              <div v-if="m.name && m.name !== m.id" class="text-xs text-muted-foreground">{{ m.name }}<span v-if="m.kind"> · {{ m.kind }}</span></div>
            </td>
            <td v-if="hasPrices" class="px-2 py-1.5 text-right font-mono text-xs tabular-nums">{{ m.inputPrice ?? '' }}</td>
            <td v-if="hasPrices" class="px-2 py-1.5 text-right font-mono text-xs tabular-nums">{{ m.outputPrice ?? '' }}</td>
            <td class="px-2 py-1.5 text-center">
              <Checkbox :model-value="allowed.has(m.id)" :aria-label="`Allow ${m.id}`" @update:model-value="(v) => setAllowed(m.id, v)" />
            </td>
            <td class="px-2 py-1.5 text-center">
              <Checkbox :model-value="tools.has(m.id)" :aria-label="`Tools for ${m.id}`" @update:model-value="(v) => setTools(m.id, v)" />
            </td>
          </tr>
          <tr v-if="catalog && !rows.length">
            <td colspan="5" class="px-2 py-6 text-center text-xs text-muted-foreground">Nothing matches.</td>
          </tr>
        </tbody>
      </table>
    </div>

    <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive">{{ error }}</p>
    <p v-else-if="done" class="text-sm text-muted-foreground">{{ done }}</p>

    <div class="flex justify-end">
      <Button size="sm" :disabled="!!busy || !dirty" @click="save">
        <Loader2 v-if="busy === 'save'" class="h-4 w-4 animate-spin" /> Save shortlist
      </Button>
    </div>
  </div>
</template>
