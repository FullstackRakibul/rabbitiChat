<script setup>
/**
 * Access → Models, by role: pick a role, edit its models for Chat, HR and Jiji. Above it the
 * HR default model — what every user locked in HR gets, and what HR uses when no model is named.
 */
import { computed, onMounted, ref } from 'vue'
import { Loader2 } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import ModelAccessEditor from '@/components/rbac/ModelAccessEditor.vue'
import { getModelAccessCatalog, setModuleDefault } from '@/services/model-access.service.js'

const catalog = ref(null)
const role = ref('General')
const hrChoice = ref('')
const busy = ref(false)
const error = ref('')
const done = ref('')

const key = (source, model) => `${source}\u0000${model}`

async function load() {
  try {
    catalog.value = await getModelAccessCatalog()
    const d = catalog.value.defaults?.hr
    hrChoice.value = d ? key(d.source, d.model) : ''
  } catch (e) { error.value = e.message }
}
onMounted(load)

const options = computed(() => (catalog.value?.sources || []).flatMap((s) =>
  s.models.map((m) => ({ value: key(s.key, m.name), label: `${s.label} · ${m.name}${m.multiplier != null ? ` (${m.multiplier}x)` : ''}` }))))

const current = computed(() => {
  const d = catalog.value?.defaults?.hr
  return d ? key(d.source, d.model) : ''
})

async function saveDefault() {
  busy.value = true; error.value = ''; done.value = ''
  const [source, model] = hrChoice.value.split('\u0000')
  try {
    catalog.value = await setModuleDefault('hr', source, model)
    done.value = 'HR default saved.'
  } catch (e) { error.value = e.message } finally { busy.value = false }
}
</script>

<template>
  <div class="flex flex-col gap-4" data-model-access-roles>
    <section class="flex flex-wrap items-center gap-2 rounded-lg border bg-card p-4 text-sm">
      <span class="font-medium">HR default model</span>
      <select v-model="hrChoice" class="h-8 min-w-[16rem] rounded-md border bg-background px-2 text-sm" aria-label="HR default model" data-hr-default>
        <option v-if="!options.some((o) => o.value === hrChoice) && hrChoice" :value="hrChoice">{{ hrChoice.replace('\u0000', ' · ') }} (not listed now)</option>
        <option v-for="o in options" :key="o.value" :value="o.value">{{ o.label }}</option>
      </select>
      <Button size="sm" variant="outline" :disabled="busy || !hrChoice || hrChoice === current" @click="saveDefault">
        <Loader2 v-if="busy" class="h-4 w-4 animate-spin" /> Save
      </Button>
      <span class="text-xs text-muted-foreground">Users locked in HR get this model and no dropdown.</span>
      <span v-if="done" class="text-xs text-emerald-600 dark:text-emerald-400">{{ done }}</span>
      <span v-if="error" class="text-xs text-destructive">{{ error }}</span>
    </section>

    <nav class="flex flex-wrap gap-1" aria-label="Role">
      <button v-for="r in catalog?.roles || []" :key="r" type="button" :data-role="r"
        :class="['rounded-md border px-3 py-1 text-sm', role === r ? 'bg-primary text-primary-foreground' : 'hover:bg-accent']"
        @click="role = r">{{ r }}</button>
    </nav>

    <ModelAccessEditor :role="role" />
  </div>
</template>
