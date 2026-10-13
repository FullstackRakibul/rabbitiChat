<script setup>
// Library: the drop zone and the table of POs. The entry point of the studio —
// one main action (drop a PDF), and one row per document with its status, its
// last run and what to do next.

import { computed, ref } from 'vue'
import { Braces, Check, MessageSquare, Paperclip, Trash2 } from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'
import UploadStepper from './UploadStepper.vue'

const props = defineProps({
  documents: { type: Array, default: () => [] },
  /** Buyer profiles from /api/buyer-profiles — never a hard-coded list. */
  profiles: { type: Array, default: () => [] },
  /** '' means auto-detect. */
  buyer: { type: String, default: '' },
  file: { type: Object, default: null },
  /** The stepper's state, owned by the parent which runs the pipeline. */
  progress: { type: Object, default: () => ({ current: '', failedStep: '', error: '', done: false }) },
  busy: { type: Boolean, default: false },
  error: { type: String, default: '' },
  /** False when the user lacks the `docs` tab; the section explains itself. */
  canUse: { type: Boolean, default: true },
})

const emit = defineEmits([
  'update:buyer', 'file-picked', 'upload', 'retry', 'delete', 'review', 'chat',
])

const buyerValue = computed({
  get: () => props.buyer,
  set: (v) => emit('update:buyer', v),
})

const fileInput = ref(null)
const dragging = ref(false)

function onDrop(e) {
  dragging.value = false
  const f = e.dataTransfer?.files?.[0]
  if (f) emit('file-picked', f)
}

function onPicked(e) {
  const f = e.target.files?.[0] ?? null
  if (f) emit('file-picked', f)
}

/**
 * The status chip for a row. The three states of §3.2, mapped onto the badge
 * variants the rest of the app already uses — so a chip here reads the same as
 * a chip in Logs.
 */
function chip(doc) {
  if (doc.confirmed) return { label: 'confirmed', variant: 'default' }
  if (doc.failedChecks) return { label: 'checks failed', variant: 'destructive' }
  if (doc.lastRunAt) return { label: 'needs review', variant: 'secondary' }
  return { label: doc.status ?? 'new', variant: 'outline' }
}

/** Accuracy is only meaningful once a person has confirmed the run. */
function accuracyLabel(doc) {
  if (typeof doc.accuracy !== 'number') return '—'
  return `${Math.round(doc.accuracy * 100)}%`
}

defineExpose({ reset: () => { if (fileInput.value) fileInput.value.value = '' } })
</script>

<template>
  <div class="flex flex-col gap-4 sm:gap-6">
    <!-- A section whose API this user cannot call says so, instead of failing
         on the first click. -->
    <p
      v-if="!canUse"
      class="rounded-lg border border-dashed p-4 text-sm text-muted-foreground"
    >
      Uploading and extracting POs needs the <b>Documents</b> access.
      Ask an administrator to grant it.
    </p>

    <template v-else>
      <!-- Drop zone: the one main action of this screen. -->
      <div
        :class="cn(
          'flex flex-col items-center gap-3 rounded-xl border-2 border-dashed p-6 text-center transition-colors sm:p-10',
          dragging && 'border-primary bg-accent',
        )"
        @dragover.prevent="dragging = true"
        @dragleave.prevent="dragging = false"
        @drop.prevent="onDrop"
      >
        <input
          ref="fileInput"
          type="file"
          accept=".pdf,.xlsx,.txt,.csv"
          class="hidden"
          @change="onPicked"
        >

        <Paperclip class="h-6 w-6 text-muted-foreground" />
        <div>
          <p class="text-sm font-medium">
            {{ file ? file.name : 'Drag a purchase order here' }}
          </p>
          <p class="mt-0.5 text-xs text-muted-foreground">
            PDF, XLSX, TXT or CSV — or click to choose
          </p>
        </div>

        <div class="flex flex-wrap items-center justify-center gap-2">
          <Button variant="outline" size="sm" @click="fileInput?.click()">
            Choose a file
          </Button>

          <Select v-model="buyerValue">
            <SelectTrigger class="h-8 w-auto min-w-36 text-xs">
              <SelectValue placeholder="Buyer: auto-detect" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="">
                Buyer: auto-detect
              </SelectItem>
              <SelectItem v-for="p in profiles" :key="p.id" :value="String(p.id)">
                {{ p.name }}
              </SelectItem>
            </SelectContent>
          </Select>

          <Button v-if="file" size="sm" :disabled="busy" @click="emit('upload')">
            {{ busy ? 'Working…' : 'Start' }}
          </Button>
        </div>
      </div>

      <!-- The pipeline's progress, while it runs or after it failed. -->
      <UploadStepper
        v-if="progress.current || progress.failedStep || progress.done"
        :current="progress.current"
        :failed-step="progress.failedStep"
        :error="progress.error"
        :done="progress.done"
        @retry="emit('retry')"
      />

      <p v-if="error" class="text-sm font-medium text-destructive">
        {{ error }}
      </p>

      <!-- The table. Scrolls sideways rather than squeezing at 360 px. -->
      <div v-if="documents.length" class="overflow-x-auto">
        <table class="w-full min-w-[40rem] border-collapse text-sm">
          <thead>
            <tr class="border-b text-left text-xs text-muted-foreground">
              <th scope="col" class="px-2 py-2 font-medium">File</th>
              <th scope="col" class="px-2 py-2 font-medium">Buyer</th>
              <th scope="col" class="px-2 py-2 font-medium">Status</th>
              <th scope="col" class="px-2 py-2 font-medium">Last run</th>
              <th scope="col" class="px-2 py-2 font-medium">Accuracy</th>
              <th scope="col" class="px-2 py-2 font-medium">Confirmed</th>
              <th scope="col" class="px-2 py-2 text-right font-medium">Actions</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="d in documents" :key="d.id" class="border-b last:border-0">
              <td class="max-w-56 truncate px-2 py-2 font-medium" :title="d.fileName">
                {{ d.fileName }}
              </td>
              <td class="px-2 py-2 text-muted-foreground">
                {{ d.buyer || '—' }}
              </td>
              <td class="px-2 py-2">
                <Badge :variant="chip(d).variant" class="whitespace-nowrap">
                  {{ chip(d).label }}
                </Badge>
              </td>
              <td class="whitespace-nowrap px-2 py-2 text-xs text-muted-foreground">
                {{ d.lastRunAt ? new Date(d.lastRunAt).toLocaleString() : '—' }}
              </td>
              <td class="px-2 py-2 tabular-nums text-muted-foreground">
                {{ accuracyLabel(d) }}
              </td>
              <td class="px-2 py-2">
                <Check v-if="d.confirmed" class="h-4 w-4 text-primary" />
                <span v-else class="text-muted-foreground">—</span>
              </td>
              <td class="px-2 py-2">
                <div class="flex items-center justify-end gap-1">
                  <Button
                    variant="outline"
                    size="sm"
                    class="h-7"
                    title="Review this extraction"
                    @click="emit('review', d)"
                  >
                    <Braces class="h-3.5 w-3.5" />
                    Review
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    class="h-7"
                    title="Ask about this PO"
                    @click="emit('chat', d)"
                  >
                    <MessageSquare class="h-3.5 w-3.5" />
                    Chat
                  </Button>
                  <Button
                    variant="ghost"
                    size="icon"
                    class="h-7 w-7 text-muted-foreground hover:text-destructive"
                    title="Delete"
                    @click="emit('delete', d.id)"
                  >
                    <Trash2 class="h-3.5 w-3.5" />
                  </Button>
                </div>
              </td>
            </tr>
          </tbody>
        </table>
      </div>

      <p v-else class="py-8 text-center text-sm text-muted-foreground">
        No purchase orders yet — drop your first one above.
      </p>
    </template>
  </div>
</template>
