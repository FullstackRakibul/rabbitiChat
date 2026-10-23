<script setup>
// A meme banner (§3A.5): a small sticker — ribbon, stamp card or mini telescreen —
// with an optional mascot. Decoration only: never inside safety information, and
// gone in serious mode. `redact` covers it with a bar that slides away on a tap
// (the 404 page): a joke hidden, never a fact.
import { ref } from 'vue'
import Mascot from './Mascot.vue'
import { lang } from '../lib/lang.js'

defineProps({
  meme: { type: Object, required: true },
  redact: { type: Boolean, default: false },
})
const shown = ref(false)
</script>

<template>
  <aside :data-meme="meme.id" :data-mood="meme.mood" class="meme" :class="`meme-${meme.style}`">
    <Mascot v-if="meme.pose" :pose="meme.pose" :size="40" />
    <button v-if="redact" type="button" class="meme-text redact" :class="{ shown }" :aria-pressed="shown" @click="shown = !shown">
      <span data-content>{{ meme[lang] }}</span>
    </button>
    <p v-else class="meme-text m-0" data-content>{{ meme[lang] }}</p>
  </aside>
</template>
