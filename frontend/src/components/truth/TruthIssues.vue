<script setup>
// Every issue, newest week first, and a new draft.
import { onMounted, ref, watch } from 'vue'
import { Plus } from '@lucide/vue'
import { createTruthIssue, listTruthIssues } from '@/services/index.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { formatDhaka, isoWeekSlug, STATUS_BADGE } from '@/lib/truth-admin.js'

defineProps({ canEdit: { type: Boolean, default: false } })
const emit = defineEmits(['open'])

const issues = ref([])
const error = ref('')
const creating = ref(false)
const form = ref({ slug: '', weekStart: '', weekEnd: '', titleBn: '', titleEn: '', subtitleBn: '', subtitleEn: '' })
const field = 'h-9 w-full rounded-md border border-input bg-transparent px-3 text-sm'

async function refresh() {
  try { issues.value = (await listTruthIssues()).issues } catch (e) { error.value = e.message }
}

watch(() => form.value.weekStart, (d) => {
  if (!d) return
  const end = new Date(`${d}T00:00:00Z`); end.setUTCDate(end.getUTCDate() + 6)
  form.value.weekEnd = end.toISOString().slice(0, 10)
  form.value.slug = isoWeekSlug(d)
})

async function create() {
  error.value = ''
  try {
    const issue = await createTruthIssue(form.value)
    creating.value = false
    emit('open', issue.id)
  } catch (e) { error.value = e.message }
}

onMounted(refresh)
</script>

<template>
  <section class="flex flex-col gap-3" data-truth-issues>
    <div class="flex items-center gap-2">
      <h3 class="flex-1 font-semibold">সংখ্যা <span class="text-sm font-normal text-muted-foreground">Issues</span></h3>
      <Button v-if="canEdit" size="sm" variant="outline" data-truth-new-issue @click="creating = !creating"><Plus class="size-4" /> নতুন <span class="text-xs opacity-70">New</span></Button>
    </div>
    <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>

    <form v-if="creating" class="grid gap-3 rounded-lg border bg-card p-4 sm:grid-cols-3" @submit.prevent="create">
      <label class="text-sm">সপ্তাহ শুরু <span class="text-muted-foreground">Week starts</span><input v-model="form.weekStart" type="date" required :class="field"></label>
      <label class="text-sm">শেষ <span class="text-muted-foreground">Ends</span><input v-model="form.weekEnd" type="date" required :class="field"></label>
      <label class="text-sm">স্লাগ <span class="text-muted-foreground">Slug</span><input v-model="form.slug" required pattern="[a-z0-9][a-z0-9-]{0,39}" :class="field"></label>
      <label class="text-sm sm:col-span-3">শিরোনাম (বাংলা) <span class="text-muted-foreground">Title, Bangla — required</span><input v-model="form.titleBn" :class="field"></label>
      <label class="text-sm sm:col-span-3">Title (English) <span class="text-muted-foreground">optional</span><input v-model="form.titleEn" :class="field"></label>
      <div class="sm:col-span-3"><Button type="submit" size="sm">খসড়া তৈরি <span class="text-xs opacity-70">Create the draft</span></Button></div>
    </form>

    <p v-if="!issues.length" class="rounded-lg border bg-card p-4 text-sm text-muted-foreground">এখনো কোনো সংখ্যা নেই। <span class="opacity-70">No issues yet.</span></p>
    <ul class="grid gap-2">
      <li v-for="i in issues" :key="i.id">
        <button type="button" class="flex w-full flex-wrap items-center gap-x-3 gap-y-1 rounded-lg border bg-card p-3 text-left hover:bg-accent" :data-truth-issue="i.slug" @click="emit('open', i.id)">
          <span class="font-mono text-xs text-muted-foreground">{{ i.slug }}</span>
          <span class="min-w-0 flex-1 truncate font-medium">{{ i.titleBn || '—' }}</span>
          <Badge :variant="STATUS_BADGE[i.status]">{{ i.status }}</Badge>
          <Badge v-if="i.unpublishedChanges" variant="outline">unpublished changes</Badge>
          <span class="w-full text-xs text-muted-foreground">{{ i.weekStart }} – {{ i.weekEnd }} · {{ i.items }} items<template v-if="i.publishedAt"> · published {{ formatDhaka(i.publishedAt) }} by {{ i.publishedBy }}</template></span>
        </button>
      </li>
    </ul>
  </section>
</template>
