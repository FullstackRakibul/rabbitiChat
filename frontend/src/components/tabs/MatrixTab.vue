<script setup>
/**
 * ম্যাট্রিক্স · Matrix — RMG manpower and attendance data from Excel (docs/MATRIX_PROMPT.md).
 *
 * Sections: Data (upload → profile and mapping → import → a dataset's page),
 * Dashboards (automatic and edited ones) and Definitions (KPIs and fields).
 *
 * A dataset's page has three tabs: রিপোর্ট Report (the report card's tiles and the
 * automatic charts — where an import lands), ডেটা Data (the viewer) and মান Quality.
 *
 * Which views the user gets comes from the server (/api/me/access: sections and
 * actions); the server checks every route again. The view is in the URL —
 * ?upload=, ?dataset= (with &tab=data|quality, or &highlight= for the viewer), ?quality=, ?section=dashboards,
 * ?dashboard= (with &print=1), ?section=definitions — so a citation, a drill-down
 * or an issue's "Show" is a link, and back works.
 */
import { computed, defineAsyncComponent, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { FileSpreadsheet, LayoutDashboard, Loader2, Plus } from '@lucide/vue'
import {
  createMatrixDashboard, getMatrixCatalog, getMatrixDataset, getMatrixProfile, importMatrixUpload, listMatrixDashboards,
  listMatrixDatasets, listMatrixUploads,
} from '@/services/index.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import MatrixUpload from '@/components/matrix/MatrixUpload.vue'
import MatrixMapping from '@/components/matrix/MatrixMapping.vue'
import MatrixDatasets from '@/components/matrix/MatrixDatasets.vue'
import MatrixQuality from '@/components/matrix/MatrixQuality.vue'
import MatrixViewer from '@/components/matrix/MatrixViewer.vue'
import MatrixDefinitions from '@/components/matrix/MatrixDefinitions.vue'
import { formatBytes, formatDate } from '@/lib/matrix-format.js'

// ECharts comes with the dashboards only, so the rest of the app does not load it.
const MatrixDashboard = defineAsyncComponent(() => import('@/components/matrix/MatrixDashboard.vue'))
const MatrixReport = defineAsyncComponent(() => import('@/components/matrix/MatrixReport.vue'))
const MatrixAsk = defineAsyncComponent(() => import('@/components/matrix/MatrixAsk.vue'))

const props = defineProps({
  /** The user's section keys and action keys, as /api/me/access gave them. */
  sections: { type: Array, default: () => [] },
  actions: { type: Array, default: () => [] },
})

const route = useRoute()
const router = useRouter()

const canData = computed(() => props.sections.includes('section:matrix.data'))
const canDashboards = computed(() => props.sections.includes('section:matrix.dashboards'))
const canUpload = computed(() => props.actions.includes('action:matrix.upload'))
const canFix = computed(() => props.actions.includes('action:matrix.fix'))
const canEditDashboards = computed(() => props.actions.includes('action:matrix.dashboards.edit'))
const canEditKpis = computed(() => props.actions.includes('action:matrix.kpis.edit'))
const canAsk = computed(() => props.sections.includes('section:matrix.ask'))

const datasets = ref([])
const uploads = ref([])
const dashboards = ref([])
const catalog = ref([])
const error = ref('')
const notice = ref('')
const openUpload = ref(null)      // { id, fileName, reportDate, profile }
const openDataset = ref(null)
const busy = ref(false)

const view = computed(() => {
  if (route.query.dashboard) return 'dashboard'
  if (route.query.section === 'dashboards') return 'dashboards'
  if (route.query.section === 'definitions') return 'definitions'
  if (route.query.section === 'ask') return 'ask'
  if (route.query.upload) return 'mapping'
  if (route.query.dataset || route.query.quality) return 'dataset'
  return 'home'
})
/** The dataset page's tab: Report unless the URL asks for the rows (a highlight) or Quality. */
const datasetTab = computed(() => {
  if (route.query.quality || route.query.tab === 'quality') return 'quality'
  if (route.query.highlight || route.query.tab === 'data' || !canDashboards.value) return 'data'
  return 'report'
})
const DATASET_TABS = computed(() => [
  ['report', 'রিপোর্ট', 'Report', canDashboards.value],
  ['data', 'ডেটা', 'Data', true],
  ['quality', 'মান', 'Quality', true],
].filter((x) => x[3]))
const section = computed(() => (['dashboard', 'dashboards'].includes(view.value) ? 'dashboards' : ['definitions', 'ask'].includes(view.value) ? view.value : 'data'))
const printing = computed(() => view.value === 'dashboard' && !!route.query.print)
const SECTIONS = computed(() => [
  ['data', 'ডেটা', 'Data', {}, canData.value],
  ['dashboards', 'ড্যাশবোর্ড', 'Dashboards', { section: 'dashboards' }, canDashboards.value],
  ['ask', 'প্রশ্ন', 'Ask', { section: 'ask' }, canAsk.value],
  ['definitions', 'সংজ্ঞা', 'Definitions', { section: 'definitions' }, canData.value || canDashboards.value],
].filter((s) => s[4]))

function go(query) {
  router.push({ path: route.path, query })
}

async function refresh() {
  if (!canData.value) return
  try {
    const [d, u] = await Promise.all([listMatrixDatasets(), listMatrixUploads()])
    datasets.value = d.datasets
    uploads.value = u.uploads
  } catch (e) {
    error.value = e.message
  }
}

async function loadDashboards() {
  if (!canDashboards.value) return
  try { dashboards.value = (await listMatrixDashboards()).dashboards } catch (e) { error.value = e.message }
}

async function newDashboard() {
  try {
    const d = await createMatrixDashboard({ name: 'নতুন ড্যাশবোর্ড · New dashboard' })
    go({ dashboard: d.id })
  } catch (e) { error.value = e.message }
}

function openTab(tab) {
  go(tab === 'report' ? { dataset: openDataset.value.id } : { dataset: openDataset.value.id, tab })
}

/** A drill-down: the dataset viewer, with the clicked group's rows highlighted. */
function drill({ datasetId, cite }) {
  if (datasetId && cite) go({ dataset: datasetId, highlight: cite })
}

/** The dataset or upload the URL names, loaded when the URL changes. */
async function follow() {
  error.value = ''
  const id = Number(route.query.dataset || route.query.quality || 0)
  openDataset.value = id ? (datasets.value.find((d) => d.id === id) ?? await getMatrixDataset(id).catch((e) => { error.value = e.message; return null })) : null
  const up = Number(route.query.upload || 0)
  if (up && openUpload.value?.id !== up) {
    try {
      const profile = await getMatrixProfile(up)
      const meta = uploads.value.find((x) => x.id === up)
      openUpload.value = { id: up, fileName: meta?.fileName ?? profile.fileName, reportDate: profile.reportDate, profile }
    } catch (e) { error.value = e.message }
  }
  if (!up) openUpload.value = null
  if (view.value === 'dashboards') loadDashboards()
}

/**
 * After an upload: a file whose every selected table matches a saved template
 * imports at once ("no questions"); the first one that needs a person opens
 * its mapping.
 */
async function onUploaded(list) {
  notice.value = ''
  busy.value = true
  const needMapping = []
  let firstImported = null
  try {
    for (const u of list) {
      const selected = u.profile.sheets.flatMap((s) => s.tables).filter((t) => t.selected)
      if (selected.length && selected.every((t) => t.template)) {
        const res = await importMatrixUpload(u.id)
        notice.value = `${u.fileName}: ${res.message}.`
        firstImported ??= res.datasets?.[0]?.id ?? null
      } else needMapping.push(u)
    }
    await refresh()
    if (needMapping.length) {
      openUpload.value = { id: needMapping[0].id, fileName: needMapping[0].fileName, reportDate: needMapping[0].reportDate, profile: needMapping[0].profile }
      go({ upload: needMapping[0].id })
    } else if (firstImported) {
      go({ dataset: firstImported })
    }
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = false
  }
}

async function onImported(res) {
  notice.value = res.message + '.'
  await refresh()
  const first = res.datasets[0]
  go(first ? { dataset: first.id } : {})
}

function show(issue) {
  go({ dataset: openDataset.value.id, highlight: issue.highlight })
}

watch(() => route.fullPath, () => { if (route.path === '/matrix') follow() })
watch(canData, (v) => { if (v) refresh().then(follow) })
watch(canDashboards, (v) => { if (v && view.value === 'dashboards') loadDashboards() })
onMounted(async () => {
  if (canData.value) {
    await refresh()
    getMatrixCatalog().then((c) => { catalog.value = c.fields }).catch(() => {})
  }
  await follow()
})
</script>

<template>
  <div class="flex h-full min-h-0 min-w-0 flex-col gap-4 overflow-y-auto p-4 sm:p-6" data-matrix-tab>
    <div v-if="!printing">
      <h2 class="flex items-center gap-2 text-lg font-semibold tracking-tight">
        <FileSpreadsheet class="size-5" aria-hidden="true" />
        ম্যাট্রিক্স <span class="text-sm font-normal text-muted-foreground">Matrix</span>
      </h2>
      <nav class="mt-2 flex flex-wrap gap-1" aria-label="Matrix sections">
        <button
          v-for="[key, bn, en, query] in SECTIONS"
          :key="key"
          type="button"
          :data-matrix-section="key"
          :aria-current="section === key ? 'page' : undefined"
          class="rounded-md px-3 py-1.5 text-sm font-medium"
          :class="section === key ? 'bg-secondary text-secondary-foreground' : 'text-muted-foreground hover:bg-accent hover:text-foreground'"
          @click="go(query)"
        >{{ bn }} <span class="text-xs opacity-70">{{ en }}</span></button>
      </nav>
    </div>

    <MatrixDashboard v-if="view === 'dashboard' && canDashboards" :id="Number(route.query.dashboard)" :key="String(route.query.dashboard) + (printing ? ':print' : '')"
      :print="printing" :can-edit-action="canEditDashboards" @drill="drill" @close="go({ section: 'dashboards' })" />

    <section v-else-if="view === 'dashboards' && canDashboards" class="flex flex-col gap-3" data-matrix-dashboards>
      <div class="flex items-center gap-2">
        <h3 class="flex-1 font-semibold">ড্যাশবোর্ড <span class="text-sm font-normal text-muted-foreground">Dashboards</span></h3>
        <Button v-if="canEditDashboards" size="sm" variant="outline" data-dash-new @click="newDashboard"><Plus class="size-4" /> নতুন <span class="text-xs opacity-70">New</span></Button>
      </div>
      <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>
      <p v-if="!dashboards.length" class="rounded-lg border bg-card p-4 text-sm text-muted-foreground">
        ফাইল ইমপোর্ট করলে ড্যাশবোর্ড তৈরি হয়। <span class="opacity-70">Import a file and its dashboards appear here.</span>
      </p>
      <ul class="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
        <li v-for="d in dashboards" :key="d.id">
          <button type="button" class="flex w-full items-start gap-3 rounded-lg border bg-card p-3 text-left hover:bg-accent"
            :data-dashboard-link="d.auto ?? d.id" @click="go({ dashboard: d.id })">
            <LayoutDashboard class="mt-0.5 size-5 shrink-0 text-muted-foreground" aria-hidden="true" />
            <span class="min-w-0 flex-1">
              <span class="block truncate font-medium">{{ d.nameBn ?? d.name }}</span>
              <span class="block truncate text-xs text-muted-foreground">{{ d.nameBn ? d.name + ' · ' : '' }}{{ d.widgets }} widgets</span>
            </span>
            <Badge v-if="d.auto" variant="outline">auto</Badge>
            <Badge v-else-if="!d.shared" variant="secondary">private</Badge>
          </button>
        </li>
      </ul>
    </section>

    <MatrixAsk v-else-if="view === 'ask' && canAsk" :dataset-id="null" :can-edit-dashboards="canEditDashboards" data-ask-section
      @drill="drill" @definition="(k) => go({ section: 'definitions', kpi: k })" />

    <MatrixDefinitions v-else-if="view === 'definitions'" :can-kpis="canDashboards" :can-fields="canData" :can-edit-kpis="canEditKpis"
      :focus="String(route.query.kpi ?? '')" />

    <p v-else-if="!canData" class="rounded-lg border bg-card p-4 text-sm text-muted-foreground">
      The Data section needs the “Data” permission of Matrix. Ask an administrator.
    </p>

    <template v-else>
      <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>
      <p v-if="notice" class="rounded-md border bg-card p-3 text-sm" role="status" data-matrix-notice>
        {{ notice }}
        <button v-if="canDashboards" type="button" class="ml-1 underline underline-offset-2" @click="go({ section: 'dashboards' })">ড্যাশবোর্ড দেখুন · Dashboards</button>
      </p>
      <p v-if="busy" class="flex items-center gap-2 text-sm text-muted-foreground"><Loader2 class="size-4 animate-spin" /> Importing…</p>

      <template v-if="view === 'home'">
        <MatrixUpload :can-upload="canUpload" @uploaded="onUploaded" />

        <section v-if="uploads.some((u) => u.status === 'profiled')" class="rounded-lg border bg-card">
          <h3 class="border-b p-3 font-semibold">ম্যাপিং বাকি <span class="text-sm font-normal text-muted-foreground">Waiting for a mapping</span></h3>
          <ul class="divide-y">
            <li v-for="u in uploads.filter((x) => x.status === 'profiled')" :key="u.id" class="flex flex-wrap items-center gap-2 p-3 text-sm">
              <span class="min-w-0 flex-1 truncate font-medium">{{ u.fileName }}</span>
              <span class="text-xs text-muted-foreground">{{ formatBytes(u.size) }}<span v-if="u.reportDate"> · {{ formatDate(u.reportDate) }}</span></span>
              <Badge variant="outline">{{ u.status }}</Badge>
              <Button size="sm" variant="outline" @click="go({ upload: u.id })">Map columns</Button>
            </li>
          </ul>
        </section>

        <MatrixDatasets :datasets="datasets" @view="(d) => go({ dataset: d.id })" @quality="(d) => go({ quality: d.id })" />
      </template>

      <MatrixMapping v-else-if="view === 'mapping' && openUpload" :upload="openUpload" :catalog="catalog"
        @imported="onImported" @close="go({})" />

      <section v-else-if="view === 'dataset' && openDataset" class="flex min-w-0 flex-col gap-3" data-dataset-page :data-dataset-id="openDataset.id">
        <div class="flex flex-wrap items-end gap-x-4 gap-y-2 border-b">
          <h3 class="min-w-0 flex-1 truncate pb-2 font-semibold">{{ openDataset.name }}</h3>
          <nav class="flex gap-1" aria-label="Dataset">
            <button v-for="[key, bn, en] in DATASET_TABS" :key="key" type="button" :data-dataset-tab="key"
              :aria-current="datasetTab === key ? 'page' : undefined"
              class="-mb-px border-b-2 px-3 py-1.5 text-sm font-medium"
              :class="datasetTab === key ? 'border-foreground text-foreground' : 'border-transparent text-muted-foreground hover:text-foreground'"
              @click="openTab(key)">{{ bn }} <span class="text-xs opacity-70">{{ en }}</span></button>
          </nav>
        </div>

        <MatrixReport v-if="datasetTab === 'report'" :dataset="openDataset" :can-ask="canAsk" :can-edit-dashboards="canEditDashboards"
          @drill="drill" @quality="openTab('quality')" @definition="(k) => go({ section: 'definitions', kpi: k })" />

        <MatrixQuality v-else-if="datasetTab === 'quality'" :dataset="openDataset" :can-fix="canFix"
          @show="show" @close="go({})" @fixed="refresh" />

        <MatrixViewer v-else :dataset="openDataset" :highlight="String(route.query.highlight ?? '')"
          @close="go({})" @clear-highlight="openTab('data')" />
      </section>
    </template>
  </div>
</template>
