<script setup>
import { computed, ref } from 'vue'
import {
  BookOpen, ChevronUp, ExternalLink, Globe, MessageSquare, Search as SearchIcon,
} from '@lucide/vue'
import { onMounted } from 'vue'
import { search as runSearch, fetchPage, health } from '@/services/search.service.js'
import { HttpError } from '@/services/http.js'
import { renderMarkdown } from '@/lib/markdown'
import { cn } from '@/lib/utils'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Badge } from '@/components/ui/badge'
import { Separator } from '@/components/ui/separator'
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from '@/components/ui/select'

const emit = defineEmits(['ask'])

const query = ref('')
const maxResults = ref('5')           // reka-ui Select works in strings
const results = ref([])
const busy = ref(false)
const error = ref('')
const searched = ref(false)           // separates "no results" from "not searched yet"
const lastQuery = ref('')

// Session-local only — deliberately not persisted.
const history = ref([])

// Per-result expansion state, keyed by url: { loading, content, error, title }
const pages = ref({})

const HISTORY_LIMIT = 10

// ---- Provider health ----
// Answers "is search usable right now", so a key/plan problem is visible before
// the user spends a query on it. Never carries the key itself.
const status = ref({ state: 'checking', label: 'Checking…', detail: '' })

const STATUS_STYLE = {
  checking: { dot: 'bg-muted-foreground/40', text: 'text-muted-foreground' },
  ready: { dot: 'bg-emerald-500', text: 'text-muted-foreground' },
  warn: { dot: 'bg-amber-500', text: 'text-amber-700 dark:text-amber-400' },
  error: { dot: 'bg-destructive', text: 'text-destructive' },
}

/** Maps the backend's status vocabulary to the three-light indicator. */
function toIndicator(res) {
  if (res.ok) return { state: 'ready', label: 'Search ready', detail: res.detail }

  switch (res.status) {
    case 'not_configured':
      return { state: 'warn', label: 'Not configured', detail: res.detail }
    case 'rate_limited':
      return { state: 'warn', label: 'Rate limited', detail: res.detail }
    case 'provider_rejected':
      return { state: 'error', label: 'Key or plan issue', detail: res.detail }
    case 'not_found':
    case 'provider_error':
    case 'unreachable':
    case 'timeout':
      return { state: 'error', label: 'Provider issue', detail: res.detail }
    default:
      return { state: 'error', label: 'Unavailable', detail: res.detail || '' }
  }
}

async function refreshHealth() {
  status.value = { state: 'checking', label: 'Checking…', detail: '' }
  try {
    status.value = toIndicator(await health())
  } catch (e) {
    status.value = {
      state: 'error',
      label: 'Unavailable',
      detail: e.message || 'Could not reach the server.',
    }
  }
}

onMounted(refreshHealth)

function remember(q) {
  history.value = [q, ...history.value.filter((h) => h !== q)].slice(0, HISTORY_LIMIT)
}

/** Map transport failures to something a user can act on. */
function describe(e) {
  if (e instanceof HttpError) {
    if (e.status === 503) {
      return 'Web search is not configured. Ask an admin to set OllamaCloud:ApiKey in the backend configuration.'
    }
    if (e.status === 429) return 'Slow down — search is rate-limited.'
    if (e.status === 502) return 'The search provider could not be reached. Try again shortly.'
    if (e.status === 504) return 'The search provider timed out. Try again.'
  }
  return e.message || 'Search failed.'
}

async function submit(q = query.value) {
  const text = (q || '').trim()
  if (!text || busy.value) return

  query.value = text
  error.value = ''
  busy.value = true
  try {
    results.value = await runSearch(text, Number(maxResults.value))
    pages.value = {}
    lastQuery.value = text
    searched.value = true
    remember(text)
  } catch (e) {
    error.value = describe(e)
    results.value = []
    searched.value = true
  } finally {
    busy.value = false
  }
}

/** Expand a result inline with the page's readable content. */
async function readPage(url) {
  const existing = pages.value[url]
  if (existing && !existing.error) {
    // Already loaded (or loading) — collapse.
    pages.value = { ...pages.value, [url]: { ...existing, open: !existing.open } }
    return
  }

  pages.value = { ...pages.value, [url]: { loading: true, open: true } }
  try {
    const page = await fetchPage(url)
    pages.value = {
      ...pages.value,
      [url]: { loading: false, open: true, title: page.title, content: page.content, truncated: page.truncated },
    }
  } catch (e) {
    pages.value = { ...pages.value, [url]: { loading: false, open: true, error: describe(e) } }
  }
}

function hostOf(url) {
  try {
    return new URL(url).hostname.replace(/^www\./, '')
  } catch {
    return url
  }
}

const hasResults = computed(() => results.value.length > 0)
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <!-- Search bar -->
    <div class="shrink-0 border-b px-4 py-3">
      <div class="mx-auto flex w-full max-w-3xl flex-col gap-3">
        <div class="flex flex-wrap items-center gap-2">
          <div class="relative min-w-0 flex-1">
            <SearchIcon
              class="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
            />
            <Input
              v-model="query"
              placeholder="Search the web…"
              class="pl-9"
              :disabled="busy"
              @keydown.enter.exact.prevent="submit()"
            />
          </div>

          <Select v-model="maxResults">
            <SelectTrigger class="h-9 w-auto min-w-20 text-xs">
              <SelectValue placeholder="Results" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="5">
                5 results
              </SelectItem>
              <SelectItem value="10">
                10 results
              </SelectItem>
            </SelectContent>
          </Select>

          <Button :disabled="busy || !query.trim()" @click="submit()">
            <SearchIcon />
            {{ busy ? 'Searching…' : 'Search' }}
          </Button>
        </div>

        <!-- Provider status + metered-usage note -->
        <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
          <button
            type="button"
            class="flex items-center gap-1.5 text-xs transition-opacity hover:opacity-80"
            :title="status.detail || 'Re-check search availability'"
            @click="refreshHealth"
          >
            <span :class="cn('h-2 w-2 rounded-full', STATUS_STYLE[status.state].dot)" />
            <span :class="STATUS_STYLE[status.state].text">{{ status.label }}</span>
          </button>

          <span class="text-xs text-muted-foreground">
            Powered by Ollama cloud — usage is limited.
          </span>
        </div>

        <p
          v-if="status.detail && status.state !== 'ready' && status.state !== 'checking'"
          class="text-xs text-muted-foreground"
        >
          {{ status.detail }}
        </p>

        <!-- Recent queries (session only) -->
        <div v-if="history.length" class="flex flex-wrap items-center gap-1.5">
          <span class="text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
            Recent
          </span>
          <button
            v-for="h in history"
            :key="h"
            type="button"
            :class="cn(
              'rounded-md border px-2 py-0.5 text-xs transition-colors hover:bg-accent hover:text-accent-foreground',
              h === lastQuery ? 'bg-accent text-accent-foreground' : 'text-muted-foreground',
            )"
            :disabled="busy"
            @click="submit(h)"
          >
            {{ h }}
          </button>
        </div>
      </div>
    </div>

    <!-- Results -->
    <div class="min-h-0 flex-1 overflow-y-auto px-4 py-6">
      <div class="mx-auto w-full max-w-3xl">
        <p v-if="error" class="mb-4 rounded-lg border border-destructive/40 bg-destructive/5 px-3 py-2 text-sm font-medium text-destructive">
          {{ error }}
        </p>

        <!-- Loading skeletons -->
        <div v-if="busy" class="flex flex-col gap-3">
          <div v-for="i in Number(maxResults)" :key="i" class="rounded-xl border bg-card p-4">
            <div class="h-4 w-2/3 animate-pulse rounded bg-muted" />
            <div class="mt-2 h-3 w-1/3 animate-pulse rounded bg-muted" />
            <div class="mt-3 h-3 w-full animate-pulse rounded bg-muted" />
            <div class="mt-1.5 h-3 w-5/6 animate-pulse rounded bg-muted" />
          </div>
        </div>

        <!-- Empty states -->
        <div v-else-if="!hasResults" class="py-16 text-center">
          <Globe class="mx-auto mb-3 h-8 w-8 text-muted-foreground/50" />
          <p class="text-sm text-muted-foreground">
            {{ searched && !error
              ? `No results for “${lastQuery}”. Try different wording.`
              : 'Search the web and bring the results into your chat.' }}
          </p>
        </div>

        <template v-else>
          <div class="mb-3 flex flex-wrap items-center gap-2">
            <span class="text-xs text-muted-foreground">
              {{ results.length }} result{{ results.length === 1 ? '' : 's' }} for
              <b class="text-foreground">{{ lastQuery }}</b>
            </span>
            <Button variant="outline" size="sm" class="ml-auto h-8" @click="emit('ask', lastQuery)">
              <MessageSquare />
              Ask about these results
            </Button>
          </div>

          <div class="flex flex-col gap-3">
            <article
              v-for="r in results"
              :key="r.url"
              class="rounded-xl border bg-card p-4 shadow-sm"
            >
              <a
                :href="r.url"
                target="_blank"
                rel="noopener noreferrer"
                class="group flex items-start gap-1.5 text-sm font-semibold text-foreground hover:underline"
              >
                <span class="min-w-0">{{ r.title || r.url }}</span>
                <ExternalLink class="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground" />
              </a>

              <p class="mt-0.5 truncate text-xs text-muted-foreground">
                {{ hostOf(r.url) }}
              </p>

              <p class="mt-2 line-clamp-3 text-sm leading-relaxed text-muted-foreground">
                {{ r.content }}
              </p>

              <div class="mt-3 flex items-center gap-2">
                <Button variant="ghost" size="sm" class="h-7" @click="readPage(r.url)">
                  <component :is="pages[r.url]?.open ? ChevronUp : BookOpen" />
                  {{ pages[r.url]?.open ? 'Collapse' : 'Read page' }}
                </Button>
                <Badge v-if="pages[r.url]?.truncated" variant="secondary">
                  truncated
                </Badge>
              </div>

              <!-- Inline page content -->
              <template v-if="pages[r.url]?.open">
                <Separator class="my-3" />

                <div v-if="pages[r.url].loading" class="flex flex-col gap-1.5">
                  <div class="h-3 w-full animate-pulse rounded bg-muted" />
                  <div class="h-3 w-11/12 animate-pulse rounded bg-muted" />
                  <div class="h-3 w-4/5 animate-pulse rounded bg-muted" />
                </div>

                <p v-else-if="pages[r.url].error" class="text-sm font-medium text-destructive">
                  {{ pages[r.url].error }}
                </p>

                <div v-else class="max-h-96 overflow-y-auto pr-1">
                  <div class="md text-sm" v-html="renderMarkdown(pages[r.url].content)" />
                </div>
              </template>
            </article>
          </div>
        </template>
      </div>
    </div>
  </div>
</template>
