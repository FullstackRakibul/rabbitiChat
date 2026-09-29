<script setup>
/**
 * One proposed child component, in review.
 *
 * Every field here is editable EXCEPT the template block, and the distinction is
 * deliberate and labelled: name and selector came from the model and are the
 * user's to change freely; inputs and outputs came from the parser and are shown
 * as derived facts that the user may override knowingly. The markup itself is
 * moved verbatim by the generator and is not editable text — editing it here
 * would be editing the original component, which this tool never does.
 *
 * Nothing on this card generates anything. It edits a local draft; the parent
 * owns the single Generate button.
 */
import { computed } from 'vue'
import {
  AlertTriangle, ChevronDown, ChevronRight, Plus, Repeat, Trash2, X, Layers,
} from '@lucide/vue'
import { cn } from '@/lib/utils'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'

const props = defineProps({
  component: { type: Object, required: true },
  expanded: { type: Boolean, default: true },
  /** Other components, for the merge target list. */
  mergeTargets: { type: Array, default: () => [] },
})

const emit = defineEmits(['update', 'toggle', 'reject', 'restore', 'merge'])

const c = computed(() => props.component)

/** Patch one field on the draft; the parent holds the array. */
function set(field, value) {
  emit('update', { ...c.value, [field]: value })
}

function setInput(idx, field, value) {
  const inputs = c.value.inputs.map((it, i) => (i === idx ? { ...it, [field]: value } : it))
  emit('update', { ...c.value, inputs })
}

function setOutput(idx, field, value) {
  const outputs = c.value.outputs.map((o, i) => (i === idx ? { ...o, [field]: value } : o))
  emit('update', { ...c.value, outputs })
}

function addInput() {
  emit('update', {
    ...c.value,
    inputs: [...c.value.inputs, { name: '', type: 'any', parentExpression: '', added: true }],
  })
}

function removeInput(idx) {
  emit('update', { ...c.value, inputs: c.value.inputs.filter((_, i) => i !== idx) })
}

function addOutput() {
  emit('update', {
    ...c.value,
    outputs: [...c.value.outputs,
      { name: '', payloadType: 'any', parentHandler: '', handlerArguments: [], added: true }],
  })
}

function removeOutput(idx) {
  emit('update', { ...c.value, outputs: c.value.outputs.filter((_, i) => i !== idx) })
}

const blockers = computed(() => (c.value.flags || []).filter((f) => f.severity === 'blocker'))
const warnings = computed(() => (c.value.flags || []).filter((f) => f.severity === 'warning'))
</script>

<template>
  <div :class="cn(
    'rounded-lg border transition-colors',
    !c.keep && 'border-dashed opacity-60',
    blockers.length && 'border-destructive/50',
  )">
    <!-- Header: name, selector, and the keep/reject control. -->
    <div class="flex flex-wrap items-center gap-2 border-b px-3 py-2">
      <button class="shrink-0 text-muted-foreground hover:text-foreground" @click="emit('toggle')">
        <component :is="expanded ? ChevronDown : ChevronRight" class="h-4 w-4" />
      </button>

      <Badge v-if="c.origin === 'RepeatedBlock' || c.origin === 0" variant="secondary" class="gap-1 shrink-0">
        <Repeat class="h-3 w-3" /> *ngFor
      </Badge>
      <Badge v-else variant="outline" class="gap-1 shrink-0">
        <Layers class="h-3 w-3" /> group
      </Badge>

      <Input
        :model-value="c.componentName"
        class="h-7 w-52 font-mono text-xs"
        placeholder="ComponentName"
        :disabled="!c.keep"
        @update:model-value="set('componentName', $event)"
      />
      <Input
        :model-value="c.selector"
        class="h-7 w-44 font-mono text-xs"
        placeholder="app-selector"
        :disabled="!c.keep"
        @update:model-value="set('selector', $event)"
      />

      <div class="ml-auto flex shrink-0 items-center gap-1">
        <select
          v-if="c.keep && mergeTargets.length"
          class="h-7 rounded-md border bg-background px-2 text-xs"
          title="Merge this component into another"
          @change="emit('merge', Number($event.target.value)); $event.target.value = ''"
        >
          <option value="">Merge into…</option>
          <option v-for="t in mergeTargets" :key="t.candidateId" :value="t.candidateId">
            {{ t.componentName }}
          </option>
        </select>

        <Button v-if="c.keep" variant="ghost" size="sm" class="h-7 text-xs" @click="emit('reject')">
          <X class="h-3 w-3" /> Keep inline
        </Button>
        <Button v-else variant="outline" size="sm" class="h-7 text-xs" @click="emit('restore')">
          Restore
        </Button>
      </div>
    </div>

    <div v-if="expanded" class="space-y-3 p-3">
      <!-- Flags. A blocker is shown as the reason Generate is unavailable for
           this component, rather than the component silently vanishing. -->
      <div v-for="(flag, i) in blockers" :key="`b${i}`"
        class="flex items-start gap-2 rounded-md border border-destructive/40 bg-destructive/10 px-2.5 py-2 text-xs">
        <AlertTriangle class="mt-0.5 h-3.5 w-3.5 shrink-0 text-destructive" />
        <span>{{ flag.message }}</span>
      </div>
      <div v-for="(flag, i) in warnings" :key="`w${i}`"
        class="flex items-start gap-2 rounded-md border border-amber-500/40 bg-amber-500/10 px-2.5 py-2 text-xs">
        <AlertTriangle class="mt-0.5 h-3.5 w-3.5 shrink-0 text-amber-600" />
        <span>{{ flag.message }}</span>
      </div>

      <p v-if="c.rationale" class="text-xs italic text-muted-foreground">
        "{{ c.rationale }}" <span class="not-italic opacity-60">— model's rationale</span>
      </p>

      <div class="grid gap-3 lg:grid-cols-2">
        <!-- Inputs and outputs: derived by the parser, editable here. -->
        <div class="space-y-1.5">
          <div class="flex items-center justify-between">
            <span class="text-xs font-medium">
              &#64;Input()
              <span class="font-normal text-muted-foreground">— derived from bindings</span>
            </span>
            <Button variant="ghost" size="sm" class="h-6 px-1.5 text-xs" :disabled="!c.keep" @click="addInput">
              <Plus class="h-3 w-3" />
            </Button>
          </div>

          <p v-if="!c.inputs.length" class="text-xs text-muted-foreground">None required.</p>

          <div v-for="(input, idx) in c.inputs" :key="`i${idx}`" class="flex items-center gap-1">
            <Input :model-value="input.name" placeholder="name" :disabled="!c.keep"
              class="h-7 flex-1 font-mono text-xs"
              @update:model-value="setInput(idx, 'name', $event)" />
            <span class="text-xs text-muted-foreground">:</span>
            <Input :model-value="input.type" placeholder="type" :disabled="!c.keep"
              class="h-7 w-24 font-mono text-xs"
              @update:model-value="setInput(idx, 'type', $event)" />
            <Button variant="ghost" size="icon" class="h-7 w-7 shrink-0" :disabled="!c.keep"
              @click="removeInput(idx)">
              <Trash2 class="h-3 w-3" />
            </Button>
          </div>
        </div>

        <div class="space-y-1.5">
          <div class="flex items-center justify-between">
            <span class="text-xs font-medium">
              &#64;Output()
              <span class="font-normal text-muted-foreground">— derived from handlers</span>
            </span>
            <Button variant="ghost" size="sm" class="h-6 px-1.5 text-xs" :disabled="!c.keep" @click="addOutput">
              <Plus class="h-3 w-3" />
            </Button>
          </div>

          <p v-if="!c.outputs.length" class="text-xs text-muted-foreground">None required.</p>

          <div v-for="(output, idx) in c.outputs" :key="`o${idx}`" class="flex items-center gap-1">
            <Input :model-value="output.name" placeholder="event" :disabled="!c.keep"
              class="h-7 flex-1 font-mono text-xs"
              @update:model-value="setOutput(idx, 'name', $event)" />
            <span class="text-xs text-muted-foreground">&rarr;</span>
            <Input :model-value="output.parentHandler" placeholder="handler" :disabled="!c.keep"
              class="h-7 w-28 font-mono text-xs"
              @update:model-value="setOutput(idx, 'parentHandler', $event)" />
            <Button variant="ghost" size="icon" class="h-7 w-7 shrink-0" :disabled="!c.keep"
              @click="removeOutput(idx)">
              <Trash2 class="h-3 w-3" />
            </Button>
          </div>
        </div>
      </div>

      <!-- The block itself: read-only, because it is moved verbatim. -->
      <div>
        <span class="text-xs font-medium">Template block
          <span class="font-normal text-muted-foreground">— moved verbatim</span>
        </span>
        <pre class="mt-1 max-h-48 overflow-auto rounded-md bg-muted p-2 text-[11px] leading-relaxed"><code>{{ c.templateBlock }}</code></pre>
      </div>
    </div>
  </div>
</template>
