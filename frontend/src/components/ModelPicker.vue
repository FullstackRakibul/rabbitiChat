<script setup>
/**
 * Model picker, grouped by source.
 *
 * "Local (private)" runs on your own Ollama; "Ollama Cloud (metered)" are the
 * hosted open models (gpt-oss, nemotron, gemma) and every call spends quota, so
 * they carry a badge. The last choice is remembered in localStorage.
 */
import { computed, onMounted, ref, watch } from 'vue'
import { Check, ChevronDown, Cloud, HardDrive } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { listModelsGrouped, loadModelChoice, saveModelChoice } from '@/services/model.service.js'

const props = defineProps({
  /** Selected model name. */
  modelValue: { type: String, default: '' },
  /** Selected source key ("local" | "cloud"). */
  source: { type: String, default: 'local' },
  disabled: { type: Boolean, default: false },
})

const emit = defineEmits(['update:modelValue', 'update:source'])

const groups = ref([])
const loading = ref(true)
const error = ref('')
const open = ref(false)

// Anything that isn't the local daemon is remote, so Cloud is the safe default:
// a HardDrive glyph beside a metered provider would imply it runs privately.
const GROUP_ICON = { local: HardDrive }
const iconFor = (key) => GROUP_ICON[key] || Cloud

const selected = computed(() => {
  for (const g of groups.value) {
    const hit = g.models.find((m) => m.name === props.modelValue && m.source === props.source)
    if (hit) return hit
  }
  return null
})

function choose(model) {
  open.value = false
  emit('update:modelValue', model.name)
  emit('update:source', model.source)
  saveModelChoice(model.source, model.name)
}

onMounted(async () => {
  try {
    groups.value = await listModelsGrouped()

    // Restore the remembered choice when it still exists; otherwise fall back to
    // the first local model, so a vanished cloud plan can't leave a dead picker.
    const remembered = loadModelChoice()
    const all = groups.value.flatMap((g) => g.models)
    const match = remembered
      && all.find((m) => m.name === remembered.model && m.source === remembered.source)

    // Never auto-select a metered model: spending someone's quota has to be a
    // deliberate act. With no local model available the picker stays empty and
    // the user opts in explicitly.
    const pick = match || all.find((m) => m.source === 'local' && !m.metered)
    if (pick && !props.modelValue) choose(pick)
  } catch (e) {
    error.value = e.message || 'Could not load models.'
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div class="relative">
    <button
      type="button"
      :disabled="disabled || loading"
      :class="cn(
        'flex h-9 w-full items-center gap-2 rounded-md border bg-background px-3 text-sm transition-colors',
        'hover:bg-accent hover:text-accent-foreground disabled:cursor-not-allowed disabled:opacity-50',
      )"
      @click="open = !open"
    >
      <component :is="iconFor(source)" class="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
      <span class="min-w-0 flex-1 truncate text-left">
        {{ loading ? 'Loading models…' : (modelValue || 'Select a model') }}
      </span>
      <span
        v-if="selected?.metered"
        class="shrink-0 rounded-full bg-amber-500/10 px-1.5 py-0.5 text-[10px] font-medium text-amber-700 ring-1 ring-inset ring-amber-500/20 dark:text-amber-400"
      >
        metered
      </span>
      <ChevronDown class="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
    </button>

    <template v-if="open">
      <div class="fixed inset-0 z-40" @click="open = false" />
      <div class="absolute left-0 z-50 mt-1 max-h-80 w-full min-w-[16rem] overflow-y-auto rounded-lg border bg-popover p-1 shadow-md">
        <p v-if="error" class="px-2 py-3 text-sm text-destructive">
          {{ error }}
        </p>

        <template v-for="g in groups" :key="g.key">
          <p class="flex items-center gap-1.5 px-2 py-1.5 text-[11px] font-medium uppercase tracking-wide text-muted-foreground">
            <component :is="iconFor(g.key)" class="h-3 w-3" />
            {{ g.label }}
          </p>

          <button
            v-for="m in g.models"
            :key="`${m.source}:${m.name}`"
            type="button"
            :class="cn(
              'flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm transition-colors hover:bg-accent',
              m.name === modelValue && m.source === source && 'bg-accent/60 font-medium',
            )"
            @click="choose(m)"
          >
            <Check
              :class="cn(
                'h-3.5 w-3.5 shrink-0',
                m.name === modelValue && m.source === source ? 'opacity-100' : 'opacity-0',
              )"
            />
            <span class="min-w-0 flex-1 truncate">{{ m.name }}</span>
            <span
              v-if="m.metered"
              class="shrink-0 rounded-full bg-amber-500/10 px-1.5 py-0.5 text-[10px] font-medium text-amber-700 ring-1 ring-inset ring-amber-500/20 dark:text-amber-400"
            >
              metered
            </span>
          </button>
        </template>

        <p v-if="!groups.length && !loading && !error" class="px-2 py-3 text-sm text-muted-foreground">
          No models available.
        </p>
      </div>
    </template>
  </div>
</template>
