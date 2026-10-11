<script setup>
/**
 * Oddity — Settings board (the form; the live Ollama panel arrives in Phase 5).
 *
 * One install-wide row: there is one Ollama and one GPU behind the gateway, so
 * every Ground Control user edits the same settings, and a save applies to the
 * very next gateway call. The server validates; this form only reports.
 */
import { onMounted, ref } from 'vue'
import { Loader2, SlidersHorizontal } from '@lucide/vue'
import { getOdditySettings, saveOdditySettings } from '@/services/index.js'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'

const form = ref(null)
const saved = ref(null)
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const toast = ref('')

/** The API row → form fields. The allowed list edits as one comma-separated line. */
function toForm(s) {
  return { ...s, allowedModels: s.allowedModels.join(', ') }
}

function toPayload(f) {
  return {
    enabled: f.enabled,
    defaultModel: f.defaultModel,
    allowedModels: f.allowedModels.split(',').map((m) => m.trim()).filter(Boolean),
    numCtx: Number(f.numCtx),
    replyReserve: Number(f.replyReserve),
    keepAlive: String(f.keepAlive),
    defaultRatePerMinute: Number(f.defaultRatePerMinute),
    maxInlineFileMb: Number(f.maxInlineFileMb),
    logBodies: f.logBodies,
    retentionDays: Number(f.retentionDays),
    publicBaseUrl: f.publicBaseUrl,
  }
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    saved.value = await getOdditySettings()
    form.value = toForm(saved.value)
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

async function save() {
  if (saving.value || !form.value) return
  saving.value = true
  error.value = ''
  toast.value = ''
  try {
    saved.value = await saveOdditySettings(toPayload(form.value))
    form.value = toForm(saved.value)
    toast.value = 'Saved. The next gateway call uses these settings.'
  } catch (e) {
    // The server names every invalid field in its message.
    error.value = e.message
  } finally {
    saving.value = false
  }
}

function reset() {
  if (saved.value) form.value = toForm(saved.value)
  error.value = ''
  toast.value = ''
}

onMounted(load)
</script>

<template>
  <div class="mx-auto flex w-full max-w-2xl flex-col gap-4">
    <div>
      <h2 class="flex items-center gap-2 text-lg font-semibold tracking-tight">
        <SlidersHorizontal class="h-5 w-5" /> Settings
      </h2>
      <p class="text-sm text-muted-foreground">
        Shared by every Ground Control user. Callers cannot override the window or keep-alive.
      </p>
    </div>

    <p v-if="loading && !form" class="text-sm text-muted-foreground">Loading…</p>

    <form v-if="form" class="space-y-5 rounded-lg border bg-card p-4 sm:p-6" @submit.prevent="save">
      <div class="flex items-center justify-between gap-4">
        <div>
          <Label for="odd-enabled">Gateway</Label>
          <p class="text-xs text-muted-foreground">Off answers every call with 503.</p>
        </div>
        <div class="flex items-center gap-2">
          <span class="text-sm text-muted-foreground">{{ form.enabled ? 'On' : 'Off' }}</span>
          <Switch id="odd-enabled" v-model="form.enabled" />
        </div>
      </div>

      <div class="space-y-1.5">
        <Label for="odd-public-url">Public gateway URL</Label>
        <Input id="odd-public-url" v-model="form.publicBaseUrl" type="url" placeholder="https://ai.example.com/oddity" />
        <p class="text-xs text-muted-foreground">
          The address callers outside this network use. Keys and docs show it next to the address
          you are browsing from. Blank shows only that one.
        </p>
      </div>

      <div class="grid gap-4 sm:grid-cols-2">
        <div class="space-y-1.5">
          <Label for="odd-default-model">Default model</Label>
          <Input id="odd-default-model" v-model="form.defaultModel" placeholder="qwen3.8:27b" />
          <p class="text-xs text-muted-foreground">Used when a call names no model.</p>
        </div>
        <div class="space-y-1.5">
          <Label for="odd-allowed">Allowed models</Label>
          <Input id="odd-allowed" v-model="form.allowedModels" placeholder="Any model" />
          <p class="text-xs text-muted-foreground">Comma-separated. Blank allows any.</p>
        </div>
        <div class="space-y-1.5">
          <Label for="odd-numctx">Context window (num_ctx)</Label>
          <Input id="odd-numctx" v-model="form.numCtx" type="number" min="2048" max="262144" step="1024" />
          <p class="text-xs text-muted-foreground">2048–262144. Changing it reloads the model on the next call.</p>
        </div>
        <div class="space-y-1.5">
          <Label for="odd-reserve">Reply reserve</Label>
          <Input id="odd-reserve" v-model="form.replyReserve" type="number" min="0" />
          <p class="text-xs text-muted-foreground">Tokens kept free for the reply. At most half the window.</p>
        </div>
        <div class="space-y-1.5">
          <Label for="odd-keepalive">Keep-alive</Label>
          <Input id="odd-keepalive" v-model="form.keepAlive" placeholder="-1" />
          <p class="text-xs text-muted-foreground">-1 keeps the model loaded; or 30m, 2h.</p>
        </div>
        <div class="space-y-1.5">
          <Label for="odd-rate">Default calls a minute</Label>
          <Input id="odd-rate" v-model="form.defaultRatePerMinute" type="number" min="0" />
          <p class="text-xs text-muted-foreground">For keys without their own rate. 0 is no limit.</p>
        </div>
        <div class="space-y-1.5">
          <Label for="odd-filemb">Inline file limit (MB)</Label>
          <Input id="odd-filemb" v-model="form.maxInlineFileMb" type="number" min="1" max="20" />
        </div>
        <div class="space-y-1.5">
          <Label for="odd-retention">Keep requests for (days)</Label>
          <Input id="odd-retention" v-model="form.retentionDays" type="number" min="1" />
        </div>
        <div class="space-y-1.5 sm:col-span-2">
          <Label for="odd-logbodies">Body logging</Label>
          <Select v-model="form.logBodies">
            <SelectTrigger id="odd-logbodies"><SelectValue /></SelectTrigger>
            <SelectContent>
              <SelectItem value="off">Off — store no text</SelectItem>
              <SelectItem value="preview">Preview — first 2,000 characters of question and reply</SelectItem>
              <SelectItem value="full">Full — the whole body and reply (files and images never stored)</SelectItem>
            </SelectContent>
          </Select>
        </div>
      </div>

      <p v-if="error" class="rounded-md bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>

      <div class="flex flex-wrap items-center gap-2">
        <Button type="submit" :disabled="saving">
          <Loader2 v-if="saving" class="h-4 w-4 animate-spin" /> Save settings
        </Button>
        <Button type="button" variant="ghost" :disabled="saving" @click="reset">Reset</Button>
        <span v-if="toast" class="text-sm text-muted-foreground">{{ toast }}</span>
        <span v-else-if="saved" class="ml-auto text-xs text-muted-foreground">
          Last saved {{ new Date(saved.updatedAtUtc).toLocaleString() }}
        </span>
      </div>
    </form>
  </div>
</template>
