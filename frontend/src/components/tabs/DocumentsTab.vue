<script setup>
import { computed, ref } from 'vue'
import { Paperclip, Trash2 } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Badge } from '@/components/ui/badge'
import {
  Card, CardContent, CardDescription, CardHeader, CardTitle,
} from '@/components/ui/card'

const props = defineProps({
  documents: { type: Array, default: () => [] },
  file: { type: Object, default: null },
  buyer: { type: String, default: '' },
  uploading: { type: Boolean, default: false },
  error: { type: String, default: '' },
  notice: { type: String, default: '' },
})

const emit = defineEmits(['update:buyer', 'file-picked', 'upload', 'delete'])

const buyerValue = computed({
  get: () => props.buyer,
  set: (v) => emit('update:buyer', v),
})

const fileInput = ref(null)

function onPicked(e) {
  emit('file-picked', e.target.files?.[0] ?? null)
}

defineExpose({ reset: () => { if (fileInput.value) fileInput.value.value = '' } })
</script>

<template>
  <div class="h-full overflow-y-auto p-4 sm:p-6">
    <Card class="mx-auto w-full max-w-3xl">
      <CardHeader>
        <CardTitle>Documents</CardTitle>
        <CardDescription>
          Upload a PO / order file (PDF, XLSX, TXT, CSV). It's parsed, chunked and
          embedded for retrieval.
        </CardDescription>
      </CardHeader>

      <CardContent class="flex flex-col gap-4">
        <form class="flex flex-wrap items-center gap-2" @submit.prevent="emit('upload')">
          <input
            ref="fileInput"
            type="file"
            accept=".pdf,.xlsx,.txt,.csv"
            class="hidden"
            @change="onPicked"
          >
          <Button type="button" variant="outline" class="flex-1 justify-start" @click="fileInput?.click()">
            <Paperclip />
            <span class="truncate">{{ file ? file.name : 'Choose a file…' }}</span>
          </Button>
          <Input v-model="buyerValue" placeholder="Buyer (optional)" class="w-full sm:w-48" />
          <Button type="submit" :disabled="uploading || !file">
            {{ uploading ? 'Ingesting…' : 'Upload' }}
          </Button>
        </form>

        <p v-if="notice" class="text-sm text-muted-foreground">
          {{ notice }}
        </p>
        <p v-if="error" class="text-sm font-medium text-destructive">
          {{ error }}
        </p>

        <ul v-if="documents.length" class="flex flex-col gap-2">
          <li
            v-for="d in documents"
            :key="d.id"
            class="flex flex-wrap items-center gap-2 rounded-lg border p-3 text-sm"
          >
            <Badge
              :variant="d.status === 'failed' ? 'destructive' : 'secondary'"
              class="shrink-0 capitalize"
            >
              {{ d.status }}
            </Badge>
            <span class="min-w-0 flex-1 truncate font-medium" :title="d.fileName">
              {{ d.fileName }}
            </span>
            <Badge v-if="d.buyer" variant="outline">
              {{ d.buyer }}
            </Badge>
            <span class="shrink-0 text-xs text-muted-foreground">{{ d.chunks }} chunks</span>
            <Button
              variant="ghost"
              size="icon"
              class="h-7 w-7 shrink-0 text-muted-foreground hover:text-destructive"
              title="Delete"
              @click="emit('delete', d.id)"
            >
              <Trash2 class="h-3.5 w-3.5" />
            </Button>
          </li>
        </ul>

        <p v-else class="py-6 text-center text-sm text-muted-foreground">
          No documents yet — upload your first PO above.
        </p>
      </CardContent>
    </Card>
  </div>
</template>
