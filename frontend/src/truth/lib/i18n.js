// The site text in the visitor's language (docs/TRUTH_PROMPT.md §5).
//
// Plain text with {name} placeholders — the same rule the share pages and the
// emails apply (lib/site-text.js, backend/Truth/TruthText.cs) — so a "|" or an
// "@" an editor types stays as typed. A locale and a lookup are all the page
// needs, so they are written here rather than taken from an i18n library; the
// components ask through useI18n(), like they would with one. Text is never HTML.

import { reactive, ref } from 'vue'
import defaults from '../content/site.default.json'
import { formatText, lookup, mergeText } from './site-text.js'

const messages = reactive({ bn: defaults.bn, en: defaults.en })
const locale = ref('bn')

/** The current language's text for a key, its {name} placeholders filled; the key itself if there is none. */
function t(key, values) {
  const text = lookup(messages[locale.value], key)
  if (typeof text !== 'string') return key
  return values ? formatText(text, values) : text
}

export const i18n = {
  global: {
    locale,
    t,
    getLocaleMessage: (lang) => messages[lang],
    setLocaleMessage: (lang, value) => { messages[lang] = value },
  },
  /** For createApp().use(): nothing to install; components call useI18n(). */
  install() {},
}

export function useI18n() {
  return { t, locale }
}

/** The site text an administrator published, laid over the built-in default (null: the default alone). */
export function applySiteText(override) {
  for (const lang of ['bn', 'en']) messages[lang] = mergeText(defaults[lang], override?.[lang])
}

/** The raw text of a key in the current language — for a message rendered in parts (Msg.js). */
export function rawText(key) {
  const v = lookup(messages[locale.value], key)
  return typeof v === 'string' ? v : key
}
