<script setup>
import { ref, computed } from 'vue'
import { Moon, Sun, Settings, BookOpen, KeyRound, Loader2 } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { changeSecret } from '@/services/index.js'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { Separator } from '@/components/ui/separator'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import {
  Card, CardContent, CardDescription, CardHeader, CardTitle,
} from '@/components/ui/card'
import { useTheme } from '@/composables/useTheme'
import MostiskoDocsPage from '@/components/mostisko-docs/MostiskoDocsPage.vue'

const props = defineProps({
  username: { type: String, default: '' },
  initials: { type: String, default: '?' },
  status: { type: String, default: '' },
  connected: { type: Boolean, default: false },
  apiBaseUrl: { type: String, default: '' },
  models: { type: Array, default: () => [] },
  model: { type: String, default: '' },
})

const emit = defineEmits(['update:model', 'logout'])

const { theme, toggleTheme } = useTheme()
const isDark = computed({
  get: () => theme.value === 'dark',
  set: () => toggleTheme(),
})

const modelValue = computed({
  get: () => props.model,
  set: (v) => emit('update:model', v),
})

// ---- Change secret ----
// The server checks the current secret and the rules; this form only reports.
// Held in memory only and cleared after every attempt.
const secretOpen = ref(false)
const secretForm = ref({ current: '', next: '', confirm: '' })
const secretBusy = ref(false)
const secretError = ref('')
const secretDone = ref('')

const secretMismatch = computed(() =>
  secretForm.value.confirm.length > 0 && secretForm.value.next !== secretForm.value.confirm)

async function submitSecret() {
  if (secretBusy.value || secretMismatch.value) return
  secretBusy.value = true
  secretError.value = ''
  secretDone.value = ''
  try {
    await changeSecret(secretForm.value.current, secretForm.value.next)
    secretDone.value = 'Secret changed. Your other sessions are signed out; this one carries on.'
    secretOpen.value = false
  } catch (e) {
    secretError.value = e.message
  } finally {
    secretForm.value = { current: '', next: '', confirm: '' }
    secretBusy.value = false
  }
}

/** @type {import('vue').Ref<'settings' | 'docs'>} */
const settingsView = ref('settings')

/** @type {{ id: 'settings' | 'docs', label: string, icon: object }[]} */
const viewTabs = [
  { id: 'settings', label: 'General',       icon: Settings },
  { id: 'docs',     label: 'Documentation', icon: BookOpen },
]
</script>

<template>
  <div class="flex h-full flex-col">
    <!-- Sub-navigation — switches between General Settings and Documentation -->
    <div
      class="flex shrink-0 items-center gap-1 border-b bg-background/95 px-4 py-2 backdrop-blur sm:px-6"
    >
      <button
        v-for="tab in viewTabs"
        :key="tab.id"
        :class="[
          'inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-sm font-medium transition-colors',
          'focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring',
          settingsView === tab.id
            ? 'bg-primary text-primary-foreground shadow-sm'
            : 'text-muted-foreground hover:bg-muted hover:text-foreground',
        ]"
        @click="settingsView = tab.id"
      >
        <component :is="tab.icon" class="h-3.5 w-3.5" />
        {{ tab.label }}
      </button>
    </div>

    <!-- General Settings view -->
    <div v-if="settingsView === 'settings'" class="flex-1 overflow-y-auto p-4 sm:p-6">
      <Card class="mx-auto w-full max-w-2xl">
        <CardHeader>
          <CardTitle>Settings</CardTitle>
          <CardDescription>Session and workspace defaults.</CardDescription>
        </CardHeader>

        <CardContent class="flex flex-col gap-6">
          <!-- Account -->
          <section class="flex flex-col gap-3">
            <p class="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
              Account
            </p>
            <div class="flex flex-wrap items-center gap-3 rounded-lg border p-3">
              <span
                class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-primary text-xs font-semibold text-primary-foreground"
              >
                {{ initials }}
              </span>
              <span class="flex-1 truncate text-sm font-medium">{{ username }}</span>
              <Button variant="outline" size="sm" :aria-expanded="secretOpen" @click="secretOpen = !secretOpen; secretError = ''; secretDone = ''">
                <KeyRound class="h-4 w-4" /> Change secret
              </Button>
              <Button variant="destructive" size="sm" @click="emit('logout')">
                Log out
              </Button>
            </div>

            <form v-if="secretOpen" class="flex flex-col gap-3 rounded-lg border p-3" @submit.prevent="submitSecret">
              <div class="flex flex-col gap-1.5">
                <Label for="secret-current">Current secret</Label>
                <Input id="secret-current" v-model="secretForm.current" type="password" autocomplete="current-password" required />
              </div>
              <div class="grid gap-3 sm:grid-cols-2">
                <div class="flex flex-col gap-1.5">
                  <Label for="secret-next">New secret</Label>
                  <Input id="secret-next" v-model="secretForm.next" type="password" autocomplete="new-password" minlength="8" required />
                </div>
                <div class="flex flex-col gap-1.5">
                  <Label for="secret-confirm">Repeat the new secret</Label>
                  <Input id="secret-confirm" v-model="secretForm.confirm" type="password" autocomplete="new-password" required />
                </div>
              </div>
              <p class="text-xs text-muted-foreground">
                At least 8 characters. Changing it signs out your other sessions.
              </p>
              <p v-if="secretMismatch" class="text-sm text-destructive">The two new secrets do not match.</p>
              <p v-if="secretError" class="text-sm text-destructive">{{ secretError }}</p>
              <div class="flex gap-2">
                <Button type="submit" size="sm" :disabled="secretBusy || secretMismatch || !secretForm.confirm">
                  <Loader2 v-if="secretBusy" class="h-4 w-4 animate-spin" /> Save new secret
                </Button>
                <Button type="button" variant="ghost" size="sm" @click="secretOpen = false">Cancel</Button>
              </div>
            </form>
            <p v-if="secretDone" class="text-sm text-muted-foreground">{{ secretDone }}</p>
          </section>

          <Separator />

          <!-- Appearance -->
          <section class="flex flex-col gap-3">
            <p class="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
              Appearance
            </p>
            <div class="flex items-center gap-3 rounded-lg border p-3">
              <component :is="isDark ? Moon : Sun" class="h-4 w-4 shrink-0 text-muted-foreground" />
              <Label for="dark-mode" class="flex-1 cursor-pointer">Dark mode</Label>
              <Switch id="dark-mode" v-model="isDark" />
            </div>
          </section>

          <Separator />

          <!-- Connection -->
          <section class="flex flex-col gap-3">
            <p class="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
              Connection
            </p>
            <div class="flex flex-wrap items-center gap-3 rounded-lg border p-3">
              <span
                :class="cn(
                  'h-2 w-2 shrink-0 rounded-full',
                  connected ? 'bg-emerald-500' : 'bg-muted-foreground/40',
                )"
              />
              <span class="flex-1 text-sm">{{ status }}</span>
              <span class="truncate text-xs text-muted-foreground">API: {{ apiBaseUrl }}</span>
            </div>
          </section>

          <Separator />

          <!-- Defaults -->
          <section class="flex flex-col gap-3">
            <p class="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
              Defaults
            </p>
            <div class="flex flex-wrap gap-4">
              <div class="flex min-w-40 flex-1 flex-col gap-2">
                <Label>Model</Label>
                <Select v-model="modelValue">
                  <SelectTrigger>
                    <SelectValue placeholder="Model" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem v-for="m in models" :key="m" :value="m">
                      {{ m }}
                    </SelectItem>
                  </SelectContent>
                </Select>
              </div>

            </div>
          </section>
        </CardContent>
      </Card>
    </div>

    <!-- Documentation view — fills remaining space, manages its own scrolling -->
    <MostiskoDocsPage v-else class="min-h-0 flex-1" />
  </div>
</template>
