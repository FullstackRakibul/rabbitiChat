<script setup>
// The technologies items are about, each with its neutral lettermark (§3): one or
// two letters on a muted square made from a hue. Never a vendor logo.
import { onMounted, ref } from 'vue'
import { Plus } from '@lucide/vue'
import { createTruthTech, listTruthTechs, updateTruthTech } from '@/services/index.js'
import { Button } from '@/components/ui/button'
import { CATEGORIES } from '@/lib/truth-admin.js'

defineProps({ canEdit: { type: Boolean, default: false } })

const techs = ref([])
const error = ref('')
const editing = ref(null)   // { id?, slug, name, … }
const field = 'h-9 w-full rounded-md border border-input bg-transparent px-3 text-sm'

async function refresh() {
  try { techs.value = (await listTruthTechs()).techs } catch (e) { error.value = e.message }
}
const blank = () => ({ slug: '', name: '', category: 'language', monogram: '', hue: 210, homepage: '', versionLines: '', eolLines: '' })
function edit(t) {
  editing.value = t ? { ...t, versionLines: JSON.parse(t.versionLinesJson || '[]').join(', '), eolLines: JSON.parse(t.eolLinesJson || '[]').join(', ') } : blank()
}
async function save() {
  error.value = ''
  const t = editing.value
  const lines = (s) => String(s ?? '').split(',').map((v) => v.trim()).filter(Boolean)
  const body = { ...t, hue: Number(t.hue), homepage: t.homepage || null, versionLines: lines(t.versionLines), eolLines: lines(t.eolLines) }
  try {
    if (t.id) await updateTruthTech(t.id, body); else await createTruthTech(body)
    editing.value = null
    await refresh()
  } catch (e) { error.value = e.message }
}
onMounted(refresh)
</script>

<template>
  <section class="flex flex-col gap-3" data-truth-techs>
    <div class="flex items-center gap-2">
      <h3 class="flex-1 font-semibold">প্রযুক্তি <span class="text-sm font-normal text-muted-foreground">Technologies</span></h3>
      <Button v-if="canEdit" size="sm" variant="outline" @click="edit(null)"><Plus class="size-4" /> নতুন <span class="text-xs opacity-70">New</span></Button>
    </div>
    <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>

    <form v-if="editing" class="grid gap-3 rounded-lg border bg-card p-4 sm:grid-cols-4" @submit.prevent="save">
      <label class="text-sm">Name<input v-model="editing.name" required :class="field"></label>
      <label class="text-sm">Slug<input v-model="editing.slug" required pattern="[a-z0-9][a-z0-9-]{0,39}" :class="field"></label>
      <label class="text-sm">Category<select v-model="editing.category" :class="field"><option v-for="c in CATEGORIES" :key="c">{{ c }}</option></select></label>
      <label class="text-sm">Homepage (https)<input v-model="editing.homepage" type="url" :class="field"></label>
      <label class="text-sm">Lettermark (1–2)<input v-model="editing.monogram" required maxlength="2" :class="field"></label>
      <label class="text-sm">Hue {{ editing.hue }}<input v-model="editing.hue" type="range" min="0" max="359" class="w-full"></label>
      <div class="flex items-end gap-2" aria-hidden="true">
        <svg v-for="mode in ['light', 'dark']" :key="mode" width="40" height="40" viewBox="0 0 40 40">
          <rect width="40" height="40" rx="10" :fill="`hsl(${editing.hue} ${mode === 'light' ? '42% 88%' : '30% 25%'})`" />
          <text x="20" y="21" text-anchor="middle" dominant-baseline="middle" font-weight="700" :font-size="String(editing.monogram).length > 1 ? 15 : 18"
            :fill="`hsl(${editing.hue} 55% ${mode === 'light' ? '24%' : '88%'})`">{{ editing.monogram.toUpperCase() }}</text>
        </svg>
      </div>
      <label class="text-sm">Version lines<input v-model="editing.versionLines" placeholder="3.1, 3.2" :class="field"></label>
      <label class="text-sm">End-of-life lines<input v-model="editing.eolLines" placeholder="3.0" :class="field"></label>
      <div class="flex gap-2 sm:col-span-4"><Button type="submit" size="sm">Save</Button><Button type="button" size="sm" variant="ghost" @click="editing = null">Cancel</Button></div>
    </form>

    <ul class="grid gap-2 sm:grid-cols-2 lg:grid-cols-3">
      <li v-for="t in techs" :key="t.id">
        <button type="button" class="flex w-full items-center gap-3 rounded-lg border bg-card p-3 text-left hover:bg-accent" :disabled="!canEdit" @click="edit(t)">
          <svg width="36" height="36" viewBox="0 0 40 40" aria-hidden="true">
            <rect width="40" height="40" rx="10" :fill="`hsl(${t.hue} 42% 88%)`" />
            <text x="20" y="21" text-anchor="middle" dominant-baseline="middle" font-weight="700" :font-size="t.monogram.length > 1 ? 15 : 18" :fill="`hsl(${t.hue} 55% 24%)`">{{ t.monogram }}</text>
          </svg>
          <span class="min-w-0"><span class="block font-medium">{{ t.name }}</span><span class="block text-xs text-muted-foreground">{{ t.slug }} · {{ t.category }}</span></span>
        </button>
      </li>
    </ul>
  </section>
</template>
