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
 * this component only hides the controls that would be refused. A Rulebook
 * page's confirmed copy (`rulebookPageId`) carries a Rulebook badge and no edit
 * controls: it is changed only from the Rulebook (the server answers 409).
 */
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import {
  BookOpen, Check, ChevronDown, ChevronUp, FileUp, Loader2, Pencil, Plus, RefreshCw, Trash2, X,
} from '@lucide/vue'
import {
  listHrLibrary, getHrItem, getHrChunks, createHrItem, uploadHrFiles, retryHrItem, updateHrItem, deleteHrItem,
  embedHrPending,
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
  // The Bengali ↔ English term list the search reads; one may be active.
  glossary: { bn: 'শব্দকোষ', en: 'Glossary' },
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
const accept = ref('.md,.txt,.sql,.json,.csv,.pdf,.xlsx')
const busy = ref(0)          // id of the item being changed, -1 for the editor or an upload

/** The editor: null when closed; { id?, name, hrType, mode, content, approvedBy } when open. */
const editor = ref(null)
const editorError = ref('')

/** id → { loading, chunks, error } for the open chunk previews. */
const previews = ref({})

const fileInput = ref(null)

/** The server's running total: persona plus active always items, against the 8,000 cap. */
const alwaysTokens = ref(0)
const alwaysCap = ref(8000)

/** Filters, so a library of hundreds of items stays usable. */
const filterText = ref('')
const filterType = ref('all')

/** Per-file results of the last upload that had failures. */
const uploadErrors = ref([])

const totals = computed(() => ({
  items: items.value.length,
  pending: items.value.reduce((n, i) => n + (i.chunkCount - i.embeddedCount), 0),
  indexing: items.value.filter((i) => i.indexState === 'indexing').length,
}))

const shown = computed(() => {
  const q = filterText.value.trim().toLowerCase()
  return items.value.filter((i) =>
    (filterType.value === 'all' || i.hrType === filterType.value) && (!q || i.name.toLowerCase().includes(q)))
})

let refreshTimer = null

async function load() {
  error.value = ''
  try {
    const res = await listHrLibrary()
    items.value = res.items ?? []
    canEdit.value = !!res.canEdit
    types.value = res.types ?? []
    modes.value = res.modes ?? []
    alwaysTokens.value = res.alwaysTokens ?? 0
    alwaysCap.value = res.alwaysCap ?? 8000
    if (res.uploadExtensions?.length) accept.value = res.uploadExtensions.join(',')
  } catch (e) {
    error.value = e.message || 'The library could not be loaded.'
  } finally {
    loading.value = false
  }
  // Indexing happens in the background; follow it until every item is ready.
  clearTimeout(refreshTimer)
  if (items.value.some((i) => i.indexState === 'indexing')) refreshTimer = setTimeout(load, 2000)
}

onMounted(load)
onBeforeUnmount(() => clearTimeout(refreshTimer))

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

/** One or many files in one request; each is answered on its own, so one bad file loses nothing else. */
async function onFile(event) {
  const files = [...(event.target.files ?? [])]
  event.target.value = ''
  if (!files.length) return
  error.value = ''
  uploadErrors.value = []
  busy.value = -1
  try {
    const res = await uploadHrFiles(files)
    uploadErrors.value = (res.results ?? []).filter((r) => !r.ok)
  } catch (e) {
    // All refused: the body still lists why, file by file.
    error.value = e.message
  } finally {
    busy.value = 0
    await load()
  }
}

async function retry(item) {
  busy.value = item.id
  try {
    await retryHrItem(item.id)
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
          {{ totals.items }} টি আইটেম · সবসময় পাঠানো হয়
          <span :class="alwaysTokens > alwaysCap * 0.9 ? 'font-medium text-destructive' : 'font-medium text-foreground'">
            {{ alwaysTokens.toLocaleString() }} / {{ alwaysCap.toLocaleString() }}</span> টোকেন
          <span class="text-muted-foreground/80">({{ totals.items }} items · always sent, with the persona, of the cap)</span>
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
        <input ref="fileInput" type="file" multiple class="hidden" :accept="accept" @change="onFile" />
      </div>
    </div>

    <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/5 px-3 py-2 text-sm text-destructive">
      {{ error }}
    </p>

    <ul
      v-if="uploadErrors.length"
      class="space-y-1 rounded-md border border-destructive/40 bg-destructive/5 px-3 py-2 text-xs text-destructive"
    >
      <li v-for="u in uploadErrors" :key="u.file"><span class="font-medium">{{ u.file }}</span>: {{ u.error }}</li>
    </ul>

    <!-- Filters -->
    <div v-if="items.length" class="flex flex-wrap items-center gap-2">
      <Input v-model="filterText" class="h-8 w-full sm:w-64" placeholder="নাম দিয়ে খুঁজুন · Filter by name" />
      <Select v-model="filterType">
        <SelectTrigger class="h-8 w-full sm:w-48"><SelectValue /></SelectTrigger>
        <SelectContent>
          <SelectItem value="all">সব ধরন · All types</SelectItem>
          <SelectItem v-for="t in types" :key="t" :value="t">
            {{ TYPE_LABELS[t]?.bn ?? t }} · {{ TYPE_LABELS[t]?.en ?? t }}
          </SelectItem>
        </SelectContent>
      </Select>
      <span v-if="shown.length !== items.length" class="text-xs text-muted-foreground">
        {{ shown.length }} / {{ items.length }}
      </span>
    </div>

    <p
      v-if="totals.pending > 0 && totals.indexing === 0"
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
        <p class="text-xs text-muted-foreground">
          JSON পেস্ট করলে প্রতিটি উপাদান আলাদা অংশ হয়; ভুল JSON হলে লাইন ও কলাম দেখানো হবে।
          <span>(Pasted JSON is split per element; invalid JSON is refused with its line and column.)</span>
        </p>
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
      <li v-if="!shown.length" class="px-1 text-sm text-muted-foreground">কিছু মেলেনি। <span>Nothing matches.</span></li>
      <li v-for="item in shown" :key="item.id" class="rounded-lg border bg-card">
        <div class="flex flex-wrap items-start gap-x-3 gap-y-2 p-3">
          <div class="min-w-0 flex-1 basis-56">
            <p class="truncate text-sm font-medium" :title="item.name">{{ item.name }}</p>
            <div class="mt-1 flex flex-wrap items-center gap-1.5 text-xs">
              <Badge variant="secondary" class="font-normal">
                {{ TYPE_LABELS[item.hrType]?.bn ?? item.hrType }}
                <span class="text-muted-foreground">{{ TYPE_LABELS[item.hrType]?.en ?? '' }}</span>
              </Badge>
              <Badge v-if="item.rulebookPageId" class="font-normal" data-rulebook-item :title="'Rulebook page ' + item.rulebookPageId">
                নিয়মাবলি <span class="opacity-80">Rulebook</span>
              </Badge>
              <Badge variant="outline" class="font-normal">
                {{ MODE_LABELS[item.mode]?.bn ?? item.mode }}
                <span class="text-muted-foreground">{{ MODE_LABELS[item.mode]?.en ?? '' }}</span>
              </Badge>
              <span class="text-muted-foreground">v{{ item.version }}</span>
              <span class="text-muted-foreground">· {{ item.tokens.toLocaleString() }} টোকেন / tokens</span>
              <span v-if="item.mode === 'search'" class="text-muted-foreground">
                · {{ item.chunkCount }} অংশ / chunks
              </span>
              <span v-if="item.indexState === 'indexing'" class="flex items-center gap-1 text-muted-foreground">
                · <Loader2 class="h-3 w-3 animate-spin" />
                ইনডেক্স হচ্ছে… {{ item.embeddedCount }} / {{ item.chunkCount }}
                <span>(indexing… {{ item.embeddedCount }} of {{ item.chunkCount }})</span>
              </span>
              <span v-else-if="item.indexState === 'failed'" class="text-destructive" :title="item.indexError">
                · ইনডেক্স ব্যর্থ / indexing failed<template v-if="item.indexError">: {{ item.indexError }}</template>
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
              <Button
                v-if="item.indexState === 'failed'"
                size="sm" variant="ghost" class="h-8" :disabled="busy !== 0" @click="retry(item)"
              >
                <Loader2 v-if="busy === item.id" class="h-3.5 w-3.5 animate-spin" />
                <RefreshCw v-else class="h-3.5 w-3.5" />
                আবার চেষ্টা <span class="text-xs text-muted-foreground">Retry</span>
              </Button>
              <!-- A Rulebook page's copy is changed only from the Rulebook, so its paper trail can't be bypassed. -->
              <span v-if="item.rulebookPageId" class="px-2 text-xs text-muted-foreground">
                নিয়মাবলিতে সম্পাদনা করুন <span>(edit it in the Rulebook)</span>
              </span>
              <template v-else>
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
