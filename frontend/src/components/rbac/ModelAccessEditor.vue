<script setup>
/**
 * Access → Models — one role's or one user's model rules for Chat, HR and Jiji: a checklist
 * of models grouped by provider (with each paid model's cost multiplier), "every model" with
 * an optional multiplier ceiling, a Locked switch per module and the monthly cap in weighted
 * tokens. A user's rules only add to the role's: the role's models show ticked and fixed, a
 * locked module can be unlocked for them, and their cap can only be higher.
 * The server enforces all of it; this is the editor.
 */
import { computed, ref, watch } from 'vue'
import { Loader2, Lock } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Switch } from '@/components/ui/switch'
import {
  MODEL_MODULES, getModelAccessCatalog, getRoleModelRules, getUserModelRules, grantCovers,
  saveRoleModelRules, saveUserModelRules,
} from '@/services/model-access.service.js'
import { formatTokens } from '@/services/model-providers.service.js'

const props = defineProps({
  /** A role name, or null when editing a user. */
  role: { type: String, default: null },
  /** A user id, or null when editing a role. */
  userId: { type: Number, default: null },
})

const catalog = ref(null)
const roleRules = ref(null)    // the role's rules: fixed in the user editor
const userInfo = ref(null)
const grants = ref([])         // this subject's own grants
const locked = ref({ chat: false, hr: false, jiji: false })
const cap = ref('')
const module = ref('chat')
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const toast = ref('')
const baseline = ref('')

const isUser = computed(() => props.userId != null)

async function load() {
  loading.value = true; error.value = ''; toast.value = ''
  try {
    catalog.value = await getModelAccessCatalog()
    if (isUser.value) {
      const u = await getUserModelRules(props.userId)
      userInfo.value = u
      roleRules.value = u.roleRules
      take(u.user)
    } else {
      roleRules.value = null
      take(await getRoleModelRules(props.role))
    }
  } catch (e) { error.value = e.message } finally { loading.value = false }
}

function take(rules) {
  grants.value = rules.grants.map((g) => ({ ...g }))
  locked.value = { chat: false, hr: false, jiji: false, ...rules.locked }
  cap.value = rules.cap == null ? '' : String(rules.cap)
  baseline.value = snapshot()
}

const snapshot = () => JSON.stringify([grants.value, locked.value, cap.value])
const dirty = computed(() => baseline.value !== '' && snapshot() !== baseline.value)

watch(() => [props.role, props.userId], load, { immediate: true })

// ---- What the module's grants cover ----

const moduleGrants = computed(() => grants.value.filter((g) => g.module === module.value))
const roleModuleGrants = computed(() => (roleRules.value?.grants || []).filter((g) => g.module === module.value))

function own(providerKey, modelId) {
  return moduleGrants.value.find((g) => g.providerKey === providerKey && g.modelId === modelId)
}

/** 'role' when the role's rules already allow it, 'all' when one of ours with "*" does, '' otherwise. */
function coveredBy(source, model) {
  if (roleModuleGrants.value.some((g) => grantCovers(g, source, model))) return 'role'
  if (moduleGrants.value.some((g) => g.modelId === '*' && grantCovers(g, source, model))) return 'all'
  return ''
}

function setGrant(providerKey, modelId, on, maxMultiplier = null) {
  const rest = grants.value.filter((g) => !(g.module === module.value && g.providerKey === providerKey && g.modelId === modelId))
  grants.value = on ? [...rest, { module: module.value, providerKey, modelId, maxMultiplier }] : rest
}

function setCeiling(providerKey, value) {
  const g = own(providerKey, '*')
  if (!g) return
  const n = value === '' ? null : Number(value)
  g.maxMultiplier = Number.isFinite(n) ? n : null
  grants.value = [...grants.value]
}

const paidSources = computed(() => (catalog.value?.sources || []).filter((s) => s.paid))

/** Models of this module this subject ends up with, for the summary line. */
const allowedCount = computed(() => {
  let n = 0
  for (const s of catalog.value?.sources || [])
    for (const m of s.models)
      if (coveredBy(s, m) || own(s.key, m.name)) n++
  return n
})

const roleLocked = computed(() => !!roleRules.value?.locked?.[module.value])
const hrDefault = computed(() => catalog.value?.defaults?.hr)

async function save() {
  saving.value = true; error.value = ''; toast.value = ''
  const capValue = cap.value === '' ? null : Math.round(Number(cap.value))
  if (capValue != null && (!Number.isFinite(capValue) || capValue < 0)) {
    error.value = 'The cap is a whole number of weighted tokens, or blank for none.'
    saving.value = false
    return
  }
  const rules = { grants: grants.value, locked: locked.value, cap: capValue }
  try {
    if (isUser.value) {
      const u = await saveUserModelRules(props.userId, rules)
      userInfo.value = u
      take(u.user)
    } else {
      take(await saveRoleModelRules(props.role, rules))
    }
    toast.value = 'Saved. It applies to the next request.'
  } catch (e) { error.value = e.message } finally { saving.value = false }
}
</script>

<template>
  <section class="flex flex-col gap-3 rounded-lg border bg-card p-4" data-model-access :data-subject="isUser ? `user-${userId}` : role">
    <div class="flex flex-wrap items-center gap-2">
      <h3 class="font-semibold">Models</h3>
      <span class="text-xs text-muted-foreground">
        {{ isUser ? 'Adds to the role’s models; never removes them.' : 'What every user of this role may run in Chat, HR and Jiji.' }}
      </span>
      <nav class="ml-auto flex gap-1 rounded-md border bg-muted/40 p-0.5" aria-label="Module">
        <button v-for="m in MODEL_MODULES" :key="m.key" type="button" :data-module="m.key"
          :class="['rounded px-2.5 py-1 text-xs', module === m.key ? 'bg-background font-medium shadow-sm' : 'text-muted-foreground hover:text-foreground']"
          @click="module = m.key">
          {{ m.label }}
          <Lock v-if="(isUser ? roleRules?.locked?.[m.key] && locked[m.key] : locked[m.key])" class="ml-0.5 inline h-3 w-3" />
        </button>
      </nav>
    </div>

    <p v-if="loading" class="text-sm text-muted-foreground">Loading…</p>
    <template v-else-if="catalog">
      <!-- Locked -->
      <div class="flex flex-wrap items-center gap-3 rounded-md border px-3 py-2 text-sm">
        <template v-if="!isUser">
          <Switch v-model="locked[module]" :aria-label="`Locked in ${module}`" data-locked />
          <span>Locked</span>
        </template>
        <template v-else>
          <Switch :model-value="roleLocked && locked[module]" :disabled="!roleLocked" :aria-label="`Locked in ${module}`" data-locked
            @update:model-value="(v) => (locked[module] = v)" />
          <span>{{ roleLocked ? 'Locked (from the role) — switch off to unlock for this user' : 'Not locked for the role' }}</span>
        </template>
        <span class="text-xs text-muted-foreground">
          <template v-if="module === 'hr'">
            Locked users get the HR default<template v-if="hrDefault"> ({{ hrDefault.model }})</template> and no dropdown.
          </template>
          <template v-else>Locked users get the first allowed model and no dropdown.</template>
        </span>
      </div>

      <!-- Every Settings provider at once, with a multiplier ceiling -->
      <div v-if="paidSources.length" class="flex flex-wrap items-center gap-2 rounded-md border px-3 py-2 text-sm">
        <Checkbox :model-value="!!own('*', '*')" aria-label="Every model of every Settings provider"
          @update:model-value="(v) => setGrant('*', '*', v)" />
        <span>Every paid model</span>
        <template v-if="own('*', '*')">
          <span class="text-muted-foreground">up to</span>
          <Input :model-value="own('*', '*').maxMultiplier ?? ''" class="h-7 w-16 text-xs" inputmode="decimal" placeholder="any"
            aria-label="Highest multiplier for every paid model" @update:model-value="(v) => setCeiling('*', v)" />
          <span class="text-muted-foreground">x</span>
        </template>
        <span v-for="g in roleModuleGrants.filter((g) => g.providerKey === '*')" :key="`r${g.maxMultiplier}`" class="text-xs text-muted-foreground">
          role: every paid model{{ g.maxMultiplier != null ? ` up to ${g.maxMultiplier}x` : '' }}
        </span>
      </div>

      <!-- Per provider -->
      <div class="grid gap-3 md:grid-cols-2">
        <div v-for="s in catalog.sources" :key="s.key" class="rounded-md border" :data-source="s.key">
          <div class="flex items-center gap-2 border-b bg-muted/40 px-3 py-1.5 text-xs">
            <span class="font-medium">{{ s.label }}</span>
            <span v-if="s.paid" class="rounded-full bg-amber-500/10 px-1.5 text-[10px] text-amber-700 dark:text-amber-400">metered</span>
            <span v-else class="text-[10px] text-muted-foreground">free</span>
            <label class="ml-auto flex items-center gap-1.5">
              <Checkbox :model-value="!!own(s.key, '*')" :aria-label="`All ${s.label} models`" @update:model-value="(v) => setGrant(s.key, '*', v)" />
              All
            </label>
            <template v-if="s.paid && own(s.key, '*')">
              <span class="text-muted-foreground">≤</span>
              <Input :model-value="own(s.key, '*').maxMultiplier ?? ''" class="h-6 w-12 px-1 text-xs" inputmode="decimal" placeholder="any"
                :aria-label="`Highest multiplier for ${s.label}`" @update:model-value="(v) => setCeiling(s.key, v)" />
              <span class="text-muted-foreground">x</span>
            </template>
          </div>
          <ul class="max-h-56 divide-y overflow-y-auto">
            <li v-for="m in s.models" :key="m.name" class="flex items-center gap-2 px-3 py-1 text-xs">
              <Checkbox :model-value="!!coveredBy(s, m) || !!own(s.key, m.name)" :disabled="!!coveredBy(s, m)"
                :aria-label="`Allow ${m.name}`" @update:model-value="(v) => setGrant(s.key, m.name, v)" />
              <span class="min-w-0 flex-1 truncate font-mono">{{ m.name }}</span>
              <span v-if="m.multiplier != null" :class="['font-mono tabular-nums', m.multiplier > 1.3 ? 'text-amber-600 dark:text-amber-400' : 'text-muted-foreground']">{{ m.multiplier }}x</span>
              <span v-if="coveredBy(s, m)" class="text-[10px] uppercase tracking-wide text-muted-foreground">{{ coveredBy(s, m) }}</span>
            </li>
            <li v-if="!s.models.length" class="px-3 py-2 text-xs text-muted-foreground">No models listed right now.</li>
          </ul>
        </div>
      </div>

      <!-- Cap -->
      <div class="flex flex-wrap items-center gap-2 text-sm">
        <span>Monthly cap</span>
        <Input v-model="cap" class="h-8 w-32" inputmode="numeric" :placeholder="isUser ? 'role’s' : 'no cap'" aria-label="Monthly cap in weighted tokens" />
        <span class="text-xs text-muted-foreground">
          weighted tokens (tokens × multiplier), all modules, from the 1st (Asia/Dhaka). Ollama is free and never counted.
          <template v-if="isUser && userInfo">
            Role’s cap: {{ roleRules?.cap == null ? 'none' : formatTokens(roleRules.cap) }} ·
            effective: {{ userInfo.effectiveCap == null ? 'none' : formatTokens(userInfo.effectiveCap) }} ·
            used: {{ formatTokens(Math.round(userInfo.usedThisMonth)) }}. A user’s cap can only raise the role’s.
          </template>
        </span>
      </div>

      <div class="flex flex-wrap items-center gap-2 border-t pt-3">
        <Button size="sm" :disabled="!dirty || saving" data-save-models @click="save">
          <Loader2 v-if="saving" class="h-4 w-4 animate-spin" /> Save models
        </Button>
        <Button variant="ghost" size="sm" :disabled="!dirty || saving" @click="load">Reset</Button>
        <span class="text-xs text-muted-foreground">{{ allowedCount }} models allowed in {{ MODEL_MODULES.find((m) => m.key === module).label }}.</span>
        <span v-if="toast" class="ml-auto text-sm text-emerald-600 dark:text-emerald-400">{{ toast }}</span>
        <span v-else-if="error" class="ml-auto text-sm text-destructive">{{ error }}</span>
      </div>
    </template>
    <p v-else-if="error" class="text-sm text-destructive">{{ error }}</p>
  </section>
</template>
