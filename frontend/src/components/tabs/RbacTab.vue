<script setup>
/**
 * Access control — the SystemAdmin tab.
 *
 * Left: search users by username, paged. Right: the selected user's role and
 * tab checklist.
 *
 * The backend is the authority on every rule here; this component mirrors the
 * rules so the UI can explain a refusal before it happens (own row, last admin),
 * but it never decides one. Anything it lets through is still checked server-side,
 * and any refusal that arrives anyway is surfaced as the server worded it.
 */
import { computed, onMounted, ref, watch } from 'vue'
import { Loader2, Search, ShieldCheck, UserCog } from '@lucide/vue'
import { searchUsers, getUser, setRole, setTabs, listTabs } from '@/services/index.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Separator } from '@/components/ui/separator'
import { cn } from '@/lib/utils'

const props = defineProps({
  /** The signed-in admin's own id — used to lock their own row. */
  currentUserId: { type: Number, default: 0 },
})

const emit = defineEmits(['changed'])

const PAGE_SIZE = 20

// ---- List state ----
const search = ref('')
const page = ref(1)
const users = ref([])
const total = ref(0)
const listBusy = ref(false)
const listError = ref('')

// ---- Detail state ----
const tabCatalog = ref([])
const selected = ref(null)      // UserDetailDto
const detailBusy = ref(false)
const saving = ref(false)
const detailError = ref('')
const toast = ref('')           // transient success line

const draftRole = ref('General')
const draftGrants = ref([])     // explicit grants only — baseline is not stored here

const ROLES = ['General', 'System', 'SystemAdmin']

const pageCount = computed(() => Math.max(1, Math.ceil(total.value / PAGE_SIZE)))

/** The admin cannot edit their own row — the API refuses it, so the UI says why. */
const isSelf = computed(() => !!selected.value && selected.value.id === props.currentUserId)

/**
 * Whether demoting this user would strip the last admin. The count here covers
 * only the current page, so it is a hint, not the rule: the server does the real
 * check and answers 409. Shown to explain the button state, never to hide it.
 */
const adminsOnPage = computed(() =>
  users.value.filter((u) => u.role === 'SystemAdmin').length)

const wouldDropLastAdmin = computed(() =>
  !!selected.value
  && selected.value.role === 'SystemAdmin'
  && draftRole.value !== 'SystemAdmin'
  && adminsOnPage.value <= 1
  && total.value <= users.value.length)

const roleChanged = computed(() =>
  !!selected.value && draftRole.value !== selected.value.role)

const grantsChanged = computed(() => {
  if (!selected.value) return false
  const before = [...selected.value.grantedTabs].sort().join(',')
  const after = [...draftGrants.value].sort().join(',')
  return before !== after
})

const dirty = computed(() => roleChanged.value || grantsChanged.value)

/** Tabs the *draft* role gives for free — checked and locked in the checklist. */
const baselineTabs = ref([])

/**
 * The baseline follows the draft role, not the saved one, so switching the
 * dropdown to System immediately shows which tabs that role would cover. It is
 * read from the server's own answer for the saved role and adjusted optimistically
 * only when the role picker moves; saving re-reads the truth.
 */
watch(draftRole, (role) => {
  if (!selected.value) return
  if (role === selected.value.role) {
    baselineTabs.value = [...selected.value.roleDefaultTabs]
    return
  }
  baselineTabs.value = predictBaseline(role)
})

/**
 * Mirror of the backend's role → default-tabs map, used only to preview a role
 * the user has not saved yet. The saved state always comes from the server.
 */
function predictBaseline(role) {
  const general = ['chat', 'dashboard', 'todo', 'jsonto', '5lakhs', 'ielts', 'settings']
  const system = [...general, 'workspace', 'docs', 'search', 'sources']
  if (role === 'SystemAdmin') return tabCatalog.value.map((t) => t.key)
  if (role === 'System') return system
  return general
}

function isBaseline(key) {
  return baselineTabs.value.includes(key)
}

function isChecked(key) {
  return isBaseline(key) || draftGrants.value.includes(key)
}

function toggleGrant(key, next) {
  if (isBaseline(key)) return                     // baseline is not removable
  const has = draftGrants.value.includes(key)
  if (next && !has) draftGrants.value = [...draftGrants.value, key]
  if (!next && has) draftGrants.value = draftGrants.value.filter((k) => k !== key)
}

// ---- Loading ----

async function refreshList() {
  listBusy.value = true
  listError.value = ''
  try {
    const res = await searchUsers({ search: search.value, page: page.value, pageSize: PAGE_SIZE })
    users.value = res.items
    total.value = res.total
  } catch (e) {
    listError.value = e.message
    users.value = []
    total.value = 0
  } finally {
    listBusy.value = false
  }
}

async function openUser(id) {
  detailBusy.value = true
  detailError.value = ''
  toast.value = ''
  try {
    const detail = await getUser(id)
    selected.value = detail
    draftRole.value = detail.role
    draftGrants.value = [...detail.grantedTabs]
    baselineTabs.value = [...detail.roleDefaultTabs]
  } catch (e) {
    detailError.value = e.message
    selected.value = null
  } finally {
    detailBusy.value = false
  }
}

/**
 * Saves whichever half changed. Role goes first: it moves the baseline, and the
 * grant call drops anything the new role already covers, so doing it in this
 * order means one save can promote and re-grant without a stale baseline in
 * between.
 */
async function save() {
  if (!selected.value || saving.value) return
  saving.value = true
  detailError.value = ''
  toast.value = ''

  const id = selected.value.id
  try {
    if (roleChanged.value) await setRole(id, draftRole.value)
    if (grantsChanged.value) await setTabs(id, draftGrants.value)

    await openUser(id)      // re-read: the server decides the final shape
    await refreshList()
    toast.value = 'Saved.'
    emit('changed')
  } catch (e) {
    // 403 (own row) and 409 (last admin) arrive here with the server's wording.
    detailError.value = e.message
    await openUser(id)      // resync so the form never shows an unsaved lie
  } finally {
    saving.value = false
  }
}

function resetDraft() {
  if (!selected.value) return
  draftRole.value = selected.value.role
  draftGrants.value = [...selected.value.grantedTabs]
  baselineTabs.value = [...selected.value.roleDefaultTabs]
  detailError.value = ''
  toast.value = ''
}

function runSearch() {
  page.value = 1
  refreshList()
}

function goPage(next) {
  const target = Math.min(Math.max(1, next), pageCount.value)
  if (target === page.value) return
  page.value = target
  refreshList()
}

function roleVariant(role) {
  if (role === 'SystemAdmin') return 'default'
  if (role === 'System') return 'secondary'
  return 'outline'
}

function labelFor(key) {
  return tabCatalog.value.find((t) => t.key === key)?.label ?? key
}

onMounted(async () => {
  try {
    tabCatalog.value = await listTabs()
  } catch (e) {
    listError.value = e.message
  }
  refreshList()
})

defineExpose({ refresh: refreshList })
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-4 overflow-y-auto p-4 sm:p-6">
    <div>
      <h2 class="flex items-center gap-2 text-lg font-semibold tracking-tight">
        <ShieldCheck class="h-5 w-5" />
        Access control
      </h2>
      <p class="mt-1 text-sm text-muted-foreground">
        Roles set the baseline tabs; individual grants add to it. Nothing here can
        take a tab away that the role already gives.
      </p>
    </div>

    <div class="grid min-h-0 flex-1 gap-4 lg:grid-cols-[minmax(0,22rem)_minmax(0,1fr)]">
      <!-- ── Users ── -->
      <section class="flex min-h-0 flex-col rounded-lg border bg-card">
        <div class="flex items-center gap-2 border-b p-3">
          <div class="relative flex-1">
            <Search class="absolute left-2 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              v-model="search"
              class="pl-8"
              placeholder="Search username…"
              @keydown.enter="runSearch"
            />
          </div>
          <Button size="sm" :disabled="listBusy" @click="runSearch">
            <Loader2 v-if="listBusy" class="h-4 w-4 animate-spin" />
            <span v-else>Search</span>
          </Button>
        </div>

        <p v-if="listError" class="border-b bg-destructive/10 p-3 text-sm text-destructive">
          {{ listError }}
        </p>

        <div class="min-h-0 flex-1 overflow-y-auto">
          <p v-if="!listBusy && !users.length" class="p-4 text-sm text-muted-foreground">
            No users match that search.
          </p>

          <button
            v-for="u in users"
            :key="u.id"
            :class="cn(
              'flex w-full items-center gap-3 border-b px-3 py-2.5 text-left transition-colors hover:bg-accent',
              selected && selected.id === u.id && 'bg-accent',
            )"
            @click="openUser(u.id)"
          >
            <div class="min-w-0 flex-1">
              <div class="flex items-center gap-1.5">
                <span class="truncate text-sm font-medium">{{ u.username }}</span>
                <span
                  v-if="u.id === currentUserId"
                  class="shrink-0 text-[10px] uppercase tracking-wide text-muted-foreground"
                >you</span>
              </div>
              <span class="text-xs text-muted-foreground">{{ u.tabCount }} tabs</span>
            </div>
            <Badge :variant="roleVariant(u.role)" class="shrink-0">{{ u.role }}</Badge>
          </button>
        </div>

        <div
          v-if="pageCount > 1"
          class="flex items-center justify-between gap-2 border-t p-2 text-xs text-muted-foreground"
        >
          <Button variant="ghost" size="sm" :disabled="page <= 1" @click="goPage(page - 1)">
            Previous
          </Button>
          <span>Page {{ page }} of {{ pageCount }} · {{ total }} users</span>
          <Button variant="ghost" size="sm" :disabled="page >= pageCount" @click="goPage(page + 1)">
            Next
          </Button>
        </div>
      </section>

      <!-- ── Detail ── -->
      <section class="flex min-h-0 flex-col rounded-lg border bg-card">
        <div
          v-if="!selected"
          class="flex flex-1 items-center justify-center p-8 text-center text-sm text-muted-foreground"
        >
          <span v-if="detailBusy">Loading…</span>
          <span v-else>Select a user to manage their role and tabs.</span>
        </div>

        <template v-else>
          <div class="flex items-start gap-3 border-b p-4">
            <UserCog class="mt-0.5 h-5 w-5 shrink-0 text-muted-foreground" />
            <div class="min-w-0 flex-1">
              <h3 class="truncate font-semibold">{{ selected.username }}</h3>
              <p class="text-xs text-muted-foreground">
                {{ selected.effectiveTabs.length }} effective tabs ·
                {{ selected.grantedTabs.length }} explicit grants
              </p>
            </div>
            <Badge :variant="roleVariant(selected.role)">{{ selected.role }}</Badge>
          </div>

          <!-- Guardrail notices: why the controls below may be locked. -->
          <p v-if="isSelf" class="border-b bg-muted/50 p-3 text-sm text-muted-foreground">
            This is your own account. You cannot change your own role or tabs —
            that rule is what stops an admin locking themselves out.
          </p>
          <p
            v-else-if="wouldDropLastAdmin"
            class="border-b bg-amber-500/10 p-3 text-sm text-amber-700 dark:text-amber-400"
          >
            This looks like the last SystemAdmin. Promote someone else before
            demoting them, or the change will be refused.
          </p>

          <div class="min-h-0 flex-1 overflow-y-auto p-4">
            <div class="max-w-sm">
              <Label for="rbac-role">Role</Label>
              <select
                id="rbac-role"
                v-model="draftRole"
                :disabled="isSelf || saving"
                class="mt-1.5 flex h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-sm transition-colors focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
              >
                <option v-for="r in ROLES" :key="r" :value="r">{{ r }}</option>
              </select>
              <p class="mt-1.5 text-xs text-muted-foreground">
                Sets the baseline tabs below. Grants add on top of it.
              </p>
            </div>

            <Separator class="my-4" />

            <div class="mb-2 flex items-baseline justify-between gap-2">
              <Label>Tabs</Label>
              <span class="text-xs text-muted-foreground">
                Locked rows come with the role
              </span>
            </div>

            <div class="grid gap-1.5 sm:grid-cols-2">
              <label
                v-for="t in tabCatalog"
                :key="t.key"
                :class="cn(
                  'flex items-center gap-2.5 rounded-md border p-2.5 text-sm transition-colors',
                  isBaseline(t.key)
                    ? 'bg-muted/40 text-muted-foreground'
                    : 'cursor-pointer hover:bg-accent',
                  isSelf && 'cursor-not-allowed opacity-60',
                )"
              >
                <Checkbox
                  :model-value="isChecked(t.key)"
                  :disabled="isBaseline(t.key) || isSelf || saving"
                  @update:model-value="(v) => toggleGrant(t.key, v)"
                />
                <span class="min-w-0 flex-1 truncate">{{ t.label }}</span>
                <span
                  v-if="isBaseline(t.key)"
                  class="shrink-0 text-[10px] uppercase tracking-wide"
                >role</span>
              </label>
            </div>
          </div>

          <div class="flex items-center gap-2 border-t p-3">
            <Button :disabled="!dirty || isSelf || saving" @click="save">
              <Loader2 v-if="saving" class="h-4 w-4 animate-spin" />
              <span>{{ saving ? 'Saving…' : 'Save changes' }}</span>
            </Button>
            <Button variant="ghost" :disabled="!dirty || saving" @click="resetDraft">
              Reset
            </Button>

            <span v-if="toast" class="ml-auto text-sm text-emerald-600 dark:text-emerald-400">
              {{ toast }}
            </span>
            <span v-else-if="detailError" class="ml-auto text-sm text-destructive">
              {{ detailError }}
            </span>
          </div>
        </template>
      </section>
    </div>
  </div>
</template>
