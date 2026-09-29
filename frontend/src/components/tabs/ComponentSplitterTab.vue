<script setup>
/**
 * Angular component splitter.
 *
 * The UI mirrors the pipeline exactly, and the ordering is load-bearing rather
 * than cosmetic:
 *
 *   Parse → { Facts ‖ Reason } → Merge → REVIEW → Generate → Validate
 *
 * Two rules this component exists to enforce:
 *
 *  1. NOTHING IS GENERATED UNTIL "Generate" IS CLICKED. `analyse` cannot produce
 *     a file — the endpoint has no code path to one — and `files` stays null
 *     until the click. The gate is not a disabled button over a ready result;
 *     the result does not exist yet.
 *
 *  2. VALIDATION IS NOT THE GENERATOR'S OPINION. A successful generate call can
 *     come back with a failing report, and when it does the UI says so loudly
 *     instead of showing green because the request returned 200.
 */
import { computed, ref } from 'vue'
import {
  AlertTriangle, Check, Copy, Download, FileCode2, Loader2, Play,
  RotateCcw, Scissors, Sparkles, X,
} from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import ModelPicker from '@/components/ModelPicker.vue'
import PipelineStages from '@/components/splitter/PipelineStages.vue'
import ProposedComponentCard from '@/components/splitter/ProposedComponentCard.vue'
import {
  analyse, generate, parseComponent, toPlan,
} from '@/services/component-splitter.service.js'
import { HttpError } from '@/services/http.js'

// ---- Inputs ----
const componentTs = ref('')
const templateHtml = ref('')
const model = ref('')
const source = ref('local')

// ---- Pipeline state ----
const stages = ref({})
const parsed = ref(null)
const proposal = ref(null)
const draft = ref([])          // the editable review state
const aiText = ref('')
const error = ref('')
const rejected = ref(false)    // a clear non-Angular refusal, shown on its own
const busy = ref(false)
const expanded = ref({})

// ---- Output (stage 5/6) ----
const files = ref(null)
const validation = ref(null)
const generating = ref(false)
const copiedPath = ref('')

let controller = null

const setStage = (key, status) => { stages.value = { ...stages.value, [key]: status } }

/** Everything after the source box, reset between runs. */
function resetPipeline() {
  stages.value = {}
  parsed.value = null
  proposal.value = null
  draft.value = []
  aiText.value = ''
  error.value = ''
  rejected.value = false
  files.value = null
  validation.value = null
}

/**
 * Stage 1 only. Run before the model so a non-Angular paste is refused without
 * spending a token, and so the user sees what was detected before committing.
 */
async function runParse() {
  resetPipeline()
  busy.value = true
  setStage('parse', 'running')

  try {
    parsed.value = await parseComponent({
      componentTs: componentTs.value,
      templateHtml: templateHtml.value || null,
    })
    setStage('parse', 'done')
  } catch (e) {
    setStage('parse', 'failed')
    if (e instanceof HttpError && e.status === 400) rejected.value = true
    error.value = e.message
  } finally {
    busy.value = false
  }
}

/** Stages 2a ‖ 2b → 3. Produces a PROPOSAL and nothing else. */
async function runAnalyse() {
  resetPipeline()
  busy.value = true
  aiText.value = ''
  controller = new AbortController()

  try {
    await analyse(
      {
        componentTs: componentTs.value,
        templateHtml: templateHtml.value || null,
        model: model.value,
        source: source.value,
      },
      {
        onStage: (stage, status) => setStage(stage, status),
        onComponent: (c) => { parsed.value = c },
        onAiDelta: (text) => { aiText.value += text },
        onProposal: (p) => {
          proposal.value = p
          draft.value = (p.components || []).map((c) => ({
            ...c,
            keep: true,
            hasBlocker: (c.flags || []).some((f) => f.severity === 'blocker'),
          }))
          draft.value.forEach((c) => { expanded.value[c.candidateId] = true })
          setStage('review', 'running')
        },
        onError: (message) => { error.value = message },
      },
      controller.signal,
    )
  } catch (e) {
    if (e.name !== 'AbortError') {
      if (e instanceof HttpError && e.status === 400) rejected.value = true
      error.value = e.message
      setStage('reason', 'failed')
    }
  } finally {
    busy.value = false
    controller = null
  }
}

function stop() {
  controller?.abort()
  busy.value = false
}

// ---- Review edits ----
function updateComponent(candidateId, next) {
  draft.value = draft.value.map((c) => (c.candidateId === candidateId ? next : c))
}

function reject(candidateId) {
  updateComponent(candidateId, {
    ...draft.value.find((c) => c.candidateId === candidateId), keep: false,
  })
}

function restore(candidateId) {
  updateComponent(candidateId, {
    ...draft.value.find((c) => c.candidateId === candidateId), keep: true,
  })
}

/**
 * Merge one component into another: the target absorbs the source's inputs and
 * outputs (de-duplicated by name) and the source is dropped from the plan.
 *
 * The TEMPLATE blocks are not concatenated here — the server resolves markup
 * from the IR by candidateId, so a merge of two non-adjacent blocks cannot be
 * expressed as one child. Merging is therefore offered as contract-merging, and
 * the dropped block stays inline, which is the honest outcome.
 */
function merge(sourceId, targetId) {
  const src = draft.value.find((c) => c.candidateId === sourceId)
  const tgt = draft.value.find((c) => c.candidateId === targetId)
  if (!src || !tgt) return

  const byName = (list, extra) => {
    const seen = new Set(list.map((x) => x.name))
    return [...list, ...extra.filter((x) => !seen.has(x.name))]
  }

  draft.value = draft.value
    .map((c) => (c.candidateId === targetId
      ? {
          ...c,
          inputs: byName(c.inputs, src.inputs),
          outputs: byName(c.outputs, src.outputs),
        }
      : c))
    .map((c) => (c.candidateId === sourceId ? { ...c, keep: false } : c))
}

const kept = computed(() => draft.value.filter((c) => c.keep && !c.hasBlocker))
const canGenerate = computed(() => kept.value.length > 0 && !busy.value && !generating.value)

/** Stage 5 + 6. The ONLY path that produces a file. */
async function runGenerate() {
  generating.value = true
  error.value = ''
  setStage('generate', 'running')
  setStage('validate', 'running')

  try {
    const result = await generate({
      componentTs: componentTs.value,
      templateHtml: templateHtml.value || null,
      plan: toPlan(draft.value),
    })

    files.value = result.files
    validation.value = result.validation

    setStage('review', 'done')
    setStage('generate', 'done')
    // Generation succeeding says nothing about correctness — the report does.
    setStage('validate', result.validationPassed ? 'done' : 'failed')
  } catch (e) {
    error.value = e.message
    setStage('generate', 'failed')
    setStage('validate', 'idle')
  } finally {
    generating.value = false
  }
}

async function copyFile(file) {
  try {
    await navigator.clipboard.writeText(file.contents)
    copiedPath.value = file.path
    setTimeout(() => { copiedPath.value = '' }, 1500)
  } catch { /* clipboard blocked; the text is on screen anyway */ }
}

function downloadAll() {
  // One text file per component, concatenated with clear separators. A zip would
  // need a new dependency, and this stays copy-pasteable.
  const body = files.value
    .map((f) => `// ===== ${f.path} =====\n${f.contents}`)
    .join('\n\n')

  const url = URL.createObjectURL(new Blob([body], { type: 'text/plain' }))
  const a = document.createElement('a')
  a.href = url
  a.download = `${parsed.value?.className || 'split'}-components.txt`
  a.click()
  URL.revokeObjectURL(url)
}

const SAMPLE = `import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-product-list',
  standalone: true,
  imports: [CommonModule],
  template: \`
    <div class="wrap">
      <h2>{{ heading }}</h2>
      <div class="row" *ngFor="let product of products; let i = index">
        <span class="name">{{ product.name }}</span>
        <span class="price">{{ product.price | currency }}</span>
        <button (click)="addToCart(product)">Add</button>
        <button (click)="removeItem(product)">Remove</button>
      </div>
      <p class="total">Total: {{ total }}</p>
    </div>
  \`,
})
export class ProductListComponent {
  @Input() heading: string = 'Products';
  products: Product[] = [];
  total: number = 0;
  addToCart(p: Product) {}
  removeItem(p: Product) {}
}`
</script>

<template>
  <div class="flex h-full min-h-0 flex-col gap-3 overflow-y-auto p-4">
    <!-- Header -->
    <div class="flex flex-wrap items-center gap-2">
      <Scissors class="h-5 w-5 shrink-0" />
      <h2 class="text-lg font-semibold">Angular component splitter</h2>
      <Badge variant="outline" class="shrink-0">Angular only</Badge>
      <span class="text-xs text-muted-foreground">
        Structural facts are parsed deterministically; the model only picks and names.
      </span>
    </div>

    <PipelineStages :stages="stages" />

    <!-- Source input -->
    <div class="grid gap-3 lg:grid-cols-2">
      <div class="flex min-h-0 flex-col gap-1">
        <label class="text-xs font-medium">Component .ts</label>
        <textarea
          v-model="componentTs"
          spellcheck="false"
          placeholder="Paste your Angular component here…"
          class="h-56 w-full resize-y rounded-md border bg-background p-2 font-mono text-[11px] leading-relaxed focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
        />
        <button class="self-start text-[11px] text-muted-foreground underline-offset-2 hover:underline"
          @click="componentTs = SAMPLE">
          Load a sample component
        </button>
      </div>

      <div class="flex min-h-0 flex-col gap-1">
        <label class="text-xs font-medium">
          Template .html
          <span class="font-normal text-muted-foreground">— only if the component uses templateUrl</span>
        </label>
        <textarea
          v-model="templateHtml"
          spellcheck="false"
          placeholder="Leave empty for an inline template."
          class="h-56 w-full resize-y rounded-md border bg-background p-2 font-mono text-[11px] leading-relaxed focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
        />
      </div>
    </div>

    <!-- Controls -->
    <div class="flex flex-wrap items-center gap-2">
      <ModelPicker v-model="model" v-model:source="source" :disabled="busy" />

      <Button variant="outline" size="sm" :disabled="!componentTs.trim() || busy" @click="runParse">
        <FileCode2 class="h-4 w-4" /> Parse only
      </Button>

      <Button size="sm" :disabled="!componentTs.trim() || !model || busy" @click="runAnalyse">
        <Sparkles class="h-4 w-4" /> Analyse
      </Button>

      <Button v-if="busy" variant="ghost" size="sm" @click="stop">
        <X class="h-4 w-4" /> Stop
      </Button>

      <Loader2 v-if="busy" class="h-4 w-4 animate-spin text-muted-foreground" />
    </div>

    <!-- A non-Angular input is refused clearly, not as a generic failure. -->
    <div v-if="rejected"
      class="flex items-start gap-2 rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm">
      <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
      <div>
        <p class="font-medium">Not an Angular component</p>
        <p class="text-xs text-muted-foreground">{{ error }}</p>
      </div>
    </div>

    <div v-else-if="error"
      class="flex items-start gap-2 rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm">
      <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
      <span>{{ error }}</span>
    </div>

    <!-- What the parser detected. Ground truth, shown as such. -->
    <div v-if="parsed" class="flex flex-wrap items-center gap-2 rounded-md border bg-muted/40 px-3 py-2 text-xs">
      <span class="font-medium">{{ parsed.className }}</span>
      <code class="rounded bg-muted px-1">{{ parsed.selector }}</code>
      <Badge variant="secondary">
        {{ parsed.wiring === 'standalone' ? 'standalone → imports[]' : 'NgModule → declarations[]' }}
      </Badge>
      <Badge v-if="parsed.uiKit" variant="outline">{{ parsed.uiKit }}</Badge>
      <Badge variant="outline">{{ parsed.templateIsInline ? 'inline template' : 'templateUrl' }}</Badge>
      <span class="text-muted-foreground">
        {{ parsed.nodeCount }} nodes · {{ parsed.bindingCount }} bindings
      </span>
    </div>

    <!-- The AI stage, streaming. Labelled with its scope so the output is read
         as a selection, not as authority over the contract. -->
    <details v-if="aiText" class="rounded-md border" open>
      <summary class="cursor-pointer px-3 py-2 text-xs font-medium">
        Model reasoning
        <span class="font-normal text-muted-foreground">— selection + naming only</span>
      </summary>
      <pre class="max-h-40 overflow-auto px-3 pb-2 text-[11px] leading-relaxed"><code>{{ aiText }}</code></pre>
    </details>

    <!-- The model failed to produce usable JSON: offer the raw text rather than
         an empty proposal that looks like "nothing to split". -->
    <div v-if="proposal?.aiFailed"
      class="rounded-md border border-amber-500/40 bg-amber-500/10 p-3 text-xs">
      <p class="font-medium">The model's response could not be parsed after one retry.</p>
      <p class="text-muted-foreground">{{ proposal.aiError }}</p>
      <pre class="mt-2 max-h-32 overflow-auto rounded bg-muted p-2"><code>{{ proposal.aiRawResponse }}</code></pre>
    </div>

    <!-- ================= STAGE 4: HUMAN REVIEW ================= -->
    <div v-if="draft.length" class="space-y-2">
      <div class="flex flex-wrap items-center gap-2">
        <h3 class="text-sm font-semibold">Review</h3>
        <Badge variant="secondary">{{ kept.length }} of {{ draft.length }} to extract</Badge>
        <span class="text-xs text-muted-foreground">
          Nothing is generated until you click Generate.
        </span>
      </div>

      <ProposedComponentCard
        v-for="c in draft"
        :key="c.candidateId"
        :component="c"
        :expanded="expanded[c.candidateId]"
        :merge-targets="draft.filter((t) => t.keep && t.candidateId !== c.candidateId)"
        @toggle="expanded[c.candidateId] = !expanded[c.candidateId]"
        @update="updateComponent(c.candidateId, $event)"
        @reject="reject(c.candidateId)"
        @restore="restore(c.candidateId)"
        @merge="merge(c.candidateId, $event)"
      />

      <!-- Candidates the model passed on, offered anyway: the human gate
           overrules the model in both directions. -->
      <details v-if="proposal?.declined?.length" class="rounded-md border">
        <summary class="cursor-pointer px-3 py-2 text-xs text-muted-foreground">
          {{ proposal.declined.length }} candidate(s) the model kept inline
        </summary>
        <ul class="space-y-1 px-3 pb-2 text-xs">
          <li v-for="d in proposal.declined" :key="d.candidateId" class="text-muted-foreground">
            &lt;{{ d.tag }}&gt; · {{ d.nodeCount }} nodes
            <span v-if="d.rationale">— "{{ d.rationale }}"</span>
          </li>
        </ul>
      </details>

      <!-- The gate. -->
      <div class="flex flex-wrap items-center gap-2 border-t pt-3">
        <Button :disabled="!canGenerate" @click="runGenerate">
          <Loader2 v-if="generating" class="h-4 w-4 animate-spin" />
          <Play v-else class="h-4 w-4" />
          Generate {{ kept.length }} component{{ kept.length === 1 ? '' : 's' }}
        </Button>

        <Button v-if="files" variant="ghost" size="sm" @click="runGenerate">
          <RotateCcw class="h-4 w-4" /> Regenerate
        </Button>

        <span v-if="!kept.length" class="text-xs text-muted-foreground">
          Nothing selected — every candidate is rejected or blocked.
        </span>
      </div>
    </div>

    <!-- ================= STAGE 6: VALIDATION ================= -->
    <div v-if="validation" class="space-y-2">
      <div class="flex flex-wrap items-center gap-2">
        <h3 class="text-sm font-semibold">Validation</h3>
        <Badge :variant="validation.passed ? 'secondary' : 'destructive'">
          {{ validation.passed ? 'All checks passed' : 'Failed' }}
        </Badge>
        <span v-if="!validation.passed" class="text-xs text-muted-foreground">
          The files were generated, but they are not correct — see below.
        </span>
      </div>

      <div v-for="check in validation.checks" :key="check.check" :class="cn(
        'rounded-md border px-3 py-2 text-xs',
        check.passed ? 'border-emerald-500/40 bg-emerald-500/5' : 'border-destructive/50 bg-destructive/10',
      )">
        <div class="flex items-center gap-2">
          <component :is="check.passed ? Check : X"
            :class="cn('h-3.5 w-3.5 shrink-0', check.passed ? 'text-emerald-600' : 'text-destructive')" />
          <span class="font-medium capitalize">{{ check.check }}</span>
          <span class="text-muted-foreground">{{ check.summary }}</span>
        </div>
        <ul v-if="check.details?.length" class="mt-1.5 space-y-1 pl-5">
          <li v-for="(d, i) in check.details" :key="i" class="list-disc text-muted-foreground">{{ d }}</li>
        </ul>
      </div>
    </div>

    <!-- ================= STAGE 7: FINAL FILES ================= -->
    <div v-if="files" class="space-y-2">
      <div class="flex flex-wrap items-center gap-2">
        <h3 class="text-sm font-semibold">Files</h3>
        <Badge variant="outline">{{ files.length }}</Badge>
        <Button variant="outline" size="sm" class="ml-auto" @click="downloadAll">
          <Download class="h-4 w-4" /> Download all
        </Button>
      </div>

      <p class="text-xs text-muted-foreground">
        Copy these into your project. The splitter never writes to a repository.
      </p>

      <div v-for="file in files" :key="file.path" class="rounded-md border">
        <div class="flex items-center gap-2 border-b px-3 py-1.5">
          <FileCode2 class="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
          <code class="text-xs font-medium">{{ file.path }}</code>
          <Badge variant="outline" class="shrink-0 text-[10px]">{{ file.kind }}</Badge>
          <Button variant="ghost" size="sm" class="ml-auto h-6 text-xs" @click="copyFile(file)">
            <Check v-if="copiedPath === file.path" class="h-3 w-3" />
            <Copy v-else class="h-3 w-3" />
            {{ copiedPath === file.path ? 'Copied' : 'Copy' }}
          </Button>
        </div>
        <pre class="max-h-72 overflow-auto p-3 text-[11px] leading-relaxed"><code>{{ file.contents }}</code></pre>
      </div>
    </div>
  </div>
</template>
