<script setup>
/**
 * Opens a cited library item at the cited place (docs/HR_MODULE_PROMPT.md §3.4).
 *
 * A citation is either an excerpt's own SourceRef, which carries its lines
 * ("ProcessFinalSettelment.sql §Service Benefit L653-735"), or a section of an
 * always item ("HR_SETTLEMENT_RULES §5"), which is found by its heading. The
 * item's text is shown with line numbers, the cited lines marked and scrolled to.
 */
import { computed, nextTick, ref, watch } from 'vue'
import { Loader2 } from '@lucide/vue'
import { getHrItem } from '@/services/hr.service.js'
import {
  Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle,
} from '@/components/ui/dialog'

const props = defineProps({
  open: { type: Boolean, default: false },
  /** { itemId, label, lineFrom, lineTo, section } — section is the part after § when no lines are known. */
  target: { type: Object, default: null },
})
const emit = defineEmits(['update:open'])

const item = ref(null)
const error = ref('')
const loading = ref(false)
const body = ref(null)

const lines = computed(() => (item.value?.content ?? '').split('\n'))

/** The 1-based [from, to] to mark: the given lines, or the cited section's heading to the next heading of its level. */
const range = computed(() => {
  const t = props.target
  if (!t) return null
  if (t.lineFrom && t.lineTo) return [t.lineFrom, t.lineTo]
  if (!t.section) return null
  const want = String(t.section).trim().toLowerCase()
  const headings = lines.value
    .map((l, i) => ({ i, m: /^(#{1,6})\s+(.+)$/.exec(l) }))
    .filter((h) => h.m)
  const start = headings.find((h) => {
    const title = h.m[2].trim().toLowerCase()
    return title === want || title.startsWith(`${want}.`) || title.startsWith(`${want} `) || title.startsWith(want)
  })
  if (!start) return null
  const level = start.m[1].length
  const next = headings.find((h) => h.i > start.i && h.m[1].length <= level)
  return [start.i + 1, next ? next.i : lines.value.length]
})

async function load() {
  if (!props.target?.itemId) return
  loading.value = true
  error.value = ''
  item.value = null
  try {
    item.value = await getHrItem(props.target.itemId)
  } catch (e) {
    error.value = e.message
  } finally {
    loading.value = false
  }
  // The text renders only once loading is off; then bring the cited lines into view.
  await nextTick()
  requestAnimationFrame(() => body.value?.querySelector('[data-marked="true"]')?.scrollIntoView({ block: 'center' }))
}

watch(() => [props.open, props.target], () => { if (props.open) load() }, { immediate: true })

const marked = (n) => range.value && n >= range.value[0] && n <= range.value[1]
</script>

<template>
  <Dialog :open="open" @update:open="(v) => emit('update:open', v)">
    <DialogContent class="flex max-h-[85vh] w-[calc(100vw-2rem)] max-w-4xl flex-col gap-3 p-4 sm:p-6">
      <DialogHeader>
        <DialogTitle class="truncate text-base">{{ target?.label }}</DialogTitle>
        <DialogDescription class="text-xs">
          {{ item ? `${item.name} · v${item.version}` : '' }}
          <template v-if="item?.approvedBy"> · অনুমোদন / approved: {{ item.approvedBy }}</template>
          <template v-if="range"> · L{{ range[0] }}–{{ range[1] }}</template>
        </DialogDescription>
      </DialogHeader>

      <p v-if="loading" class="flex items-center gap-2 text-sm text-muted-foreground">
        <Loader2 class="h-3.5 w-3.5 animate-spin" /> Loading…
      </p>
      <p v-else-if="error" class="text-sm text-destructive">{{ error }}</p>

      <div v-else-if="item" ref="body" class="min-h-0 flex-1 overflow-auto rounded-md border bg-muted/30 font-mono text-[11px] leading-snug">
        <div
          v-for="(line, i) in lines"
          :key="i"
          :data-marked="marked(i + 1) ? 'true' : 'false'"
          :class="['flex gap-3 px-2', marked(i + 1) ? 'bg-primary/10' : '']"
        >
          <span class="w-10 shrink-0 select-none text-right text-muted-foreground">{{ i + 1 }}</span>
          <span class="whitespace-pre-wrap break-all">{{ line || ' ' }}</span>
        </div>
      </div>
    </DialogContent>
  </Dialog>
</template>
