<script setup>
// A section header (§3A.6): the plain name, large, and the themed nickname small
// above it in typewriter type. The plain name comes first in the markup, so a
// screen reader says it first; a blanked nickname simply isn't drawn.
import { useI18n } from '../lib/i18n.js'

defineProps({
  /** Site-text key of the plain name. */
  plain: { type: String, required: true },
  /** Site-text key of the nickname. */
  nick: { type: String, required: true },
  tag: { type: String, default: 'h2' },
  id: { type: String, default: undefined },
  large: { type: Boolean, default: false },
})
const { t } = useI18n()
</script>

<template>
  <component :is="tag" :id="id" class="m-0 mb-3 flex flex-col-reverse">
    <span class="font-semibold" :class="large ? 'text-2xl' : 'text-lg'">{{ t(plain) }}</span>
    <span v-if="t(nick).trim()" data-nickname class="nick">{{ t(nick) }}</span>
  </component>
</template>
