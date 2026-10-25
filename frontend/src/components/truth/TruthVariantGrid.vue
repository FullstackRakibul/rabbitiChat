<script setup>
/**
 * The variant grid (docs/TRUTH_PROMPT.md §6.2, §6.3): every platform × every
 * variable combination the guide declares, rendered by the server, with the
 * safety check's findings for each. Opening it is recorded — publishing needs it
 * opened since the last edit. A warning is acknowledged here, with a note; a
 * block can't be, it has to be fixed in the step.
 */
import { computed, onMounted, ref } from 'vue'
import { ackTruthFinding, openTruthGuideGrid } from '@/services/index.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'

const props = defineProps({
  id: { type: Number, required: true },
  canAck: { type: Boolean, default: false },
})
const emit = defineEmits(['changed'])

const grid = ref(null)
const error = ref('')
const notes = ref({})
const only = ref('all')      // all | findings | errors
const openKey = ref(null)

async function load() {
  error.value = ''
  try { grid.value = await openTruthGuideGrid(props.id) } catch (e) { error.value = e.message }
}
async function ack(f) {
  error.value = ''
  try { await ackTruthFinding(props.id, f.key, notes.value[f.key] ?? ''); await load(); emit('changed') } catch (e) { error.value = e.message }
}

const rows = computed(() => (grid.value?.variants ?? []).filter((v) =>
  only.value === 'all' || (only.value === 'findings' ? v.findings.length : v.errors.length)))
const label = (v) => Object.entries(v.values).map(([k, x]) => (k === 'platform' ? x : `${k} ${x}`)).join(' · ')

onMounted(async () => { await load(); emit('changed') })
</script>

<template>
  <section v-if="grid" class="rounded-lg border bg-card p-4" data-truth-variant-grid>
    <div class="flex flex-wrap items-center gap-2">
      <h4 class="flex-1 font-semibold">ভ্যারিয়েন্ট গ্রিড <span class="text-sm font-normal text-muted-foreground">Variant grid · {{ grid.summary.variants }} variants{{ grid.capped ? ` (of ${grid.total}, capped)` : '' }}</span></h4>
      <Badge :variant="grid.summary.blocks ? 'destructive' : 'secondary'" data-grid-blocks>{{ grid.summary.blocks }} blocked</Badge>
      <Badge variant="outline" data-grid-warnings>{{ grid.summary.warnings }} warnings · {{ grid.summary.unacknowledged }} not acknowledged</Badge>
      <Badge v-if="grid.summary.templateErrors" variant="destructive">{{ grid.summary.templateErrors }} template errors</Badge>
      <select v-model="only" class="h-8 rounded-md border border-input bg-transparent px-2 text-sm">
        <option value="all">All variants</option><option value="findings">With findings</option><option value="errors">With template errors</option>
      </select>
    </div>
    <p v-if="error" class="mt-2 rounded-md border border-destructive/40 bg-destructive/10 p-2 text-sm text-destructive">{{ error }}</p>

    <div class="mt-3 overflow-x-auto">
      <table class="w-full text-sm">
        <thead><tr class="text-left text-xs text-muted-foreground"><th class="py-1 pr-2">Variant</th><th class="py-1 pr-2">Steps</th><th class="py-1 pr-2">Findings</th><th /></tr></thead>
        <tbody>
          <template v-for="v in rows" :key="v.key">
            <tr class="border-t align-top" :data-variant="v.key">
              <td class="py-2 pr-2 font-mono text-xs">{{ label(v) }}</td>
              <td class="py-2 pr-2">{{ v.steps.length }}</td>
              <td class="py-2 pr-2">
                <span v-for="f in v.findings" :key="f.key" class="mr-1 inline-block rounded px-1.5 text-xs font-semibold"
                  :class="f.level === 'block' ? 'bg-red-600 text-white' : f.acknowledged ? 'bg-green-100 text-green-900 dark:bg-green-900/40 dark:text-green-200' : 'bg-amber-100 text-amber-900 dark:bg-amber-900/40 dark:text-amber-200'"
                  :data-rule="f.ruleId">{{ f.ruleId }}{{ f.acknowledged ? ' ✓' : '' }}</span>
                <span v-for="e in v.errors" :key="e" class="block text-xs text-destructive">{{ e }}</span>
                <span v-if="!v.findings.length && !v.errors.length" class="text-xs text-green-700 dark:text-green-400">clean</span>
              </td>
              <td class="py-2 text-right"><Button size="sm" variant="ghost" @click="openKey = openKey === v.key ? null : v.key">{{ openKey === v.key ? 'Hide' : 'Show' }}</Button></td>
            </tr>
            <tr v-if="openKey === v.key" class="border-t-0">
              <td colspan="4" class="pb-3">
                <ol class="grid gap-2">
                  <li v-for="s in v.steps" :key="s.order" class="rounded border p-2">
                    <p class="text-xs text-muted-foreground">step {{ s.order }} · {{ s.kind }} · {{ s.platform }} · {{ s.language }}</p>
                    <pre v-if="s.code" class="mt-1 overflow-x-auto rounded bg-muted p-2 text-xs">{{ s.code }}</pre>
                    <pre v-if="s.expected" class="mt-1 overflow-x-auto rounded bg-muted p-2 text-xs">{{ s.expected }}</pre>
                  </li>
                </ol>
                <ul class="mt-2 grid gap-2">
                  <li v-for="f in v.findings" :key="f.key" class="rounded border p-2 text-xs" :class="f.level === 'block' ? 'border-red-600' : ''">
                    <p class="font-semibold">{{ f.level }} · {{ f.ruleId }} · step {{ f.stepOrder }}</p>
                    <p>{{ f.message }}</p>
                    <pre class="mt-1 overflow-x-auto rounded bg-muted p-1">{{ f.line }}</pre>
                    <p v-if="f.acknowledged" class="text-green-700 dark:text-green-400">✓ acknowledged by {{ f.acknowledged.by }}: “{{ f.acknowledged.note }}”</p>
                    <form v-else-if="f.level === 'warn' && canAck" class="mt-1 flex gap-1" @submit.prevent="ack(f)">
                      <input v-model="notes[f.key]" placeholder="Why this is acceptable here" class="h-8 flex-1 rounded-md border border-input bg-transparent px-2" :data-ack-input="f.key">
                      <Button size="sm" variant="outline" type="submit">Acknowledge</Button>
                    </form>
                    <p v-else-if="f.level === 'block'" class="text-destructive">Change the step: a block can't be acknowledged.</p>
                  </li>
                </ul>
              </td>
            </tr>
          </template>
        </tbody>
      </table>
    </div>
  </section>
  <p v-else-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>
</template>
