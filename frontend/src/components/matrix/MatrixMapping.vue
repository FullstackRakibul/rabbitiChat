<script setup>
/**
 * ম্যাট্রিক্স — an upload's profile, and the mapping a person confirms before import
 * (docs/MATRIX_PROMPT.md §3.3–§3.5, §3.11).
 *
 * Per table: range, header row, rows, report kind and date, quirks, and whether
 * it looks like a copy of another sheet. Per column: its type and quirks, the
 * suggested field and where the suggestion came from (synonym, saved template,
 * or the model — which waits for confirmation), and its sensitivity. Sensitive
 * columns are marked and will not be imported. Everything here comes from the
 * server's profile and field catalog.
 */
import { computed, reactive, ref, watch } from 'vue'
import { AlertTriangle, Bot, Check, Copy, Loader2, Lock, Sparkles } from '@lucide/vue'
import { importMatrixUpload } from '@/services/index.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { formatDate, lakh } from '@/lib/matrix-format.js'

const props = defineProps({
  upload: { type: Object, required: true },
  catalog: { type: Array, default: () => [] },
})
const emit = defineEmits(['imported', 'close'])

const KINDS = ['manpower', 'master', 'attendance-daily', 'designation-lookup', 'separation-list', 'custom']

const tables = computed(() => props.upload.profile.sheets.flatMap((s) => s.tables))
const mappable = computed(() => props.catalog.filter((f) => !f.derived))
const groups = computed(() => [...new Set(mappable.value.map((f) => f.group))])

/** The person's choices: per table id → { selected, kind, reportDate, fields: { index → key | 'keep' | '' } }. */
const draft = reactive({})
function reset() {
  for (const t of tables.value) {
    draft[t.id] = {
      selected: t.selected,
      kind: t.kind,
      reportDate: t.reportDate ?? props.upload.reportDate ?? '',
      fields: Object.fromEntries(t.columns.map((c) => [c.index, c.sensitivity === 'sensitive' ? '' : (c.suggestion?.field ?? 'keep')])),
    }
  }
}
watch(() => props.upload.id, reset, { immediate: true })

const busy = ref(false)
const error = ref('')

const modelPending = computed(() => tables.value.reduce((n, t) =>
  n + t.columns.filter((c) => c.suggestion?.source === 'model' && draft[t.id]?.fields[c.index] === c.suggestion.field).length, 0))

async function confirmAndImport() {
  busy.value = true
  error.value = ''
  try {
    const asks = tables.value.filter((t) => draft[t.id].selected).map((t) => ({
      table: t.id,
      kind: draft[t.id].kind,
      reportDate: /^\d{4}-\d{2}-\d{2}$/.test(draft[t.id].reportDate) ? draft[t.id].reportDate : undefined,
      mapping: t.columns.filter((c) => c.sensitivity !== 'sensitive').map((c) => {
        const f = draft[t.id].fields[c.index]
        return { index: c.index, field: f === '' ? null : f, sensitivity: f === 'keep' ? 'personal' : undefined }
      }),
    }))
    if (!asks.length) { error.value = 'Choose at least one table to import.'; return }
    const res = await importMatrixUpload(props.upload.id, asks)
    emit('imported', res)
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = false
  }
}

const sensClass = (s) => ({
  public: 'border-border text-muted-foreground',
  personal: 'border-primary/40 text-foreground',
  salary: 'border-primary text-primary',
  sensitive: 'border-destructive text-destructive',
}[s] ?? '')
</script>

<template>
  <section class="flex min-w-0 flex-col gap-3" data-matrix-mapping>
    <header class="flex flex-wrap items-center gap-2 rounded-lg border bg-card p-3">
      <div class="min-w-0 basis-full sm:basis-0 sm:flex-1">
        <h3 class="truncate font-semibold">{{ upload.fileName }}</h3>
        <p class="text-xs text-muted-foreground">
          {{ upload.profile.sheets.length }} শিট sheets · {{ tables.length }} টেবিল tables
          <span v-if="upload.reportDate"> · report date {{ formatDate(upload.reportDate) }}</span>
        </p>
      </div>
      <Button size="sm" variant="ghost" @click="emit('close')">Cancel</Button>
      <Button size="sm" :disabled="busy" data-confirm-import @click="confirmAndImport">
        <Loader2 v-if="busy" class="size-4 animate-spin" /><Check v-else class="size-4" />
        নিশ্চিত করে ইমপোর্ট <span class="text-xs opacity-80">Confirm and import</span>
      </Button>
      <p v-if="modelPending" class="basis-full text-xs text-muted-foreground">
        <Bot class="inline size-3.5" /> {{ modelPending }} column(s) use the model's suggestion — check them before you confirm.
      </p>
      <p v-if="error" class="basis-full text-sm text-destructive">{{ error }}</p>
    </header>

    <article v-for="t in tables" :key="t.id" class="rounded-lg border bg-card" :data-table="t.id">
      <div class="flex flex-wrap items-center gap-2 border-b p-3">
        <label class="flex items-center gap-2">
          <input v-model="draft[t.id].selected" type="checkbox" class="accent-primary" :data-select-table="t.id" />
          <span class="font-medium">{{ t.sheet.trim() || t.sheet }}</span>
        </label>
        <span class="text-xs text-muted-foreground">{{ t.range }} · header row {{ t.headerRows.join('+') }} · {{ lakh(t.dataRows) }} rows</span>
        <Badge v-if="t.template" variant="secondary" class="gap-1"><Sparkles class="size-3" /> template {{ t.template.name }} v{{ t.template.version }}</Badge>
        <Badge v-if="t.copyOf" variant="outline" class="gap-1 text-destructive"><Copy class="size-3" />
          {{ t.copyOf.near ? 'near-copy' : 'copy' }} of '{{ t.copyOf.sheet }}' · {{ t.copyOf.overlapPct }}%<span v-if="t.copyOf.extraColumns"> · +{{ t.copyOf.extraColumns }} columns</span>
        </Badge>
        <div class="ml-auto flex flex-wrap items-center gap-2">
          <select v-model="draft[t.id].kind" class="h-8 rounded-md border border-input bg-background px-2 text-xs" aria-label="Report kind">
            <option v-for="k in KINDS" :key="k" :value="k">{{ k }}</option>
          </select>
          <input v-model="draft[t.id].reportDate" type="date" class="h-8 rounded-md border border-input bg-background px-2 text-xs" aria-label="Report date" />
        </div>
      </div>
      <p v-if="t.details.length" class="border-b px-3 py-2 text-xs text-muted-foreground">{{ t.details.join(' · ') }}</p>
      <p v-if="t.quirks.length" class="flex flex-wrap gap-1 border-b px-3 py-2">
        <Badge v-for="q in t.quirks" :key="q" variant="outline" class="font-normal">{{ q }}</Badge>
      </p>

      <div class="overflow-x-auto">
        <table class="w-full min-w-160 text-sm">
          <thead class="bg-muted/50 text-xs text-muted-foreground">
            <tr>
              <th class="px-3 py-2 text-left font-medium">কলাম Column</th>
              <th class="px-3 py-2 text-left font-medium">ধরন Type</th>
              <th class="px-3 py-2 text-left font-medium">ক্ষেত্র Field</th>
              <th class="px-3 py-2 text-left font-medium">গোপনীয়তা Sensitivity</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="c in t.columns" :key="c.index" class="border-t align-top" :data-column="c.header">
              <td class="px-3 py-2">
                <div class="font-medium">{{ c.header || `(${c.letter})` }}</div>
                <div class="text-[11px] text-muted-foreground">{{ c.letter }} · {{ c.distinct }} distinct<span v-if="c.nulls"> · {{ c.nulls }} empty</span></div>
                <div v-if="c.quirks.length" class="mt-1 flex flex-wrap gap-1">
                  <span v-for="q in c.quirks" :key="q" class="rounded bg-muted px-1.5 py-0.5 text-[10px] text-muted-foreground">{{ q }}</span>
                </div>
              </td>
              <td class="px-3 py-2 text-xs">{{ c.type }}<span v-if="c.unit"> ({{ c.unit }})</span></td>
              <td class="px-3 py-2">
                <div v-if="c.sensitivity === 'sensitive'" class="flex items-center gap-1 text-xs text-destructive">
                  <Lock class="size-3.5" /> not imported ({{ c.sensitiveCategory }})
                </div>
                <template v-else>
                  <select v-model="draft[t.id].fields[c.index]" class="h-8 w-full max-w-60 rounded-md border border-input bg-background px-2 text-xs" :data-field-select="c.index">
                    <option value="">— leave out —</option>
                    <option value="keep">keep, unmapped (personal)</option>
                    <optgroup v-for="g in groups" :key="g" :label="g">
                      <option v-for="f in mappable.filter((x) => x.group === g)" :key="f.key" :value="f.key">{{ f.bn }} · {{ f.en }}</option>
                    </optgroup>
                  </select>
                  <div class="mt-1 text-[11px] text-muted-foreground">
                    <span v-if="c.suggestion?.source === 'model'" class="inline-flex items-center gap-1 text-primary">
                      <AlertTriangle class="size-3" /> model's suggestion — confirm
                    </span>
                    <span v-else-if="c.suggestion">{{ c.suggestion.source }}</span>
                    <span v-else>no match</span>
                  </div>
                </template>
              </td>
              <td class="px-3 py-2">
                <span :class="['inline-block rounded-full border px-2 py-0.5 text-[11px]', sensClass(c.sensitivity)]">{{ c.sensitivity }}</span>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </article>
  </section>
</template>
