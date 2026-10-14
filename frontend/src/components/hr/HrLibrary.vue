<script setup>
/**
 * লাইব্রেরি (Library) — HR's own context for the মানব সম্পদ assistant
 * (docs/HR_MODULE_PROMPT.md §3.2, §3.4).
 *
 * Each item shows its type, mode, version, approval, size in tokens and, for a
 * `search` item, how many chunks it was split into. HR adds items by pasting
 * text or uploading a file, edits them, switches between "always" (sent whole
 * with every question) and "search" (split; the best excerpts are sent), signs
 * them off, and previews a search item's chunks.
 *
 * Who may change the library is the server's decision, reported as `canEdit`;
 * this component only hides the controls that would be refused.
 */
import { computed, onMounted, ref } from 'vue'
import {
  BookOpen, Check, ChevronDown, ChevronUp, FileUp, Loader2, Pencil, Plus, RefreshCw, Trash2, X,
} from '@lucide/vue'
import {
  listHrLibrary, getHrItem, getHrChunks, createHrItem, uploadHrFile, updateHrItem, deleteHrItem, embedHrPending,
} from '@/services/hr.service.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Switch } from '@/components/ui/switch'
import { Textarea } from '@/components/ui/textarea'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'

/** Display names only; which types and modes exist is the server's list. */
const TYPE_LABELS = {
  rules: { bn: 'নিয়ম', en: 'Rules' },
  procedure: { bn: 'প্রসিডিউর', en: 'Procedure' },
  codes: { bn: 'কোড টেবিল', en: 'Code table' },
  note: { bn: 'নোট', en: 'Note' },
}
const MODE_LABELS = {
  always: { bn: 'সবসময়', en: 'Always sent' },
  search: { bn: 'খোঁজা হয়', en: 'Searched' },
}

const loading = ref(true)
const error = ref('')
const items = ref([])
const canEdit = ref(false)
const types = ref([])
const modes = ref([])
const accept = ref('.md,.txt,.sql,.csv')
const busy = ref(0)          // id of the item being changed, -1 for the editor or an upload

/** The editor: null when closed; { id?, name, hrType, mode, content, approvedBy } when open. */
const editor = ref(null)
const editorError = ref('')

/** id → { loading, chunks, error } for the open chunk previews. */
const previews = ref({})

const fileInput = ref(null)

const totals = computed(() => ({
  items: items.value.length,
  always: items.value.filter((i) => i.mode === 'always' && i.isActive).reduce((n, i) => n + i.tokens, 0),
  pending: items.value.reduce((n, i) => n + (i.chunkCount - i.embeddedCount), 0),
}))

async function load() {
  error.value = ''
  try {
    const res = await listHrLibrary()
    items.value = res.items ?? []
    canEdit.value = !!res.canEdit
    types.value = res.types ?? []
    modes.value = res.modes ?? []
    if (res.uploadExtensions?.length) accept.value = res.uploadExtensions.join(',')
  } catch (e) {
    error.value = e.message || 'The library could not be loaded.'
  } finally {
    loading.value = false
  }
}

onMounted(load)

// ---- add, edit ----

function openNew() {
  editorError.value = ''
  editor.value = { name: '', hrType: 'note', mode: '', content: '', approvedBy: '' }
}

async function openEdit(item) {
  editorError.value = ''
  busy.value = item.id
  try {
    const full = await getHrItem(item.id)
    editor.value = {
      id: full.id, name: full.name, hrType: full.hrType, mode: full.mode,
      content: full.content ?? '', approvedBy: full.approvedBy ?? '',
    }
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = 0
  }
}

async function saveEditor() {
  const d = editor.value
  if (!d) return
  editorError.value = ''
  busy.value = -1
  try {
    if (d.id) {
      await updateHrItem(d.id, {
        name: d.name, hrType: d.hrType, mode: d.mode || undefined, content: d.content, approvedBy: d.approvedBy,
      })
    } else {
      // No mode chosen: the server's default for the type (a procedure is searched).
      await createHrItem({
        name: d.name, hrType: d.hrType, mode: d.mode || undefined, content: d.content, approvedBy: d.approvedBy || undefined,
      })
    }
    editor.value = null
    await load()
  } catch (e) {
    editorError.value = e.message
  } finally {
    busy.value = 0
  }
}

async function onFile(event) {
  const file = event.target.files?.[0]
  event.target.value = ''
  if (!file) return
  error.value = ''
  busy.value = -1
  try {
    await uploadHrFile(file)
    await load()
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = 0
  }
}

// ---- per-item actions ----

async function change(item, changes) {
  error.value = ''
  busy.value = item.id
  try {
    await updateHrItem(item.id, changes)
    if (previews.value[item.id]) delete previews.value[item.id]
    await load()
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = 0
  }
}

const toggleMode = (item) => change(item, { mode: item.mode === 'search' ? 'always' : 'search' })
const setActive = (item, value) => change(item, { isActive: value })

async function remove(item) {
  if (!confirm(`“${item.name}” লাইব্রেরি থেকে মুছে ফেলবেন? / Delete it from the library?`)) return
  busy.value = item.id
  try {
    await deleteHrItem(item.id)
    await load()
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = 0
  }
}

async function togglePreview(item) {
  if (previews.value[item.id]) {
    delete previews.value[item.id]
    return
  }
  previews.value[item.id] = { loading: true, chunks: [], error: '' }
  try {
    const res = await getHrChunks(item.id)
    previews.value[item.id] = { loading: false, chunks: res.chunks ?? [], error: '' }
  } catch (e) {
    previews.value[item.id] = { loading: false, chunks: [], error: e.message }
  }
}

async function retryEmbedding() {
  busy.value = -1
  try {
    await embedHrPending()
    await load()
  } catch (e) {
    error.value = e.message
  } finally {
    busy.value = 0
  }
}

const formatDate = (d) => (d ? new Date(d).toLocaleDateString() : '')
</script>

<template>
  <section class="space-y-4">
    <!-- Header and actions -->
    <div class="flex flex-wrap items-end justify-between gap-3">
      <div class="min-w-0">
        <h2 class="flex items-center gap-2 text-base font-semibold">
          <BookOpen class="h-4 w-4 text-muted-foreground" />
          লাইব্রেরি <span class="text-xs font-normal text-muted-foreground">Library</span>
        </h2>
        <p class="text-xs text-muted-foreground">
          {{ totals.items }} টি আইটেম · সবসময় পাঠানো হয় {{ totals.always }} টোকেন
          <span class="text-muted-foreground/80">({{ totals.items }} items · {{ totals.always }} tokens always sent)</span>
        </p>
      </div>

      <div v-if="canEdit" class="flex flex-wrap items-center gap-2">
        <Button size="sm" variant="outline" class="h-8" :disabled="busy !== 0" @click="openNew">
          <Plus class="h-3.5 w-3.5" />
          পেস্ট করে যোগ করুন
          <span class="text-xs text-muted-foreground">Paste</span>
        </Button>
        <Button size="sm" variant="outline" class="h-8" :disabled="busy !== 0" @click="fileInput?.click()">
          <FileUp class="h-3.5 w-3.5" />
          ফাইল আপলোড
          <span class="text-xs text-muted-foreground">Upload</span>
        </Button>
        <input ref="fileInput" type="file" class="hidden" :accept="accept" @change="onFile" />
      </div>
    </div>

    <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/5 px-3 py-2 text-sm text-destructive">
      {{ error }}
    </p>

    <p
      v-if="totals.pending > 0"
      class="flex flex-wrap items-center gap-2 rounded-md border border-dashed px-3 py-2 text-xs text-muted-foreground"
    >
      {{ totals.pending }} টি অংশ এখনও এমবেড হয়নি; সেগুলো খোঁজে আসবে না।
      <span>({{ totals.pending }} chunks wait for the embedding model and are not searchable yet.)</span>
      <Button v-if="canEdit" size="sm" variant="ghost" class="h-7" :disabled="busy !== 0" @click="retryEmbedding">
        <RefreshCw class="h-3.5 w-3.5" />
        আবার চেষ্টা <span class="text-muted-foreground">Retry</span>
      </Button>
    </p>

    <!-- Editor (add by paste, or edit) -->
    <div v-if="editor" class="space-y-3 rounded-lg border bg-card p-3 sm:p-4">
      <div class="flex items-center justify-between gap-2">
        <h3 class="text-sm font-semibold">
          {{ editor.id ? 'সম্পাদনা' : 'নতুন আইটেম' }}
          <span class="text-xs font-normal text-muted-foreground">{{ editor.id ? 'Edit' : 'New item' }}</span>
        </h3>
        <Button size="icon" variant="ghost" class="h-7 w-7" aria-label="Close" @click="editor = null">
          <X class="h-4 w-4" />
        </Button>
      </div>

      <div class="grid gap-3 sm:grid-cols-2">
        <div class="space-y-1.5">
          <Label class="text-xs">নাম <span class="text-muted-foreground">Name</span></Label>
          <Input v-model="editor.name" placeholder="e.g. HR_SETTLEMENT_RULES" />
        </div>
        <div class="space-y-1.5">
          <Label class="text-xs">অনুমোদন করেছেন <span class="text-muted-foreground">Approved by (optional)</span></Label>
          <Input v-model="editor.approvedBy" placeholder="e.g. HR Manager" />
        </div>
        <div class="space-y-1.5">
          <Label class="text-xs">ধরন <span class="text-muted-foreground">Type</span></Label>
          <Select v-model="editor.hrType">
            <SelectTrigger class="h-9 w-full"><SelectValue /></SelectTrigger>
            <SelectContent>
              <SelectItem v-for="t in types" :key="t" :value="t">
                {{ TYPE_LABELS[t]?.bn ?? t }} · {{ TYPE_LABELS[t]?.en ?? t }}
              </SelectItem>
            </SelectContent>
          </Select>
        </div>
        <div class="space-y-1.5">
          <Label class="text-xs">মোড <span class="text-muted-foreground">Mode</span></Label>
          <Select v-model="editor.mode">
            <SelectTrigger class="h-9 w-full"><SelectValue placeholder="ধরন অনুযায়ী · By type" /></SelectTrigger>
            <SelectContent>
              <SelectItem v-for="m in modes" :key="m" :value="m">
                {{ MODE_LABELS[m]?.bn ?? m }} · {{ MODE_LABELS[m]?.en ?? m }}
              </SelectItem>
            </SelectContent>
          </Select>
        </div>
      </div>

      <div class="space-y-1.5">
        <Label class="text-xs">বিষয়বস্তু <span class="text-muted-foreground">Content</span></Label>
        <Textarea v-model="editor.content" rows="12" class="font-mono text-xs" placeholder="Paste the rules, a procedure, a code table or a note…" />
      </div>

      <p v-if="editorError" class="text-sm text-destructive">{{ editorError }}</p>

      <div class="flex flex-wrap items-center gap-2">
        <Button size="sm" :disabled="busy !== 0 || !editor.name.trim()" @click="saveEditor">
          <Loader2 v-if="busy === -1" class="h-3.5 w-3.5 animate-spin" />
          <Check v-else class="h-3.5 w-3.5" />
          সংরক্ষণ <span class="text-xs opacity-80">Save</span>
        </Button>
        <span class="text-xs text-muted-foreground">
          বিষয়বস্তু বদলালে ভার্সন বাড়ে। <span>(Changing the content raises the version.)</span>
        </span>
      </div>
    </div>

    <p v-if="loading" class="flex items-center gap-2 text-sm text-muted-foreground">
      <Loader2 class="h-3.5 w-3.5 animate-spin" /> লোড হচ্ছে… <span>Loading…</span>
    </p>

    <p v-else-if="!items.length" class="rounded-lg border border-dashed px-4 py-8 text-center text-sm text-muted-foreground">
      লাইব্রেরি খালি। <span>The library is empty.</span>
    </p>

    <!-- Items -->
    <ul v-else class="space-y-2">
      <li v-for="item in items" :key="item.id" class="rounded-lg border bg-card">
        <div class="flex flex-wrap items-start gap-x-3 gap-y-2 p-3">
          <div class="min-w-0 flex-1 basis-56">
            <p class="truncate text-sm font-medium" :title="item.name">{{ item.name }}</p>
            <div class="mt-1 flex flex-wrap items-center gap-1.5 text-xs">
              <Badge variant="secondary" class="font-normal">
                {{ TYPE_LABELS[item.hrType]?.bn ?? item.hrType }}
                <span class="text-muted-foreground">{{ TYPE_LABELS[item.hrType]?.en ?? '' }}</span>
              </Badge>
              <Badge variant="outline" class="font-normal">
                {{ MODE_LABELS[item.mode]?.bn ?? item.mode }}
                <span class="text-muted-foreground">{{ MODE_LABELS[item.mode]?.en ?? '' }}</span>
              </Badge>
              <span class="text-muted-foreground">v{{ item.version }}</span>
              <span class="text-muted-foreground">· {{ item.tokens.toLocaleString() }} টোকেন / tokens</span>
              <span v-if="item.mode === 'search'" class="text-muted-foreground">
                · {{ item.chunkCount }} অংশ / chunks<template v-if="item.embeddedCount < item.chunkCount">
                  ({{ item.embeddedCount }} embedded)</template>
              </span>
              <span v-if="!item.isActive" class="text-muted-foreground">· বন্ধ / off</span>
            </div>
            <p class="mt-1 text-xs text-muted-foreground">
              <template v-if="item.approvedBy">
                <Check class="inline h-3 w-3" /> অনুমোদন: {{ item.approvedBy }}
                <span>(approved {{ formatDate(item.approvedAt) }})</span>
              </template>
              <template v-else>অনুমোদিত নয় <span>(not approved)</span></template>
            </p>
          </div>

          <div class="flex flex-wrap items-center gap-1">
            <Button
              v-if="item.mode === 'search'"
              size="sm" variant="ghost" class="h-8"
              @click="togglePreview(item)"
            >
              <ChevronUp v-if="previews[item.id]" class="h-3.5 w-3.5" />
              <ChevronDown v-else class="h-3.5 w-3.5" />
              অংশ দেখুন <span class="text-xs text-muted-foreground">Chunks</span>
            </Button>

            <template v-if="canEdit">
              <label class="flex h-8 items-center gap-1.5 px-2 text-xs text-muted-foreground" :title="item.isActive ? 'On' : 'Off'">
                <Switch :model-value="item.isActive" :disabled="busy !== 0" @update:model-value="(v) => setActive(item, v)" />
                চালু
              </label>
              <Button size="sm" variant="ghost" class="h-8" :disabled="busy !== 0" @click="toggleMode(item)">
                <Loader2 v-if="busy === item.id" class="h-3.5 w-3.5 animate-spin" />
                {{ item.mode === 'search' ? 'সবসময় পাঠান' : 'খোঁজা হোক' }}
                <span class="text-xs text-muted-foreground">{{ item.mode === 'search' ? 'Always' : 'Search' }}</span>
              </Button>
              <Button size="icon" variant="ghost" class="h-8 w-8" aria-label="Edit" :disabled="busy !== 0" @click="openEdit(item)">
                <Pencil class="h-3.5 w-3.5" />
              </Button>
              <Button size="icon" variant="ghost" class="h-8 w-8 text-destructive" aria-label="Delete" :disabled="busy !== 0" @click="remove(item)">
                <Trash2 class="h-3.5 w-3.5" />
              </Button>
            </template>
          </div>
        </div>

        <!-- Chunk preview -->
        <div v-if="previews[item.id]" class="border-t px-3 py-2">
          <p v-if="previews[item.id].loading" class="flex items-center gap-2 text-xs text-muted-foreground">
            <Loader2 class="h-3 w-3 animate-spin" /> Loading chunks…
          </p>
          <p v-else-if="previews[item.id].error" class="text-xs text-destructive">{{ previews[item.id].error }}</p>
          <ol v-else class="max-h-96 space-y-2 overflow-y-auto">
            <li v-for="c in previews[item.id].chunks" :key="c.id" class="rounded-md bg-muted/40 p-2">
              <p class="flex flex-wrap items-center gap-x-2 text-xs">
                <span class="font-medium">{{ c.sourceRef }}</span>
                <span class="text-muted-foreground">{{ c.chars }} chars · {{ c.tokens }} tokens</span>
                <span v-if="!c.embedded" class="text-muted-foreground">· not embedded yet</span>
              </p>
              <pre class="mt-1 max-h-48 overflow-auto whitespace-pre-wrap break-words font-mono text-[11px] leading-snug">{{ c.text }}</pre>
            </li>
          </ol>
        </div>
      </li>
    </ul>
  </section>
</template>
