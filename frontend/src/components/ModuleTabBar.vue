<script setup>
/**
 * Module tab bar — a recessed pill track with a raised, sliding active pill.
 *
 * Drop-in for AppShell: same props (`tabs`, `modelValue`). It renders reka's
 * TabsList / TabsTrigger / TabsIndicator, so it plugs into the <Tabs> root that
 * App.vue already wraps the whole app in. Clicking a trigger updates that root's
 * v-model (activeTab → router), exactly as before. Keyboard support (arrows, Home,
 * End) and ARIA come from reka.
 *
 * Responsive "slider": when the modules don't fit, the track scrolls sideways
 * (swipe, trackpad, or mouse wheel), soft edge fades show there is more, arrow
 * buttons appear on pointer devices, and the active tab is kept centred. On
 * phones, inactive tabs collapse to icon-only; the active one keeps its label.
 */
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { TabsIndicator, TabsList, TabsTrigger } from 'reka-ui'
import { ChevronLeft, ChevronRight } from '@lucide/vue'

const props = defineProps({
  tabs: { type: Array, default: () => [] },         // [{ id, label, icon }]
  modelValue: { type: String, default: '' },        // active tab id (for centring)
})

const list = ref(null)            // TabsList component instance
const canLeft = ref(false)
const canRight = ref(false)
const ready = ref(false)          // no slide-in animation on first paint

const el = () => list.value?.$el ?? null
const reduceMotion = () => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches

function updateEdges() {
  const e = el()
  if (!e) return
  canLeft.value = e.scrollLeft > 1
  canRight.value = e.scrollLeft + e.clientWidth < e.scrollWidth - 1
}

/** Bring the active trigger to the middle of the track (or as close as it can go). */
function centerActive(instant = false) {
  const e = el()
  const t = e?.querySelector('[role="tab"][data-state="active"]')
  if (!e || !t || e.scrollWidth <= e.clientWidth) return
  const left = t.offsetLeft - (e.clientWidth - t.offsetWidth) / 2
  e.scrollTo({ left, behavior: instant || reduceMotion() ? 'auto' : 'smooth' })
}

function scrollByPage(dir) {
  const e = el()
  if (!e) return
  e.scrollBy({ left: dir * e.clientWidth * 0.7, behavior: reduceMotion() ? 'auto' : 'smooth' })
}

/** Vertical wheel scrolls the strip sideways — only when it actually overflows. */
function onWheel(ev) {
  const e = el()
  if (!e || e.scrollWidth <= e.clientWidth) return
  if (Math.abs(ev.deltaY) <= Math.abs(ev.deltaX)) return   // trackpad already horizontal
  e.scrollLeft += ev.deltaY
  ev.preventDefault()
}

/** Keyboard focus moving along the strip keeps the focused tab visible. */
function onFocusIn(ev) {
  const t = ev.target.closest?.('[role="tab"]')
  t?.scrollIntoView({ block: 'nearest', inline: 'nearest', behavior: reduceMotion() ? 'auto' : 'smooth' })
}

let ro
onMounted(async () => {
  await nextTick()
  const e = el()
  if (!e) return
  e.addEventListener('scroll', updateEdges, { passive: true })
  e.addEventListener('wheel', onWheel, { passive: false })
  ro = new ResizeObserver(() => { updateEdges() })
  ro.observe(e)
  document.fonts?.ready.then(() => { updateEdges(); centerActive(true) })
  centerActive(true)
  updateEdges()
  // Two frames: let reka place the indicator first, then turn transitions on.
  requestAnimationFrame(() => requestAnimationFrame(() => { ready.value = true }))
})
onBeforeUnmount(() => {
  const e = el()
  e?.removeEventListener('scroll', updateEdges)
  e?.removeEventListener('wheel', onWheel)
  ro?.disconnect()
})

watch(() => props.modelValue, () => nextTick(() => centerActive()))
watch(() => props.tabs.length, () => nextTick(() => { updateEdges(); centerActive(true) }))

const fade = computed(() => ({
  'fade-left': canLeft.value,
  'fade-right': canRight.value,
}))
</script>

<template>
  <div class="module-tabs relative flex min-w-0 flex-1 items-center" :data-ready="ready || undefined">
    <!-- Permissions not loaded yet: quiet placeholders, never a guessed tab set. -->
    <div v-if="!tabs.length" class="track flex h-10 w-full items-center gap-1 rounded-full p-1" aria-hidden="true">
      <span v-for="n in 5" :key="n" class="h-8 w-24 shrink-0 animate-pulse rounded-full bg-foreground/5" />
    </div>

    <template v-else>
      <button type="button" class="edge-btn left-0" :class="{ 'is-on': canLeft }" tabindex="-1" aria-hidden="true"
        @click="scrollByPage(-1)">
        <ChevronLeft class="size-4" />
      </button>

      <TabsList ref="list" aria-label="Modules" data-testid="app-header-tabs"
        class="track scroller relative flex h-10 w-full items-center gap-0.5 overflow-x-auto overflow-y-hidden rounded-full p-1"
        :class="fade" @focusin="onFocusIn">
        <TabsIndicator data-testid="app-tabs-indicator" class="pill" />

        <TabsTrigger v-for="t in tabs" :key="t.id" :value="t.id" :data-tab-key="t.id" :title="t.label"
          class="trigger group relative z-1 inline-flex h-8 shrink-0 grow items-center justify-center gap-2 whitespace-nowrap rounded-full px-3.5 text-sm font-medium text-muted-foreground outline-none select-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring/60 data-[state=active]:font-semibold data-[state=active]:text-(--pill-fg) max-sm:px-2.5">
          <component :is="t.icon" class="size-4.5 shrink-0 transition-transform duration-300 group-active:scale-90"
            :stroke-width="1.75" />
          <span class="label">{{ t.label }}</span>
        </TabsTrigger>
      </TabsList>

      <button type="button" class="edge-btn right-0" :class="{ 'is-on': canRight }" tabindex="-1" aria-hidden="true"
        @click="scrollByPage(1)">
        <ChevronRight class="size-4" />
      </button>
    </template>
  </div>
</template>

<style scoped>
/* NOTE: dark rules use :global(.dark .module-tabs …) as ONE selector. Vue compiles
   `:global(.dark) .x` to just `.dark` (it drops `.x`), which would restyle <html>.

   Black & white theme through the existing shadcn tokens:
   light → off-white recessed track, white raised pill, black text
   dark  → near-black recessed track, white raised pill, black text (true B/W inversion) */
.module-tabs {
  --track-bg: color-mix(in oklab, var(--muted) 88%, var(--foreground) 3%);
  --pill-bg: var(--background);
  --pill-fg: var(--foreground);
  --ease: cubic-bezier(.22, 1, .36, 1);
  /* soft "settle" curve */
  --dur: 420ms;
}

:global(.dark .module-tabs) {
  --track-bg: color-mix(in oklab, var(--muted) 70%, black 30%);
  --pill-bg: var(--foreground);
  --pill-fg: var(--background);
}

/* Depth: the track is pressed in, the pill sits on top of it. */
.track {
  background: var(--track-bg);
  box-shadow:
    inset 0 1px 2px rgb(0 0 0 / .07),
    inset 0 0 0 1px rgb(0 0 0 / .035);
}

:global(.dark .module-tabs .track) {
  box-shadow: inset 0 1px 2px rgb(0 0 0 / .6), inset 0 0 0 1px rgb(255 255 255 / .05);
}

.pill {
  position: absolute;
  left: 0;
  top: 4px;
  height: calc(100% - 8px);
  width: var(--reka-tabs-indicator-size);
  transform: translateX(var(--reka-tabs-indicator-position));
  border-radius: 9999px;
  background: var(--pill-bg);
  box-shadow:
    0 1px 1px rgb(0 0 0 / .06),
    0 2px 6px -1px rgb(0 0 0 / .10),
    0 6px 14px -6px rgb(0 0 0 / .18),
    inset 0 1px 0 rgb(255 255 255 / .7);
  pointer-events: none;
}

:global(.dark .module-tabs .pill) {
  box-shadow: 0 1px 2px rgb(0 0 0 / .5), 0 6px 16px -6px rgb(0 0 0 / .8), inset 0 -1px 0 rgb(0 0 0 / .08);
}

[data-ready] .pill {
  transition: transform var(--dur) var(--ease), width var(--dur) var(--ease);
}

.trigger {
  transition: color 200ms ease;
  -webkit-tap-highlight-color: transparent;
}

/* The slider itself: hidden scrollbar, gentle snapping, fades at the edges that have more. */
.scroller {
  scrollbar-width: none;
  scroll-snap-type: x proximity;
  scroll-padding-inline: 2.5rem;
  overscroll-behavior-x: contain;
}

.scroller::-webkit-scrollbar {
  display: none;
}

.scroller> :deep([role="tab"]) {
  scroll-snap-align: center;
}

.fade-left {
  --fl: 28px;
}

.fade-right {
  --fr: 28px;
}

.fade-left,
.fade-right {
  -webkit-mask-image: linear-gradient(90deg, transparent 0, #000 var(--fl, 0px), #000 calc(100% - var(--fr, 0px)), transparent 100%);
  mask-image: linear-gradient(90deg, transparent 0, #000 var(--fl, 0px), #000 calc(100% - var(--fr, 0px)), transparent 100%);
}

/* Arrow buttons: pointer devices only, fade in when that side has more tabs. */
.edge-btn {
  position: absolute;
  z-index: 2;
  top: 50%;
  display: none;
  place-items: center;
  width: 28px;
  height: 28px;
  margin-top: -14px;
  border-radius: 9999px;
  background: var(--background);
  color: var(--foreground);
  box-shadow: 0 1px 3px rgb(0 0 0 / .15);
  opacity: 0;
  pointer-events: none;
  transition: opacity 200ms ease, transform 200ms var(--ease);
}

.edge-btn.left-0 {
  transform: translateX(-4px);
}

.edge-btn.right-0 {
  transform: translateX(4px);
}

.edge-btn.is-on {
  opacity: 1;
  pointer-events: auto;
  transform: none;
}

@media (hover: hover) and (pointer: fine) {
  .edge-btn {
    display: grid;
  }
}

/* Phones: inactive tabs are icon-only; the active tab keeps its label. */
@media (max-width: 639px) {
  .trigger[data-state="inactive"] .label {
    display: none;
  }

  .trigger {
    flex-grow: 0;
  }
}

@media (prefers-reduced-motion: reduce) {

  [data-ready] .pill,
  .trigger,
  .edge-btn,
  .trigger :deep(svg) {
    transition: none !important;
  }

  .scroller {
    scroll-behavior: auto;
  }
}
</style>