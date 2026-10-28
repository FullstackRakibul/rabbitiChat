<script setup>
// The Records Department on /truth (docs/TRUTH_PROMPT_P5.md §4): the live status strip and the wire,
// loaded after the issue (this component is its own chunk), refreshed every minute while the page is
// visible. With the backend down, the last copy shows, with when it was collected (lib/api.js).
import { onMounted, onUnmounted, ref } from 'vue'
import { useI18n } from '../lib/i18n.js'
import { lang } from '../lib/lang.js'
import { formatDateTime } from '../lib/time.js'
import { loadStatus, loadWire } from '../lib/records.js'
import LiveStatus from './LiveStatus.vue'
import WireList from './WireList.vue'

const { t } = useI18n()
const status = ref(null)
const wire = ref(null)
const savedAt = ref(null)
let timer = null

async function refresh() {
  const [s, w] = await Promise.allSettled([loadStatus(), loadWire()])
  if (s.status === 'fulfilled' && !s.value.notFound) status.value = s.value.data
  if (w.status === 'fulfilled' && !w.value.notFound) {
    wire.value = w.value.data
    savedAt.value = w.value.offline ? w.value.savedAt : null
  }
}

onMounted(() => {
  refresh()
  timer = setInterval(() => { if (document.visibilityState === 'visible') refresh() }, 60_000)
})
onUnmounted(() => clearInterval(timer))
</script>

<template>
  <div class="space-y-10" data-testid="records">
    <p v-if="savedAt" class="m-0 rounded-lg bg-surface-2 px-3 py-2 text-sm text-muted" data-testid="records-offline">
      {{ t('wire.lastCollected', { time: formatDateTime(t, lang, wire?.lastCollected ?? savedAt) }) }}
    </p>
    <LiveStatus v-if="status?.sources?.length" :sources="status.sources" />
    <WireList v-if="wire" :first="wire" />
  </div>
</template>
