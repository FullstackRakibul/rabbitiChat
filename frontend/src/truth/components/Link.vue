<script setup>
// An internal link: client-side navigation, and the language kept in the URL.
import { computed } from 'vue'
import { navigate } from '../lib/router.js'
import { lang, withLang } from '../lib/lang.js'

const props = defineProps({ to: { type: String, required: true } })
const href = computed(() => (props.to.startsWith('#') ? props.to : withLang(props.to, lang.value)))

function go(e) {
  if (props.to.startsWith('#') || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey || e.button !== 0) return
  e.preventDefault()
  navigate(href.value)
}
</script>

<template>
  <a :href="href" @click="go"><slot /></a>
</template>
