<script setup>
/**
 * The module launcher's panel: a favourites row, then every module the user may
 * open, grouped, as tiles — the Google-apps pattern.
 *
 * Keyboard: the grid is one tab stop (roving tabindex); arrow keys move between
 * tiles by where they sit on screen, Home/End jump to the ends, Enter or Space
 * opens. "Edit" turns a tile click into pin/unpin instead of open.
 *
 * Colours are theme tokens only; motion is transform/opacity only and stops under
 * prefers-reduced-motion.
 */
import { computed, nextTick, ref } from 'vue'
import { Check, Pencil, Star } from '@lucide/vue'
import { Kbd } from '@/components/ui/kbd'
import { cn } from '@/lib/utils'
import { MAX_FAVOURITES } from '@/composables/useLauncherFavourites'

const props = defineProps({
  /** The modules this user may open: [{ id, label, icon, group }], in display order. */
  tabs: { type: Array, default: () => [] },
  activeTab: { type: String, default: '' },
  favourites: { type: Array, default: () => [] },
})

const emit = defineEmits(['launch', 'toggle-favourite'])

const editing = ref(false)
const notice = ref('')

/** Favourites first (in the order they were pinned), then each group in tab order. */
const sections = computed(() => {
  const byId = new Map(props.tabs.map((t) => [t.id, t]))
  const out = []
  const favs = props.favourites.map((id) => byId.get(id)).filter(Boolean)
  if (favs.length || editing.value) out.push({ key: 'fav', title: 'Favourites', items: favs })
  for (const t of props.tabs) {
    const title = t.group ?? 'Modules'
    let s = out.find((x) => x.key === `group:${title}`)
    if (!s) { s = { key: `group:${title}`, title, items: [] }; out.push(s) }
    s.items.push(t)
  }
  return out
})

const isFavourite = (id) => props.favourites.includes(id)

// ---- Roving tabindex: one tile in the grid is the tab stop ----

const root = ref(null)
const roving = ref('')
const tileKey = (section, id) => `${section.key}:${id}`

function initialKey() {
  const home = sections.value.find((s) => s.key !== 'fav' && s.items.some((t) => t.id === props.activeTab))
  if (home) return tileKey(home, props.activeTab)
  const first = sections.value.find((s) => s.items.length)
  return first ? tileKey(first, first.items[0].id) : ''
}

const tabStop = computed(() => roving.value || initialKey())

/** Called when the panel opens: focus the open module's tile (or the first one). */
function focusInitial() {
  roving.value = initialKey()
  nextTick(() => root.value?.querySelector(`[data-launcher-key="${CSS.escape(roving.value)}"]`)?.focus())
}

const tiles = () => [...(root.value?.querySelectorAll('[data-launcher-tile]') ?? [])]

/** Arrow keys move by on-screen position, so rows that differ in length still feel right. */
function onKeydown(e) {
  const all = tiles()
  const i = all.indexOf(document.activeElement)
  if (i < 0) return

  let next = null
  if (e.key === 'Home') next = all[0]
  else if (e.key === 'End') next = all.at(-1)
  else if (e.key === 'ArrowRight') next = all[i + 1]
  else if (e.key === 'ArrowLeft') next = all[i - 1]
  else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
    const down = e.key === 'ArrowDown'
    const here = all[i].getBoundingClientRect()
    const centre = here.left + here.width / 2
    const beyond = all.filter((t) => {
      const r = t.getBoundingClientRect()
      return down ? r.top >= here.bottom - 1 : r.bottom <= here.top + 1
    })
    if (beyond.length) {
      const tops = beyond.map((t) => t.getBoundingClientRect().top)
      const rowTop = down ? Math.min(...tops) : Math.max(...tops)
      const row = beyond.filter((t) => Math.abs(t.getBoundingClientRect().top - rowTop) < 2)
      next = row.reduce((best, t) => {
        const r = t.getBoundingClientRect()
        const distance = Math.abs(r.left + r.width / 2 - centre)
        return !best || distance < best.distance ? { t, distance } : best
      }, null)?.t
    }
  } else {
    return
  }
  e.preventDefault()
  next?.focus()
}

function onTile(id) {
  notice.value = ''
  if (!editing.value) {
    emit('launch', id)
    return
  }
  if (!isFavourite(id) && props.favourites.length >= MAX_FAVOURITES) {
    notice.value = `${MAX_FAVOURITES} favourites at most — unpin one first.`
    return
  }
  emit('toggle-favourite', id)
}

function toggleEditing() {
  editing.value = !editing.value
  notice.value = ''
}

defineExpose({ focusInitial })
</script>

<template>
  <div ref="root" data-testid="launcher-panel" @keydown="onKeydown">
    <div class="mb-1 flex items-center justify-between gap-2 px-1">
      <p class="text-sm font-semibold">Modules</p>
      <button
        type="button"
        class="inline-flex items-center gap-1 rounded-md px-2 py-1 text-xs text-muted-foreground transition-colors hover:bg-accent hover:text-accent-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        :aria-pressed="editing"
        @click="toggleEditing"
      >
        <Check v-if="editing" class="size-3.5" />
        <Pencil v-else class="size-3.5" />
        {{ editing ? 'Done' : 'Edit favourites' }}
      </button>
    </div>
    <p v-if="editing" class="px-1 pb-1 text-xs text-muted-foreground">
      Tap a module to pin or unpin it. Up to {{ MAX_FAVOURITES }}.
    </p>
    <p v-if="notice" class="px-1 pb-1 text-xs text-destructive" role="status">{{ notice }}</p>

    <section
      v-for="s in sections"
      :key="s.key"
      :aria-label="s.title"
      :class="cn('mt-1', s.key === 'fav' && 'rounded-xl bg-muted/40 p-1.5')"
    >
      <h3 class="px-1 pb-1 pt-1.5 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
        {{ s.title }}
      </h3>
      <p v-if="!s.items.length" class="px-1 pb-2 text-xs text-muted-foreground">
        Nothing pinned yet. Tap modules below to add them.
      </p>
      <div class="grid grid-cols-3 gap-1 sm:grid-cols-4">
        <button
          v-for="t in s.items"
          :key="t.id"
          type="button"
          data-launcher-tile
          :data-launcher-key="tileKey(s, t.id)"
          :data-tab-key="t.id"
          :tabindex="tabStop === tileKey(s, t.id) ? 0 : -1"
          :aria-current="t.id === activeTab ? 'page' : undefined"
          :aria-pressed="editing ? isFavourite(t.id) : undefined"
          :aria-label="editing ? `${t.label}, ${isFavourite(t.id) ? 'pinned' : 'not pinned'}` : undefined"
          class="group relative flex min-w-0 flex-col items-center gap-1.5 rounded-xl px-1 py-2 text-center transition-colors hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring aria-[current=page]:bg-accent"
          @focus="roving = tileKey(s, t.id)"
          @click="onTile(t.id)"
        >
          <span
            class="grid size-10 place-items-center rounded-full bg-muted text-foreground transition-transform duration-150 group-active:scale-95 group-aria-[current=page]:bg-primary group-aria-[current=page]:text-primary-foreground motion-reduce:transition-none"
          >
            <component :is="t.icon" class="size-5" :stroke-width="1.75" aria-hidden="true" />
          </span>
          <span class="line-clamp-2 w-full wrap-break-word text-xs leading-tight">{{ t.label }}</span>
          <Star
            v-if="editing && s.key !== 'fav'"
            :class="cn('absolute right-2 top-1.5 size-3.5', isFavourite(t.id) ? 'fill-current text-foreground' : 'text-muted-foreground')"
            aria-hidden="true"
          />
        </button>
      </div>
    </section>

    <div class="mt-2 flex flex-wrap items-center justify-between gap-2 border-t px-1 pt-2 text-[11px] text-muted-foreground">
      <span class="inline-flex items-center gap-1"><Kbd keys="g" size="sm" /> then <Kbd keys="m" size="sm" /> opens this</span>
      <span class="inline-flex items-center gap-1"><Kbd keys="mod + K" size="sm" /> to search</span>
    </div>
  </div>
</template>
