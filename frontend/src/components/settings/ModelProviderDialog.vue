<script setup>
/**
 * Model providers — add or edit one. The key field is a password input that is
 * always empty: the server never sends the key back, only its last 4. Leaving it
 * blank on edit keeps the stored key. Every rule is checked by the server; the
 * form shows its errors by field.
 */
import { computed, ref, watch } from 'vue'
import { Loader2 } from '@lucide/vue'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Checkbox } from '@/components/ui/checkbox'
import { Switch } from '@/components/ui/switch'
import { PROVIDER_CAPABILITIES, createModelProvider, updateModelProvider } from '@/services/model-providers.service.js'

const props = defineProps({
  open: { type: Boolean, default: false },
  /** The provider to edit, or null to add one. */
  provider: { type: Object, default: null },
})
const emit = defineEmits(['update:open', 'saved'])

const form = ref(blank())
const busy = ref(false)
const error = ref('')
const errors = ref({})

function blank() {
  return { key: '', name: '', baseUrl: 'https://', apiKey: '', capabilities: ['chat'], enabled: true, budgetM: '' }
}

watch(() => props.open, (open) => {
  if (!open) return
  error.value = ''; errors.value = {}
  const p = props.provider
  form.value = p
    ? { key: p.key, name: p.name, baseUrl: p.baseUrl, apiKey: '', capabilities: [...p.capabilities], enabled: p.enabled,
        budgetM: p.monthlyTokenBudget ? String(p.monthlyTokenBudget / 1e6) : '' }
    : blank()
})

const editing = computed(() => !!props.provider)

function toggleCap(value, on) {
  const caps = new Set(form.value.capabilities)
  if (on) caps.add(value); else caps.delete(value)
  form.value.capabilities = PROVIDER_CAPABILITIES.map((c) => c.value).filter((c) => caps.has(c))
}

/** The key slug from the name, while the key has not been typed by hand. */
const keyTouched = ref(false)
watch(() => form.value.name, (name) => {
  if (editing.value || keyTouched.value) return
  form.value.key = name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 32)
})

async function save() {
  busy.value = true; error.value = ''; errors.value = {}
  const budget = form.value.budgetM === '' ? 0 : Math.round(Number(form.value.budgetM) * 1e6)
  const input = {
    key: form.value.key.trim(), name: form.value.name.trim(), baseUrl: form.value.baseUrl.trim(),
    apiKey: form.value.apiKey.trim() || null, capabilities: form.value.capabilities, enabled: form.value.enabled,
    monthlyTokenBudget: Number.isFinite(budget) ? budget : -1,
  }
  try {
    const saved = editing.value
      ? await updateModelProvider(props.provider.id, input)
      : await createModelProvider(input)
    emit('saved', saved)
    emit('update:open', false)
  } catch (e) {
    error.value = e.message || 'Could not save.'
    errors.value = e.data?.errors || {}
  } finally {
    // The key leaves memory with the attempt, saved or not.
    form.value.apiKey = ''
    busy.value = false
  }
}
</script>

<template>
  <Dialog :open="open" @update:open="(v) => emit('update:open', v)">
    <DialogContent class="flex max-h-[90vh] w-[calc(100vw-2rem)] max-w-lg flex-col gap-4 overflow-y-auto p-4 sm:p-6" data-provider-dialog>
      <DialogHeader>
        <DialogTitle>{{ editing ? `Edit ${provider.name}` : 'Add a model provider' }}</DialogTitle>
        <DialogDescription>
          Any OpenAI-compatible API. The key is stored encrypted and never shown again.
        </DialogDescription>
      </DialogHeader>

      <form class="flex flex-col gap-3" @submit.prevent="save">
        <div class="grid gap-3 sm:grid-cols-2">
          <div class="flex flex-col gap-1.5">
            <Label for="mp-name">Name</Label>
            <Input id="mp-name" v-model="form.name" maxlength="60" placeholder="MangoMind" required />
            <p v-if="errors.name" class="text-xs text-destructive">{{ errors.name }}</p>
          </div>
          <div class="flex flex-col gap-1.5">
            <Label for="mp-key">Key</Label>
            <Input id="mp-key" v-model="form.key" maxlength="32" placeholder="mangomind" :disabled="editing"
              class="font-mono" @input="keyTouched = true" />
            <p v-if="errors.key" class="text-xs text-destructive">{{ errors.key }}</p>
            <p v-else class="text-xs text-muted-foreground">{{ editing ? 'Fixed once saved.' : 'The source key, e.g. for Jiji:ModelSource.' }}</p>
          </div>
        </div>

        <div class="flex flex-col gap-1.5">
          <Label for="mp-url">Base URL</Label>
          <Input id="mp-url" v-model="form.baseUrl" placeholder="https://api.mangomindbd.com/v1" class="font-mono" required />
          <p v-if="errors.baseUrl" class="text-xs text-destructive">{{ errors.baseUrl }}</p>
          <p v-else class="text-xs text-muted-foreground">Including the version, e.g. /v1. http:// only for localhost or a LAN address.</p>
        </div>

        <div class="flex flex-col gap-1.5">
          <Label for="mp-apikey">API key</Label>
          <Input id="mp-apikey" v-model="form.apiKey" type="password" autocomplete="off" spellcheck="false"
            :placeholder="editing && provider.hasKey ? 'Leave blank to keep it' : 'Paste the key'" class="font-mono" />
          <p v-if="errors.apiKey" class="text-xs text-destructive">{{ errors.apiKey }}</p>
          <p v-else-if="editing && provider.hasKey" class="text-xs text-muted-foreground">
            Stored key {{ provider.apiKeyHint ? `ends in ${provider.apiKeyHint}` : 'is set' }} — leave blank to keep it.
          </p>
          <p v-else class="text-xs text-muted-foreground">Optional only for a server on this machine or the LAN.</p>
        </div>

        <div class="flex flex-col gap-1.5">
          <span class="text-sm font-medium">Capabilities</span>
          <div class="flex flex-wrap gap-4">
            <label v-for="c in PROVIDER_CAPABILITIES" :key="c.value" class="flex items-center gap-2 text-sm">
              <Checkbox :model-value="form.capabilities.includes(c.value)" @update:model-value="(v) => toggleCap(c.value, v)" />
              {{ c.label }}
            </label>
          </div>
          <p v-if="errors.capabilities" class="text-xs text-destructive">{{ errors.capabilities }}</p>
        </div>

        <div class="grid gap-3 sm:grid-cols-2">
          <div class="flex flex-col gap-1.5">
            <Label for="mp-budget">Monthly budget (million tokens)</Label>
            <Input id="mp-budget" v-model="form.budgetM" inputmode="decimal" placeholder="4.5" />
            <p v-if="errors.monthlyTokenBudget" class="text-xs text-destructive">{{ errors.monthlyTokenBudget }}</p>
            <p v-else class="text-xs text-muted-foreground">Optional; shown next to this month's use.</p>
          </div>
          <label class="flex items-center gap-2 self-center text-sm">
            <Switch v-model="form.enabled" /> Enabled
          </label>
        </div>

        <p v-if="error && !Object.keys(errors).length" class="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive">
          {{ error }}
        </p>

        <DialogFooter class="gap-2">
          <Button type="button" variant="ghost" @click="emit('update:open', false)">Cancel</Button>
          <Button type="submit" :disabled="busy">
            <Loader2 v-if="busy" class="h-4 w-4 animate-spin" /> {{ editing ? 'Save' : 'Add provider' }}
          </Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
</template>
