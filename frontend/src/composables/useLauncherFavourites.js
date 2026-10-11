// Module launcher favourites — the modules a user pins to the top of the grid.
//
// Per user, in localStorage under `mostisko.launcher.v1:<userId>`. A convenience,
// not a permission: the list is always filtered by the tabs the server says the
// user has, so a pinned module the user lost simply disappears from the row.
//
// Nothing is read or written until the user id is known (it is 0 before
// /api/me/tabs answers and after logout); otherwise one user's favourites could
// land under a shared key and show up for the next account.

import { computed, ref, watch } from 'vue'

export const MAX_FAVOURITES = 6

/** What a user who has never pinned anything starts with, in this order, where allowed. */
const DEFAULTS = ['chat', 'workspace', 'docs', 'notes', 'todo', 'search', 'dashboard']

/** userId → saved list (null = never saved). Module scope: every launcher shares it. */
const saved = ref({})

const storageKey = (userId) => `mostisko.launcher.v1:${userId}`

function read(userId) {
  try {
    const value = JSON.parse(localStorage.getItem(storageKey(userId)) ?? 'null')
    return Array.isArray(value?.favourites) ? value.favourites.filter((id) => typeof id === 'string') : null
  } catch {
    return null       // storage blocked or corrupt: fall back to the defaults
  }
}

function write(userId, favourites) {
  try {
    localStorage.setItem(storageKey(userId), JSON.stringify({ favourites }))
  } catch {
    /* storage blocked: the pin still holds for this page's life */
  }
}

function defaults(allowed) {
  return [...DEFAULTS.filter((id) => allowed.includes(id)), ...allowed]
    .filter((id, i, all) => all.indexOf(id) === i)
    .slice(0, 4)
}

/**
 * @param userId  ref/computed — the signed-in user's id, 0 while unknown
 * @param tabs    ref/computed — the tabs the user may open: [{ id, … }]
 */
export function useLauncherFavourites(userId, tabs) {
  watch(userId, (uid) => {
    if (uid && !(uid in saved.value)) saved.value = { ...saved.value, [uid]: read(uid) }
  }, { immediate: true })

  const favourites = computed(() => {
    const allowed = tabs.value.map((t) => t.id)
    const mine = userId.value ? saved.value[userId.value] : null
    return (mine ?? defaults(allowed)).filter((id) => allowed.includes(id)).slice(0, MAX_FAVOURITES)
  })

  /** Pins or unpins a module. False when it cannot: no user yet, or the row is full. */
  function toggle(id) {
    const uid = userId.value
    if (!uid) return false
    const current = favourites.value
    const next = current.includes(id) ? current.filter((x) => x !== id) : [...current, id]
    if (next.length > MAX_FAVOURITES) return false
    saved.value = { ...saved.value, [uid]: next }
    write(uid, next)
    return true
  }

  return { favourites, toggle }
}
