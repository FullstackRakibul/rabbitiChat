<script setup>
/**
 * প্রসিডিউর (Procedures) — the Procedure Analyzer (docs/HR_PROCEDURE_ANALYZER_PROMPT.md §3.7).
 *
 * The list of uploaded procedures and functions: object, kind, version, who
 * uploaded it and when, the analysis status and the open questions. DevOps and
 * SystemAdmin upload a .sql file (≤ 2 MB) — it is read as text and scanned,
 * never run. Opening one shows HrProcedurePage (Facts, Questions, Setup list);
 * রেজিস্ট্রি (Registry) opens the lookup registry with its change log.
 *
 * What a person may do is the server's decision (canUpload, canAnswer,
 * canEditRegistry). Theme tokens only; usable at 360 px.
 */
import { onMounted, ref } from 'vue'
import { BookKey, FileCode2, Loader2, Upload } from '@lucide/vue'
import { listProcedures, uploadProcedure } from '@/services/hr.service.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import HrProcedurePage from '@/components/hr/HrProcedurePage.vue'
import HrLookups from '@/components/hr/HrLookups.vue'
import { toDate } from '@/lib/time'

const procedures = ref([])
const can = ref({ upload: false, answer: false, editRegistry: false })
const loading = ref(true)
const error = ref('')
const notice = ref('')

/** null: the list; 'registry'; or a procedure id. */
const open = ref(null)

const fileInput = ref(null)
const note = ref('')
const uploading = ref(false)

async function load() {
  error.value = ''
  try {
    const res = await listProcedures()
    procedures.value = res.procedures ?? []
    can.value = { upload: !!res.canUpload, answer: !!res.canAnswer, editRegistry: !!res.canEditRegistry }
  } catch (e) {
    error.value = e.message || 'The procedures could not be loaded.'
  } finally {
    loading.value = false
  }
}

onMounted(load)

function fmt(value) {
  const d = toDate(value)
  return d ? d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : ''
}

async function onFile(event) {
  const file = event.target.files?.[0]
  event.target.value = ''
  if (!file) return
  error.value = ''
  notice.value = ''
  if (!file.name.toLowerCase().endsWith('.sql')) { error.value = 'শুধু .sql ফাইল · Only .sql files can be uploaded here.'; return }
  if (file.size > 2 * 1024 * 1024) { error.value = 'ফাইলটি ২ MB-এর বড় · The file is larger than 2 MB.'; return }
  uploading.value = true
  try {
    const res = await uploadProcedure(file, note.value.trim())
    notice.value = `${res.procedure.objectName} v${res.version.version} — সংরক্ষিত · saved`
    note.value = ''
    await load()
  } catch (e) {
    error.value = e.message || 'The upload failed.'
  } finally {
    uploading.value = false
  }
}

function back() {
  open.value = null
  load()
}
</script>

<template>
  <HrProcedurePage v-if="typeof open === 'number'" :key="open" :procedure-id="open" :can-answer="can.answer" @back="back" />

  <HrLookups v-else-if="open === 'registry'" :can-edit="can.editRegistry" @back="back" />

  <div v-else class="mx-auto w-full max-w-5xl p-4 sm:p-6" data-testid="procedures-list">
    <div class="flex flex-wrap items-start justify-between gap-3">
      <div class="min-w-0">
        <h2 class="flex items-center gap-2 text-lg font-semibold">
          <FileCode2 class="size-5 shrink-0 text-muted-foreground" aria-hidden="true" />
          প্রসিডিউর <span class="text-sm font-normal text-muted-foreground">Procedures</span>
        </h2>
        <p class="mt-1 text-sm text-muted-foreground">
          স্টোরড প্রসিডিউর থেকে তথ্য, প্রশ্ন আর সেটআপ তালিকা। SQL শুধু পড়া হয়, চালানো হয় না।
          <span class="text-xs">Facts, questions and a setup list from stored procedures. The SQL is read, never run.</span>
        </p>
      </div>
      <Button size="sm" variant="outline" data-testid="procedures-registry" @click="open = 'registry'">
        <BookKey /> রেজিস্ট্রি <span class="text-xs opacity-70">Registry</span>
      </Button>
    </div>

    <!-- Upload: DevOps and SystemAdmin -->
    <div v-if="can.upload" class="mt-4 flex flex-col gap-2 rounded-lg border bg-card p-3 sm:flex-row sm:items-center">
      <Input v-model="note" class="h-9 sm:flex-1" placeholder="নোট (ঐচ্ছিক) · Note (optional)" aria-label="Upload note" />
      <input ref="fileInput" type="file" accept=".sql" class="hidden" data-testid="procedures-file" @change="onFile" />
      <Button size="sm" :disabled="uploading" data-testid="procedures-upload" @click="fileInput?.click()">
        <Loader2 v-if="uploading" class="animate-spin" />
        <Upload v-else />
        .sql আপলোড <span class="text-xs opacity-80">Upload (≤ 2 MB)</span>
      </Button>
    </div>

    <p v-if="notice" class="mt-3 text-sm text-muted-foreground" role="status">{{ notice }}</p>
    <p v-if="error" class="mt-3 text-sm text-destructive" role="alert">{{ error }}</p>

    <div v-if="loading" class="mt-8 flex justify-center text-muted-foreground">
      <Loader2 class="size-5 animate-spin" aria-label="Loading" />
    </div>

    <div v-else-if="!procedures.length" class="mt-10 rounded-lg border border-dashed p-8 text-center text-sm text-muted-foreground">
      এখনো কোনো প্রসিডিউর আপলোড হয়নি। <span class="block text-xs">No procedures uploaded yet.</span>
    </div>

    <ul v-else class="mt-4 grid gap-2">
      <li v-for="p in procedures" :key="p.id">
        <button
          type="button"
          class="flex w-full flex-col gap-1.5 rounded-lg border bg-card px-4 py-3 text-left transition-colors hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          :data-procedure="p.objectName"
          @click="open = p.id"
        >
          <span class="flex min-w-0 flex-wrap items-start justify-between gap-2">
            <span class="min-w-0 break-all font-mono text-sm font-medium">{{ p.objectName }}</span>
            <span class="flex shrink-0 flex-wrap gap-1">
              <Badge variant="outline" class="font-normal">{{ p.kind === 'function' ? 'ফাংশন · function' : 'প্রসিডিউর · procedure' }}</Badge>
              <Badge variant="secondary" class="font-normal">v{{ p.currentVersion }}</Badge>
              <Badge :variant="p.openQuestions ? 'default' : 'outline'" class="font-normal" data-open-questions>
                {{ p.openQuestions }} খোলা প্রশ্ন <span class="ml-1 opacity-75">open</span>
              </Badge>
            </span>
          </span>
          <span class="text-xs text-muted-foreground">
            {{ p.uploadedBy ?? '—' }} · {{ fmt(p.uploadedAtUtc) }} ·
            বিশ্লেষণ: তথ্য প্রস্তুত <span>(analysis: facts ready; rules in Phase A2)</span>
          </span>
        </button>
      </li>
    </ul>
  </div>
</template>
