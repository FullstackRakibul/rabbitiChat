<script setup>
// One issue: its fields, the pre-publish checklist (§8), its items, and
// publishing. What publishing checks is the server's; this shows the answer.
import { computed, onMounted, ref } from 'vue'
import { ArrowLeft, Plus } from '@lucide/vue'
import {
  checkTruthIssue, deleteTruthIssue, getTruthIssue, listTruthTechs, publishTruthIssue, updateTruthIssue, withdrawTruthIssue,
} from '@/services/index.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import TruthItemEditor from '@/components/truth/TruthItemEditor.vue'
import { formatDhaka, STATUS_BADGE } from '@/lib/truth-admin.js'

const props = defineProps({
  id: { type: Number, required: true },
  can: { type: Object, required: true },
})
const emit = defineEmits(['close'])

const data = ref(null)
const techs = ref([])
const form = ref(null)
const check = ref(null)
const error = ref('')
const notice = ref('')
const adding = ref(false)
const field = 'h-9 w-full rounded-md border border-input bg-transparent px-3 text-sm'

const issue = computed(() => data.value?.issue)
const numbersFor = (itemId) => check.value?.items?.find((i) => i.itemId === itemId)?.numbers ?? []
const problems = computed(() => check.value?.problems ?? [])
const headline = (itemId) => data.value?.items.find((i) => i.id === itemId)?.headlineBn || `item ${itemId}`

async function load() {
  error.value = ''
  try {
    const [d, t] = await Promise.all([getTruthIssue(props.id), listTruthTechs()])
    data.value = d
    techs.value = t.techs
    const i = d.issue
    form.value = { slug: i.slug, titleBn: i.titleBn, titleEn: i.titleEn ?? '', subtitleBn: i.subtitleBn ?? '', subtitleEn: i.subtitleEn ?? '', weekStart: i.weekStart, weekEnd: i.weekEnd }
    check.value = await checkTruthIssue(props.id)
  } catch (e) { error.value = e.message }
}

async function act(fn, done) {
  error.value = ''; notice.value = ''
  try { await fn(); notice.value = done; await load() } catch (e) { error.value = e.message }
}
const saveIssue = () => act(() => updateTruthIssue(props.id, form.value), 'Saved.')
const withdraw = () => { if (confirm('Take this issue off the public site? What was published is kept.')) act(() => withdrawTruthIssue(props.id), 'Withdrawn.') }
async function remove() {
  if (!confirm('Delete this draft and its items?')) return
  try { await deleteTruthIssue(props.id); emit('close') } catch (e) { error.value = e.message }
}
async function publish() {
  error.value = ''; notice.value = ''
  try {
    const r = await publishTruthIssue(props.id)
    if (r.ok) notice.value = `Published ${formatDhaka(r.publishedAt)}.`
    else error.value = r.error
    await load()
  } catch (e) { error.value = e.message }
}

onMounted(load)
</script>

<template>
  <section v-if="data" class="flex flex-col gap-4" data-truth-issue-editor>
    <div class="flex flex-wrap items-center gap-2">
      <Button size="sm" variant="ghost" @click="emit('close')"><ArrowLeft class="size-4" /> সংখ্যা <span class="text-xs opacity-70">Issues</span></Button>
      <span class="font-mono text-sm">{{ issue.slug }}</span>
      <Badge :variant="STATUS_BADGE[issue.status]">{{ issue.status }}</Badge>
      <Badge v-if="issue.unpublishedChanges" variant="outline">unpublished changes</Badge>
      <span class="flex-1" />
      <a v-if="issue.status === 'published'" :href="`/truth/${issue.slug}?lang=bn`" target="_blank" rel="noopener" class="text-sm underline">public page ↗</a>
      <Button v-if="can.publish" size="sm" data-truth-publish :disabled="problems.length > 0" @click="publish">প্রকাশ <span class="text-xs opacity-70">{{ issue.status === 'published' ? 'Publish again' : 'Publish' }}</span></Button>
      <Button v-if="can.publish && issue.status === 'published'" size="sm" variant="outline" @click="withdraw">প্রত্যাহার <span class="text-xs opacity-70">Withdraw</span></Button>
      <Button v-if="can.edit && !issue.firstPublishedAt" size="sm" variant="ghost" class="text-destructive" @click="remove">Delete</Button>
    </div>
    <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>
    <p v-if="notice" class="rounded-md border bg-card p-3 text-sm">{{ notice }}</p>

    <!-- The pre-publish checklist: what blocks publishing, from the server. -->
    <section class="rounded-lg border p-4" :class="problems.length ? 'border-destructive/50 bg-destructive/5' : 'bg-card'" data-truth-checklist>
      <h4 class="font-semibold">প্রকাশের আগে <span class="text-sm font-normal text-muted-foreground">Pre-publish checklist</span></h4>
      <p v-if="!problems.length" class="mt-1 text-sm text-green-700 dark:text-green-400">✓ Clean: sources, Bangla, numbers, patch times and urgent notes all pass.</p>
      <ul v-else class="mt-2 grid gap-1 text-sm">
        <li v-for="(p, n) in problems" :key="n" class="text-destructive">
          <span class="font-medium">{{ p.itemId ? headline(p.itemId) : 'Issue' }}:</span> {{ p.message }}
        </li>
      </ul>
    </section>

    <form class="grid gap-3 rounded-lg border bg-card p-4 sm:grid-cols-2" @submit.prevent="saveIssue">
      <label class="text-sm">শিরোনাম (বাংলা) <span class="text-muted-foreground">Title, Bangla</span><input v-model="form.titleBn" :class="field" :disabled="!can.edit"></label>
      <label class="text-sm">Title (English)<input v-model="form.titleEn" :class="field" :disabled="!can.edit"></label>
      <label class="text-sm">উপশিরোনাম (বাংলা) <span class="text-muted-foreground">Subtitle, Bangla</span><input v-model="form.subtitleBn" :class="field" :disabled="!can.edit"></label>
      <label class="text-sm">Subtitle (English)<input v-model="form.subtitleEn" :class="field" :disabled="!can.edit"></label>
      <label class="text-sm">সপ্তাহ শুরু <span class="text-muted-foreground">Week starts</span><input v-model="form.weekStart" type="date" :class="field" :disabled="!can.edit"></label>
      <label class="text-sm">শেষ <span class="text-muted-foreground">Ends</span><input v-model="form.weekEnd" type="date" :class="field" :disabled="!can.edit"></label>
      <label class="text-sm">স্লাগ <span class="text-muted-foreground">Slug — fixed once published</span><input v-model="form.slug" :class="field" :disabled="!can.edit || !!issue.firstPublishedAt"></label>
      <div class="flex items-end"><Button v-if="can.edit" type="submit" size="sm">Save</Button></div>
    </form>

    <div class="flex items-center gap-2">
      <h4 class="flex-1 font-semibold">বিষয় <span class="text-sm font-normal text-muted-foreground">Items ({{ data.items.length }})</span></h4>
      <Button v-if="can.edit" size="sm" variant="outline" data-truth-add-item @click="adding = true"><Plus class="size-4" /> Item</Button>
    </div>
    <TruthItemEditor v-if="adding" :issue-id="id" :item="null" :techs="techs" :numbers="[]" :can-edit="can.edit" @saved="adding = false; load()" @cancel="adding = false" />
    <TruthItemEditor v-for="i in data.items" :key="i.id" :issue-id="id" :item="i" :techs="techs" :numbers="numbersFor(i.id)" :can-edit="can.edit" @saved="load" />

    <section v-if="data.snapshots.length" class="rounded-lg border bg-card p-4 text-sm">
      <h4 class="font-semibold">প্রকাশের ইতিহাস <span class="font-normal text-muted-foreground">Published snapshots</span></h4>
      <ul class="mt-2 grid gap-1 text-muted-foreground">
        <li v-for="s in data.snapshots" :key="s.id">{{ formatDhaka(s.at) }} · {{ s.by }} · site text v{{ s.siteContentVersion }}</li>
      </ul>
    </section>
  </section>
  <p v-else-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>
</template>
