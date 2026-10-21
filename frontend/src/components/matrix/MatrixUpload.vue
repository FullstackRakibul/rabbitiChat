<script setup>
/**
 * ম্যাট্রিক্স — upload: drop one or many .xlsx/.csv files; each is profiled on
 * the server (its file is held in memory until import, never stored).
 */
import { ref } from 'vue'
import { Loader2, Upload } from '@lucide/vue'
import { uploadMatrixFiles } from '@/services/index.js'
import { cn } from '@/lib/utils'

defineProps({ canUpload: { type: Boolean, default: false } })
const emit = defineEmits(['uploaded'])

const over = ref(false)
const busy = ref(false)
const error = ref('')
const input = ref(null)

async function send(files) {
  const list = [...files].filter((f) => /\.(xlsx|xlsm|csv)$/i.test(f.name))
  if (!list.length) { error.value = 'Choose .xlsx or .csv files.'; return }
  busy.value = true
  error.value = ''
  try {
    const res = await uploadMatrixFiles(list)
    emit('uploaded', res.uploads)
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = false
    if (input.value) input.value.value = ''
  }
}

function onDrop(e) {
  over.value = false
  send(e.dataTransfer?.files ?? [])
}
</script>

<template>
  <section class="rounded-lg border bg-card p-3">
    <label
      v-if="canUpload"
      :class="cn(
        'flex cursor-pointer flex-col items-center justify-center gap-2 rounded-md border-2 border-dashed p-6 text-center transition-colors',
        over ? 'border-primary bg-primary/5' : 'border-border hover:bg-accent',
      )"
      @dragover.prevent="over = true"
      @dragleave="over = false"
      @drop.prevent="onDrop"
    >
      <Loader2 v-if="busy" class="size-6 animate-spin text-muted-foreground" aria-hidden="true" />
      <Upload v-else class="size-6 text-muted-foreground" aria-hidden="true" />
      <span class="text-sm font-medium">ফাইল এখানে ছাড়ুন <span class="font-normal text-muted-foreground">Drop Excel or CSV files</span></span>
      <span class="text-xs text-muted-foreground">Manpower lists, master files, daily attendance, designation lists · up to 50 MB each</span>
      <input ref="input" type="file" accept=".xlsx,.xlsm,.csv" multiple class="sr-only" data-matrix-upload @change="send($event.target.files)" />
    </label>
    <p v-else class="text-sm text-muted-foreground">Uploading needs the “Upload and import files” permission.</p>
    <p v-if="error" class="mt-2 text-sm text-destructive">{{ error }}</p>
  </section>
</template>
