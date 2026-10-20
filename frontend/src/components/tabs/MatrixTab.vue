<script setup>
/**
 * ম্যাট্রিক্স · Matrix — RMG manpower and attendance data from Excel (docs/MATRIX_PROMPT.md).
 *
 * Phase M1 is the Data section: upload → profile and mapping → import → quality
 * → the dataset viewer. Dashboards, Ask and Definitions arrive in M2–M4.
 *
 * Which views the user gets comes from the server (/api/me/access: sections and
 * actions); the server checks every route again. The view is in the URL —
 * ?upload=, ?dataset= (with &highlight=), ?quality= — so a citation or an
 * issue's "Show" is a link, and back works.
 */
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { FileSpreadsheet, Loader2 } from '@lucide/vue'
import {
  getMatrixCatalog, getMatrixDataset, getMatrixProfile, importMatrixUpload, listMatrixDatasets, listMatrixUploads,
} from '@/services/index.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import MatrixUpload from '@/components/matrix/MatrixUpload.vue'
import MatrixMapping from '@/components/matrix/MatrixMapping.vue'
import MatrixDatasets from '@/components/matrix/MatrixDatasets.vue'
import MatrixQuality from '@/components/matrix/MatrixQuality.vue'
import MatrixViewer from '@/components/matrix/MatrixViewer.vue'
import { formatBytes, formatDate } from '@/lib/matrix-format.js'

const props = defineProps({
  /** The user's section keys and action keys, as /api/me/access gave them. */
  sections: { type: Array, default: () => [] },
  actions: { type: Array, default: () => [] },
})

const route = useRoute()
const router = useRouter()

const canData = computed(() => props.sections.includes('section:matrix.data'))
const canUpload = computed(() => props.actions.includes('action:matrix.upload'))
const canFix = computed(() => props.actions.includes('action:matrix.fix'))

const datasets = ref([])
const uploads = ref([])
const catalog = ref([])
const error = ref('')
const notice = ref('')
const openUpload = ref(null)      // { id, fileName, reportDate, profile }
const openDataset = ref(null)
const busy = ref(false)

const view = computed(() => {
  if (route.query.upload) return 'mapping'
  if (route.query.quality) return 'quality'
  if (route.query.dataset) return 'viewer'
  return 'home'
})

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
  try {
    for (const u of list) {
      const selected = u.profile.sheets.flatMap((s) => s.tables).filter((t) => t.selected)
      if (selected.length && selected.every((t) => t.template)) {
        const res = await importMatrixUpload(u.id)
        notice.value = `${u.fileName}: ${res.message}.`
      } else needMapping.push(u)
    }
    await refresh()
    if (needMapping.length) {
      openUpload.value = { id: needMapping[0].id, fileName: needMapping[0].fileName, reportDate: needMapping[0].reportDate, profile: needMapping[0].profile }
      go({ upload: needMapping[0].id })
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
  go(first ? { quality: first.id } : {})
}

function show(issue) {
  go({ dataset: openDataset.value.id, highlight: issue.highlight })
}

watch(() => route.fullPath, () => { if (route.path === '/matrix') follow() })
watch(canData, (v) => { if (v) refresh().then(follow) })
onMounted(async () => {
  if (!canData.value) return
  await refresh()
  getMatrixCatalog().then((c) => { catalog.value = c.fields }).catch(() => {})
  await follow()
})
</script>

<template>
  <div class="flex h-full min-h-0 min-w-0 flex-col gap-4 overflow-y-auto p-4 sm:p-6" data-matrix-tab>
    <div>
      <h2 class="flex items-center gap-2 text-lg font-semibold tracking-tight">
        <FileSpreadsheet class="size-5" aria-hidden="true" />
        ম্যাট্রিক্স <span class="text-sm font-normal text-muted-foreground">Matrix</span>
      </h2>
      <nav class="mt-2 flex gap-1" aria-label="Matrix sections">
        <button
          type="button"
          data-matrix-section="data"
          :aria-current="'page'"
          class="rounded-md bg-secondary px-3 py-1.5 text-sm font-medium text-secondary-foreground"
          @click="go({})"
        >ডেটা <span class="text-xs opacity-70">Data</span></button>
      </nav>
    </div>

    <p v-if="!canData" class="rounded-lg border bg-card p-4 text-sm text-muted-foreground">
      The Data section needs the “Data” permission of Matrix. Ask an administrator.
    </p>

    <template v-else>
      <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>
      <p v-if="notice" class="rounded-md border bg-card p-3 text-sm" role="status" data-matrix-notice>{{ notice }}</p>
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

      <MatrixQuality v-else-if="view === 'quality' && openDataset" :dataset="openDataset" :can-fix="canFix"
        @show="show" @close="go({})" @fixed="refresh" />

      <MatrixViewer v-else-if="view === 'viewer' && openDataset" :dataset="openDataset"
        :highlight="String(route.query.highlight ?? '')"
        @close="go({})" @clear-highlight="go({ dataset: openDataset.id })" />
    </template>
  </div>
</template>
