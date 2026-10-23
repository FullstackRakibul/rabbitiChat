<script setup>
/**
 * The glossary (docs/TRUTH_PROMPT.md §3A.7): plain, correct one-line explanations
 * in both languages, and the words that get a dotted underline in summaries.
 * Only the public page's nickname is themed; the entries never are.
 */
import { computed, onMounted } from 'vue'
import { Plus, RotateCcw, X } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import { useContentDoc } from '@/components/truth/useContentDoc.js'
import { validateGlossary } from '@/truth/lib/content-docs.js'
import { formatDhaka } from '@/lib/truth-admin.js'

const { state, doc, note, error, notice, problems, load, saveDraft, publish, rollback } = useContentDoc('glossary')
const field = 'h-9 w-full rounded-md border border-input bg-transparent px-2 text-sm'

const errors = computed(() => (doc.value && state.value ? validateGlossary(doc.value, state.value.schema) : []))
const errorsAt = (i) => errors.value.filter((e) => e.path === `entries[${i}]` || e.path.startsWith(`entries[${i}].`))
const add = () => doc.value.entries.push({ id: `term-${doc.value.entries.length + 1}`, term: { bn: '', en: '' }, def: { bn: '', en: '' }, match: [] })
const setMatch = (e, v) => { e.match = v.split(',').map((w) => w.trim()).filter(Boolean) }

onMounted(load)
</script>

<template>
  <section v-if="doc" class="flex flex-col gap-3" data-truth-glossary>
    <div class="flex flex-wrap items-center gap-2">
      <h3 class="flex-1 font-semibold">শব্দকোষ <span class="text-sm font-normal text-muted-foreground">Glossary · live v{{ state.published?.version ?? 0 }}</span></h3>
      <Button size="sm" variant="outline" @click="saveDraft">খসড়া <span class="text-xs opacity-70">Save draft</span></Button>
      <input v-model="note" placeholder="What changed (for the history)" class="h-8 rounded-md border border-input bg-transparent px-2 text-sm">
      <Button size="sm" :disabled="errors.length > 0" @click="publish">প্রকাশ <span class="text-xs opacity-70">Publish</span></Button>
    </div>
    <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}
      <span v-for="(p, n) in problems" :key="n" class="block">{{ p.field }}: {{ p.message }}</span></p>
    <p v-if="notice" class="rounded-md border bg-card p-3 text-sm">{{ notice }}</p>
    <Button size="sm" variant="outline" class="self-start" @click="add"><Plus class="size-4" /> Entry</Button>

    <ul class="grid gap-2">
      <li v-for="(e, i) in doc.entries" :key="i" class="grid gap-2 rounded-md border bg-card p-3 sm:grid-cols-2">
        <div class="flex gap-2 sm:col-span-2">
          <input v-model="e.id" :class="field" class="font-mono" aria-label="id">
          <button type="button" aria-label="Remove entry" @click="doc.entries.splice(i, 1)"><X class="size-4" /></button>
        </div>
        <label class="text-xs text-muted-foreground">শব্দ (বাংলা)<input v-model="e.term.bn" lang="bn" :class="field"></label>
        <label class="text-xs text-muted-foreground">Term (English)<input v-model="e.term.en" :class="field"></label>
        <label class="text-xs text-muted-foreground">ব্যাখ্যা (বাংলা)<textarea v-model="e.def.bn" lang="bn" rows="2" class="w-full rounded-md border border-input bg-transparent px-2 py-1 text-sm" /></label>
        <label class="text-xs text-muted-foreground">Explanation (English)<textarea v-model="e.def.en" rows="2" class="w-full rounded-md border border-input bg-transparent px-2 py-1 text-sm" /></label>
        <label class="text-xs text-muted-foreground sm:col-span-2">Words to underline in summaries, comma-separated
          <input :value="e.match.join(', ')" :class="field" @change="setMatch(e, $event.target.value)"></label>
        <p v-for="x in errorsAt(i)" :key="x.path + x.message" class="m-0 text-xs text-destructive sm:col-span-2">{{ x.path }}: {{ x.message }}</p>
      </li>
    </ul>

    <div class="rounded-md border bg-card p-3 text-sm">
      <h4 class="font-semibold">ইতিহাস <span class="font-normal text-muted-foreground">Versions</span></h4>
      <p v-if="!state.history.length" class="text-muted-foreground">Only the built-in glossary so far.</p>
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
