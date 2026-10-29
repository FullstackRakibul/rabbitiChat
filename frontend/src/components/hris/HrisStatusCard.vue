<script setup>
/**
 * এইচআর ওয়ার্কস্পেস — the data status card (docs/HRIS_WORKSPACE_PROMPT.md §3.4).
 *
 * Read-only for everyone with the tab: each current dataset's report date and open
 * quality issues, the KPIs its card cannot give and why, and the last Rulebook
 * confirmation. Every figure is the server's (Matrix's datasets and cards).
 */
import { onMounted, ref } from 'vue'
import { AlertTriangle, Loader2 } from '@lucide/vue'
import { getHrisStatus } from '@/services/index.js'
import { formatDate } from '@/lib/matrix-format.js'

const status = ref(null)
const error = ref('')
const loading = ref(false)

const KINDS = {
  manpower: ['জনবল', 'Manpower'], master: ['মাস্টার', 'Master'], 'attendance-daily': ['দৈনিক হাজিরা', 'Daily attendance'],
  'designation-lookup': ['পদবি তালিকা', 'Designation lookup'], 'separation-list': ['বিচ্ছেদ তালিকা', 'Separation list'], custom: ['অন্যান্য', 'Custom'],
}

async function load() {
  loading.value = true
  error.value = ''
  try { status.value = await getHrisStatus() } catch (e) { error.value = e.message } finally { loading.value = false }
}
onMounted(load)
defineExpose({ load })
</script>

<template>
  <section class="rounded-lg border bg-card p-3" data-hris-status>
    <h3 class="mb-2 text-sm font-semibold">ডেটার অবস্থা <span class="text-xs font-normal text-muted-foreground">Data status</span></h3>
    <p v-if="loading" class="flex items-center gap-2 text-sm text-muted-foreground"><Loader2 class="size-4 animate-spin" /> লোড হচ্ছে…</p>
    <p v-else-if="error" class="text-sm text-destructive">{{ error }}</p>
    <template v-else-if="status">
      <p v-if="!status.datasets.length" class="text-sm text-muted-foreground">এখনো কোনো ডেটা ইমপোর্ট হয়নি। <span class="text-xs">No data imported yet.</span></p>
      <ul class="grid gap-2 sm:grid-cols-2">
        <li v-for="d in status.datasets" :key="d.datasetId" class="min-w-0 rounded-md border p-2 text-sm" :data-status-kind="d.kind">
          <div class="flex flex-wrap items-baseline justify-between gap-1">
            <span class="font-medium">{{ KINDS[d.kind]?.[0] ?? d.kind }} <span class="text-xs text-muted-foreground">{{ KINDS[d.kind]?.[1] }}</span></span>
            <span class="text-xs text-muted-foreground">{{ d.reportDate ? formatDate(d.reportDate) : '—' }}</span>
          </div>
          <p class="truncate text-xs text-muted-foreground" :title="d.name">{{ d.name }}</p>
          <p class="text-xs" :class="d.openIssues ? 'text-amber-600 dark:text-amber-400' : 'text-muted-foreground'">
            খোলা ত্রুটি {{ d.openIssues }} <span class="opacity-75">open quality issues</span>
          </p>
          <p v-for="u in d.unavailable" :key="u.key" class="mt-1 flex gap-1 text-xs text-muted-foreground">
            <AlertTriangle class="mt-0.5 size-3 shrink-0" /> <span><b>{{ u.labelBn }}</b> · {{ u.label }}: {{ u.reason }}</span>
          </p>
        </li>
      </ul>
      <p class="mt-2 text-xs text-muted-foreground" data-status-rulebook>
        নিয়মাবলির শেষ নিশ্চিতকরণ · Last Rulebook confirmation:
        <template v-if="status.lastRulebookConfirmation">{{ formatDate(status.lastRulebookConfirmation.slice(0, 10)) }} — {{ status.lastRulebookPage }}</template>
        <template v-else>—</template>
      </p>
    </template>
  </section>
</template>
