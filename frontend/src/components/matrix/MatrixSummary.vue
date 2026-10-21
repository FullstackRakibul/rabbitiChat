<script setup>
/**
 * ম্যাট্রিক্স — সারসংক্ষেপ Summary, at the top of the Report page (docs/MATRIX_PROMPT.md §3.8).
 *
 * The model's 5–8 points, written from the report card in Bengali or English. The
 * server checks them before they arrive: numbers no card line gives are marked, a
 * point that cites no card line is marked, a label the card does not have reads
 * "unknown source". Each [C3] opens the rows of that card line. Written once per
 * language and kept; written again when the card is made again.
 */
import { computed, ref, watch } from 'vue'
import { AlertTriangle, Loader2, RefreshCw, Sparkles } from '@lucide/vue'
import { getMatrixSummary, writeMatrixSummary } from '@/services/index.js'
import { renderChecked } from '@/lib/matrix-text.js'
import { bnDigits, formatDate } from '@/lib/matrix-format.js'

const props = defineProps({
  datasetId: { type: Number, required: true },
})
const emit = defineEmits(['drill'])

const LANG_KEY = 'matrix.summary.lang'
const readLang = () => { try { return localStorage.getItem(LANG_KEY) || 'bn' } catch { return 'bn' } }
const lang = ref(readLang())
const summary = ref(null)
const writing = ref(false)
const error = ref('')

const html = computed(() => summary.value ? renderChecked(summary.value.text, {
  formulas: summary.value.formulas,
  unverified: summary.value.unverified,
  unknown: summary.value.unknownCitations,
  known: (l) => summary.value.labels.some((x) => x.label === l),
}) : '')
const problems = computed(() => {
  const s = summary.value
  if (!s) return []
  const list = []
  if (s.unverified.length) list.push(`${s.unverified.length} number(s) not on the card: ${s.unverified.join(', ')}`)
  if (s.uncitedPoints.length) list.push(`point ${s.uncitedPoints.join(', ')} cites no card line`)
  if (s.unknownCitations.length) list.push(`unknown source: ${s.unknownCitations.join(', ')}`)
  if (s.points < 5 || s.points > 8) list.push(`${s.points} points (5–8 asked)`)
  return list
})

async function load(force = false) {
  error.value = ''
  summary.value = null
  if (!force) {
    try {
      const s = await getMatrixSummary(props.datasetId, lang.value)
      if (!s.stale) { summary.value = s; return }
    } catch (e) {
      if (e.status !== 404) { error.value = e.message; return }
    }
  }
  writing.value = true
  try {
    summary.value = await writeMatrixSummary(props.datasetId, lang.value)
  } catch (e) {
    error.value = e.message
  } finally {
    writing.value = false
  }
}

function setLang(l) {
  lang.value = l
  try { localStorage.setItem(LANG_KEY, l) } catch { /* the choice just isn't remembered */ }
}

/** A chip: its card line's rows in the viewer. */
function onClick(e) {
  const label = e.target.closest?.('[data-cite]')?.getAttribute('data-cite')
  const line = summary.value?.labels.find((x) => x.label === label)
  if (line?.cite) emit('drill', { datasetId: props.datasetId, cite: line.cite })
}

watch(() => [props.datasetId, lang.value], () => load(), { immediate: true })
</script>

<template>
  <section class="rounded-lg border bg-card" data-summary :data-summary-lang="lang">
    <header class="flex flex-wrap items-center gap-2 border-b px-3 py-2">
      <Sparkles class="size-4 text-muted-foreground" aria-hidden="true" />
      <h4 class="flex-1 text-sm font-semibold">সারসংক্ষেপ <span class="font-normal text-muted-foreground">Summary</span></h4>
      <div class="inline-flex rounded-md border p-0.5 text-xs" role="group" aria-label="Language">
        <button v-for="[l, name] in [['bn', 'বাংলা'], ['en', 'English']]" :key="l" type="button" :data-summary-lang-button="l"
          class="rounded px-2 py-0.5" :class="lang === l ? 'bg-secondary font-medium text-secondary-foreground' : 'text-muted-foreground hover:text-foreground'"
          :aria-pressed="lang === l" @click="setLang(l)">{{ name }}</button>
      </div>
      <button type="button" class="rounded p-1 text-muted-foreground hover:bg-accent hover:text-foreground disabled:opacity-50" :disabled="writing"
        title="Write again" aria-label="Write the summary again" data-summary-rewrite @click="load(true)">
        <RefreshCw class="size-3.5" />
      </button>
    </header>

    <div class="px-3 py-2.5 text-sm">
      <p v-if="writing" class="flex items-center gap-2 text-muted-foreground" role="status" data-summary-writing>
        <Loader2 class="size-4 animate-spin" /> {{ lang === 'bn' ? 'রিপোর্ট কার্ড থেকে সারসংক্ষেপ লেখা হচ্ছে…' : 'Writing the summary from the report card…' }}
      </p>
      <p v-else-if="error" class="text-destructive">{{ error }}</p>
      <template v-else-if="summary">
        <!-- eslint-disable-next-line vue/no-v-html -- sanitized by renderMarkdown; chips are ours -->
        <div class="md leading-relaxed" data-summary-text v-html="html" @click="onClick" />
        <p v-if="problems.length" class="mt-2 flex items-start gap-1.5 text-xs text-destructive" data-summary-problems>
          <AlertTriangle class="mt-0.5 size-3.5 shrink-0" aria-hidden="true" /> {{ problems.join(' · ') }}
        </p>
        <p class="mt-1.5 text-[11px] text-muted-foreground">
          {{ summary.model }} · {{ lang === 'bn' ? bnDigits(summary.points) : summary.points }} points · {{ formatDate(summary.createdAtUtc?.slice(0, 10)) }}
          · {{ lang === 'bn' ? 'প্রতিটি সংখ্যা রিপোর্ট কার্ডের সাথে মিলিয়ে দেখা হয়েছে' : 'every number checked against the report card' }}
        </p>
      </template>
    </div>
  </section>
</template>
