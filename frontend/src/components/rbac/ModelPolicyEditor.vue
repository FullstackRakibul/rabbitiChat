<script setup>
/**
 * Models by role — the SystemAdmin editor for the model policy.
 *
 * One row per role: which local models it may run, and which of them is used
 * when a request names none. An empty list means no restriction.
 *
 * Every rule about what is valid belongs to the server: this component posts a
 * draft and shows the refusal exactly as it came back. It deliberately keeps no
 * copy of the rules — a second copy here would drift from the one that is
 * actually enforced, and the drift would show as a UI that allows what the
 * server refuses.
 */
import { computed, onMounted, ref } from 'vue'
import { Loader2, Lock } from '@lucide/vue'
import { listModelPolicies, setModelPolicy } from '@/services/index.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Label } from '@/components/ui/label'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import { Separator } from '@/components/ui/separator'
import { cn } from '@/lib/utils'

const loading = ref(true)
const loadError = ref('')
const installed = ref([])
const policies = ref([])

/** role -> { allowed: string[], def: string }, the unsaved edit. */
const draft = ref({})
/** role -> the server's message for that row. */
const rowError = ref({})
/** role currently being saved. */
const saving = ref('')
/** role -> true briefly after a successful save. */
const saved = ref({})

function resetDraft(list) {
  const next = {}
  for (const p of list) next[p.role] = { allowed: [...p.allowedModels], def: p.defaultModel }
  draft.value = next
}

async function load() {
  loading.value = true
  loadError.value = ''
  try {
    const res = await listModelPolicies()
    policies.value = res.policies ?? []
    installed.value = res.installedModels ?? []
    resetDraft(policies.value)
  } catch (e) {
    loadError.value = e.message || 'Could not load the model policy.'
  } finally {
    loading.value = false
  }
}

onMounted(load)

const saveRow = async (role) => {
  const d = draft.value[role]
  if (!d) return

  saving.value = role
  rowError.value = { ...rowError.value, [role]: '' }
  try {
    const updated = await setModelPolicy(role, d.allowed, d.def)

    // Take the server's own view back, so the row shows what was stored rather
    // than what was typed.
    policies.value = policies.value.map((p) => (p.role === role ? updated : p))
    draft.value = {
      ...draft.value,
      [role]: { allowed: [...updated.allowedModels], def: updated.defaultModel },
    }

    saved.value = { ...saved.value, [role]: true }
    setTimeout(() => { saved.value = { ...saved.value, [role]: false } }, 2000)
  } catch (e) {
    // Worded by the server. Shown beside the row, not restated here.
    rowError.value = { ...rowError.value, [role]: e.message || 'Could not save.' }
  } finally {
    saving.value = ''
  }
}

function toggleModel(role, name, next) {
  const d = draft.value[role]
  if (!d) return

  const allowed = next
    ? [...d.allowed.filter((m) => m !== name), name]
    : d.allowed.filter((m) => m !== name)

  // Clearing the model that was the default would leave a default outside the
  // list, which the server refuses; drop it rather than send a known-bad draft.
  const def = allowed.includes(d.def) ? d.def : ''
  draft.value = { ...draft.value, [role]: { allowed, def } }
}

function setDefault(role, name) {
  const d = draft.value[role]
  if (!d) return
  draft.value = { ...draft.value, [role]: { ...d, def: name } }
}

const same = (a, b) =>
  a.length === b.length && [...a].sort().join(',') === [...b].sort().join(',')

/** Save is offered only once something actually differs from the stored row. */
function isDirty(role) {
  const stored = policies.value.find((p) => p.role === role)
  const d = draft.value[role]
  if (!stored || !d) return false
  return !same(stored.allowedModels, d.allowed) || stored.defaultModel !== d.def
}

const rows = computed(() => policies.value.map((p) => ({
  ...p,
  draft: draft.value[p.role] ?? { allowed: [], def: '' },
  dirty: isDirty(p.role),
})))
</script>

<template>
  <section class="space-y-4">
    <header class="space-y-1">
      <h3 class="flex items-center gap-2 text-sm font-semibold">
        <Lock class="h-4 w-4 text-muted-foreground" />
        Models by role
      </h3>
      <p class="text-xs text-muted-foreground">
        Which models each role may run. No models ticked means no restriction.
        A role held to one model sees it as fixed, with no picker.
      </p>
    </header>

    <p v-if="loading" class="flex items-center gap-2 text-sm text-muted-foreground">
      <Loader2 class="h-3.5 w-3.5 animate-spin" />
      Loading…
    </p>

    <p v-else-if="loadError" class="text-sm text-destructive">
      {{ loadError }}
    </p>

    <p v-else-if="!installed.length" class="text-sm text-muted-foreground">
      No local models are installed, so there is nothing to allow yet.
    </p>

    <div v-else class="space-y-4">
      <div
        v-for="row in rows"
        :key="row.role"
        class="rounded-lg border p-3 sm:p-4"
      >
        <div class="flex flex-wrap items-center gap-2">
          <span class="text-sm font-medium">{{ row.role }}</span>

          <Badge v-if="!row.draft.allowed.length" variant="secondary" class="font-normal">
            No restriction
          </Badge>
          <Badge v-else-if="row.draft.allowed.length === 1" variant="outline" class="gap-1 font-normal">
            <Lock class="h-3 w-3" />
            Locked to one model
          </Badge>

          <span v-if="!row.editable" class="text-xs text-muted-foreground">
            Never restricted
          </span>
        </div>

        <template v-if="row.editable">
          <Separator class="my-3" />

          <div class="space-y-3">
            <div class="space-y-2">
              <Label class="text-xs text-muted-foreground">Allowed models</Label>
              <div class="grid gap-2 sm:grid-cols-2">
                <label
                  v-for="m in installed"
                  :key="m"
                  :class="cn(
                    'flex min-w-0 items-center gap-2 rounded-md border px-2 py-1.5 text-sm',
                    row.draft.allowed.includes(m) && 'bg-accent/50',
                  )"
                >
                  <Checkbox
                    :model-value="row.draft.allowed.includes(m)"
                    @update:model-value="(v) => toggleModel(row.role, m, v)"
                  />
                  <span class="min-w-0 flex-1 truncate">{{ m }}</span>
                </label>
              </div>
            </div>

            <div v-if="row.draft.allowed.length" class="space-y-2">
              <Label class="text-xs text-muted-foreground">
                Default — used when a request names no model
              </Label>
              <Select
                :model-value="row.draft.def"
                @update:model-value="(v) => setDefault(row.role, v)"
              >
                <SelectTrigger class="h-9 w-full sm:w-72">
                  <SelectValue placeholder="First allowed model" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem v-for="m in row.draft.allowed" :key="m" :value="m">
                    {{ m }}
                  </SelectItem>
                </SelectContent>
              </Select>
            </div>

            <!-- The server's wording, placed with the fields it refused. -->
            <p v-if="rowError[row.role]" class="text-sm text-destructive">
              {{ rowError[row.role] }}
            </p>

            <div class="flex items-center gap-2">
              <Button
                size="sm"
                :disabled="!row.dirty || saving === row.role"
                @click="saveRow(row.role)"
              >
                <Loader2 v-if="saving === row.role" class="h-3.5 w-3.5 animate-spin" />
                Save
              </Button>
              <span v-if="saved[row.role]" class="text-xs text-muted-foreground">
                Saved — it applies to the next request.
              </span>
            </div>
          </div>
        </template>
      </div>
    </div>
  </section>
</template>
