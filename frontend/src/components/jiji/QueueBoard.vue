<script setup>
/**
 * Jiji — the queue: your items by status, newest change first, a filter chip per
 * status and "New". Opening an item shows its editor; the list re-reads whenever
 * the editor changes something. The open item is in the URL (?item=), so a
 * refresh or a link lands on it.
 */
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ArrowLeft, Loader2, Plus, RefreshCw } from '@lucide/vue'
import { Button } from '@/components/ui/button'
import NewIdeaDialog from '@/components/jiji/NewIdeaDialog.vue'
import ScriptEditor from '@/components/jiji/ScriptEditor.vue'
import { JIJI_CONTENT_TYPES, JIJI_STATUSES, listJijiContents } from '@/services/jiji.service.js'

const route = useRoute()
const router = useRouter()

const items = ref([])
const filter = ref('')
const loading = ref(false)
const error = ref('')
const newOpen = ref(false)

const openId = computed(() => Number(route.query.item) || null)
const shown = computed(() => filter.value ? items.value.filter((i) => i.status === filter.value) : items.value)
const countFor = (s) => items.value.filter((i) => i.status === s).length
const statusLabel = (s) => JIJI_STATUSES.find((x) => x.value === s)?.label ?? s
const typeLabel = (t) => JIJI_CONTENT_TYPES.find((x) => x.value === t)?.label ?? t
const when = (iso) => new Date(iso).toLocaleString('en-GB', { timeZone: 'Asia/Dhaka', dateStyle: 'medium', timeStyle: 'short' })

async function load() {
  loading.value = true; error.value = ''
  try { items.value = await listJijiContents() } catch (e) { error.value = e.message } finally { loading.value = false }
}

function open(id) {
  router.replace({ path: route.path, query: { ...route.query, item: id ?? undefined } })
}

function created(item) {
  load()
  open(item.id)
}

onMounted(load)
</script>

<template>
  <div class="mx-auto flex w-full max-w-4xl flex-col gap-4" data-jiji-queue>
    <template v-if="openId">
      <div>
        <Button size="sm" variant="ghost" @click="open(null)"><ArrowLeft /> Queue</Button>
      </div>
      <ScriptEditor :id="openId" @changed="load" @close="open(null)" />
    </template>

    <template v-else>
      <div class="flex flex-wrap items-center gap-2">
        <button type="button" class="rounded-full border px-3 py-1 text-xs"
          :class="filter === '' ? 'bg-primary text-primary-foreground' : 'hover:bg-muted'" @click="filter = ''">
          All {{ items.length }}
        </button>
        <template v-for="s in JIJI_STATUSES" :key="s.value">
          <button v-if="countFor(s.value)" type="button" class="rounded-full border px-3 py-1 text-xs"
            :class="filter === s.value ? 'bg-primary text-primary-foreground' : 'hover:bg-muted'" @click="filter = s.value">
            {{ s.label }} {{ countFor(s.value) }}
          </button>
        </template>
        <span class="ml-auto flex gap-2">
          <Button size="sm" variant="ghost" :disabled="loading" @click="load">
            <Loader2 v-if="loading" class="animate-spin" /><RefreshCw v-else />
          </Button>
          <Button size="sm" data-jiji-new-button @click="newOpen = true"><Plus /> New</Button>
        </span>
      </div>

      <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive">{{ error }}</p>

      <div v-if="!loading && !items.length && !error" class="rounded-lg border border-dashed p-8 text-center text-sm text-muted-foreground">
        Nothing yet. Start with “New”: a subject and your own notes.
      </div>

      <ul v-else class="flex flex-col divide-y rounded-lg border bg-card">
        <li v-for="i in shown" :key="i.id">
          <button type="button" class="flex w-full flex-col gap-1 px-4 py-3 text-left hover:bg-muted/50" :data-jiji-item="i.id" @click="open(i.id)">
            <span class="flex flex-wrap items-center gap-2">
              <span class="font-medium">{{ i.title || i.subject }}</span>
              <span class="rounded border px-1.5 text-xs">{{ statusLabel(i.status) }}</span>
              <span class="text-xs text-muted-foreground">{{ typeLabel(i.contentType) }}</span>
              <span class="ml-auto text-xs text-muted-foreground">{{ when(i.updatedAt) }}</span>
            </span>
            <span v-if="i.angle" class="text-xs text-muted-foreground">{{ i.angle }}</span>
            <span v-if="i.lastError" class="text-xs text-destructive">{{ i.lastError }}</span>
          </button>
        </li>
      </ul>
    </template>

    <NewIdeaDialog v-model:open="newOpen" @created="created" />
  </div>
</template>
