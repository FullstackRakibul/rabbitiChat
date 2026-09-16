<script setup>
/**
 * The module tab bar: an animated, responsive, keyboard-accessible row of tabs
 * that drives routing.
 *
 * Three things it has to get right at once:
 *
 *  1. ANIMATION — a single pill slides between tabs. It is one absolutely
 *     positioned element moved with `transform: translate3d` and scaled on X,
 *     so the browser animates it on the compositor; animating left/width would
 *     relayout the row on every frame. Under `prefers-reduced-motion` the
 *     transition is dropped and the pill simply appears in place.
 *
 *  2. RESPONSIVE — the row scrolls horizontally with scroll-snap rather than
 *     wrapping, so it degrades cleanly to 320px instead of breaking into a
 *     second line. The active tab is scrolled into view when it changes, and
 *     edge fades hint that there is more to reach.
 *
 *  3. ACCESSIBLE — reka-ui's Tabs primitives supply the roving tabindex, arrow
 *     keys, Home/End, `role="tab"` and `aria-selected`; this component adds
 *     visible focus rings and touch-sized hit targets. Labels stay readable at
 *     every width (icons never become the only affordance on mobile, which
 *     would strand a screen-reader user with an unlabelled control).
 */
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { TabsList, TabsTrigger } from '@/components/ui/tabs'
import { cn } from '@/lib/utils'

const props = defineProps({
  /** [{ id, label, icon }] — already filtered to what this user may see. */
  tabs: { type: Array, default: () => [] },
  /** The active tab id. */
  modelValue: { type: String, default: '' },
})

/** The scroller and the per-tab elements, for measuring the indicator. */
const scroller = ref(null)
const triggerEls = ref(new Map())

/** Indicator geometry in pixels, relative to the scroller's content box. */
const indicator = ref({ x: 0, width: 0, ready: false })

/**
 * Honour the OS "reduce motion" setting. Read once and kept live: a user can
 * change it while the app is open, and the bar should respond without a reload.
 */
const reduceMotion = ref(false)
let motionQuery = null
function syncMotionPreference(e) {
  reduceMotion.value = e.matches
}

/** Whether the row actually overflows — drives the edge fades. */
const overflowing = ref(false)
const atStart = ref(true)
const atEnd = ref(false)

function setTriggerRef(id, el) {
  // reka-ui hands back the component instance; unwrap to the DOM node.
  const node = el?.$el ?? el
  if (node) triggerEls.value.set(id, node)
  else triggerEls.value.delete(id)
}

/**
 * Measure the active tab and move the pill onto it. Called after any change
 * that can move a tab: selection, tab-set changes, resize, font load.
 */
function measure() {
  const el = triggerEls.value.get(props.modelValue)
  const box = scroller.value
  if (!el || !box) {
    indicator.value = { ...indicator.value, ready: false }
    return
  }

  // offsetLeft is relative to the scrolling content, so the pill travels with
  // the row when it scrolls instead of needing a scroll-position correction.
  indicator.value = { x: el.offsetLeft, width: el.offsetWidth, ready: true }
  updateEdges()
}

function updateEdges() {
  const box = scroller.value
  if (!box) return
  const max = box.scrollWidth - box.clientWidth
  overflowing.value = max > 1
  atStart.value = box.scrollLeft <= 1
  atEnd.value = box.scrollLeft >= max - 1
}

/** Keep the selected tab visible when selection moves via keyboard or routing. */
function scrollActiveIntoView() {
  const el = triggerEls.value.get(props.modelValue)
  if (!el) return
  el.scrollIntoView({
    behavior: reduceMotion.value ? 'auto' : 'smooth',
    block: 'nearest',
    inline: 'nearest',
  })
}

let resizeObserver = null

onMounted(() => {
  motionQuery = window.matchMedia('(prefers-reduced-motion: reduce)')
  reduceMotion.value = motionQuery.matches
  motionQuery.addEventListener('change', syncMotionPreference)

  // A ResizeObserver catches the cases a window resize listener misses: the
  // sidebar collapsing, a font swapping in, the row itself changing width.
  if (scroller.value && 'ResizeObserver' in window) {
    resizeObserver = new ResizeObserver(() => measure())
    resizeObserver.observe(scroller.value)
    for (const el of triggerEls.value.values()) resizeObserver.observe(el)
  }

  nextTick(measure)
})

onBeforeUnmount(() => {
  motionQuery?.removeEventListener('change', syncMotionPreference)
  resizeObserver?.disconnect()
})

// Re-measure whenever the selection or the permitted tab set changes.
watch(() => props.modelValue, async () => {
  await nextTick()
  measure()
  scrollActiveIntoView()
})

watch(() => props.tabs.map((t) => t.id).join(','), async () => {
  await nextTick()
  measure()
})

/**
 * The pill's transform. A single translate+scale keeps it on the compositor:
 * the element is 1px wide and scaled to the tab's width, so no layout property
 * ever animates.
 */
const indicatorStyle = computed(() => ({
  transform: `translate3d(${indicator.value.x}px, 0, 0) scaleX(${indicator.value.width})`,
  opacity: indicator.value.ready ? '1' : '0',
  transition: reduceMotion.value
    ? 'none'
    : 'transform 320ms cubic-bezier(0.22, 1, 0.36, 1), opacity 160ms linear',
}))
</script>

<template>
  <div class="relative min-w-0 flex-1">
    <!-- Edge fades: only shown on the side that has more content. -->
    <div
      v-show="overflowing && !atStart"
      aria-hidden="true"
      class="pointer-events-none absolute inset-y-0 left-0 z-10 w-6 bg-gradient-to-r from-background to-transparent"
    />
    <div
      v-show="overflowing && !atEnd"
      aria-hidden="true"
      class="pointer-events-none absolute inset-y-0 right-0 z-10 w-6 bg-gradient-to-l from-background to-transparent"
    />

    <div
      ref="scroller"
      class="module-tabbar-scroller overflow-x-auto overflow-y-hidden"
      @scroll.passive="updateEdges"
    >
      <TabsList
        class="relative h-auto w-max justify-start gap-0.5 bg-transparent p-0"
      >
        <!-- The sliding pill. Sits behind the triggers and is never focusable. -->
        <span
          aria-hidden="true"
          class="pointer-events-none absolute bottom-0 left-0 top-0 z-0 w-px origin-left rounded-md bg-muted"
          :style="indicatorStyle"
        />

        <TabsTrigger
          v-for="t in tabs"
          :key="t.id"
          :ref="(el) => setTriggerRef(t.id, el)"
          :value="t.id"
          class="relative z-10 h-9 shrink-0 snap-start scroll-mx-2 px-3 data-[state=active]:bg-transparent data-[state=active]:shadow-none sm:px-3"
        >
          <component :is="t.icon" />
          <span class="whitespace-nowrap">{{ t.label }}</span>
        </TabsTrigger>
      </TabsList>
    </div>
  </div>
</template>

<style scoped>
/*
 * Scroll snapping makes a flicked row settle on a tab rather than mid-label.
 * The scrollbar itself is hidden: the edge fades already signal overflow, and a
 * visible bar under a 36px-tall row is mostly noise.
 */
.module-tabbar-scroller {
  scroll-snap-type: x proximity;
  scrollbar-width: none;
  -ms-overflow-style: none;
  /* Momentum scrolling on touch, and no vertical rubber-banding. */
  -webkit-overflow-scrolling: touch;
  overscroll-behavior-x: contain;
}

.module-tabbar-scroller::-webkit-scrollbar {
  display: none;
}
</style>
