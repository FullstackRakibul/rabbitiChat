<script setup>
/**
 * Access → Roles: what every role gets, as a matrix of tabs against roles
 * (docs/RBAC_ROLES_PROMPT.md §3.5). Phase P1 edits tabs; sections and actions
 * arrive with the expanded rows.
 *
 * Everything drawn here comes from the server: the rows from the permission
 * catalog (labels, groups, which keys are always on), the columns and their
 * values from the role presets (which role is locked, how many users hold it).
 * This component keeps no list of roles or tabs of its own, and decides no rule:
 * a save the server refuses comes back with the invariant it would break.
 *
 * Unsaved changes are counted in a Save bar with a note field; leaving with
 * changes asks first. On a phone the matrix shows one role at a time.
 */
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useMediaQuery } from '@vueuse/core'
import { Check, Loader2, Lock, Undo2 } from '@lucide/vue'
import { getCatalog, getRolePresets, saveRolePreset } from '@/services/index.js'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'

const emit = defineEmits(['open-models', 'dirty-change'])

const catalog = ref(null)          // { groups, permissions }
const roles = ref([])              // [{ role, label, locked, userCount, values }]
const draft = ref({})              // role → key → bool, the values as edited
const loading = ref(true)
const loadError = ref('')
const saving = ref(false)
const saveError = ref('')
const toast = ref('')
const note = ref('')

const narrow = useMediaQuery('(max-width: 639px)')
const pickedRole = ref('')

/** Tab rows, grouped as the module menu groups them. */
const groups = computed(() => {
  if (!catalog.value) return []
  const tabs = catalog.value.permissions.filter((p) => p.kind === 'tab')
  return catalog.value.groups
    .map((g) => ({ name: g, rows: tabs.filter((p) => p.group === g) }))
    .filter((g) => g.rows.length)
})

const columns = computed(() =>
  narrow.value ? roles.value.filter((r) => r.role === pickedRole.value) : roles.value)

function saved(role, key) {
  return !!roles.value.find((r) => r.role === role)?.values[key]
}

function value(role, key) {
  return draft.value[role]?.[key] ?? saved(role, key)
}

/** Why a cell cannot change, or '' when it can. Both reasons are the server's flags. */
function lockReason(column, perm) {
  if (column.locked) return `${column.label} always has everything`
  if (perm.alwaysOn) return `${perm.en} is on for every role`
  return ''
}

const changes = computed(() => {
  const out = []
  for (const [role, keys] of Object.entries(draft.value))
    for (const [key, v] of Object.entries(keys)) if (v !== saved(role, key)) out.push({ role, key, value: v })
  return out
})

const dirty = computed(() => changes.value.length > 0)

function toggle(column, perm) {
  if (lockReason(column, perm) || saving.value) return
  const next = !value(column.role, perm.key)
  draft.value = { ...draft.value, [column.role]: { ...(draft.value[column.role] ?? {}), [perm.key]: next } }
  toast.value = ''
  saveError.value = ''
  emit('dirty-change', dirty.value)
}

function discard() {
  draft.value = {}
  saveError.value = ''
  emit('dirty-change', false)
}

async function load() {
  loading.value = true
  loadError.value = ''
  try {
    const [cat, presets] = await Promise.all([getCatalog(), getRolePresets()])
    catalog.value = cat
    roles.value = presets.roles
    if (!roles.value.some((r) => r.role === pickedRole.value)) pickedRole.value = roles.value[0]?.role ?? ''
  } catch (e) {
    loadError.value = e.message
  } finally {
    loading.value = false
  }
}

/** One request per role that changed; the server writes only the keys that differ. */
async function save() {
  if (!dirty.value || saving.value) return
  saving.value = true
  saveError.value = ''
  toast.value = ''
  const byRole = {}
  for (const c of changes.value) (byRole[c.role] ??= {})[c.key] = c.value
  let changed = 0
  let affected = 0
  try {
    for (const [role, values] of Object.entries(byRole)) {
      const res = await saveRolePreset(role, values, note.value.trim() || null)
      changed += res.changed.length
      affected += res.affectedUsers
      roles.value = roles.value.map((r) => (r.role === role ? { ...r, values: res.values } : r))
      const { [role]: _, ...rest } = draft.value
      draft.value = rest
    }
    note.value = ''
    toast.value = `Saved ${changed} change${changed === 1 ? '' : 's'} · ${affected} user${affected === 1 ? '' : 's'} affected`
  } catch (e) {
    // An invariant refusal arrives here with the server's wording; nothing of that role was saved.
    saveError.value = e.message
  } finally {
    saving.value = false
    emit('dirty-change', dirty.value)
  }
}

// ---- leaving with unsaved changes ----

const LEAVE = 'You have unsaved changes to role access. Leave without saving?'

function beforeUnload(e) {
  if (!dirty.value) return
  e.preventDefault()
  e.returnValue = ''
}

const router = useRouter()
let removeGuard = null

onMounted(() => {
  load()
  window.addEventListener('beforeunload', beforeUnload)
  removeGuard = router.beforeEach((to, from) => {
    if (!dirty.value || to.path === from.path) return true
    return window.confirm(LEAVE)
  })
})

onBeforeUnmount(() => {
  window.removeEventListener('beforeunload', beforeUnload)
  removeGuard?.()
})

defineExpose({ dirty, confirmLeave: () => !dirty.value || window.confirm(LEAVE), reload: load })
</script>

<template>
  <section class="flex min-w-0 flex-col rounded-lg border bg-card" data-role-matrix-root>
    <div class="flex flex-wrap items-start justify-between gap-2 border-b p-4">
      <div class="min-w-0">
        <h3 class="font-semibold">Roles</h3>
        <p class="mt-0.5 text-sm text-muted-foreground">
          What every user of a role can open. A change reaches them on their next request.
        </p>
      </div>
      <Button variant="ghost" size="sm" class="shrink-0" @click="emit('open-models')">
        Models by role →
      </Button>
    </div>

    <p v-if="loadError" class="border-b bg-destructive/10 p-3 text-sm text-destructive">{{ loadError }}</p>

    <div v-if="loading && !catalog" class="flex items-center gap-2 p-6 text-sm text-muted-foreground">
      <Loader2 class="size-4 animate-spin" /> Loading roles…
    </div>

    <template v-else-if="catalog">
      <!-- Phone: one role at a time. -->
      <div v-if="narrow" class="border-b p-3">
        <label for="role-picker" class="text-xs font-medium text-muted-foreground">Role</label>
        <select
          id="role-picker"
          v-model="pickedRole"
          data-role-picker
          class="mt-1 flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
        >
          <option v-for="r in roles" :key="r.role" :value="r.role">
            {{ r.label }} · {{ r.userCount }} user{{ r.userCount === 1 ? '' : 's' }}
          </option>
        </select>
      </div>

      <div class="min-w-0 overflow-x-auto" data-role-matrix>
        <table class="w-full border-collapse text-sm">
          <thead>
            <tr class="border-b">
              <th scope="col" class="sticky left-0 z-10 bg-card p-2 text-left font-medium text-muted-foreground">Tab</th>
              <th
                v-for="c in columns"
                :key="c.role"
                scope="col"
                class="min-w-20 p-2 text-center align-bottom font-medium"
              >
                <span class="block whitespace-nowrap">{{ c.label }}</span>
                <span class="block text-[11px] font-normal text-muted-foreground">
                  {{ c.userCount }} user{{ c.userCount === 1 ? '' : 's' }}
                </span>
              </th>
            </tr>
          </thead>
          <tbody v-for="g in groups" :key="g.name">
            <tr>
              <th
                :colspan="columns.length + 1"
                scope="rowgroup"
                class="bg-muted/50 px-2 py-1.5 text-left text-xs font-semibold uppercase tracking-wide text-muted-foreground"
              >{{ g.name }}</th>
            </tr>
            <tr v-for="p in g.rows" :key="p.key" class="border-b last:border-b-0">
              <th scope="row" class="sticky left-0 z-10 bg-card p-2 text-left font-normal">
                <span class="block whitespace-nowrap">{{ p.bn }}</span>
                <span class="block text-xs text-muted-foreground">{{ p.en }}</span>
              </th>
              <td v-for="c in columns" :key="c.role" class="p-1.5 text-center">
                <button
                  type="button"
                  role="checkbox"
                  :data-cell="`${c.role}|${p.key}`"
                  :data-state="value(c.role, p.key) ? 'on' : 'off'"
                  :aria-checked="value(c.role, p.key)"
                  :aria-label="`${c.label}: ${p.en}`"
                  :aria-disabled="!!lockReason(c, p) || saving"
                  :title="lockReason(c, p) || undefined"
                  :class="cn(
                    'inline-grid size-7 place-items-center rounded-md border transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring',
                    value(c.role, p.key) ? 'border-primary bg-primary text-primary-foreground' : 'bg-background hover:bg-accent',
                    lockReason(c, p) && 'cursor-not-allowed opacity-60',
                    value(c.role, p.key) !== saved(c.role, p.key) && 'ring-2 ring-ring ring-offset-1 ring-offset-background',
                  )"
                  @click="toggle(c, p)"
                >
                  <Lock v-if="lockReason(c, p)" class="size-3.5" aria-hidden="true" />
                  <Check v-else-if="value(c.role, p.key)" class="size-4" aria-hidden="true" />
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <p class="border-t px-4 py-2 text-xs text-muted-foreground">
        <Lock class="inline size-3 align-[-1px]" aria-hidden="true" /> locked: a role that always has everything, or a tab every role keeps.
      </p>

      <!-- The Save bar: only while there is something to save, or something to report. -->
      <div
        v-if="dirty || toast || saveError"
        class="sticky bottom-0 z-20 flex flex-wrap items-center gap-2 border-t bg-card p-3"
      >
        <template v-if="dirty">
          <span class="text-sm font-medium" data-unsaved-count>
            {{ changes.length }} unsaved change{{ changes.length === 1 ? '' : 's' }}
          </span>
          <Input
            v-model="note"
            data-save-note
            class="h-8 min-w-0 flex-1 basis-40"
            placeholder="Note (optional) — why this changed"
            :disabled="saving"
          />
          <div class="flex gap-2">
            <Button size="sm" variant="ghost" :disabled="saving" @click="discard">
              <Undo2 class="size-4" /> Discard
            </Button>
            <Button size="sm" data-save-roles :disabled="saving" @click="save">
              <Loader2 v-if="saving" class="size-4 animate-spin" />
              {{ saving ? 'Saving…' : 'Save' }}
            </Button>
          </div>
        </template>
        <p v-if="saveError" class="basis-full text-sm text-destructive">{{ saveError }}</p>
        <p v-else-if="toast && !dirty" class="text-sm text-muted-foreground" data-saved-toast role="status">
          <Check class="inline size-4 align-[-3px] text-primary" aria-hidden="true" /> {{ toast }}
        </p>
      </div>
    </template>
  </section>
</template>
