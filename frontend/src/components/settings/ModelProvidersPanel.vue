<script setup>
/**
 * Settings → Model providers (SystemAdmin). OpenAI-compatible providers kept in the
 * database: add one with its key, Test it, tick the models to offer. A saved provider
 * reaches Chat's and Jiji's pickers on the next request, with no restart. The server
 * refuses everyone else; this view is only hidden from them.
 */
import { onMounted, ref } from 'vue'
import { CheckCircle2, ChevronDown, ChevronRight, Loader2, Pencil, Plus, Trash2, XCircle, Zap } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import { Switch } from '@/components/ui/switch'
import {
  Card, CardContent, CardDescription, CardHeader, CardTitle,
} from '@/components/ui/card'
import ModelProviderDialog from './ModelProviderDialog.vue'
import ModelPickerList from './ModelPickerList.vue'
import {
  PROVIDER_STATUS, deleteModelProvider, formatTokens, listModelProviders, testModelProvider, updateModelProvider,
} from '@/services/model-providers.service.js'

const providers = ref(null)
const error = ref('')
const dialogOpen = ref(false)
const editing = ref(null)
const expanded = ref(null)
const busy = ref({})
const tests = ref({})
const rowError = ref({})

async function load() {
  error.value = ''
  try { providers.value = await listModelProviders() } catch (e) { error.value = e.message || 'Could not load the providers.' }
}

onMounted(load)

function replace(p) {
  const i = providers.value.findIndex((x) => x.id === p.id)
  if (i >= 0) providers.value.splice(i, 1, p); else providers.value.push(p)
}

function openAdd() { editing.value = null; dialogOpen.value = true }
function openEdit(p) { editing.value = p; dialogOpen.value = true }

function onSaved(p) {
  replace(p)
  // A new provider goes straight to its shortlist: nothing is offered until a model is ticked.
  if (!editing.value) expanded.value = p.id
}

async function run(p, what, fn) {
  busy.value = { ...busy.value, [p.id]: what }
  rowError.value = { ...rowError.value, [p.id]: '' }
  try { await fn() } catch (e) { rowError.value = { ...rowError.value, [p.id]: e.message } } finally {
    busy.value = { ...busy.value, [p.id]: '' }
  }
}

const test = (p) => run(p, 'test', async () => {
  const r = await testModelProvider(p.id)
  tests.value = { ...tests.value, [p.id]: r }
  replace(r.provider)
})

const toggle = (p, enabled) => run(p, 'toggle', async () => replace(await updateModelProvider(p.id, { enabled })))

const remove = (p) => run(p, 'delete', async () => {
  if (!window.confirm(`Remove ${p.name}? Its key is deleted; the usage history stays.`)) return
  await deleteModelProvider(p.id)
  providers.value = providers.value.filter((x) => x.id !== p.id)
})

function status(p) { return PROVIDER_STATUS[p.status] || { label: p.status, tone: 'bad' } }

const DOT = { ok: 'bg-emerald-500', warn: 'bg-amber-500', bad: 'bg-destructive', idle: 'bg-muted-foreground/40' }

function usage(p) {
  const used = formatTokens(p.usage.totalTokens)
  return p.monthlyTokenBudget ? `${used} of ${formatTokens(p.monthlyTokenBudget)}` : used
}

function budgetShare(p) {
  return p.monthlyTokenBudget ? Math.min(100, (p.usage.totalTokens / p.monthlyTokenBudget) * 100) : 0
}
</script>

<template>
  <div class="flex-1 overflow-y-auto p-4 sm:p-6">
    <Card class="mx-auto w-full max-w-3xl" data-model-providers>
      <CardHeader class="flex flex-row items-start justify-between gap-3 space-y-0">
        <div class="flex flex-col gap-1.5">
          <CardTitle>Model providers</CardTitle>
          <CardDescription>
            OpenAI-compatible APIs added here appear in Chat's and Jiji's model pickers, grouped by name and marked metered.
            Keys are stored encrypted and never shown again.
          </CardDescription>
        </div>
        <Button size="sm" @click="openAdd"><Plus class="h-4 w-4" /> Add provider</Button>
      </CardHeader>

      <CardContent class="flex flex-col gap-3">
        <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive">{{ error }}</p>
        <p v-else-if="!providers" class="text-sm text-muted-foreground">Loading…</p>
        <p v-else-if="!providers.length" class="rounded-lg border border-dashed p-6 text-center text-sm text-muted-foreground">
          No providers yet. Add one with its base URL and key, then Test it.
        </p>

        <div v-for="p in providers || []" :key="p.id" class="rounded-lg border" :data-provider="p.key">
          <div class="flex flex-wrap items-center gap-3 p-3">
            <button class="flex min-w-0 flex-1 items-center gap-2 text-left" :aria-expanded="expanded === p.id"
              @click="expanded = expanded === p.id ? null : p.id">
              <component :is="expanded === p.id ? ChevronDown : ChevronRight" class="h-4 w-4 shrink-0 text-muted-foreground" />
              <span :class="['h-2 w-2 shrink-0 rounded-full', DOT[status(p).tone]]" :title="status(p).label" />
              <span class="min-w-0">
                <span class="flex items-center gap-2">
                  <span class="truncate text-sm font-medium">{{ p.name }}</span>
                  <span class="font-mono text-xs text-muted-foreground">{{ p.key }}</span>
                </span>
                <span class="block truncate font-mono text-xs text-muted-foreground">{{ p.baseUrl }}</span>
              </span>
            </button>

            <div class="flex flex-col items-end gap-0.5 text-xs">
              <span :class="status(p).tone === 'bad' ? 'text-destructive' : status(p).tone === 'ok' ? 'text-emerald-600 dark:text-emerald-400' : 'text-muted-foreground'">
                {{ status(p).label }}
              </span>
              <span class="text-muted-foreground">
                {{ p.hasKey ? (p.apiKeyHint ? `key …${p.apiKeyHint}` : 'key set') : 'no key' }} · {{ p.allowedModels.length }} allowed
              </span>
            </div>

            <div class="flex w-32 flex-col gap-1 text-xs" :title="`${p.usage.calls} calls this month`">
              <span class="text-muted-foreground">This month: <span class="tabular-nums text-foreground">{{ usage(p) }}</span></span>
              <div v-if="p.monthlyTokenBudget" class="h-1.5 overflow-hidden rounded-full bg-muted">
                <div class="h-full rounded-full" :class="budgetShare(p) > 85 ? 'bg-destructive' : 'bg-primary'" :style="{ width: `${budgetShare(p)}%` }" />
              </div>
            </div>

            <label class="flex items-center gap-2 text-xs text-muted-foreground">
              <Switch :model-value="p.enabled" :disabled="!!busy[p.id]" @update:model-value="(v) => toggle(p, v)" /> Enabled
            </label>
          </div>

          <div class="flex flex-wrap items-center gap-2 border-t px-3 py-2">
            <Button variant="outline" size="sm" :disabled="!!busy[p.id]" @click="test(p)">
              <Loader2 v-if="busy[p.id] === 'test'" class="h-4 w-4 animate-spin" /><Zap v-else class="h-4 w-4" /> Test
            </Button>
            <Button variant="ghost" size="sm" :disabled="!!busy[p.id]" @click="openEdit(p)"><Pencil class="h-4 w-4" /> Edit</Button>
            <Button variant="ghost" size="sm" :disabled="!!busy[p.id] || p.usedByJiji" :title="p.usedByJiji ? 'Jiji:ModelSource points at this provider' : ''"
              @click="remove(p)">
              <Trash2 class="h-4 w-4" /> Remove
            </Button>
            <span v-if="p.usedByJiji" class="text-xs text-muted-foreground">Used by Jiji</span>
            <span v-if="p.lastTestAt" class="ml-auto text-xs text-muted-foreground">Last checked {{ new Date(p.lastTestAt).toLocaleString() }}</span>
          </div>

          <p v-if="p.statusMessage && status(p).tone !== 'ok'" class="mx-3 mb-2 rounded-md border border-destructive/30 bg-destructive/5 px-3 py-2 text-xs text-destructive">
            {{ p.statusMessage }}
          </p>
          <p v-if="rowError[p.id]" class="mx-3 mb-2 rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive">{{ rowError[p.id] }}</p>

          <ul v-if="tests[p.id]" class="mx-3 mb-3 divide-y rounded-md border text-xs" data-provider-test>
            <li v-for="(s, i) in tests[p.id].steps" :key="i" class="flex items-start gap-2 px-3 py-1.5">
              <CheckCircle2 v-if="s.ok" class="mt-0.5 h-3.5 w-3.5 shrink-0 text-emerald-600" />
              <XCircle v-else class="mt-0.5 h-3.5 w-3.5 shrink-0 text-destructive" />
              <span class="font-medium">{{ s.step }}</span>
              <span class="text-muted-foreground">{{ s.detail }}</span>
            </li>
          </ul>

          <div v-if="expanded === p.id" class="border-t p-3">
            <ModelPickerList :provider="p" @saved="load" />
          </div>
        </div>
      </CardContent>
    </Card>

    <ModelProviderDialog v-model:open="dialogOpen" :provider="editing" @saved="onSaved" />
  </div>
</template>
