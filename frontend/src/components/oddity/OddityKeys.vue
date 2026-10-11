<script setup>
/**
 * Oddity — Keys board.
 *
 * A new key is shown once, in the dialog that created it, and lives only in this
 * component's memory until the dialog closes: it is never written to
 * localStorage, and the API can never return it again.
 */
import { computed, onMounted, ref } from 'vue'
import { Check, Copy, KeyRound, Loader2, Pencil, Plus, RefreshCcw, Trash2 } from '@lucide/vue'
import {
  listOddityKeys, createOddityKey, updateOddityKey, deleteOddityKey, getOdditySettings, gatewayUrls,
} from '@/services/index.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import {
  Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle,
} from '@/components/ui/dialog'

const keys = ref([])
const loading = ref(false)
const error = ref('')

/** Only for the public gateway URL; the addresses fall back to this page's own without it. */
const settings = ref(null)
const urls = computed(() => gatewayUrls(settings.value))

async function loadSettings() {
  try {
    settings.value = await getOdditySettings()
  } catch {
    /* the page's own address is still shown */
  }
}

async function refresh() {
  loading.value = true
  error.value = ''
  try {
    keys.value = await listOddityKeys()
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

// ---- Create and edit ----

const editorOpen = ref(false)
const editing = ref(null)          // null = creating
const form = ref(blankForm())
const saving = ref(false)
const formError = ref('')

/** The new key, held only while its dialog is open. */
const createdKey = ref('')
const copied = ref('')

function blankForm() {
  return { name: '', note: '', rate: '', enabled: true, expires: '' }
}

function openCreate() {
  editing.value = null
  form.value = blankForm()
  formError.value = ''
  createdKey.value = ''
  editorOpen.value = true
}

function openEdit(k) {
  editing.value = k
  form.value = {
    name: k.name,
    note: k.note ?? '',
    rate: k.ratePerMinute ?? '',
    enabled: k.enabled,
    expires: k.expiresAtUtc ? k.expiresAtUtc.slice(0, 10) : '',
  }
  formError.value = ''
  createdKey.value = ''
  editorOpen.value = true
}

/** The form's fields as the API wants them: blank rate = settings default, blank date = never. */
function payload() {
  const rate = String(form.value.rate).trim()
  return {
    name: form.value.name,
    note: form.value.note,
    ratePerMinute: rate === '' ? null : Number(rate),
    enabled: form.value.enabled,
    // End of the chosen day, UTC, so a key "expiring on the 12th" works all of the 12th.
    expiresAtUtc: form.value.expires ? `${form.value.expires}T23:59:59Z` : null,
  }
}

async function save() {
  if (saving.value) return
  saving.value = true
  formError.value = ''
  try {
    if (editing.value) {
      await updateOddityKey(editing.value.id, payload())
      editorOpen.value = false
    } else {
      const created = await createOddityKey(payload())
      createdKey.value = created.key
    }
    await refresh()
  } catch (e) {
    formError.value = e.message
  } finally {
    saving.value = false
  }
}

function onEditorOpenChange(open) {
  editorOpen.value = open
  if (!open) createdKey.value = ''      // the key leaves memory with the dialog
}

/** Built for the first address — the public one, when set: callers are usually elsewhere. */
const snippet = computed(() =>
  `curl ${urls.value[0].url}/api/chat \\\n  -H "Authorization: Bearer ${createdKey.value}" \\\n`
  + `  -d '{"model":"qwen3.8:27b","messages":[{"role":"user","content":"Hello"}]}'`)

async function copy(text, what) {
  try {
    await navigator.clipboard.writeText(text)
    copied.value = what
    setTimeout(() => { if (copied.value === what) copied.value = '' }, 1500)
  } catch {
    formError.value = 'Copy failed — select the text and copy it by hand.'
  }
}

// ---- Revoke ----

const revoking = ref(null)
const revokeBusy = ref(false)

async function confirmRevoke() {
  if (!revoking.value || revokeBusy.value) return
  revokeBusy.value = true
  try {
    await deleteOddityKey(revoking.value.id)
    revoking.value = null
    await refresh()
  } catch (e) {
    error.value = e.message
  } finally {
    revokeBusy.value = false
  }
}

// ---- Display ----

function rateLabel(rate) {
  if (rate === null || rate === undefined) return 'Default'
  return rate === 0 ? 'No limit' : `${rate}/min`
}

function when(ts) {
  return ts ? new Date(ts).toLocaleString() : '—'
}

function expired(k) {
  return !!k.expiresAtUtc && new Date(k.expiresAtUtc) <= new Date()
}

// Mounted only when the board is first opened, so this is the first-open fetch.
onMounted(() => Promise.all([refresh(), loadSettings()]))
</script>

<template>
  <div class="mx-auto flex w-full max-w-5xl flex-col gap-4">
    <div class="flex flex-wrap items-center gap-2">
      <div class="min-w-0 flex-1">
        <h2 class="flex items-center gap-2 text-lg font-semibold tracking-tight">
          <KeyRound class="h-5 w-5" /> Keys
        </h2>
        <p class="text-sm text-muted-foreground">A key lets an Ollama client call the gateway at:</p>
        <ul class="mt-1 space-y-0.5 text-sm">
          <li v-for="u in urls" :key="u.url" class="flex flex-wrap items-baseline gap-x-2">
            <span v-if="urls.length > 1" class="w-24 shrink-0 text-xs text-muted-foreground">{{ u.label }}</span>
            <code class="break-all rounded bg-muted px-1 font-mono text-xs">{{ u.url }}</code>
          </li>
        </ul>
      </div>
      <Button variant="outline" size="sm" :disabled="loading" @click="refresh">
        <RefreshCcw class="h-4 w-4" /> Refresh
      </Button>
      <Button size="sm" @click="openCreate">
        <Plus class="h-4 w-4" /> New key
      </Button>
    </div>

    <p v-if="error" class="rounded-md bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>

    <div class="overflow-x-auto rounded-lg border bg-card">
      <table class="w-full min-w-176 text-sm">
        <thead class="border-b text-left text-xs text-muted-foreground">
          <tr>
            <th class="px-3 py-2 font-medium">Name</th>
            <th class="px-3 py-2 font-medium">Prefix</th>
            <th class="px-3 py-2 font-medium">Default profile</th>
            <th class="px-3 py-2 font-medium">Rate</th>
            <th class="px-3 py-2 font-medium">Status</th>
            <th class="px-3 py-2 font-medium">Expires</th>
            <th class="px-3 py-2 font-medium">Last used</th>
            <th class="px-3 py-2 text-right font-medium">24 h</th>
            <th class="px-3 py-2"><span class="sr-only">Actions</span></th>
          </tr>
        </thead>
        <tbody>
          <tr v-if="!loading && !keys.length">
            <td colspan="9" class="px-3 py-8 text-center text-muted-foreground">No keys yet.</td>
          </tr>
          <tr v-for="k in keys" :key="k.id" class="border-b last:border-0">
            <td class="px-3 py-2">
              <div class="font-medium">{{ k.name }}</div>
              <div v-if="k.note" class="max-w-64 truncate text-xs text-muted-foreground">{{ k.note }}</div>
            </td>
            <td class="px-3 py-2 font-mono text-xs">odd_{{ k.prefix }}_…</td>
            <td class="px-3 py-2 text-muted-foreground">—</td>
            <td class="px-3 py-2">{{ rateLabel(k.ratePerMinute) }}</td>
            <td class="px-3 py-2">
              <Badge v-if="!k.enabled" variant="outline">Disabled</Badge>
              <Badge v-else-if="expired(k)" variant="outline">Expired</Badge>
              <Badge v-else variant="secondary">Enabled</Badge>
            </td>
            <td class="px-3 py-2 text-muted-foreground">{{ k.expiresAtUtc ? new
              Date(k.expiresAtUtc).toLocaleDateString() : 'Never' }}</td>
            <td class="px-3 py-2 text-muted-foreground">{{ when(k.lastUsedUtc) }}</td>
            <td class="px-3 py-2 text-right tabular-nums">{{ k.requests24h }}</td>
            <td class="px-3 py-2">
              <div class="flex justify-end gap-1">
                <Button variant="ghost" size="icon" :aria-label="`Edit ${k.name}`" @click="openEdit(k)">
                  <Pencil class="h-4 w-4" />
                </Button>
                <Button variant="ghost" size="icon" :aria-label="`Revoke ${k.name}`" @click="revoking = k">
                  <Trash2 class="h-4 w-4" />
                </Button>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- Create / edit — and, once created, the key itself, shown this one time. -->
    <Dialog :open="editorOpen" @update:open="onEditorOpenChange">
      <DialogContent class="max-h-[90vh] overflow-y-auto">
        <template v-if="createdKey">
          <DialogHeader>
            <DialogTitle>Key created</DialogTitle>
            <DialogDescription>
              Copy it now. This is the only time it is shown; it cannot be read back.
            </DialogDescription>
          </DialogHeader>
          <!-- min-w-0: a grid child otherwise grows to its longest line and the whole dialog scrolls sideways. -->
          <div class="min-w-0 space-y-3">
            <div class="flex items-center gap-2">
              <code
                class="min-w-0 flex-1 break-all rounded-md bg-muted px-3 py-2 font-mono text-xs">{{ createdKey }}</code>
              <Button variant="outline" size="icon" aria-label="Copy key" @click="copy(createdKey, 'key')">
                <Check v-if="copied === 'key'" class="h-4 w-4" />
                <Copy v-else class="h-4 w-4" />
              </Button>
            </div>
            <div>
              <div class="mb-1 flex items-center justify-between">
                <Label>Try it</Label>
                <Button variant="ghost" size="sm" @click="copy(snippet, 'snippet')">
                  <Check v-if="copied === 'snippet'" class="h-4 w-4" />
                  <Copy v-else class="h-4 w-4" /> Copy
                </Button>
              </div>
              <pre class="overflow-x-auto rounded-md bg-muted p-3 font-mono text-xs">{{ snippet }}</pre>
              <p v-if="urls.length > 1" class="mt-1 text-xs text-muted-foreground">
                On this network: <code class="font-mono">{{ urls[1].url }}</code>
              </p>
            </div>
          </div>
          <DialogFooter>
            <Button @click="onEditorOpenChange(false)">Done</Button>
          </DialogFooter>
        </template>

        <form v-else class="min-w-0 space-y-4" @submit.prevent="save">
          <DialogHeader>
            <DialogTitle>{{ editing ? 'Edit key' : 'New key' }}</DialogTitle>
            <DialogDescription>
              {{ editing ? `odd_${editing.prefix}_…` : 'The key is shown once, after you create it.' }}
            </DialogDescription>
          </DialogHeader>

          <div class="space-y-1.5">
            <Label for="odd-key-name">Name</Label>
            <Input id="odd-key-name" v-model="form.name" maxlength="80" required
              placeholder="e.g. merchandising laptop" />
          </div>
          <div class="space-y-1.5">
            <Label for="odd-key-note">Note</Label>
            <Input id="odd-key-note" v-model="form.note" maxlength="500" placeholder="Who uses it, for what" />
          </div>
          <div class="grid gap-4 sm:grid-cols-2">
            <div class="space-y-1.5">
              <Label for="odd-key-rate">Calls a minute</Label>
              <Input id="odd-key-rate" v-model="form.rate" type="number" min="0" placeholder="Default" />
              <p class="text-xs text-muted-foreground">Blank uses the settings default; 0 is no limit.</p>
            </div>
            <div class="space-y-1.5">
              <Label for="odd-key-expires">Expires</Label>
              <Input id="odd-key-expires" v-model="form.expires" type="date" />
              <p class="text-xs text-muted-foreground">Blank never expires.</p>
            </div>
          </div>
          <div v-if="editing" class="flex items-center gap-2">
            <Switch id="odd-key-enabled" v-model="form.enabled" />
            <Label for="odd-key-enabled">Enabled</Label>
          </div>

          <p v-if="formError" class="text-sm text-destructive">{{ formError }}</p>

          <DialogFooter>
            <Button type="button" variant="ghost" @click="onEditorOpenChange(false)">Cancel</Button>
            <Button type="submit" :disabled="saving || !form.name.trim()">
              <Loader2 v-if="saving" class="h-4 w-4 animate-spin" />
              {{ editing ? 'Save' : 'Create key' }}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>

    <!-- Revoke -->
    <Dialog :open="!!revoking" @update:open="(o) => { if (!o) revoking = null }">
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Revoke “{{ revoking?.name }}”?</DialogTitle>
          <DialogDescription>
            Clients using odd_{{ revoking?.prefix }}_… stop working at once. Its request
            history stays in the log.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="ghost" @click="revoking = null">Cancel</Button>
          <Button variant="destructive" :disabled="revokeBusy" @click="confirmRevoke">
            <Loader2 v-if="revokeBusy" class="h-4 w-4 animate-spin" /> Revoke
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  </div>
</template>