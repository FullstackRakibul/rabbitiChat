// The published guides' index (/truth-api/guides): the guides list, and the
// buttons on each item card and guide. Loaded once a page view, kept like the
// rest when the backend is away.

import { ref } from 'vue'
import { load } from './api.js'

export const guideIndex = ref([])
let loading = null

export function loadGuides() {
  loading ??= load('/guides').then((r) => { guideIndex.value = r.notFound ? [] : r.data.guides }).catch(() => { guideIndex.value = [] })
  return loading
}

/** The published guides of one item, by phase. */
export const guidesFor = (itemId) => guideIndex.value.filter((g) => g.itemId === itemId)

export const PHASE_KEY = { before_patch: 'item.guideBefore', after_patch: 'item.guideAfter', hardening: 'item.guideHardening' }
