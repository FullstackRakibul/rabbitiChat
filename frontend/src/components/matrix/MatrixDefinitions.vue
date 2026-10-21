<script setup>
/**
 * ম্যাট্রিক্স — সংজ্ঞা Definitions (docs/MATRIX_PROMPT.md §3.7, §3.11): every KPI with its
 * formula in words, unit and thresholds, and every field of the catalog with its
 * sensitivity. Thresholds (green / amber / red) are set here by whoever has
 * action:matrix.kpis.edit; the server checks that again and refuses thresholds
 * out of order.
 */
import { computed, onMounted, reactive, ref } from 'vue'
import { Loader2, Pencil, Save, X } from '@lucide/vue'
import { getMatrixCatalog, listMatrixKpis, updateMatrixKpi } from '@/services/index.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'

const props = defineProps({
  canKpis: { type: Boolean, default: false },      // section:matrix.dashboards (the KPI list)
  canFields: { type: Boolean, default: false },    // section:matrix.data (the catalog)
  canEditKpis: { type: Boolean, default: false },  // action:matrix.kpis.edit
})

const kpis = ref([])
const fields = ref([])
const error = ref('')
const editing = ref('')
const saving = ref(false)
const form = reactive({ direction: 'higher', green: '', amber: '' })

const AREAS = {
  manpower: ['জনবল', 'Manpower'], lines: ['লাইন', 'Lines'], movement: ['যোগদান ও বিচ্ছেদ', 'Joiners and separations'],
  grade: ['চাকরিকাল ও গ্রেড', 'Tenure and grade'], attendance: ['হাজিরা', 'Attendance'], salary: ['বেতন', 'Salary'], quality: ['ডেটার মান', 'Quality'],
}
const byArea = computed(() => Object.keys(AREAS).map((a) => [a, kpis.value.filter((k) => k.area === a)]).filter(([, list]) => list.length))
const SENS = { public: 'outline', personal: 'secondary', salary: 'secondary', sensitive: 'destructive' }

function thresholdText(t) {
  if (!t || !t.direction) return 'নেই · none'
  return t.direction === 'higher'
    ? `≥ ${t.green} green · ≥ ${t.amber} amber · below red`
    : `≤ ${t.green} green · ≤ ${t.amber} amber · above red`
}

function edit(k) {
  editing.value = k.key
  form.direction = k.thresholds?.direction ?? 'higher'
  form.green = k.thresholds?.green ?? ''
  form.amber = k.thresholds?.amber ?? ''
}

async function save(k, clear = false) {
  saving.value = true
  error.value = ''
  try {
    const thresholds = clear ? { cleared: true } : { direction: form.direction, green: Number(form.green), amber: Number(form.amber) }
    const updated = await updateMatrixKpi(k.key, { thresholds })
    kpis.value = kpis.value.map((x) => (x.key === k.key ? updated : x))
    editing.value = ''
  } catch (e) {
    error.value = e.message
  } finally {
    saving.value = false
  }
}

onMounted(async () => {
  try {
    const [k, f] = await Promise.all([
      props.canKpis ? listMatrixKpis() : Promise.resolve({ kpis: [] }),
      props.canFields ? getMatrixCatalog() : Promise.resolve({ fields: [] }),
    ])
    kpis.value = k.kpis
    fields.value = f.fields
  } catch (e) {
    error.value = e.message
  }
})
</script>

<template>
  <div class="flex min-w-0 flex-col gap-4" data-matrix-definitions>
    <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>

    <section v-if="canKpis" class="rounded-lg border bg-card">
      <h3 class="border-b p-3 font-semibold">কেপিআই <span class="text-sm font-normal text-muted-foreground">KPIs — {{ kpis.length }}</span></h3>
      <div v-for="[area, list] in byArea" :key="area" class="border-b last:border-b-0">
        <h4 class="bg-muted/40 px-3 py-1.5 text-xs font-semibold uppercase tracking-wide text-muted-foreground">{{ AREAS[area][0] }} · {{ AREAS[area][1] }}</h4>
        <ul class="divide-y">
          <li v-for="k in list" :key="k.key" class="flex flex-col gap-1 p-3 text-sm" :data-kpi="k.key">
            <div class="flex flex-wrap items-baseline gap-x-2">
              <span class="font-medium">{{ k.nameBn }}</span>
              <span class="text-xs text-muted-foreground">{{ k.nameEn }}</span>
              <code class="text-[11px] text-muted-foreground">{{ k.key }}</code>
              <Badge variant="outline" class="ml-auto">{{ k.unit }}</Badge>
            </div>
            <p class="text-muted-foreground" data-kpi-words>{{ k.words }}</p>
            <div v-if="editing !== k.key" class="flex flex-wrap items-center gap-2 text-xs">
              <span class="text-muted-foreground">সীমা <span class="opacity-70">Thresholds</span>: {{ thresholdText(k.thresholds) }}</span>
              <Button v-if="canEditKpis" size="sm" variant="ghost" class="h-7" data-threshold-edit @click="edit(k)"><Pencil class="size-3.5" /> Edit</Button>
            </div>
            <form v-else class="flex flex-wrap items-end gap-2 text-xs" @submit.prevent="save(k)">
              <label class="flex flex-col text-muted-foreground">ভালো <span class="opacity-70">Better when</span>
                <select v-model="form.direction" class="h-8 rounded-md border bg-background px-1 text-sm text-foreground" data-th-direction>
                  <option value="higher">বেশি · higher</option>
                  <option value="lower">কম · lower</option>
                </select>
              </label>
              <label class="flex flex-col text-muted-foreground">সবুজ <span class="opacity-70">Green</span>
                <input v-model="form.green" type="number" step="any" required class="h-8 w-24 rounded-md border bg-background px-1.5 text-sm text-foreground" data-th-green />
              </label>
              <label class="flex flex-col text-muted-foreground">হলুদ <span class="opacity-70">Amber</span>
                <input v-model="form.amber" type="number" step="any" required class="h-8 w-24 rounded-md border bg-background px-1.5 text-sm text-foreground" data-th-amber />
              </label>
              <Button size="sm" type="submit" :disabled="saving" data-threshold-save>
                <Loader2 v-if="saving" class="size-3.5 animate-spin" /><Save v-else class="size-3.5" /> Save
              </Button>
              <Button size="sm" type="button" variant="ghost" :disabled="saving" data-threshold-clear @click="save(k, true)">Clear</Button>
              <Button size="sm" type="button" variant="ghost" aria-label="Cancel" @click="editing = ''"><X class="size-3.5" /></Button>
            </form>
          </li>
        </ul>
      </div>
    </section>

    <section v-if="canFields" class="rounded-lg border bg-card">
      <h3 class="border-b p-3 font-semibold">ক্ষেত্র <span class="text-sm font-normal text-muted-foreground">Fields — {{ fields.length }}</span></h3>
      <div class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead>
            <tr class="text-left text-xs text-muted-foreground">
              <th class="px-3 py-2 font-medium">নাম <span class="opacity-70">Name</span></th>
              <th class="px-3 py-2 font-medium">Key</th>
              <th class="px-3 py-2 font-medium">Group</th>
              <th class="px-3 py-2 font-medium">Role · type</th>
              <th class="px-3 py-2 font-medium">Sensitivity</th>
            </tr>
          </thead>
          <tbody class="divide-y">
            <tr v-for="f in fields" :key="f.key" :data-field="f.key">
              <td class="px-3 py-1.5">{{ f.bn }} <span class="text-xs text-muted-foreground">{{ f.en }}</span></td>
              <td class="px-3 py-1.5"><code class="text-xs">{{ f.key }}</code></td>
              <td class="px-3 py-1.5 text-xs text-muted-foreground">{{ f.group }}<span v-if="f.derived"> · derived</span></td>
              <td class="px-3 py-1.5 text-xs text-muted-foreground">{{ f.role }} · {{ f.type }}</td>
              <td class="px-3 py-1.5"><Badge :variant="SENS[f.sensitivity] ?? 'outline'">{{ f.sensitivity }}</Badge></td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  </div>
</template>
