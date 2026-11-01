<script setup>
/**
 * Jiji — a new item. Content type, subject and your notes go to the model, which
 * answers with 5 angles built only from the notes; pick one (or write your own)
 * and the item is created. The notes are the only facts the script may use later.
 */
import { computed, ref, watch } from 'vue'
import { Loader2, Sparkles } from '@lucide/vue'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import ModelPicker from '@/components/ModelPicker.vue'
import { JIJI_CONTENT_TYPES, createJijiContent, generateJijiIdeas, jijiIssues } from '@/services/jiji.service.js'

const props = defineProps({ open: { type: Boolean, default: false } })
const emit = defineEmits(['update:open', 'created'])

const contentType = ref('TechExplainer')
const subject = ref('')
const notes = ref('')
const model = ref('')
const source = ref('local')
const angles = ref([])
/** Index into angles, or -2 for your own. */
const picked = ref(-2)
const ownAngle = ref('')
const busy = ref('')
const error = ref('')
const issues = ref([])

watch(() => props.open, (v) => {
  if (!v) return
  angles.value = []; picked.value = -2; ownAngle.value = ''; error.value = ''; issues.value = []
})

const canAsk = computed(() => subject.value.trim() && notes.value.trim() && model.value && !busy.value)
const chosenAngle = computed(() => picked.value === -2 ? ownAngle.value.trim() : angles.value[picked.value]?.angle ?? '')
const canCreate = computed(() => subject.value.trim() && chosenAngle.value && !busy.value)

function fail(e) {
  error.value = e.message || 'Something went wrong.'
  issues.value = jijiIssues(e)
}

async function askAngles() {
  busy.value = 'ideas'; error.value = ''; issues.value = []
  try {
    const r = await generateJijiIdeas({
      contentType: contentType.value, subject: subject.value, sourceNotes: notes.value,
      model: model.value, source: source.value,
    })
    angles.value = r.angles
    picked.value = r.angles.length ? 0 : -2
  } catch (e) { fail(e) } finally { busy.value = '' }
}

async function create() {
  busy.value = 'create'; error.value = ''; issues.value = []
  try {
    const item = await createJijiContent({
      contentType: contentType.value, subject: subject.value, sourceNotes: notes.value, angle: chosenAngle.value,
    })
    emit('created', item)
    emit('update:open', false)
    subject.value = ''; notes.value = ''
  } catch (e) { fail(e) } finally { busy.value = '' }
}
</script>

<template>
  <Dialog :open="open" @update:open="(v) => emit('update:open', v)">
    <DialogContent class="flex max-h-[90vh] w-[calc(100vw-2rem)] max-w-3xl flex-col gap-4 overflow-y-auto p-4 sm:p-6" data-jiji-new>
      <DialogHeader>
        <DialogTitle>New Short</DialogTitle>
        <DialogDescription>
          Angles and scripts are built only from your notes. Paste the real example, the snippet and the lesson; nothing from an employer or a customer.
        </DialogDescription>
      </DialogHeader>

      <div class="grid gap-3 sm:grid-cols-[12rem_1fr]">
        <label class="flex flex-col gap-1 text-sm">
          <span class="text-muted-foreground">Content type</span>
          <Select v-model="contentType">
            <SelectTrigger class="h-9"><SelectValue /></SelectTrigger>
            <SelectContent>
              <SelectItem v-for="t in JIJI_CONTENT_TYPES" :key="t.value" :value="t.value">{{ t.label }}</SelectItem>
            </SelectContent>
          </Select>
        </label>
        <label class="flex flex-col gap-1 text-sm">
          <span class="text-muted-foreground">Subject</span>
          <Input v-model="subject" maxlength="200" placeholder="Your API returned 200. Why did the data never save?" data-jiji-subject />
        </label>
      </div>

      <label class="flex flex-col gap-1 text-sm">
        <span class="text-muted-foreground">Your notes <span class="text-xs">({{ notes.length.toLocaleString() }} / 20,000)</span></span>
        <Textarea v-model="notes" rows="9" maxlength="20000" class="font-mono text-xs"
          placeholder="What happened, the snippet (sanitised), what you learned, your own example." data-jiji-notes />
      </label>

      <div class="flex flex-wrap items-center gap-2">
        <ModelPicker v-model="model" v-model:source="source" :disabled="!!busy" />
        <Button :disabled="!canAsk" data-jiji-ask @click="askAngles">
          <Loader2 v-if="busy === 'ideas'" class="animate-spin" /><Sparkles v-else />
          {{ angles.length ? 'Ask again' : 'Suggest 5 angles' }}
        </Button>
        <span v-if="busy === 'ideas'" class="text-xs text-muted-foreground">A local model can take a minute.</span>
      </div>

      <div v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive">
        {{ error }}
        <ul v-if="issues.length" class="mt-1 list-disc pl-5 text-xs"><li v-for="i in issues" :key="i">{{ i }}</li></ul>
      </div>

      <fieldset class="flex flex-col gap-2" data-jiji-angles>
        <legend class="mb-1 text-sm font-medium">{{ angles.length ? 'Pick an angle' : 'Angle' }}</legend>
        <label v-for="(a, i) in angles" :key="i"
          class="flex cursor-pointer gap-3 rounded-md border p-3 text-sm transition-colors"
          :class="picked === i ? 'border-primary bg-primary/5' : 'hover:bg-muted/50'">
          <input v-model="picked" type="radio" :value="i" class="mt-1" />
          <span class="flex flex-col gap-0.5">
            <span class="font-medium">{{ a.angle }}</span>
            <span v-if="a.hook" class="text-muted-foreground">“{{ a.hook }}”</span>
            <span v-if="a.why" class="text-xs text-muted-foreground">{{ a.why }}</span>
          </span>
        </label>
        <label class="flex cursor-pointer gap-3 rounded-md border p-3 text-sm"
          :class="picked === -2 ? 'border-primary bg-primary/5' : 'hover:bg-muted/50'">
          <input v-model="picked" type="radio" :value="-2" class="mt-1" />
          <span class="flex flex-1 flex-col gap-1">
            <span class="font-medium">My own angle</span>
            <Input v-model="ownAngle" maxlength="400" placeholder="The one idea this video teaches" @focus="picked = -2" />
          </span>
        </label>
      </fieldset>

      <DialogFooter class="gap-2">
        <Button variant="outline" @click="emit('update:open', false)">Cancel</Button>
        <Button :disabled="!canCreate" data-jiji-create @click="create">
          <Loader2 v-if="busy === 'create'" class="animate-spin" /> Create
        </Button>
      </DialogFooter>
    </DialogContent>
  </Dialog>
</template>
