<script setup>
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  Card, CardContent, CardDescription, CardHeader, CardTitle,
} from '@/components/ui/card'

defineProps({
  username: { type: String, default: '' },
  secret: { type: String, default: '' },
  busy: { type: Boolean, default: false },
  error: { type: String, default: '' },
})
const emit = defineEmits(['update:username', 'update:secret', 'submit'])
</script>

<template>
  <div class="flex min-h-screen items-center justify-center bg-background p-4">
    <Card class="w-full max-w-sm">
      <CardHeader>
        <CardTitle class="text-2xl">
          মস্তিস্ক
        </CardTitle>
        <CardDescription>Sign in to the mostisko the AI</CardDescription>
      </CardHeader>

      <CardContent>
        <form class="flex flex-col gap-4" @submit.prevent="emit('submit')">
          <div class="flex flex-col gap-2">
            <Label for="login-username">Username</Label>
            <Input id="login-username" :model-value="username" autocomplete="username" placeholder="you@team"
              @update:model-value="emit('update:username', $event)" />
          </div>

          <div class="flex flex-col gap-2">
            <Label for="login-secret">Secret</Label>
            <Input id="login-secret" :model-value="secret" type="password" autocomplete="current-password"
              placeholder="••••••••" @update:model-value="emit('update:secret', $event)" />
          </div>

          <Button type="submit" :disabled="busy" class="w-full">
            {{ busy ? 'Signing in…' : 'Continue' }}
          </Button>

          <p v-if="error" class="text-sm font-medium text-destructive">
            {{ error }}
          </p>

          <p class="text-xs leading-relaxed text-muted-foreground">
            A new username creates an account. Your secret is never sent in plain
            text after hashing on the server.
          </p>
          <!-- Bottom Text -->
          <Label class="text-center text-secondary text-xs mt-6 animate-fade-in" style="animation-delay: 0.3s;">
            ᓚᘏᗢ - linadi sabaq
          </Label>
        </form>
      </CardContent>
    </Card>
  </div>
</template>