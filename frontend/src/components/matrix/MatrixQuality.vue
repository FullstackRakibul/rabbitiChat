<script setup>
/**
 * ম্যাট্রিক্স — a dataset's quality report (docs/MATRIX_PROMPT.md §3.5).
 *
 * Each issue: what was found, how many, sample cells, and how to fix it. "Show"
 * opens the viewer on those rows with the cells highlighted. "Fix" (only with
 * action:matrix.fix, which the server checks again) writes a corrected column,
 * keeps the raw values in _raw_<column>, and logs the change.
 */
import { onMounted, ref, watch } from 'vue'
import { AlertTriangle, Check, Eye, Info, Loader2, Wrench, XCircle } from '@lucide/vue'
import { fixMatrixIssue, getMatrixQuality } from '@/services/index.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { lakh } from '@/lib/matrix-format.js'

const props = defineProps({
  dataset: { type: Object, required: true },
  canFix: { type: Boolean, default: false },
})
const emit = defineEmits(['show', 'close', 'fixed'])

const issues = ref([])
const loading = ref(true)
const error = ref('')
const fixing = ref(0)
const notes = ref({})
const done = ref({})

/** The checks, as people call them. Labels only — which checks run is the server's. */
const LABELS = {
  dup_emp_id: ['একই আইডি একাধিকবার', 'Duplicate employee ID'],
  cross_sheet_person: ['একই কর্মী দুই শিটে', 'Same person in two sheets'],
  id_leading_zeros: ['শুরুর শূন্য ছাড়া আইডি', 'IDs with and without leading zeros'],
  null_text: ['“NULL” লেখা ঘর', '"NULL" text'],
  mixed_date_formats: ['মিশ্র তারিখ বিন্যাস', 'Mixed date formats'],
  separation_before_joining: ['যোগদানের আগে বিচ্ছেদ', 'Separation before joining'],
  designation_not_in_lookup: ['তালিকায় নেই এমন পদবি', 'Designation not in the lookup'],
  trailing_spaces: ['বাড়তি ফাঁকা স্থান', 'Leading or trailing spaces'],
  near_duplicate_values: ['প্রায় একই মান', 'Near-duplicate values'],
  numbers_as_dates: ['তারিখ বিন্যাসে সংখ্যা', 'Numbers formatted as dates'],
  empty_required: ['আবশ্যক ঘর খালি', 'Required field empty'],
  formula_mismatch: ['সূত্রের সাথে অমিল', 'Formula result disagrees'],
  worker_designation_no_grade: ['গ্রেড ছাড়া শ্রমিক পদবি', 'Worker designation without a grade'],
}

async function load() {
  loading.value = true
  error.value = ''
  try {
    issues.value = (await getMatrixQuality(props.dataset.id)).issues
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
}

async function fix(issue) {
  fixing.value = issue.id
  error.value = ''
  try {
    const r = await fixMatrixIssue(issue.id, notes.value[issue.id] || null)
    done.value = { ...done.value, [issue.id]: `${lakh(r.rowsChanged)} rows corrected · raw kept in ${r.rawColumn}` }
    await load()
    emit('fixed', r)
  } catch (e) {
    error.value = e.message
  } finally {
    fixing.value = 0
  }
}

const severityIcon = (s) => (s === 'error' ? XCircle : s === 'warning' ? AlertTriangle : Info)
const severityClass = (s) => (s === 'error' ? 'text-destructive' : s === 'warning' ? 'text-primary' : 'text-muted-foreground')

watch(() => props.dataset.id, load)
onMounted(load)
</script>

<template>
  <section class="flex min-w-0 flex-col rounded-lg border bg-card" data-matrix-quality>
    <header class="flex flex-wrap items-center gap-2 border-b p-3">
      <div class="min-w-0 basis-full sm:basis-0 sm:flex-1">
        <h3 class="truncate font-semibold">গুণমান <span class="text-sm font-normal text-muted-foreground">Quality</span> · {{ dataset.name }}</h3>
        <p class="text-xs text-muted-foreground">{{ issues.filter((i) => i.state === 'open').length }} open of {{ issues.length }}</p>
      </div>
      <Button size="sm" variant="outline" @click="emit('close')">Close</Button>
    </header>
    <p v-if="error" class="border-b bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>
    <div v-if="loading" class="flex items-center gap-2 p-6 text-sm text-muted-foreground"><Loader2 class="size-4 animate-spin" /> Checking…</div>
    <p v-else-if="!issues.length" class="p-6 text-sm text-muted-foreground">No issues found.</p>

    <ul v-else class="divide-y">
      <li v-for="i in issues" :key="i.id" class="flex flex-col gap-2 p-3 sm:flex-row sm:items-start" :data-issue="i.check" :data-state="i.state">
        <component :is="severityIcon(i.severity)" :class="['mt-0.5 size-4 shrink-0', severityClass(i.severity)]" aria-hidden="true" />
        <div class="min-w-0 flex-1">
          <div class="flex flex-wrap items-baseline gap-x-2">
            <span class="font-medium">{{ LABELS[i.check]?.[0] ?? i.check }}</span>
            <span class="text-xs text-muted-foreground">{{ LABELS[i.check]?.[1] ?? i.check }}</span>
            <Badge variant="secondary" class="tabular-nums">{{ lakh(i.count) }}</Badge>
            <Badge v-if="i.field" variant="outline">{{ i.field }}</Badge>
            <Badge v-if="i.state !== 'open'" variant="outline" class="gap-1"><Check class="size-3" /> {{ i.state }}</Badge>
          </div>
          <p class="mt-1 text-sm">{{ i.detail }}</p>
          <p class="mt-0.5 text-xs text-muted-foreground">Fix: {{ i.suggestedFix }}</p>
          <p v-if="i.samples.length" class="mt-1 break-words font-mono text-[11px] text-muted-foreground">
            {{ i.samples.slice(0, 8).join(' · ') }}<span v-if="i.samples.length > 8"> …</span>
          </p>
          <p v-if="done[i.id]" class="mt-1 text-xs text-primary">{{ done[i.id] }}</p>
        </div>
        <div class="flex shrink-0 flex-wrap items-center gap-2">
          <Button v-if="i.highlightRows.length" size="sm" variant="ghost" data-show-issue @click="emit('show', i)">
            <Eye class="size-4" /> দেখুন <span class="text-xs opacity-70">Show</span>
          </Button>
          <template v-if="canFix && i.fixKind && i.state === 'open'">
            <Input v-model="notes[i.id]" class="h-8 w-40" placeholder="Note (optional)" />
            <Button size="sm" :disabled="fixing === i.id" :data-fix-issue="i.id" @click="fix(i)">
              <Loader2 v-if="fixing === i.id" class="size-4 animate-spin" /><Wrench v-else class="size-4" />
              ঠিক করুন <span class="text-xs opacity-80">Fix</span>
            </Button>
          </template>
        </div>
      </li>
    </ul>
  </section>
</template>
