<script setup>
import { computed } from 'vue'
import { Moon, Sun } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
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

const props = defineProps({
  username: { type: String, default: '' },
  initials: { type: String, default: '?' },
  status: { type: String, default: '' },
  connected: { type: Boolean, default: false },
  apiBaseUrl: { type: String, default: '' },
  models: { type: Array, default: () => [] },
  model: { type: String, default: '' },
  contexts: { type: Array, default: () => [] },
  context: { type: String, default: '' },
})

const emit = defineEmits(['update:model', 'update:context', 'logout'])

const { theme, toggleTheme } = useTheme()
const isDark = computed({
  get: () => theme.value === 'dark',
  set: () => toggleTheme(),
})

const modelValue = computed({
  get: () => props.model,
  set: (v) => emit('update:model', v),
})
const contextValue = computed({
  get: () => props.context,
  set: (v) => emit('update:context', v),
})
</script>

<template>
  <div class="h-full overflow-y-auto p-4 sm:p-6">
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
          <div class="flex items-center gap-3 rounded-lg border p-3">
            <span
              class="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-primary text-xs font-semibold text-primary-foreground"
            >
              {{ initials }}
            </span>
            <span class="flex-1 truncate text-sm font-medium">{{ username }}</span>
            <Button variant="destructive" size="sm" @click="emit('logout')">
              Log out
            </Button>
          </div>
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

            <div class="flex min-w-40 flex-1 flex-col gap-2">
              <Label>Context</Label>
              <Select v-model="contextValue">
                <SelectTrigger>
                  <SelectValue placeholder="Context" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem v-for="c in contexts" :key="c" :value="c">
                    {{ c }}
                  </SelectItem>
                </SelectContent>
              </Select>
            </div>
          </div>
        </section>
      </CardContent>
    </Card>
  </div>
</template>
