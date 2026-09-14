// The log of generated novelty cheques.
//
// localStorage only, by design: this is a joke among friends, and a list of who
// you made fake cheques out to has no business on a server. Nothing here calls
// the network, and the module works with the app fully offline.
//
// Module-level state so the generator and the dashboard read one list — writing
// in one place shows up in the other without a bus or a prop chain.

import { computed, ref } from 'vue'

const STORAGE_KEY = 'rabbitic.fivelakhs.history'

/** Keeps the list (and the localStorage entry) from growing without bound. */
const MAX_ENTRIES = 50

function load() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (!raw) return []
    const parsed = JSON.parse(raw)
    return Array.isArray(parsed) ? parsed.filter((e) => e && e.id) : []
  } catch {
    // Corrupt or unavailable storage (private mode, cleared data) is not an
    // error worth surfacing — an empty history is a perfectly good start.
    return []
  }
}

const entries = ref(load())

function persist() {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(entries.value))
  } catch {
    /* storage full or disabled — the in-memory list still works this session */
  }
}

export function useCheckHistory() {
  /**
   * Records one generated cheque. Returns the stored entry so the caller can
   * reference it (a re-download, say) without re-reading the list.
   */
  function add(details) {
    const entry = {
      id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
      createdAt: new Date().toISOString(),
      payeeName: details.payeeName || '',
      bankName: details.bankName || '',
      currency: details.currency || 'BDT',
      amount: details.amount,
      date: details.date || '',
      memo: details.memo || '',
      accountHolder: details.accountHolder || '',
    }

    entries.value = [entry, ...entries.value].slice(0, MAX_ENTRIES)
    persist()
    return entry
  }

  function remove(id) {
    entries.value = entries.value.filter((e) => e.id !== id)
    persist()
  }

  function clear() {
    entries.value = []
    persist()
  }

  /** Re-reads storage — for picking up a write made in another tab. */
  function refresh() {
    entries.value = load()
  }

  const count = computed(() => entries.value.length)

  return { entries, count, add, remove, clear, refresh }
}
