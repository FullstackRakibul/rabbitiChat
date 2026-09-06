<script setup>
import { Globe, Send, Square } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'

defineProps({
  modelValue: { type: String, default: '' },
  busy: { type: Boolean, default: false },
  placeholder: { type: String, default: "What's on your mind?…" },
  disabled: { type: Boolean, default: false },
  /** Show the per-message web search toggle. Off for surfaces that don't support it. */
  showWebSearch: { type: Boolean, default: false },
  webSearch: { type: Boolean, default: false },
})
const emit = defineEmits(['update:modelValue', 'update:webSearch', 'send'])
</script>

<template>
  <div class="border-t bg-background/95 p-3 backdrop-blur supports-backdrop-filter:bg-background/60 sm:p-4">
    <div
      class="mx-auto flex w-full max-w-3xl items-center gap-2 rounded-xl border bg-card p-1.5 shadow-sm focus-within:ring-1 focus-within:ring-ring">
      <input :value="modelValue" :placeholder="placeholder" :disabled="disabled"
        class="min-w-0 flex-1 bg-transparent px-3 py-2 text-sm outline-none placeholder:text-muted-foreground disabled:cursor-not-allowed disabled:opacity-50"
        @input="emit('update:modelValue', $event.target.value)" @keydown.enter.exact.prevent="emit('send')">
      <Button v-if="showWebSearch" size="icon" variant="ghost" :title="'Search the web for this message'"
        :aria-pressed="webSearch" :disabled="disabled" :class="cn(
          'shrink-0',
          webSearch && 'bg-primary/10 text-primary hover:bg-primary/15 hover:text-primary',
        )" @click="emit('update:webSearch', !webSearch)">
        <Globe />
        <span class="sr-only">
          {{ webSearch ? 'Web search on' : 'Web search off' }}
        </span>
      </Button>

      <Button size="icon" :variant="busy ? 'destructive' : 'default'" :title="busy ? 'Stop' : 'Send'"
        :disabled="disabled" class="shrink-0" @click="emit('send')">
        <Square v-if="busy" class="fill-current" />
        <Send v-else />
        <span class="sr-only">{{ busy ? 'Stop' : 'Send' }}</span>
      </Button>
    </div>
  </div>
</template>