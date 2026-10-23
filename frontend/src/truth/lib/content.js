// Staff-written text in the visitor's language (docs/TRUTH_PROMPT.md §2): the
// English field when there is one, otherwise the Bangla one — marked lang="bn"
// so the page says what it is, with the "English version not available yet" note.

/** { text, lang, fallback } for one Bn/En field pair of an object. */
export function pick(obj, field, lang) {
  const bn = obj?.[`${field}Bn`] ?? null
  const en = obj?.[`${field}En`] ?? null
  if (lang === 'en' && en) return { text: en, lang: null, fallback: false }
  if (lang === 'en') return { text: bn, lang: bn ? 'bn' : null, fallback: !!bn }
  return { text: bn, lang: null, fallback: false }
}

/** Whether any of these fields of an object fell back to Bangla in English. */
export function anyFallback(obj, fields, lang) {
  return lang === 'en' && fields.some((f) => pick(obj, f, lang).fallback)
}

export const URGENT = ['active_attack', 'patch_scheduled']

/** The colour family of a status pill (§2): red, amber, grey or green. Its label is site text. */
export function tone(status) {
  if (['active_attack', 'breach_confirmed'].includes(status)) return 'red'
  if (['patch_scheduled', 'ongoing', 'contained'].includes(status)) return 'amber'
  if (status === 'unconfirmed') return 'grey'
  return 'green'
}
