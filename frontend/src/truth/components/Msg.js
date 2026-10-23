// A site-text message rendered in parts, so a value that must not be translated
// — a product name, a byline — sits in its own translate="no" span while the
// words around it follow the language. Plain text only: parts become text nodes
// and spans, never HTML (docs/TRUTH_PROMPT.md §5).

import { defineComponent, h } from 'vue'
import { rawText, useI18n } from '../lib/i18n.js'
import { parseText } from '../lib/site-text.js'

export default defineComponent({
  name: 'Msg',
  props: {
    /** The site-text key. */
    k: { type: String, required: true },
    values: { type: Object, default: () => ({}) },
    /** Placeholders whose values are names, not words: wrapped in translate="no". */
    keep: { type: Array, default: () => [] },
    tag: { type: String, default: 'span' },
  },
  setup(props) {
    const { locale } = useI18n()
    return () => {
      void locale.value
      const parts = parseText(rawText(props.k)).map((p) => {
        if (typeof p === 'string') return p
        const v = props.values[p.name]
        if (v === undefined || v === null) return `{${p.name}}`
        return props.keep.includes(p.name) ? h('span', { translate: 'no' }, String(v)) : String(v)
      })
      return h(props.tag, parts)
    }
  },
})
