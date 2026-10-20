<script setup>
/**
 * ম্যাট্রিক্স — the imported datasets: one per imported table, newest first,
 * with its kind, report date, size and open quality issues. The current
 * snapshot of each kind and layout is marked.
 */
import { Database, Eye, ShieldAlert } from '@lucide/vue'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { formatDate, lakh } from '@/lib/matrix-format.js'

defineProps({
  datasets: { type: Array, default: () => [] },
})
const emit = defineEmits(['view', 'quality'])
</script>

<template>
  <section class="flex min-w-0 flex-col rounded-lg border bg-card" data-matrix-datasets>
    <header class="flex items-center gap-2 border-b p-3">
      <Database class="size-4 text-muted-foreground" aria-hidden="true" />
      <h3 class="font-semibold">ডেটাসেট <span class="text-sm font-normal text-muted-foreground">Datasets</span></h3>
    </header>
    <p v-if="!datasets.length" class="p-6 text-sm text-muted-foreground">Nothing imported yet. Upload a file above.</p>
    <ul v-else class="divide-y">
      <li v-for="d in datasets" :key="d.id" class="flex flex-col gap-2 p-3 sm:flex-row sm:items-center" :data-dataset="d.id">
        <div class="min-w-0 flex-1">
          <div class="flex flex-wrap items-center gap-2">
            <span class="truncate font-medium">{{ d.sheet.trim() }}</span>
            <Badge variant="secondary">{{ d.kind }}</Badge>
            <Badge v-if="d.isCurrent" variant="outline">current</Badge>
          </div>
          <p class="mt-0.5 truncate text-xs text-muted-foreground">
            {{ d.fileName }} · {{ lakh(d.rowCount) }} rows · {{ d.columns.length }} columns
            <span v-if="d.reportDate"> · {{ formatDate(d.reportDate) }}</span>
            <span v-if="d.dropped.length"> · {{ d.dropped.length }} sensitive column(s) not imported</span>
          </p>
        </div>
        <div class="flex shrink-0 gap-2">
          <Button size="sm" variant="ghost" :data-quality-dataset="d.id" @click="emit('quality', d)">
            <ShieldAlert class="size-4" /> {{ d.openIssues }} <span class="text-xs opacity-70">issues</span>
          </Button>
          <Button size="sm" variant="outline" :data-view-dataset="d.id" @click="emit('view', d)">
            <Eye class="size-4" /> দেখুন <span class="text-xs opacity-70">View</span>
          </Button>
        </div>
      </li>
    </ul>
  </section>
</template>
