<script setup>
// Profiles: one card per buyer, holding everything that buyer's extraction
// knows — which model reads its POs, its own rules, its glossary, and up to two
// confirmed extractions used as worked examples.
//
// The point of the screen is that a merchandiser can improve accuracy without a
// release and without training anything: a rule like "quantities are packs" is
// a text field here, and it reaches the model on the very next extraction.
//
// Every save bumps the profile's version on the server, and the preview shows
// the prompt that version will actually produce — including which parts the
// token budget would drop.

import { ref } from 'vue'
import { ChevronDown, Loader2, Plus, Trash2 } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Badge } from '@/components/ui/badge'
import {
  Card, CardContent, CardHeader, CardTitle,
} from '@/components/ui/card'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'

const props = defineProps({
  profiles: { type: Array, default: () => [] },
  /** Installed Ollama models, for the model selector. */
  models: { type: Array, default: () => [] },
  /** The install default, shown as the fallback when a profile names no model. */
  defaultModel: { type: String, default: '' },
  saving: { type: Boolean, default: false },
  error: { type: String, default: '' },
  /**
   * The assembled prompt for the last previewed profile, with the budget's
   * decisions. Shown under the card so a merchandiser can see exactly what the
   * model will be told — including which parts did not fit.
   */
  preview: { type: Object, default: null },
  canUse: { type: Boolean, default: true },
})

const emit = defineEmits(['save', 'create', 'delete', 'preview'])

/** Which card is expanded. One at a time keeps the screen calm. */
const open = ref(null)

/** The working copy of the expanded card, so a cancel really cancels. */
const draft = ref({})

function expand(profile) {
  if (open.value === profile.id) { open.value = null; return }
  open.value = profile.id
  draft.value = {
    name: profile.name,
    detectPattern: profile.detectPattern,
    extractionModel: profile.extractionModel ?? '',
    rules: profile.rules ?? '',
    glossary: profile.glossary ?? '',
    exampleRunIds: profile.exampleRunIds ?? '',
    isActive: profile.isActive,
  }
}

function save(profile) {
  emit('save', { id: profile.id, ...draft.value })
}

const newName = ref('')
function create() {
  const name = newName.value.trim()
  if (!name) return
  emit('create', { name, detectPattern: name })
  newName.value = ''
}
</script>

<template>
  <div class="flex flex-col gap-4">
    <p
      v-if="!canUse"
      class="rounded-lg border border-dashed p-4 text-sm text-muted-foreground"
    >
      Editing buyer profiles needs the <b>Documents</b> access.
      Ask an administrator to grant it.
    </p>

    <template v-else>
      <p v-if="error" class="text-sm font-medium text-destructive">
        {{ error }}
      </p>

      <!-- Add a buyer. The detect pattern starts as the name and is tuned below. -->
      <form class="flex flex-wrap items-center gap-2" @submit.prevent="create">
        <Input
          v-model="newName"
          placeholder="New buyer name (e.g. Target)"
          class="w-full sm:w-64"
        />
        <Button type="submit" size="sm" :disabled="saving || !newName.trim()">
          <Plus class="h-3.5 w-3.5" />
          Add buyer
        </Button>
      </form>

      <Card v-for="p in profiles" :key="p.id">
        <CardHeader class="cursor-pointer select-none py-3" @click="expand(p)">
          <div class="flex flex-wrap items-center gap-2">
            <CardTitle class="min-w-0 flex-1 truncate text-base">
              {{ p.name }}
            </CardTitle>
            <Badge variant="outline" class="whitespace-nowrap font-normal">
              v{{ p.version ?? 1 }}
            </Badge>
            <Badge variant="secondary" class="whitespace-nowrap font-normal">
              {{ p.extractionModel || `default · ${defaultModel || 'install default'}` }}
            </Badge>
            <Badge v-if="!p.isActive" variant="outline" class="font-normal">
              inactive
            </Badge>
            <ChevronDown
              :class="[
                'h-4 w-4 shrink-0 text-muted-foreground transition-transform',
                open === p.id && 'rotate-180',
              ]"
            />
          </div>
        </CardHeader>

        <CardContent v-if="open === p.id" class="flex flex-col gap-4 border-t pt-4">
          <div class="grid gap-3 sm:grid-cols-2">
            <label class="flex flex-col gap-1.5 text-sm">
              <span class="font-medium">Name</span>
              <Input v-model="draft.name" />
            </label>

            <label class="flex flex-col gap-1.5 text-sm">
              <span class="font-medium">Detect pattern</span>
              <Input v-model="draft.detectPattern" placeholder="SPS Commerce|Kohls|Order #:" />
              <span class="text-xs text-muted-foreground">
                Pipe-separated markers matched against page 1. A majority must match.
              </span>
            </label>
          </div>

          <label class="flex flex-col gap-1.5 text-sm">
            <span class="font-medium">Extraction model</span>
            <Select v-model="draft.extractionModel">
              <SelectTrigger class="w-full sm:w-72">
                <SelectValue :placeholder="`Install default · ${defaultModel}`" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="">
                  Install default{{ defaultModel ? ` · ${defaultModel}` : '' }}
                </SelectItem>
                <SelectItem v-for="m in models" :key="m" :value="m">
                  {{ m }}
                </SelectItem>
              </SelectContent>
            </Select>
          </label>

          <label class="flex flex-col gap-1.5 text-sm">
            <span class="font-medium">Buyer rules</span>
            <textarea
              v-model="draft.rules"
              rows="4"
              class="w-full rounded-md border bg-transparent px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
              placeholder="Quantities on Kohls POs are packs; pieces = packs × pack size."
            />
            <span class="text-xs text-muted-foreground">
              Added to the base rules, in the model's own instructions.
            </span>
          </label>

          <label class="flex flex-col gap-1.5 text-sm">
            <span class="font-medium">Glossary</span>
            <textarea
              v-model="draft.glossary"
              rows="4"
              class="w-full rounded-md border bg-transparent px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50"
              placeholder="prepack = a carton holding several sizes&#10;ex-factory = date the goods leave the factory"
            />
            <span class="text-xs text-muted-foreground">
              One <code>term = meaning</code> per line.
            </span>
          </label>

          <label class="flex flex-col gap-1.5 text-sm">
            <span class="font-medium">Worked examples</span>
            <Input v-model="draft.exampleRunIds" placeholder="e.g. 12,18" class="w-full sm:w-48" />
            <span class="text-xs text-muted-foreground">
              Up to two <b>confirmed</b> extraction run ids. A run from the held-out
              test set is refused — it would make the A/B comparison score its own
              examples.
            </span>
          </label>

          <div class="flex flex-wrap items-center gap-2 border-t pt-3">
            <Button size="sm" :disabled="saving" @click="save(p)">
              <Loader2 v-if="saving" class="h-3.5 w-3.5 animate-spin" />
              Save (bumps to v{{ (p.version ?? 1) + 1 }})
            </Button>
            <Button size="sm" variant="outline" :disabled="saving" @click="emit('preview', p)">
              Preview prompt
            </Button>
            <Button size="sm" variant="ghost" @click="open = null">
              Cancel
            </Button>
            <Button
              size="sm"
              variant="ghost"
              class="ml-auto text-muted-foreground hover:text-destructive"
              @click="emit('delete', p)"
            >
              <Trash2 class="h-3.5 w-3.5" />
              Delete
            </Button>
          </div>

          <!-- What the model will actually be told. Rendered as text, never with
               v-html: this is model-facing content assembled from stored fields. -->
          <section v-if="preview && preview.buyer === p.name" class="flex flex-col gap-2 border-t pt-3">
            <div class="flex flex-wrap items-center gap-2 text-xs">
              <Badge variant="outline" class="font-normal">
                model {{ preview.model }}
              </Badge>
              <Badge variant="outline" class="font-normal">
                {{ preview.report.systemTokens }} + {{ preview.report.documentTokens }}
                of {{ preview.report.windowTokens }} tokens
              </Badge>
              <Badge
                v-if="preview.report.dropped?.length"
                variant="destructive"
                class="font-normal"
              >
                dropped to fit: {{ preview.report.dropped.join(', ') }}
              </Badge>
            </div>
            <p v-if="preview.report.error" class="text-xs font-medium text-destructive">
              {{ preview.report.error }}
            </p>
            <pre
              v-else
              class="max-h-72 overflow-auto rounded-md border bg-muted/40 p-3 text-xs whitespace-pre-wrap"
            >{{ preview.messages?.[0]?.content }}</pre>
          </section>
        </CardContent>
      </Card>

      <p v-if="!profiles.length" class="py-8 text-center text-sm text-muted-foreground">
        No buyer profiles yet — add one above.
      </p>
    </template>
  </div>
</template>
