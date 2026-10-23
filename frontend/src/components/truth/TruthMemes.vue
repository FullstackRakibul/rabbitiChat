<script setup>
/**
 * The meme banners (docs/TRUTH_PROMPT.md §3A.5): small, cute, decoration only.
 * Each has both languages (≤ 90 characters, 2 lines), a mood, the slots it may
 * sit in, a style, an optional mascot pose and a weight. A new meme opens with
 * the rules as a checklist: it can't be added until every rule is ticked.
 */
import { computed, onMounted, ref } from 'vue'
import { Plus, RotateCcw, X } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import { useContentDoc } from '@/components/truth/useContentDoc.js'
import { validateMemes } from '@/truth/lib/content-docs.js'
import { formatDhaka } from '@/lib/truth-admin.js'

const { state, doc, note, error, notice, problems, load, saveDraft, publish, rollback } = useContentDoc('memes')
const field = 'h-9 w-full rounded-md border border-input bg-transparent px-2 text-sm'

const enums = computed(() => {
  const p = state.value?.schema.$defs.meme.properties
  return p ? { mood: p.mood.enum, placement: p.placement.items.enum, style: p.style.enum, pose: p.pose.enum } : null
})
const errors = computed(() => (doc.value && state.value ? validateMemes(doc.value, state.value.schema) : []))
const errorsAt = (i) => errors.value.filter((e) => e.path === `memes[${i}]` || e.path.startsWith(`memes[${i}].`))

const RULES = [
  'It jokes about a bad practice (unpatched servers, leaked keys, no backups), never about people or victims, and names no one.',
  'No real politics: no parties, leaders, governments, elections or religion. 1984 stays literary.',
  'No fear-mongering and no fake urgency.',
  'Short Orwell words only (Big Brother, Newspeak, thoughtcrime, Room 101, memory hole, 2 + 2); no quoted passage from the novel.',
  'Plain text only, never HTML.',
]
const adding = ref(false)
const ticked = ref(RULES.map(() => false))
function startAdd() { adding.value = true; ticked.value = RULES.map(() => false) }
function add() {
  doc.value.memes.push({ id: `meme-${doc.value.memes.length + 1}`, bn: '', en: '', mood: 'calm', placement: ['divider'], style: 'ribbon', pose: null, weight: 1, active: true })
  adding.value = false
}
function togglePlacement(m, p) {
  m.placement = m.placement.includes(p) ? m.placement.filter((x) => x !== p) : [...m.placement, p]
}

onMounted(load)
</script>

<template>
  <section v-if="doc" class="flex flex-col gap-3" data-truth-memes>
    <div class="flex flex-wrap items-center gap-2">
      <h3 class="flex-1 font-semibold">মিম <span class="text-sm font-normal text-muted-foreground">Memes · live v{{ state.published?.version ?? 0 }}</span></h3>
      <Button size="sm" variant="outline" @click="saveDraft">খসড়া <span class="text-xs opacity-70">Save draft</span></Button>
      <input v-model="note" placeholder="What changed (for the history)" class="h-8 rounded-md border border-input bg-transparent px-2 text-sm">
      <Button size="sm" data-truth-publish-memes :disabled="errors.length > 0" @click="publish">প্রকাশ <span class="text-xs opacity-70">Publish</span></Button>
    </div>
    <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}
      <span v-for="(p, n) in problems" :key="n" class="block">{{ p.field }}: {{ p.message }}</span></p>
    <p v-if="notice" class="rounded-md border bg-card p-3 text-sm">{{ notice }}</p>
    <p class="text-xs text-muted-foreground">Decoration only: memes never sit on safety information, and serious mode hides them. Each language at most 90 characters and 2 lines.</p>

    <div v-if="adding" class="rounded-lg border border-amber-500/50 bg-card p-4 text-sm" data-truth-meme-rules>
      <p class="font-semibold">Before adding a meme, tick each rule (§3A.5):</p>
      <label v-for="(r, n) in RULES" :key="n" class="mt-2 flex gap-2"><input v-model="ticked[n]" type="checkbox"> {{ r }}</label>
      <div class="mt-3 flex gap-2">
        <Button size="sm" :disabled="!ticked.every(Boolean)" @click="add">Add the meme</Button>
        <Button size="sm" variant="ghost" @click="adding = false">Cancel</Button>
      </div>
    </div>
    <Button v-else size="sm" variant="outline" class="self-start" @click="startAdd"><Plus class="size-4" /> Meme</Button>

    <ul class="grid gap-2">
      <li v-for="(m, i) in doc.memes" :key="i" class="grid gap-2 rounded-md border bg-card p-3 sm:grid-cols-6" :data-meme-row="m.id">
        <input v-model="m.id" :class="field" class="font-mono sm:col-span-2" aria-label="id">
        <label class="flex items-center gap-1 text-sm"><input v-model="m.active" type="checkbox"> active</label>
        <label class="text-xs text-muted-foreground">weight<input v-model.number="m.weight" type="number" min="1" max="10" :class="field"></label>
        <span class="text-right sm:col-span-2"><button type="button" aria-label="Remove meme" @click="doc.memes.splice(i, 1)"><X class="size-4" /></button></span>
        <label class="text-xs text-muted-foreground sm:col-span-3">বাংলা ({{ m.bn.length }}/90)<input v-model="m.bn" lang="bn" :class="field"></label>
        <label class="text-xs text-muted-foreground sm:col-span-3">English ({{ m.en.length }}/90)<input v-model="m.en" :class="field"></label>
        <label class="text-xs text-muted-foreground">mood<select v-model="m.mood" :class="field"><option v-for="o in enums.mood" :key="o">{{ o }}</option></select></label>
        <label class="text-xs text-muted-foreground">style<select v-model="m.style" :class="field"><option v-for="o in enums.style" :key="o">{{ o }}</option></select></label>
        <label class="text-xs text-muted-foreground">mascot<select v-model="m.pose" :class="field"><option v-for="o in enums.pose" :key="String(o)" :value="o">{{ o ?? 'none' }}</option></select></label>
        <fieldset class="flex flex-wrap items-center gap-2 text-xs sm:col-span-3"><legend class="text-muted-foreground">placement</legend>
          <label v-for="p in enums.placement" :key="p" class="flex items-center gap-1"><input type="checkbox" :checked="m.placement.includes(p)" @change="togglePlacement(m, p)"> {{ p }}</label>
        </fieldset>
        <p v-for="e in errorsAt(i)" :key="e.path + e.message" class="m-0 text-xs text-destructive sm:col-span-6">{{ e.path }}: {{ e.message }}</p>
      </li>
    </ul>

    <div class="rounded-md border bg-card p-3 text-sm">
      <h4 class="font-semibold">ইতিহাস <span class="font-normal text-muted-foreground">Versions</span></h4>
      <p v-if="!state.history.length" class="text-muted-foreground">Only the built-in memes so far.</p>
      <ul class="mt-1 grid gap-1">
        <li v-for="h in state.history" :key="h.version" class="flex items-center gap-2">
          <span class="font-mono">v{{ h.version }}</span>
          <span class="min-w-0 flex-1 truncate text-muted-foreground">{{ formatDhaka(h.editedAt) }} · {{ h.editedBy }}{{ h.note ? ' · ' + h.note : '' }}</span>
          <Button v-if="h.version !== state.published?.version" size="sm" variant="ghost" :aria-label="`Roll back to version ${h.version}`" @click="rollback(h.version)"><RotateCcw class="size-4" /></Button>
          <span v-else class="text-xs text-green-700 dark:text-green-400">live</span>
        </li>
      </ul>
    </div>
  </section>
  <p v-else-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>
</template>
