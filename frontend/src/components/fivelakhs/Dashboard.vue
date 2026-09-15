<script setup>
/**
 * Stats and recent activity for the 5Lakhs tab.
 *
 * The history is read from localStorage only — see useCheckHistory. Every
 * number on this page is derived from that list; the "100% success rate" is a
 * joke about a generator that cannot fail, and is labelled as one rather than
 * presented as a metric.
 */
import { computed, onMounted } from 'vue'
import { Download, FileCheck2, Sparkles, Trash2, Wallet } from '@lucide/vue'
import { useCheckHistory } from '@/composables/useCheckHistory.js'
import { useCountUp } from '@/composables/useAnimation.js'
import { FIXED_AMOUNT, formatCurrency, NOVELTY_NOTICE } from '@/utils/pdfGenerator.js'
import { Button } from '@/components/ui/button'
import { Badge } from '@/components/ui/badge'

const emit = defineEmits(['regenerate'])

const { entries, count, remove, refresh } = useCheckHistory()

const generated = useCountUp(0, 600)

onMounted(() => {
  refresh()
  generated.run(count.value)
})

/** The headline figure never changes — that is the whole premise. */
const fixedAmountLabel = computed(() => formatCurrency(FIXED_AMOUNT, 'BDT'))

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
  'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** "2 minutes ago" for the recent ones, an absolute date once it stops helping. */
function relativeTime(iso) {
  const then = Date.parse(iso)
  if (Number.isNaN(then)) return ''

  const seconds = Math.round((Date.now() - then) / 1000)
  if (seconds < 45) return 'just now'
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`
  if (seconds < 86400) return `${Math.round(seconds / 3600)} h ago`

  const d = new Date(then)
  return `${String(d.getDate()).padStart(2, '0')} ${MONTHS[d.getMonth()]} ${d.getFullYear()}`
}
</script>

<template>
  <div data-fivelakhs-dashboard class="mx-auto flex w-full max-w-3xl flex-col gap-4">
    <!-- The disclaimer leads here too, so the dashboard cannot be read as a
         ledger of real instruments. -->
    <div class="rounded-lg border border-red-500/40 bg-red-500/5 px-3 py-2">
      <p class="text-xs font-semibold uppercase tracking-wide text-red-600 dark:text-red-400">
        {{ NOVELTY_NOTICE.en }} · {{ NOVELTY_NOTICE.value }}
      </p>
      <p class="mt-0.5 text-xs text-muted-foreground">
        {{ NOVELTY_NOTICE.bn }} — every cheque here is a prop. Stored on this device only.
      </p>
    </div>

    <!-- Stats -->
    <div class="grid gap-3 sm:grid-cols-3">
      <div class="rounded-xl border bg-card p-4">
        <div class="flex items-center gap-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
          <FileCheck2 class="h-3.5 w-3.5" />
          Cheques generated
        </div>
        <p class="mt-1 text-2xl font-bold tabular-nums">{{ generated.value.value }}</p>
      </div>

      <div class="rounded-xl border bg-card p-4">
        <div class="flex items-center gap-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
          <Wallet class="h-3.5 w-3.5" />
          Amount (fixed)
        </div>
        <p class="mt-1 truncate text-2xl font-bold tabular-nums">{{ fixedAmountLabel }}</p>
        <p class="text-[11px] text-muted-foreground">Always five lakhs. Never editable.</p>
      </div>

      <div class="rounded-xl border bg-card p-4">
        <div class="flex items-center gap-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
          <Sparkles class="h-3.5 w-3.5" />
          Success rate
        </div>
        <p class="mt-1 text-2xl font-bold">100%</p>
        <p class="text-[11px] text-muted-foreground">It's a picture. It cannot fail.</p>
      </div>
    </div>

    <!-- Recent activity -->
    <div class="rounded-xl border bg-card">
      <div class="flex items-center justify-between border-b px-4 py-2.5">
        <p class="text-sm font-semibold">Recent activity</p>
        <span class="text-xs text-muted-foreground">{{ count }} saved on this device</span>
      </div>

      <div v-if="entries.length" class="divide-y">
        <div
          v-for="e in entries"
          :key="e.id"
          class="flex items-center gap-3 px-4 py-2.5"
        >
          <div class="min-w-0 flex-1">
            <p class="truncate text-sm font-medium">{{ e.payeeName || '—' }}</p>
            <p class="truncate text-xs text-muted-foreground">
              {{ e.bankName }} · {{ relativeTime(e.createdAt) }}
            </p>
          </div>

          <Badge variant="secondary" class="shrink-0 font-normal tabular-nums">
            {{ formatCurrency(e.amount ?? 500000, e.currency) }}
          </Badge>

          <Button
            size="icon"
            variant="ghost"
            class="h-7 w-7 shrink-0"
            title="Rebuild this cheque and download it again"
            @click="emit('regenerate', e)"
          >
            <Download class="h-3.5 w-3.5" />
            <span class="sr-only">Download again</span>
          </Button>

          <Button
            size="icon"
            variant="ghost"
            class="h-7 w-7 shrink-0"
            title="Remove from this list"
            @click="remove(e.id)"
          >
            <Trash2 class="h-3.5 w-3.5" />
            <span class="sr-only">Remove</span>
          </Button>
        </div>
      </div>

      <p v-else class="px-4 py-8 text-center text-sm text-muted-foreground">
        Nothing yet — generate a cheque and it will show up here.
      </p>
    </div>
  </div>
</template>
