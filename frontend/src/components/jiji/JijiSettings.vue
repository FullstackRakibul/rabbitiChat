<script setup>
/**
 * Jiji — Settings. What Jiji uses and whether it works: storage, the script model, the writing
 * style, the speech provider (checked live), the voice and YouTube (day 6). SystemAdmin edits
 * the script model, the speech provider and the voice here; they are stored on the server, and
 * the Jiji:* config values are only fallbacks. The provider itself (URL, key, voices) lives in
 * Settings → Model providers.
 */
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Loader2, Play, RefreshCw } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import ModelPicker from '@/components/ModelPicker.vue'
import { getJijiSettings, getJijiStatus, previewJijiVoice, saveJijiSettings } from '@/services/jiji.service.js'

const status = ref(null)
const settings = ref(null)
const error = ref('')
const loading = ref(false)

// The form (SystemAdmin).
const form = ref({ scriptModel: '', scriptModelSource: 'local', speechProvider: '', speechModel: 'kokoro', voice: '' })
const baseline = ref('')
const saving = ref(false)
const saveError = ref('')
const saved = ref('')

// Preview
const previewing = ref(false)
const previewError = ref('')
const previewUrl = ref('')
const audio = ref(null)

async function load() {
  loading.value = true
  error.value = ''
  try {
    const [st, se] = await Promise.all([getJijiStatus(), getJijiSettings()])
    status.value = st
    take(se)
  } catch (e) {
    error.value = e.message || 'Could not read the settings.'
  } finally {
    loading.value = false
  }
}

function take(se) {
  settings.value = se
  form.value = {
    scriptModel: se.scriptModel || '', scriptModelSource: se.scriptModelSource || 'local',
    speechProvider: se.speechProvider || '', speechModel: se.speechModel || 'kokoro', voice: se.voice || '',
  }
  baseline.value = JSON.stringify(form.value)
}

onMounted(load)
onBeforeUnmount(() => { if (previewUrl.value) URL.revokeObjectURL(previewUrl.value) })

const dirty = computed(() => !!baseline.value && JSON.stringify(form.value) !== baseline.value)
const canEdit = computed(() => !!settings.value?.canEdit)
const provider = computed(() => settings.value?.speechProviders.find((p) => p.key === form.value.speechProvider) || null)
const voices = computed(() => provider.value?.voices || [])

// Another provider: keep the voice if it has it, else take its first voice.
watch(() => form.value.speechProvider, () => {
  if (!provider.value) return
  if (!voices.value.includes(form.value.voice)) form.value.voice = voices.value[0] || ''
})

async function save() {
  saving.value = true; saveError.value = ''; saved.value = ''
  try {
    take(await saveJijiSettings({
      scriptModel: form.value.scriptModel || null, scriptModelSource: form.value.scriptModel ? form.value.scriptModelSource : null,
      speechProvider: form.value.speechProvider || null, speechModel: form.value.speechModel || null, voice: form.value.voice || null,
    }))
    status.value = await getJijiStatus()
    saved.value = 'Saved. The next narration uses it.'
  } catch (e) {
    saveError.value = e.message
  } finally {
    saving.value = false
  }
}

async function preview() {
  previewing.value = true; previewError.value = ''
  try {
    const blob = await previewJijiVoice({ provider: form.value.speechProvider || null, voice: form.value.voice || null })
    if (previewUrl.value) URL.revokeObjectURL(previewUrl.value)
    previewUrl.value = URL.createObjectURL(blob)
    await nextTick()
    audio.value?.play().catch(() => { /* the controls are there to press play */ })
  } catch (e) {
    previewError.value = e.message || 'The preview failed.'
  } finally {
    previewing.value = false
  }
}

/** Tokens as 0.3M / 12.4k, for a prepaid budget at a glance. */
function tokens(n) {
  if (n == null) return ''
  if (n >= 1e6) return `${(n / 1e6).toFixed(n >= 1e7 ? 0 : 1)}M`
  if (n >= 1e3) return `${(n / 1e3).toFixed(n >= 1e4 ? 0 : 1)}k`
  return String(n)
}

const PROVIDER_STATUS = {
  ok: 'Connected', untested: 'Not tested yet', key_rejected: 'Key rejected', no_credit: 'Credit used up — top up',
  rate_limited: 'Rate limited', unreachable: 'Unreachable', error: 'Error', key_unreadable: 'Key unreadable — paste it again',
  disabled: 'Switched off',
}

/** The script model's provider (MangoMind …): status and what Jiji spent this month. */
function providerRow(p) {
  const used = `Jiji ${tokens(p.jijiTokensThisMonth)} this month`
    + (p.monthlyTokenBudget ? ` · all ${tokens(p.tokensThisMonth)} of ${tokens(p.monthlyTokenBudget)}` : '')
  return {
    key: 'model-provider', label: `Script model provider · ${p.name}`, ok: p.status === 'ok', bad: p.status !== 'ok',
    note: `${PROVIDER_STATUS[p.status] || p.status} · ${used}`, word: p.status === 'ok' ? 'Ready' : 'Check Settings',
  }
}

const rows = (s) => [
  { key: 'storage', label: 'Storage', ok: s.storageWritable, note: s.storageWritable ? 'Writable' : 'Not writable' },
  { key: 'model', label: 'Script model', ok: !!s.model, note: s.model ? `${s.model} (${s.modelSource})` : 'None: pick one each time, or set the default below' },
  ...(s.provider ? [providerRow(s.provider)] : []),
  { key: 'style', label: 'Writing style', ok: s.voiceFound, note: s.voiceFound ? 'Context source "Jiji voice" found' : 'Optional: add a context source named "Jiji voice" in Sources' },
  {
    key: 'speech', label: 'Speech provider', ok: s.speech.reachable === true, bad: s.speech.reachable === false,
    note: s.speech.provider
      ? `${s.speech.providerName || s.speech.provider}${s.speech.reachable ? '' : ` · ${s.speech.message}`}`
      : 'Not chosen: add one with the speech capability in Settings → Model providers, then choose it below',
    word: s.speech.reachable === true ? 'Connected' : s.speech.reachable === false ? 'Not reachable' : 'Missing',
  },
  {
    key: 'voice', label: 'Voice', ok: !!s.speech.voice && s.speech.voiceListed,
    note: s.speech.voice ? `${s.speech.voice} · ${s.speech.model}${s.speech.voiceListed ? '' : ' · not in the provider\'s voice list'}` : 'Not chosen',
  },
  { key: 'yt-oauth', label: 'YouTube OAuth (day 6)', ok: s.youTubeConfigured, note: s.youTubeConfigured ? '' : 'Jiji:YouTube:ClientId and Jiji__YouTube__ClientSecret' },
  { key: 'yt-channel', label: 'YouTube channel (day 6)', ok: s.youTubeConnected, note: s.channelTitle || (s.youTubeConnected ? '' : 'Not connected') },
]
</script>

<template>
  <section class="mx-auto flex w-full max-w-2xl flex-col gap-4" data-jiji-settings>
    <div class="rounded-lg border bg-card p-6">
      <div class="flex items-center gap-2">
        <h2 class="text-lg font-semibold tracking-tight">Settings</h2>
        <Button variant="ghost" size="sm" class="ml-auto" :disabled="loading" @click="load">
          <Loader2 v-if="loading" class="h-4 w-4 animate-spin" /><RefreshCw v-else class="h-4 w-4" /> Check again
        </Button>
      </div>
      <p class="mt-1 text-sm text-muted-foreground">What Jiji uses, and whether it works right now.</p>

      <p v-if="error" class="mt-4 rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive">{{ error }}</p>
      <p v-else-if="!status" class="mt-4 text-sm text-muted-foreground">Loading…</p>
      <ul v-else class="mt-4 divide-y rounded-md border">
        <li v-for="r in rows(status)" :key="r.key" class="flex items-center justify-between gap-3 px-3 py-2 text-sm" :data-status-row="r.key">
          <span class="shrink-0">{{ r.label }}</span>
          <span class="flex min-w-0 items-center gap-2 text-right">
            <span v-if="r.note" class="min-w-0 text-xs text-muted-foreground">{{ r.note }}</span>
            <span :class="['shrink-0', r.ok ? 'text-emerald-600 dark:text-emerald-400' : r.bad ? 'text-destructive' : 'text-muted-foreground']">
              {{ r.word || (r.ok ? 'Ready' : 'Missing') }}
            </span>
          </span>
        </li>
      </ul>
    </div>

    <div v-if="settings" class="rounded-lg border bg-card p-6" data-jiji-settings-form>
      <h3 class="font-semibold">Script model, speech and voice</h3>
      <p class="mt-1 text-xs text-muted-foreground">
        {{ canEdit ? 'Stored on the server; the Jiji:* config values apply only to what is left blank.' : 'Only SystemAdmin can change these.' }}
      </p>

      <fieldset :disabled="!canEdit" class="mt-4 flex flex-col gap-4">
        <div class="flex flex-col gap-1.5 text-sm">
          <span>Default script model</span>
          <ModelPicker v-model="form.scriptModel" v-model:source="form.scriptModelSource" module="jiji" :auto-pick="false" :disabled="!canEdit" data-jiji-script-model />
          <span class="text-xs text-muted-foreground">From the models you may use in Jiji. Used when an item does not pick one.</span>
        </div>

        <div class="grid gap-4 sm:grid-cols-2">
          <label class="flex flex-col gap-1.5 text-sm">
            <span>Speech provider</span>
            <select v-model="form.speechProvider" class="h-9 rounded-md border bg-background px-2 text-sm" data-jiji-speech-provider>
              <option value="">— none —</option>
              <option v-for="p in settings.speechProviders" :key="p.key" :value="p.key">
                {{ p.name }}{{ p.enabled ? '' : ' (switched off)' }}{{ p.voices.length ? ` · ${p.voices.length} voices` : '' }}
              </option>
            </select>
            <span v-if="!settings.speechProviders.length" class="text-xs text-muted-foreground">
              None yet: add one with the speech capability in Settings → Model providers (Kokoro: http://localhost:8880/v1, no key).
            </span>
          </label>

          <div class="flex flex-col gap-1.5 text-sm">
            <span>Voice</span>
            <div class="flex gap-2">
              <select v-model="form.voice" class="h-9 min-w-0 flex-1 rounded-md border bg-background px-2 text-sm" :disabled="!voices.length"
                aria-label="Voice" data-jiji-voice>
                <option v-if="form.voice && !voices.includes(form.voice)" :value="form.voice">{{ form.voice }} (not listed)</option>
                <option v-for="v in voices" :key="v" :value="v">{{ v }}</option>
              </select>
              <Button type="button" variant="outline" size="sm" class="h-9" :disabled="!form.speechProvider || !form.voice || previewing"
                data-jiji-preview @click="preview">
                <Loader2 v-if="previewing" class="h-4 w-4 animate-spin" /><Play v-else class="h-4 w-4" /> Preview
              </Button>
            </div>
            <span v-if="provider && !voices.length" class="text-xs text-muted-foreground">No voices stored: Test the provider in Settings → Model providers.</span>
          </div>
        </div>

        <label class="flex w-48 flex-col gap-1.5 text-sm">
          <span>Speech model</span>
          <Input v-model="form.speechModel" placeholder="kokoro" class="h-9" />
        </label>
      </fieldset>

      <audio v-if="previewUrl" ref="audio" :src="previewUrl" controls class="mt-3 w-full" data-jiji-preview-audio />
      <p v-if="previewError" class="mt-2 rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive">{{ previewError }}</p>

      <div v-if="canEdit" class="mt-4 flex flex-wrap items-center gap-2 border-t pt-4">
        <Button size="sm" :disabled="!dirty || saving" data-jiji-save-settings @click="save">
          <Loader2 v-if="saving" class="h-4 w-4 animate-spin" /> Save settings
        </Button>
        <Button variant="ghost" size="sm" :disabled="!dirty || saving" @click="take(settings)">Reset</Button>
        <span v-if="saved" class="text-sm text-emerald-600 dark:text-emerald-400">{{ saved }}</span>
        <span v-if="saveError" class="text-sm text-destructive">{{ saveError }}</span>
      </div>
    </div>
  </section>
</template>
