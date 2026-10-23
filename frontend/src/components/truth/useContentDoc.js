// One editable document of the Ministry of Truth (site text, memes, glossary):
// load its state, keep a working copy, save a draft, publish with a note, roll
// back. The server validates every publish; the editors check as you type.

import { ref } from 'vue'
import { getTruthContent, publishTruthContent, rollbackTruthContent, saveTruthContentDraft } from '@/services/index.js'

export function useContentDoc(kind) {
  const state = ref(null)
  const doc = ref(null)
  const note = ref('')
  const error = ref('')
  const notice = ref('')
  const problems = ref([])

  async function load() {
    error.value = ''
    try {
      state.value = await getTruthContent(kind)
      doc.value = structuredClone(state.value.draft?.text ?? state.value.published?.text ?? state.value.default)
    } catch (e) { error.value = e.message }
  }

  async function saveDraft() {
    error.value = ''; notice.value = ''
    try { await saveTruthContentDraft(kind, doc.value); notice.value = 'Draft saved (not public).' } catch (e) { error.value = e.message }
  }

  async function publish() {
    error.value = ''; notice.value = ''; problems.value = []
    try {
      const r = await publishTruthContent(kind, doc.value, note.value || null)
      if (r.ok) { notice.value = `Published as version ${r.version}. Live for every visitor now.`; note.value = ''; await load() }
      else { error.value = r.error; problems.value = r.problems }
    } catch (e) { error.value = e.message }
  }

  async function rollback(version) {
    if (!confirm(`Publish version ${version} again?`)) return
    error.value = ''; notice.value = ''
    try { const r = await rollbackTruthContent(kind, version); notice.value = `Version ${version} is live again (as version ${r.version}).`; await load() } catch (e) { error.value = e.message }
  }

  return { state, doc, note, error, notice, problems, load, saveDraft, publish, rollback }
}
