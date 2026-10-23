<script setup>
// One item: everything the public card shows, its sources, a status change
// (logged), corrections (visible) and the number check (rule 4) with "verified
// by hand". Saving sends the whole item; the server replaces its advisories,
// timeline and sources with what is here.
import { computed, ref, watch } from 'vue'
import { ChevronDown, ChevronRight, Plus, X } from '@lucide/vue'
import {
  addTruthCorrection, changeTruthStatus, createTruthItem, deleteTruthItem, unverifyTruthNumber, updateTruthItem, verifyTruthNumber,
} from '@/services/index.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  formatDhaka, fromUtcInput, LEVELS, SEVERITIES, SOURCE_KINDS, STATUSES, statusTone, toUtcInput,
} from '@/lib/truth-admin.js'

const props = defineProps({
  issueId: { type: Number, required: true },
  /** null: a new item. */
  item: { type: Object, default: null },
  techs: { type: Array, required: true },
  numbers: { type: Array, default: () => [] },
  canEdit: { type: Boolean, default: false },
})
const emit = defineEmits(['saved', 'cancel'])

const open = ref(!props.item)
const error = ref('')
const field = 'h-9 w-full rounded-md border border-input bg-transparent px-3 text-sm'
const area = 'min-h-20 w-full rounded-md border border-input bg-transparent px-3 py-2 text-sm'

function fromItem(i) {
  return {
    techId: i?.techId ?? props.techs[0]?.id ?? 0, severity: i?.severity ?? 'medium', status: i?.status ?? 'ongoing',
    headlineBn: i?.headlineBn ?? '', headlineEn: i?.headlineEn ?? '', summaryBn: i?.summaryBn ?? '', summaryEn: i?.summaryEn ?? '',
    impactBn: i?.impactBn ?? '', impactEn: i?.impactEn ?? '', whatToDoBn: i?.whatToDoBn ?? '', whatToDoEn: i?.whatToDoEn ?? '',
    patchScheduledAt: toUtcInput(i?.patchScheduledAt), tags: (i?.tags ?? []).join(', '), authorName: i?.authorName ?? '', order: i?.order ?? 0,
    advisories: (i?.advisories ?? []).map((a) => ({ ...a, cvss: a.cvss ?? '', fixedVersions: (a.fixedVersions ?? []).join(', ') })),
    timeline: (i?.timeline ?? []).map((e) => ({ ...e, occurredAt: toUtcInput(e.occurredAt) })),
    sources: (i?.sources ?? []).map((s) => ({ ...s })),
  }
}
const form = ref(fromItem(props.item))
watch(() => props.item, (i) => { form.value = fromItem(i) })

const list = (s) => String(s ?? '').split(',').map((x) => x.trim()).filter(Boolean)
const nul = (s) => (String(s ?? '').trim() ? String(s).trim() : null)

function payload() {
  const f = form.value
  return {
    techId: Number(f.techId), severity: f.severity, status: props.item ? null : f.status,
    headlineBn: f.headlineBn, headlineEn: nul(f.headlineEn), summaryBn: f.summaryBn, summaryEn: nul(f.summaryEn),
    impactBn: nul(f.impactBn), impactEn: nul(f.impactEn), whatToDoBn: nul(f.whatToDoBn), whatToDoEn: nul(f.whatToDoEn),
    patchScheduledAt: fromUtcInput(f.patchScheduledAt), tags: list(f.tags), authorName: f.authorName, order: Number(f.order) || 0,
    advisories: f.advisories.map((a) => ({
      cveId: nul(a.cveId), cvss: a.cvss === '' || a.cvss === null ? null : Number(a.cvss), titleBn: nul(a.titleBn), titleEn: nul(a.titleEn),
      affectedRange: nul(a.affectedRange), fixedVersions: list(a.fixedVersions), exploitedInWild: !!a.exploitedInWild, kevListed: !!a.kevListed,
    })),
    timeline: f.timeline.map((e) => ({ occurredAt: fromUtcInput(e.occurredAt), level: e.level, textBn: e.textBn, textEn: nul(e.textEn) })),
    sources: f.sources.map((s) => ({ url: s.url, publisher: s.publisher, author: nul(s.author), title: s.title, publishedOn: nul(s.publishedOn), kind: s.kind, fetchedText: s.fetchedText ?? '' })),
  }
}

async function run(fn) {
  error.value = ''
  try { await fn(); emit('saved') } catch (e) { error.value = e.message }
}
const save = () => run(() => (props.item ? updateTruthItem(props.item.id, payload()) : createTruthItem(props.issueId, payload())))
const remove = () => { if (confirm('Delete this item?')) run(() => deleteTruthItem(props.item.id)) }

const addAdvisory = () => form.value.advisories.push({ cveId: '', cvss: '', titleBn: '', titleEn: '', affectedRange: '', fixedVersions: '', exploitedInWild: false, kevListed: false })
const addEvent = () => form.value.timeline.push({ occurredAt: '', level: 'INFO', textBn: '', textEn: '' })
const addSource = () => form.value.sources.push({ url: 'https://', publisher: '', author: '', title: '', publishedOn: '', kind: 'official', fetchedText: '' })

// ---- a status change: logged with its time ----
const status = ref({ status: '', patchScheduledAt: '', textBn: '', textEn: '' })
const changeStatus = () => run(async () => {
  await changeTruthStatus(props.item.id, { status: status.value.status, patchScheduledAt: fromUtcInput(status.value.patchScheduledAt), textBn: nul(status.value.textBn), textEn: nul(status.value.textEn) })
  status.value = { status: '', patchScheduledAt: '', textBn: '', textEn: '' }
})

// ---- a correction: visible, never silent ----
const correction = ref({ textBn: '', textEn: '' })
const addCorrection = () => run(async () => {
  await addTruthCorrection(props.item.id, { textBn: correction.value.textBn, textEn: nul(correction.value.textEn) })
  correction.value = { textBn: '', textEn: '' }
})

// ---- the number check ----
const notes = ref({})
const unfound = computed(() => props.numbers.filter((n) => !n.ok).length)
const verify = (token) => run(() => verifyTruthNumber(props.item.id, token, notes.value[token] ?? ''))
const unverify = (token) => run(() => unverifyTruthNumber(props.item.id, token))

const techName = computed(() => props.techs.find((t) => t.id === Number(form.value.techId))?.name ?? '?')
</script>

<template>
  <article class="rounded-lg border bg-card" :data-truth-item="item?.id ?? 'new'">
    <button type="button" class="flex w-full flex-wrap items-center gap-2 p-3 text-left" @click="open = !open">
      <ChevronDown v-if="open" class="size-4" aria-hidden="true" /><ChevronRight v-else class="size-4" aria-hidden="true" />
      <span class="font-medium">{{ techName }}</span>
      <span class="min-w-0 flex-1 truncate text-sm">{{ form.headlineBn || 'New item' }}</span>
      <span v-if="item" class="text-sm font-medium" :class="statusTone(item.status)">{{ item.status }}</span>
      <Badge v-if="unfound" variant="destructive">{{ unfound }} number(s) not in a source</Badge>
    </button>

    <div v-if="open" class="grid gap-4 border-t p-4">
      <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>
      <fieldset :disabled="!canEdit" class="grid gap-3 sm:grid-cols-3">
        <label class="text-sm">প্রযুক্তি <span class="text-muted-foreground">Technology</span>
          <select v-model="form.techId" :class="field"><option v-for="t in techs" :key="t.id" :value="t.id">{{ t.name }}</option></select></label>
        <label class="text-sm">ঝুঁকি <span class="text-muted-foreground">Severity</span>
          <select v-model="form.severity" :class="field"><option v-for="s in SEVERITIES" :key="s">{{ s }}</option></select></label>
        <label v-if="!item" class="text-sm">অবস্থা <span class="text-muted-foreground">First status</span>
          <select v-model="form.status" :class="field"><option v-for="s in STATUSES" :key="s">{{ s }}</option></select></label>
        <label class="text-sm sm:col-span-3">শিরোনাম (বাংলা) <span class="text-muted-foreground">Headline, Bangla — required</span><input v-model="form.headlineBn" :class="field" maxlength="200"></label>
        <label class="text-sm sm:col-span-3">Headline (English)<input v-model="form.headlineEn" :class="field" maxlength="200"></label>
        <label class="text-sm sm:col-span-3">সারসংক্ষেপ (বাংলা) <span class="text-muted-foreground">Summary in our own words; cite sources as [1], [2]</span><textarea v-model="form.summaryBn" :class="area" /></label>
        <label class="text-sm sm:col-span-3">Summary (English)<textarea v-model="form.summaryEn" :class="area" /></label>
        <label class="text-sm sm:col-span-3">প্রভাব (বাংলা) <span class="text-muted-foreground">Impact</span><textarea v-model="form.impactBn" :class="area" /></label>
        <label class="text-sm sm:col-span-3">Impact (English)<textarea v-model="form.impactEn" :class="area" /></label>
        <label class="text-sm sm:col-span-3">এখন যা করবেন (বাংলা) <span class="text-muted-foreground">What to do — needed when urgent and there is no guide; label a temporary step "temporary"</span><textarea v-model="form.whatToDoBn" :class="area" /></label>
        <label class="text-sm sm:col-span-3">What to do (English)<textarea v-model="form.whatToDoEn" :class="area" /></label>
        <label class="text-sm">প্যাচের সময় <span class="text-muted-foreground">Patch time, UTC</span><input v-model="form.patchScheduledAt" type="datetime-local" :class="field">
          <span v-if="form.patchScheduledAt" class="text-xs text-muted-foreground">= {{ formatDhaka(fromUtcInput(form.patchScheduledAt)) }}</span></label>
        <label class="text-sm">ট্যাগ <span class="text-muted-foreground">Tags, comma-separated</span><input v-model="form.tags" :class="field"></label>
        <label class="text-sm">লেখক <span class="text-muted-foreground">Byline</span><input v-model="form.authorName" :class="field"></label>
        <label class="text-sm">ক্রম <span class="text-muted-foreground">Order</span><input v-model="form.order" type="number" :class="field"></label>
      </fieldset>

      <fieldset :disabled="!canEdit" class="grid gap-2">
        <legend class="flex w-full items-center gap-2 text-sm font-semibold">সূত্র <span class="font-normal text-muted-foreground">Sources — public only; paste the text the numbers come from (never published)</span>
          <Button type="button" size="sm" variant="ghost" @click="addSource"><Plus class="size-4" /></Button></legend>
        <div v-for="(s, n) in form.sources" :key="n" class="grid gap-2 rounded-md border p-3 sm:grid-cols-6">
          <span class="font-mono text-sm sm:col-span-6">[{{ n + 1 }}] <button type="button" class="float-right" aria-label="Remove source" @click="form.sources.splice(n, 1)"><X class="size-4" /></button></span>
          <input v-model="s.url" placeholder="https://…" :class="field" class="sm:col-span-3">
          <input v-model="s.publisher" placeholder="Publisher" :class="field" class="sm:col-span-2">
          <select v-model="s.kind" :class="field"><option v-for="k in SOURCE_KINDS" :key="k">{{ k }}</option></select>
          <input v-model="s.title" placeholder="Title" :class="field" class="sm:col-span-3">
          <input v-model="s.author" placeholder="Author (optional)" :class="field" class="sm:col-span-2">
          <input v-model="s.publishedOn" type="date" :class="field">
          <textarea v-model="s.fetchedText" placeholder="The source's public text, for the number check" :class="area" class="sm:col-span-6" />
        </div>
      </fieldset>

      <fieldset :disabled="!canEdit" class="grid gap-2">
        <legend class="flex w-full items-center gap-2 text-sm font-semibold">নিরাপত্তা পরামর্শ <span class="font-normal text-muted-foreground">Advisories</span>
          <Button type="button" size="sm" variant="ghost" @click="addAdvisory"><Plus class="size-4" /></Button></legend>
        <div v-for="(a, n) in form.advisories" :key="n" class="grid gap-2 rounded-md border p-3 sm:grid-cols-6">
          <input v-model="a.cveId" placeholder="CVE-yyyy-nnnn" :class="field" class="sm:col-span-2">
          <input v-model="a.cvss" type="number" step="0.1" min="0" max="10" placeholder="CVSS" :class="field">
          <input v-model="a.affectedRange" placeholder="Affected range" :class="field" class="sm:col-span-2">
          <button type="button" aria-label="Remove advisory" @click="form.advisories.splice(n, 1)"><X class="size-4" /></button>
          <input v-model="a.titleBn" placeholder="শিরোনাম (বাংলা)" :class="field" class="sm:col-span-3">
          <input v-model="a.titleEn" placeholder="Title (English)" :class="field" class="sm:col-span-3">
          <input v-model="a.fixedVersions" placeholder="Fixed in: 3.1.7, 3.2.4" :class="field" class="sm:col-span-4">
          <label class="text-sm"><input v-model="a.exploitedInWild" type="checkbox"> exploited</label>
          <label class="text-sm"><input v-model="a.kevListed" type="checkbox"> KEV</label>
        </div>
      </fieldset>

      <fieldset :disabled="!canEdit" class="grid gap-2">
        <legend class="flex w-full items-center gap-2 text-sm font-semibold">সময়রেখা <span class="font-normal text-muted-foreground">Timeline (times in UTC)</span>
          <Button type="button" size="sm" variant="ghost" @click="addEvent"><Plus class="size-4" /></Button></legend>
        <div v-for="(e, n) in form.timeline" :key="n" class="grid gap-2 rounded-md border p-3 sm:grid-cols-6">
          <input v-model="e.occurredAt" type="datetime-local" :class="field" class="sm:col-span-2">
          <select v-model="e.level" :class="field"><option v-for="l in LEVELS" :key="l">{{ l }}</option></select>
          <span class="text-xs text-muted-foreground sm:col-span-2">{{ e.occurredAt ? formatDhaka(fromUtcInput(e.occurredAt)) : '' }}</span>
          <button type="button" aria-label="Remove entry" @click="form.timeline.splice(n, 1)"><X class="size-4" /></button>
          <input v-model="e.textBn" placeholder="বাংলা — required" :class="field" class="sm:col-span-3">
          <input v-model="e.textEn" placeholder="English" :class="field" class="sm:col-span-3">
        </div>
      </fieldset>

      <div v-if="canEdit" class="flex gap-2">
        <Button size="sm" data-truth-save-item @click="save">Save item</Button>
        <Button v-if="!item" size="sm" variant="ghost" @click="emit('cancel')">Cancel</Button>
        <Button v-else size="sm" variant="ghost" class="text-destructive" @click="remove">Delete</Button>
      </div>

      <template v-if="item">
        <section class="rounded-md border p-3" data-truth-numbers>
          <h5 class="text-sm font-semibold">সংখ্যা যাচাই <span class="font-normal text-muted-foreground">Number check — every number must be in a source (saved values)</span></h5>
          <p v-if="!numbers.length" class="text-sm text-muted-foreground">No numbers.</p>
          <table v-else class="mt-2 w-full text-sm">
            <tbody>
              <tr v-for="n in numbers" :key="n.token + n.kind" class="border-t align-top">
                <td class="py-1 pr-2 font-mono" :class="n.ok ? '' : 'font-semibold text-destructive'">{{ n.token }}</td>
                <td class="py-1 pr-2 text-muted-foreground">{{ n.kind }} · {{ n.fields.join(', ') }}</td>
                <td class="py-1">
                  <span v-if="n.found" class="text-green-700 dark:text-green-400">✓ in a source</span>
                  <span v-else-if="n.handNote">by hand: “{{ n.handNote }}” <button v-if="canEdit" type="button" class="underline" @click="unverify(n.token)">undo</button></span>
                  <span v-else-if="canEdit" class="flex flex-wrap gap-1">
                    <input v-model="notes[n.token]" placeholder="How it was verified" :class="field" class="h-8 flex-1">
                    <Button size="sm" variant="outline" @click="verify(n.token)">Verified by hand</Button>
                  </span>
                  <span v-else class="text-destructive">not in any source</span>
                </td>
              </tr>
            </tbody>
          </table>
        </section>

        <section class="rounded-md border p-3">
          <h5 class="text-sm font-semibold">অবস্থার পরিবর্তন <span class="font-normal text-muted-foreground">Status — each change is logged and shown with its time</span></h5>
          <ol class="mt-1 grid gap-1 text-sm">
            <li v-for="(e, n) in item.statusEvents" :key="n" class="text-muted-foreground">{{ formatDhaka(e.at) }} · {{ e.from ?? '—' }} → <span :class="statusTone(e.to)">{{ e.to }}</span> · {{ e.by }}<template v-if="e.textBn"> · {{ e.textBn }}</template></li>
          </ol>
          <form v-if="canEdit" class="mt-2 grid gap-2 sm:grid-cols-4" @submit.prevent="changeStatus">
            <select v-model="status.status" required :class="field"><option value="" disabled>new status…</option><option v-for="s in STATUSES.filter((x) => x !== item.status)" :key="s">{{ s }}</option></select>
            <input v-if="status.status === 'patch_scheduled'" v-model="status.patchScheduledAt" type="datetime-local" :class="field" title="Patch time, UTC">
            <input v-model="status.textBn" placeholder="কী বদলাল (বাংলা)" :class="field">
            <input v-model="status.textEn" placeholder="What changed (English)" :class="field">
            <Button type="submit" size="sm" variant="outline" class="sm:col-span-4 sm:justify-self-start">Change status</Button>
          </form>
        </section>

        <section class="rounded-md border p-3">
          <h5 class="text-sm font-semibold">সংশোধন <span class="font-normal text-muted-foreground">Corrections — shown to readers; needed when published words change</span></h5>
          <ul class="mt-1 grid gap-1 text-sm">
            <li v-for="(c, n) in item.corrections" :key="n" class="text-muted-foreground">{{ formatDhaka(c.at) }} · {{ c.by }} · {{ c.textBn }}</li>
          </ul>
          <form v-if="canEdit" class="mt-2 grid gap-2 sm:grid-cols-3" @submit.prevent="addCorrection">
            <input v-model="correction.textBn" required placeholder="কী সংশোধন হলো (বাংলা)" :class="field">
            <input v-model="correction.textEn" placeholder="What was corrected (English)" :class="field">
            <Button type="submit" size="sm" variant="outline" class="justify-self-start">Add correction</Button>
          </form>
        </section>
      </template>
    </div>
  </article>
</template>
