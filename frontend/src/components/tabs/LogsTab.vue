<script setup>
/**
 * LogsTab — the Observability viewer for Mostisko.
 *
 * Features:
 * - System info header strip (env badge, DB health, queue depth, drops)
 * - Live tail with SSE (pause/resume, "N new" indicator, auto-scroll at bottom)
 * - History mode with keyset pagination and filters
 * - Row detail drawer (all fields, plain text — NEVER v-html)
 * - Audit tab (SystemAdmin only)
 * - Stats cards
 * - Virtualized list via `virtua`
 */

import { ref, computed, onMounted, onUnmounted, watch, nextTick } from 'vue'
import { VList } from 'virtua/vue'
import {
  listActivityLogs, listAuditLogs, getActivityLog, getCorrelation,
  getSystemInfo, getLogStats, openLogStream
} from '@/services/logs.service.js'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Badge } from '@/components/ui/badge'
import {
  Activity, Database, Server, Pause, Play, ChevronDown, ChevronRight,
  AlertCircle, CheckCircle, Clock, ArrowRight, Filter, RefreshCcw
} from '@lucide/vue'

// ---- System info ----
const sysInfo = ref(null)
const stats = ref(null)

async function loadSystemInfo() {
  try { sysInfo.value = await getSystemInfo() } catch { /* no-op */ }
}
async function loadStats() {
  try { stats.value = await getLogStats() } catch { /* no-op */ }
}

// ---- Tabs ----
const activeTab = ref('live') // 'live' | 'history' | 'audit' | 'stats'

// ---- Filters ----
//
// The "no filter" value is the sentinel ALL, not an empty string.
//
// reka-ui reserves '' on a Select: it is how the component is told to clear its
// selection and fall back to the placeholder, so a <SelectItem value=""> throws
// ("A <SelectItem /> must have a value prop that is not an empty string").
// A named sentinel keeps an explicit "All" row selectable, and `filterValue()`
// turns it back into undefined at the one place the API is called — so the wire
// format is unchanged: absent means unfiltered.
const ALL = 'all'

const filterCategory = ref(ALL)
const filterLevel    = ref(ALL)
const filterStatus   = ref(ALL)
const filterModule   = ref('')
const filterPath     = ref('')
const filterSearch   = ref('')

/** A filter ref → the value the API expects; ALL and '' both mean "unfiltered". */
function filterValue(v) {
  return !v || v === ALL ? undefined : v
}

// ---- Live tail ----
const LIVE_MAX   = 2000
const liveEntries = ref([])    // displayed (up to LIVE_MAX)
const livePaused  = ref(false)
const newCount    = ref(0)
const listRef     = ref(null)
let closeStream   = null

function startStream() {
  closeStream?.()
  closeStream = openLogStream(
    { category: filterValue(filterCategory.value), minLevel: filterValue(filterLevel.value) },
    onLiveEntry,
    (err) => { /* silently ignore disconnects */ }
  )
}

function onLiveEntry(entry) {
  if (livePaused.value) {
    newCount.value++
    return
  }
  liveEntries.value.unshift(entry)
  if (liveEntries.value.length > LIVE_MAX) liveEntries.value.length = LIVE_MAX
}

function togglePause() {
  livePaused.value = !livePaused.value
  if (!livePaused.value) {
    newCount.value = 0
  }
}

function resumeAndScrollTop() {
  livePaused.value = false
  newCount.value = 0
}

// ---- History ----
const historyItems   = ref([])
const historyLoading = ref(false)
const nextCursor     = ref(null)

async function loadHistory(reset = false) {
  if (historyLoading.value) return
  historyLoading.value = true
  try {
    const params = {
      category:      filterValue(filterCategory.value),
      level:         filterValue(filterLevel.value),
      status:        filterValue(filterStatus.value),
      module:        filterValue(filterModule.value),
      path:          filterValue(filterPath.value),
      search:        filterValue(filterSearch.value),
      cursor:        reset ? undefined : nextCursor.value,
      pageSize:      50,
    }
    const page = await listActivityLogs(params)
    if (reset) historyItems.value = page.items
    else        historyItems.value = [...historyItems.value, ...page.items]
    nextCursor.value = page.nextCursor ?? null
  } catch { /* no-op */ }
  finally { historyLoading.value = false }
}

// ---- Audit ----
const auditItems   = ref([])
const auditLoading = ref(false)
const auditCursor  = ref(null)

async function loadAudit(reset = false) {
  if (auditLoading.value) return
  auditLoading.value = true
  try {
    const page = await listAuditLogs({ cursor: reset ? undefined : auditCursor.value, pageSize: 50 })
    if (reset) auditItems.value = page.items
    else        auditItems.value = [...auditItems.value, ...page.items]
    auditCursor.value = page.nextCursor ?? null
  } catch { /* no-op */ }
  finally { auditLoading.value = false }
}

// ---- Row detail ----
const selectedEntry    = ref(null)
const detailOpen       = ref(false)
const correlationData  = ref(null)
const loadingCorrel    = ref(false)

async function openDetail(entry) {
  selectedEntry.value = entry
  detailOpen.value    = true
  correlationData.value = null
  if (entry.correlationId) {
    loadingCorrel.value = true
    try { correlationData.value = await getCorrelation(entry.correlationId) }
    catch { /* no-op */ }
    finally { loadingCorrel.value = false }
  }
}

// ---- Helpers ----
function levelClass(level) {
  return {
    Trace:       'text-muted-foreground',
    Debug:       'text-muted-foreground',
    Information: 'text-blue-500 dark:text-blue-400',
    Warning:     'text-yellow-500 dark:text-yellow-400',
    Error:       'text-red-500 dark:text-red-400',
    Critical:    'text-red-700 dark:text-red-300 font-bold',
  }[level] ?? ''
}

function statusClass(code) {
  if (!code) return ''
  if (code < 300) return 'text-green-500 dark:text-green-400'
  if (code < 400) return 'text-blue-500'
  if (code < 500) return 'text-yellow-500 dark:text-yellow-400'
  return 'text-red-500 dark:text-red-400'
}

function formatTime(ts) {
  if (!ts) return ''
  return new Date(ts).toLocaleTimeString()
}

function formatJson(json) {
  if (!json) return ''
  try { return JSON.stringify(JSON.parse(json), null, 2) }
  catch { return json }
}

function envBadgeClass(env) {
  if (!env) return 'bg-muted text-muted-foreground'
  const lower = env.toLowerCase()
  if (lower === 'production') return 'bg-red-500 text-white'
  if (lower === 'staging')    return 'bg-yellow-500 text-white'
  return 'bg-green-500 text-white'
}

// ---- Lifecycle ----
onMounted(() => {
  loadSystemInfo()
  loadStats()
  startStream()
})

onUnmounted(() => {
  closeStream?.()
})

watch(activeTab, (tab) => {
  if (tab === 'history' && historyItems.value.length === 0) loadHistory(true)
  if (tab === 'audit'   && auditItems.value.length   === 0) loadAudit(true)
  if (tab === 'stats')   loadStats()
})

watch([filterCategory, filterLevel], () => {
  if (activeTab.value === 'live') startStream()
})

const displayedLive = computed(() => liveEntries.value.slice(0, 200))
</script>

<template>
  <div class="flex flex-col h-full overflow-hidden bg-background">

    <!-- System Info Strip -->
    <div v-if="sysInfo" class="shrink-0 border-b bg-muted/20 px-4 py-2 flex flex-wrap items-center gap-3 text-xs">
      <span :class="['px-2 py-0.5 rounded-full text-xs font-semibold', envBadgeClass(sysInfo.environment)]">
        {{ sysInfo.environment }}
      </span>
      <span class="flex items-center gap-1 text-muted-foreground">
        <Database class="w-3 h-3" />
        {{ sysInfo.dbProvider }} · {{ sysInfo.databaseName }}
        <span :class="sysInfo.dbHealthy ? 'text-green-500' : 'text-red-500'">●</span>
      </span>
      <span class="text-muted-foreground">v{{ sysInfo.appVersion }}</span>
      <span class="text-muted-foreground">Queue: {{ sysInfo.queueDepth }}</span>
      <span v-if="sysInfo.droppedCount > 0" class="text-red-500">⚠ {{ sysInfo.droppedCount }} dropped</span>
      <span class="text-muted-foreground ml-auto">{{ sysInfo.dotNetVersion }}</span>
    </div>

    <!-- Tab Bar -->
    <div class="shrink-0 flex gap-1 border-b bg-background px-4 pt-2">
      <button v-for="t in [['live','Live'], ['history','History'], ['audit','Audit'], ['stats','Stats']]" :key="t[0]"
        @click="activeTab = t[0]"
        :class="['px-3 py-1.5 text-sm rounded-t-lg border-b-2 transition-colors',
          activeTab === t[0] ? 'border-primary text-primary font-medium' : 'border-transparent text-muted-foreground hover:text-foreground']">
        {{ t[1] }}
      </button>
    </div>

    <!-- LIVE TAB -->
    <div v-show="activeTab === 'live'" class="flex-1 flex flex-col min-h-0 overflow-hidden">
      <!-- Toolbar -->
      <div class="shrink-0 flex items-center gap-2 px-4 py-2 border-b bg-muted/10">
        <Select v-model="filterCategory">
          <SelectTrigger class="h-8 w-32 text-xs"><SelectValue placeholder="Category" /></SelectTrigger>
          <SelectContent>
            <SelectItem :value="ALL">All</SelectItem>
            <SelectItem value="request">Request</SelectItem>
            <SelectItem value="client">Client</SelectItem>
            <SelectItem value="app">App</SelectItem>
            <SelectItem value="system">System</SelectItem>
          </SelectContent>
        </Select>
        <Select v-model="filterLevel">
          <SelectTrigger class="h-8 w-32 text-xs"><SelectValue placeholder="Level" /></SelectTrigger>
          <SelectContent>
            <SelectItem :value="ALL">All</SelectItem>
            <SelectItem value="Warning">Warning+</SelectItem>
            <SelectItem value="Error">Error+</SelectItem>
          </SelectContent>
        </Select>
        <div class="ml-auto flex items-center gap-2">
          <button v-if="livePaused && newCount > 0"
            @click="resumeAndScrollTop"
            class="flex items-center gap-1 text-xs bg-primary text-primary-foreground px-2 py-1 rounded-full animate-pulse">
            {{ newCount }} new — click to resume
          </button>
          <Button variant="outline" size="sm" class="h-8 gap-1 text-xs" @click="togglePause">
            <Pause v-if="!livePaused" class="w-3 h-3" />
            <Play v-else class="w-3 h-3" />
            {{ livePaused ? 'Resume' : 'Pause' }}
          </Button>
        </div>
      </div>

      <!-- Virtual list -->
      <div class="flex-1 overflow-hidden font-mono text-xs">
        <VList ref="listRef" :data="displayedLive" class="h-full w-full" v-slot="{ item: entry }">
          <div @click="openDetail(entry)"
            class="flex items-start gap-2 px-4 py-1 cursor-pointer hover:bg-muted/30 border-b border-border/30 select-none">
            <span class="text-muted-foreground w-20 shrink-0">{{ formatTime(entry.timestampUtc) }}</span>
            <span :class="['w-20 shrink-0', levelClass(entry.level)]">{{ entry.level }}</span>
            <span class="text-muted-foreground w-16 shrink-0">{{ entry.category }}</span>
            <span v-if="entry.method" class="text-foreground shrink-0">{{ entry.method }}</span>
            <span v-if="entry.path"   class="text-foreground truncate max-w-xs">{{ entry.path }}</span>
            <span v-if="entry.statusCode" :class="['shrink-0', statusClass(entry.statusCode)]">{{ entry.statusCode }}</span>
            <span v-if="entry.durationMs !== undefined && entry.durationMs !== null"
              class="text-muted-foreground shrink-0">{{ entry.durationMs }}ms</span>
            <span v-if="entry.message" class="text-muted-foreground truncate flex-1">{{ entry.message }}</span>
          </div>
        </VList>
      </div>
    </div>

    <!-- HISTORY TAB -->
    <div v-show="activeTab === 'history'" class="flex-1 flex flex-col min-h-0 overflow-hidden">
      <!-- Filters -->
      <div class="shrink-0 flex flex-wrap items-center gap-2 px-4 py-2 border-b bg-muted/10">
        <Select v-model="filterCategory">
          <SelectTrigger class="h-8 w-28 text-xs"><SelectValue placeholder="Category" /></SelectTrigger>
          <SelectContent>
            <SelectItem :value="ALL">All</SelectItem>
            <SelectItem value="request">Request</SelectItem>
            <SelectItem value="client">Client</SelectItem>
            <SelectItem value="app">App</SelectItem>
            <SelectItem value="system">System</SelectItem>
          </SelectContent>
        </Select>
        <Select v-model="filterLevel">
          <SelectTrigger class="h-8 w-28 text-xs"><SelectValue placeholder="Level" /></SelectTrigger>
          <SelectContent>
            <SelectItem :value="ALL">All</SelectItem>
            <SelectItem value="Warning">Warning</SelectItem>
            <SelectItem value="Error">Error</SelectItem>
            <SelectItem value="Critical">Critical</SelectItem>
          </SelectContent>
        </Select>
        <Select v-model="filterStatus">
          <SelectTrigger class="h-8 w-24 text-xs"><SelectValue placeholder="Status" /></SelectTrigger>
          <SelectContent>
            <SelectItem :value="ALL">All</SelectItem>
            <SelectItem value="2xx">2xx</SelectItem>
            <SelectItem value="4xx">4xx</SelectItem>
            <SelectItem value="5xx">5xx</SelectItem>
          </SelectContent>
        </Select>
        <Input v-model="filterPath" placeholder="Path…" class="h-8 w-40 text-xs" />
        <Input v-model="filterSearch" placeholder="Search…" class="h-8 w-40 text-xs" />
        <Button size="sm" class="h-8 text-xs" @click="loadHistory(true)">
          <RefreshCcw class="w-3 h-3 mr-1" /> Apply
        </Button>
      </div>

      <div class="flex-1 overflow-y-auto font-mono text-xs">
        <div v-for="entry in historyItems" :key="entry.id"
          @click="openDetail(entry)"
          class="flex items-start gap-2 px-4 py-1 cursor-pointer hover:bg-muted/30 border-b border-border/30">
          <span class="text-muted-foreground w-36 shrink-0">{{ entry.timestampUtc }}</span>
          <span :class="['w-20 shrink-0', levelClass(entry.level)]">{{ entry.level }}</span>
          <span class="text-muted-foreground w-16 shrink-0">{{ entry.category }}</span>
          <span v-if="entry.method" class="shrink-0">{{ entry.method }}</span>
          <span v-if="entry.path" class="truncate max-w-sm">{{ entry.path }}</span>
          <span v-if="entry.statusCode" :class="['shrink-0', statusClass(entry.statusCode)]">{{ entry.statusCode }}</span>
          <span v-if="entry.durationMs !== null && entry.durationMs !== undefined" class="text-muted-foreground shrink-0">{{ entry.durationMs }}ms</span>
          <span v-if="entry.message" class="text-muted-foreground truncate flex-1">{{ entry.message }}</span>
        </div>
        <div class="flex justify-center p-4">
          <Button v-if="nextCursor" variant="outline" size="sm" @click="loadHistory(false)" :disabled="historyLoading">
            {{ historyLoading ? 'Loading…' : 'Load more' }}
          </Button>
          <span v-else class="text-xs text-muted-foreground">End of results</span>
        </div>
      </div>
    </div>

    <!-- AUDIT TAB -->
    <div v-show="activeTab === 'audit'" class="flex-1 flex flex-col min-h-0 overflow-hidden">
      <div class="shrink-0 flex items-center gap-2 px-4 py-2 border-b bg-muted/10">
        <span class="text-xs text-muted-foreground">Audit log — SystemAdmin only</span>
        <Button size="sm" class="h-8 text-xs ml-auto" @click="loadAudit(true)">
          <RefreshCcw class="w-3 h-3 mr-1" /> Refresh
        </Button>
      </div>
      <div class="flex-1 overflow-y-auto font-mono text-xs">
        <div v-for="audit in auditItems" :key="audit.id"
          class="flex items-start gap-2 px-4 py-1.5 border-b border-border/30 hover:bg-muted/30">
          <span class="text-muted-foreground w-36 shrink-0">{{ audit.timestampUtc }}</span>
          <span class="font-medium w-20 shrink-0">{{ audit.entityName }}</span>
          <span class="text-muted-foreground w-16 shrink-0">#{{ audit.entityKey }}</span>
          <span :class="['shrink-0 font-semibold',
            audit.action === 'Added' ? 'text-green-500' :
            audit.action === 'Deleted' ? 'text-red-500' : 'text-yellow-500']">
            {{ audit.action }}
          </span>
          <!-- Changes diff -->
          <div v-if="audit.changesJson" class="flex-1 flex flex-col gap-0.5">
            <template v-for="change in JSON.parse(audit.changesJson)" :key="change.property">
              <span class="text-foreground">
                <span class="text-muted-foreground">{{ change.property }}:</span>
                <span v-if="change.old !== null && change.old !== undefined" class="text-red-400 line-through ml-1">{{ change.old }}</span>
                <span v-if="change.new !== null && change.new !== undefined" class="text-green-400 ml-1">→ {{ change.new }}</span>
              </span>
            </template>
          </div>
        </div>
        <div class="flex justify-center p-4">
          <Button v-if="auditCursor" variant="outline" size="sm" @click="loadAudit(false)" :disabled="auditLoading">
            {{ auditLoading ? 'Loading…' : 'Load more' }}
          </Button>
        </div>
      </div>
    </div>

    <!-- STATS TAB -->
    <div v-show="activeTab === 'stats'" class="flex-1 overflow-y-auto p-6">
      <div v-if="stats" class="grid grid-cols-2 md:grid-cols-4 gap-4 mb-6">
        <div class="rounded-xl border bg-card p-4">
          <div class="text-sm text-muted-foreground">Total Requests</div>
          <div class="text-3xl font-bold mt-1">{{ stats.totalRequests.toLocaleString() }}</div>
        </div>
        <div class="rounded-xl border bg-card p-4">
          <div class="text-sm text-muted-foreground">Error Rate</div>
          <div :class="['text-3xl font-bold mt-1', stats.errorRate > 0.1 ? 'text-red-500' : '']">
            {{ (stats.errorRate * 100).toFixed(1) }}%
          </div>
        </div>
        <div class="rounded-xl border bg-card p-4">
          <div class="text-sm text-muted-foreground">p95 Duration</div>
          <div class="text-3xl font-bold mt-1">{{ stats.p95DurationMs !== null ? stats.p95DurationMs + 'ms' : 'N/A' }}</div>
        </div>
        <div class="rounded-xl border bg-card p-4">
          <div class="text-sm text-muted-foreground">Queue Depth</div>
          <div class="text-3xl font-bold mt-1">{{ sysInfo?.queueDepth ?? '—' }}</div>
        </div>
      </div>

      <div v-if="stats" class="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div class="rounded-xl border bg-card p-4">
          <div class="text-sm font-medium mb-3">Top Paths</div>
          <div v-for="p in stats.topPaths" :key="p.path" class="flex items-center justify-between py-1 text-sm border-b border-border/30 last:border-0">
            <span class="truncate font-mono text-xs text-muted-foreground">{{ p.path }}</span>
            <span class="font-medium ml-2 shrink-0">{{ p.count }}</span>
          </div>
        </div>
        <div class="rounded-xl border bg-card p-4">
          <div class="text-sm font-medium mb-3">Top Modules</div>
          <div v-for="m in stats.topModules" :key="m.module" class="flex items-center justify-between py-1 text-sm border-b border-border/30 last:border-0">
            <span class="truncate text-xs text-muted-foreground">{{ m.module }}</span>
            <span class="font-medium ml-2 shrink-0">{{ m.count }}</span>
          </div>
        </div>
      </div>
    </div>

    <!-- Detail Drawer -->
    <Dialog v-model:open="detailOpen">
      <DialogContent class="max-w-2xl max-h-[80vh] overflow-y-auto font-mono text-xs">
        <DialogHeader>
          <DialogTitle class="font-sans text-base">Log Entry #{{ selectedEntry?.id }}</DialogTitle>
        </DialogHeader>

        <div v-if="selectedEntry" class="space-y-3 mt-2">
          <!-- Core fields — all rendered as text, no v-html -->
          <div v-for="[label, val] in [
            ['Timestamp',    selectedEntry.timestampUtc],
            ['Level',        selectedEntry.level],
            ['Category',     selectedEntry.category],
            ['Correlation',  selectedEntry.correlationId],
            ['User',         selectedEntry.userName ?? selectedEntry.userId],
            ['Method',       selectedEntry.method],
            ['Path',         selectedEntry.path],
            ['QueryString',  selectedEntry.queryString],
            ['StatusCode',   selectedEntry.statusCode],
            ['DurationMs',   selectedEntry.durationMs],
            ['ClientIp',     selectedEntry.clientIp],
            ['UserAgent',    selectedEntry.userAgent],
            ['Module',       selectedEntry.module],
            ['EventName',    selectedEntry.eventName],
            ['Message',      selectedEntry.message],
            ['Environment',  selectedEntry.environment],
            ['Machine',      selectedEntry.machineName],
          ].filter(([_, v]) => v !== null && v !== undefined && v !== '')"
            :key="label" class="flex gap-2">
            <span class="text-muted-foreground w-28 shrink-0">{{ label }}</span>
            <span class="text-foreground break-all">{{ val }}</span>
          </div>

          <!-- Properties JSON — plain text pre-block, no v-html -->
          <div v-if="selectedEntry.propertiesJson">
            <div class="text-muted-foreground mb-1">Properties</div>
            <pre class="bg-muted rounded-lg p-3 overflow-auto text-xs text-foreground whitespace-pre-wrap break-all">{{ formatJson(selectedEntry.propertiesJson) }}</pre>
          </div>

          <!-- Correlation drill-down -->
          <div v-if="selectedEntry.correlationId" class="border-t pt-3">
            <div class="font-sans text-sm font-medium mb-2">Correlated Activity</div>
            <div v-if="loadingCorrel" class="text-muted-foreground">Loading…</div>
            <div v-else-if="correlationData">
              <div v-if="correlationData.activities?.length">
                <div class="text-muted-foreground mb-1">Requests ({{ correlationData.activities.length }})</div>
                <div v-for="a in correlationData.activities" :key="a.id"
                  class="py-0.5 flex gap-2">
                  <span class="text-muted-foreground">{{ a.method }}</span>
                  <span>{{ a.path }}</span>
                  <span :class="statusClass(a.statusCode)">{{ a.statusCode }}</span>
                </div>
              </div>
              <div v-if="correlationData.audits?.length" class="mt-2">
                <div class="text-muted-foreground mb-1">Audit entries ({{ correlationData.audits.length }})</div>
                <div v-for="a in correlationData.audits" :key="a.id"
                  class="py-0.5 flex gap-2">
                  <span :class="a.action==='Added'?'text-green-500':a.action==='Deleted'?'text-red-500':'text-yellow-500'">{{ a.action }}</span>
                  <span>{{ a.entityName }} #{{ a.entityKey }}</span>
                </div>
              </div>
            </div>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  </div>
</template>
