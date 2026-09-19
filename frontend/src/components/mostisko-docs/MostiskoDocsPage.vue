<script setup>
/**
 * MostiskoDocsPage — the main documentation page.
 *
 * Layout: sticky sidebar (desktop) + scrollable content.
 * Sidebar and scroll-spy both derive from the SECTIONS array (one source).
 * Modules render via DocModule (three-register: Purpose / User Guide / Technical).
 * Architecture sections have inline content (rich, unique per section).
 *
 * Accessibility:
 *  - Sidebar uses real <a href="#id"> links — works without JS.
 *  - aria-current="true" on the active sidebar item.
 *  - prefers-reduced-motion disables smooth-scroll animation.
 *  - Focus-visible rings on all interactive elements.
 */
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { Menu, X, ChevronDown } from '@lucide/vue'
import DocSection from './DocSection.vue'
import DocCallout from './DocCallout.vue'
import DocCodeBlock from './DocCodeBlock.vue'
import DocApiExample from './DocApiExample.vue'
import DocModule from './DocModule.vue'
import { SECTIONS, MODULES, FAQ_ITEMS } from './sections-data.js'

// ── Data selectors ────────────────────────────────────────────────────────

/** @type {import('./sections-data.js').ModuleDef[]} */
const fullModules = MODULES.filter((m) => !m.brief)

/** @type {import('./sections-data.js').ModuleDef[]} */
const briefModules = MODULES.filter((m) => m.brief)

/** Sidebar groups derived from SECTIONS. */
const sidebarGroups = computed(() => [
  { label: 'Architecture', items: SECTIONS.filter((s) => s.group === 'architecture') },
  { label: 'Modules',      items: SECTIONS.filter((s) => s.group === 'modules') },
  { label: '',             items: SECTIONS.filter((s) => s.group === 'other') },
])

// ── Scroll-spy state ──────────────────────────────────────────────────────

const activeSection = ref(SECTIONS[0].id)
const scrollContainer = ref(null)
const mobileNavOpen = ref(false)
const prefersReducedMotion = ref(false)

/** @type {IntersectionObserver | null} */
let observer = null
/** @type {MediaQueryList | null} */
let motionQuery = null

onMounted(() => {
  // Reduced-motion preference — kept live so the user can change it mid-session.
  motionQuery = window.matchMedia('(prefers-reduced-motion: reduce)')
  prefersReducedMotion.value = motionQuery.matches
  const motionHandler = (/** @type {MediaQueryListEvent} */ e) => {
    prefersReducedMotion.value = e.matches
  }
  motionQuery.addEventListener('change', motionHandler)

  // IntersectionObserver — tracks which section is in the visible area.
  // rootMargin shrinks the observation window to a band in the top 20% of
  // the scroll container, so the active section updates when its heading
  // reaches roughly the top quarter of the viewport.
  observer = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (entry.isIntersecting) {
          activeSection.value = entry.target.id
        }
      }
    },
    {
      root: scrollContainer.value,
      rootMargin: '-5% 0px -80% 0px',
      threshold: 0,
    },
  )

  // Observe every section listed in SECTIONS.
  for (const section of SECTIONS) {
    const el = document.getElementById(section.id)
    if (el) observer.observe(el)
  }
})

onUnmounted(() => {
  observer?.disconnect()
  if (motionQuery) {
    motionQuery.removeEventListener('change', () => {})
  }
})

/**
 * Scroll to a section by ID. Sets active immediately (no waiting for IO),
 * closes mobile nav, and respects reduced-motion.
 * @param {string} id
 */
function scrollTo(id) {
  activeSection.value = id
  mobileNavOpen.value = false
  const el = document.getElementById(id)
  if (el) {
    el.scrollIntoView({
      behavior: prefersReducedMotion.value ? 'auto' : 'smooth',
      block: 'start',
    })
  }
}
</script>

<template>
  <div class="flex h-full overflow-hidden bg-background">
    <!-- ═══════════════════════════════════════════════════════════════════ -->
    <!-- DESKTOP SIDEBAR                                                    -->
    <!-- ═══════════════════════════════════════════════════════════════════ -->
    <aside
      class="hidden w-64 shrink-0 flex-col border-r bg-card/50 lg:flex"
    >
      <!-- Sidebar header -->
      <div class="border-b px-4 py-4">
        <h1 class="text-base font-bold tracking-tight text-foreground">
          মস্তিস্ক <span class="font-normal text-muted-foreground">docs</span>
        </h1>
        <p class="mt-0.5 text-[11px] text-muted-foreground">
          Architecture &amp; Module Reference
        </p>
      </div>

      <!-- Sidebar nav -->
      <nav
        class="flex-1 overflow-y-auto p-2"
        aria-label="Documentation sections"
      >
        <template v-for="group in sidebarGroups" :key="group.label">
          <p
            v-if="group.label"
            class="px-3 pb-1 pt-4 text-[10px] font-semibold uppercase tracking-widest text-muted-foreground first:pt-2"
          >
            {{ group.label }}
          </p>
          <a
            v-for="s in group.items"
            :key="s.id"
            :href="`#${s.id}`"
            :aria-current="activeSection === s.id ? 'true' : undefined"
            :class="[
              'flex items-center gap-2.5 rounded-md px-3 py-1.5 text-[13px] transition-colors',
              'focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring',
              activeSection === s.id
                ? 'bg-accent font-medium text-accent-foreground'
                : 'text-muted-foreground hover:bg-muted hover:text-foreground',
            ]"
            @click.prevent="scrollTo(s.id)"
          >
            <component :is="s.icon" class="h-3.5 w-3.5 shrink-0 opacity-60" />
            <span class="truncate">{{ s.title }}</span>
          </a>
        </template>
      </nav>
    </aside>

    <!-- ═══════════════════════════════════════════════════════════════════ -->
    <!-- CONTENT AREA                                                       -->
    <!-- ═══════════════════════════════════════════════════════════════════ -->
    <div ref="scrollContainer" class="flex-1 overflow-y-auto">
      <!-- ── Mobile nav toggle ────────────────────────────────────────── -->
      <div
        class="sticky top-0 z-10 flex items-center border-b bg-background/95 px-4 py-2.5 backdrop-blur lg:hidden"
      >
        <button
          class="flex items-center gap-2 rounded-md border px-3 py-1.5 text-sm text-muted-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
          :aria-expanded="mobileNavOpen"
          aria-controls="mobile-doc-nav"
          @click="mobileNavOpen = !mobileNavOpen"
        >
          <Menu class="h-4 w-4" />
          <span class="truncate">{{ SECTIONS.find((s) => s.id === activeSection)?.title || 'Sections' }}</span>
          <ChevronDown
            :class="[
              'ml-1 h-3.5 w-3.5 transition-transform',
              mobileNavOpen ? 'rotate-180' : '',
            ]"
          />
        </button>
      </div>

      <!-- ── Mobile nav dropdown ──────────────────────────────────────── -->
      <Teleport to="body">
        <div
          v-if="mobileNavOpen"
          class="fixed inset-0 z-50 lg:hidden"
          @click="mobileNavOpen = false"
        >
          <div class="absolute inset-0 bg-black/40" />
          <nav
            id="mobile-doc-nav"
            class="absolute left-3 right-3 top-14 max-h-[70vh] overflow-y-auto rounded-lg border bg-card p-2 shadow-xl"
            aria-label="Documentation sections"
            @click.stop
          >
            <template v-for="group in sidebarGroups" :key="group.label">
              <p
                v-if="group.label"
                class="px-3 pb-1 pt-3 text-[10px] font-semibold uppercase tracking-widest text-muted-foreground first:pt-1"
              >
                {{ group.label }}
              </p>
              <a
                v-for="s in group.items"
                :key="s.id"
                :href="`#${s.id}`"
                :aria-current="activeSection === s.id ? 'true' : undefined"
                :class="[
                  'flex items-center gap-2.5 rounded-md px-3 py-2 text-sm transition-colors',
                  activeSection === s.id
                    ? 'bg-accent font-medium text-accent-foreground'
                    : 'text-muted-foreground hover:bg-muted hover:text-foreground',
                ]"
                @click.prevent="scrollTo(s.id)"
              >
                <component :is="s.icon" class="h-4 w-4 shrink-0 opacity-60" />
                {{ s.title }}
              </a>
            </template>
          </nav>
        </div>
      </Teleport>

      <!-- ── Page content ─────────────────────────────────────────────── -->
      <div class="mx-auto max-w-4xl space-y-16 px-4 py-8 sm:px-6 lg:px-8">

        <!-- ============================================================ -->
        <!-- A) ARCHITECTURE SECTIONS                                      -->
        <!-- ============================================================ -->

        <!-- ── Overview ──────────────────────────────────────────────── -->
        <DocSection id="overview" title="Overview">
          <p class="text-sm leading-relaxed text-foreground/90">
            <strong>Mostisko</strong> is a self-hosted, local-first AI workspace. The browser
            never talks to the model directly — nothing private leaves the machine. It bundles
            conversational AI, document extraction, agentic search, task management, and
            training tools into a single application that runs on your hardware.
          </p>
          <p class="text-sm leading-relaxed text-foreground/90">
            Built for individual developers, small teams, and domain specialists who want
            AI-assisted tools without sending sensitive data to external services. The backend
            owns every model key and connection; the frontend is a pure UI layer.
          </p>
          <DocCallout variant="note" title="Local-First Principle">
            "Local-first" means your data stays on your machine by default. Cloud model
            providers are available as opt-in, metered sources — but the default path
            (Ollama) runs entirely on localhost. Buyer PO data, chat history, and source
            content never leave the deployment unless you explicitly configure a cloud
            provider.
          </DocCallout>
          <div class="grid gap-3 sm:grid-cols-2">
            <div class="rounded-lg border border-border bg-card p-4">
              <p class="text-xs font-semibold uppercase tracking-wide text-muted-foreground">Modules</p>
              <p class="mt-1 text-2xl font-bold text-foreground">12</p>
              <p class="text-xs text-muted-foreground">Chat, Workspace, Search, Kanban, JSON table, IELTS, 5Lakhs, and more</p>
            </div>
            <div class="rounded-lg border border-border bg-card p-4">
              <p class="text-xs font-semibold uppercase tracking-wide text-muted-foreground">Architecture</p>
              <p class="mt-1 text-2xl font-bold text-foreground">Local-first</p>
              <p class="text-xs text-muted-foreground">Vue 3 SPA → .NET 8 API → Ollama → SQLite</p>
            </div>
          </div>
        </DocSection>

        <!-- ── System Architecture ───────────────────────────────────── -->
        <DocSection id="system-architecture" title="System Architecture">
          <p class="text-sm leading-relaxed text-foreground/90">
            The stack is a straightforward SPA-over-API: a Vue 3 frontend served as static
            files, talking to a .NET 8 minimal API, which manages SQLite for persistence and
            Ollama for model inference.
          </p>

          <!-- Layer diagram -->
          <div class="space-y-2">
            <div v-for="(layer, i) in [
              { label: 'Frontend',  tech: 'Vue 3 + Vite + reka-ui/shadcn-vue + Tailwind v4', color: 'bg-blue-500/10 border-blue-500/20' },
              { label: 'API',       tech: '.NET 8 minimal API — auth, routing, business logic', color: 'bg-emerald-500/10 border-emerald-500/20' },
              { label: 'Models',    tech: 'Ollama (local) · Cloud providers (opt-in, metered)', color: 'bg-amber-500/10 border-amber-500/20' },
              { label: 'Storage',   tech: 'SQLite on persistent volume', color: 'bg-purple-500/10 border-purple-500/20' },
            ]" :key="i"
              :class="['flex items-center gap-4 rounded-lg border p-3', layer.color]"
            >
              <span class="w-20 shrink-0 text-xs font-bold uppercase tracking-wide text-foreground/70">
                {{ layer.label }}
              </span>
              <span class="text-sm text-foreground/80">{{ layer.tech }}</span>
            </div>
          </div>

          <!-- Request flow -->
          <div class="rounded-lg border border-border bg-muted/30 p-4">
            <p class="mb-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Request Flow</p>
            <div class="flex flex-wrap items-center gap-2 text-sm text-foreground/80">
              <span class="rounded bg-blue-500/10 px-2 py-0.5 text-xs font-medium">Browser</span>
              <span class="text-muted-foreground">→</span>
              <span class="rounded bg-emerald-500/10 px-2 py-0.5 text-xs font-medium">.NET API</span>
              <span class="text-muted-foreground">→</span>
              <span class="rounded bg-amber-500/10 px-2 py-0.5 text-xs font-medium">Ollama / Cloud</span>
              <span class="text-muted-foreground">→</span>
              <span class="rounded bg-purple-500/10 px-2 py-0.5 text-xs font-medium">SQLite</span>
              <span class="text-muted-foreground">→</span>
              <span class="rounded bg-blue-500/10 px-2 py-0.5 text-xs font-medium">Browser</span>
            </div>
          </div>

          <DocCallout variant="security" title="Backend owns everything">
            The frontend is a pure UI layer. It never holds API keys, model credentials, or
            direct database connections. Every sensitive operation — model inference, data
            persistence, authentication — happens server-side. The browser sends requests;
            the backend decides what to do with them.
          </DocCallout>

          <DocCodeBlock filename="App.vue — tab routing" language="javascript" :code="`// The URL owns the active tab — one source of truth
const activeTab = computed({
  get: () => tabForPath(route.path) ?? 'dashboard',
  set: (id) => {
    const target = pathForTab(id)
    if (route.path !== target) router.push(target)
  },
})

// Every tab stays mounted (unmount-on-hide=false) so a streaming
// chat reply survives navigating away and back.`" />
        </DocSection>

        <!-- ── Model Sources ─────────────────────────────────────────── -->
        <DocSection id="model-sources" title="Model Sources">
          <p class="text-sm leading-relaxed text-foreground/90">
            The <code class="rounded bg-muted px-1.5 py-0.5 text-xs font-mono">IChatModelSource</code>
            registry allows multiple model backends. Each source implements one interface;
            adding a new source touches no existing code.
          </p>

          <div class="space-y-3">
            <div v-for="source in [
              { name: 'Local Ollama',      desc: 'Default. Runs on localhost, completely private. No metering, no external calls.', badge: 'Default', badgeClass: 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400' },
              { name: 'Ollama Cloud',       desc: 'Metered, opt-in. For models too large to run locally. Configured in the backend; the browser never sees the endpoint.', badge: 'Opt-in', badgeClass: 'bg-amber-500/10 text-amber-600 dark:text-amber-400' },
              { name: 'OpenAI-Compatible',  desc: 'OpenRouter, Groq, Together AI, or any provider with an OpenAI-compatible API. Keys stay server-side.', badge: 'Config', badgeClass: 'bg-blue-500/10 text-blue-600 dark:text-blue-400' },
            ]" :key="source.name"
              class="rounded-lg border border-border bg-card p-4"
            >
              <div class="mb-1 flex items-center gap-2">
                <span class="text-sm font-semibold text-foreground">{{ source.name }}</span>
                <span :class="['rounded-full px-2 py-0.5 text-[10px] font-semibold', source.badgeClass]">
                  {{ source.badge }}
                </span>
              </div>
              <p class="text-sm text-muted-foreground">{{ source.desc }}</p>
            </div>
          </div>

          <DocCallout variant="tip" title="Adding a model source">
            Implement IChatModelSource (ListModels + StreamChat), register it in DI,
            done. The frontend's model picker aggregates all registered sources
            automatically via /api/models. No frontend changes needed.
          </DocCallout>

          <DocCodeBlock filename="Model source registry (conceptual)" language="csharp" :code="`// Each source implements one interface — no switch statements
public interface IChatModelSource
{
    string SourceId { get; }   // 'local', 'cloud', 'openai'
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync();
    IAsyncEnumerable<ChatChunk> StreamChatAsync(ChatRequest req);
}

// DI registration — adding a source touches no existing code
services.AddSingleton<IChatModelSource, OllamaLocalSource>();
services.AddSingleton<IChatModelSource, OllamaCloudSource>();
services.AddSingleton<IChatModelSource, OpenAiCompatibleSource>();`" />
        </DocSection>

        <!-- ── Security & Trust ──────────────────────────────────────── -->
        <DocSection id="security" title="Security & Trust Model">
          <p class="text-sm leading-relaxed text-foreground/90">
            The trust model is simple: the backend is the only trusted component. The browser
            is a display layer. Keys, credentials, and data access are all server-side.
          </p>

          <div class="space-y-3">
            <DocCallout variant="security" title="Keys are backend-only">
              API keys for Ollama Cloud, OpenRouter, Groq, Together, and any other provider
              live in server-side configuration (environment variables or appsettings.json).
              The browser never receives, stores, or transmits a model API key.
            </DocCallout>

            <DocCallout variant="security" title="Owner-scoping returns 404, not 403">
              When a user requests a resource they don't own (another user's conversation,
              document, or board), the API returns 404, not 403. This prevents enumeration —
              an attacker can't distinguish "exists but forbidden" from "doesn't exist".
            </DocCallout>

            <DocCallout variant="security" title="The model formats and selects — it never authors numbers">
              In the PO extraction pipeline, the LLM helps format and select data, but every
              number (quantities, amounts, totals) passes through an arithmetic validation
              gate. The gate checks that row totals match column sums, that subtotals add
              up to grand totals, and that pack-vs-piece math is consistent. Unreconciled
              data is blocked regardless of source (positional parser or LLM fallback).
            </DocCallout>
          </div>
        </DocSection>

        <!-- ── RBAC ──────────────────────────────────────────────────── -->
        <DocSection id="rbac-overview" title="RBAC (Role-Based Access Control)">
          <p class="text-sm leading-relaxed text-foreground/90">
            Three roles, additive permissions, server-enforced. The frontend hides tabs the
            user can't access, but that's convenience — the API independently refuses any
            request from a role that lacks the required permission.
          </p>

          <div class="overflow-x-auto">
            <table class="w-full text-sm">
              <thead>
                <tr class="border-b text-left">
                  <th class="px-3 py-2 font-semibold text-foreground">Role</th>
                  <th class="px-3 py-2 font-semibold text-foreground">Access</th>
                  <th class="px-3 py-2 font-semibold text-foreground">Notes</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="role in [
                  { name: 'General', access: 'Base tabs only (chat, dashboard, etc.)', notes: 'Default for new users' },
                  { name: 'System', access: 'Base + additional tabs per config', notes: 'Gains specific modules (workspace, search, etc.)' },
                  { name: 'SystemAdmin', access: 'All tabs + RBAC admin panel', notes: 'Can manage users and roles; last-admin guardrail prevents lockout' },
                ]" :key="role.name" class="border-b last:border-0"
                >
                  <td class="px-3 py-2 font-medium text-foreground">{{ role.name }}</td>
                  <td class="px-3 py-2 text-muted-foreground">{{ role.access }}</td>
                  <td class="px-3 py-2 text-muted-foreground">{{ role.notes }}</td>
                </tr>
              </tbody>
            </table>
          </div>

          <DocCallout variant="note" title="Additive permissions">
            Permissions are additive: each role level adds tabs, never removes them. A
            SystemAdmin sees everything a System user sees, plus the admin panel. Tab
            assignments are per-user overrides on top of the role's base set.
          </DocCallout>

          <DocCodeBlock filename="App.vue — permission-filtered tabs" language="javascript" :code="`// The server says which tabs this user may see
const tabs = computed(() => {
  if (allowedTabs.value === null) return []  // not loaded yet
  return ALL_TABS.filter((t) => allowedTabs.value.includes(t.id))
})

// Route guard: refuse navigation to forbidden tabs
router.beforeEach((to) => {
  const wanted = to.meta?.tab
  if (tabs && !tabs.includes(wanted)) {
    return pathForTab(fallback)  // redirect, never 404
  }
})`" />
        </DocSection>

        <!-- ============================================================ -->
        <!-- B) MODULE SECTIONS                                             -->
        <!-- ============================================================ -->

        <!-- Full modules — each gets its own DocSection + DocModule -->
        <template v-for="m in fullModules" :key="m.id">
          <DocSection :id="m.id" :title="m.title">
            <DocModule :module="m" />
          </DocSection>
        </template>

        <!-- Brief modules — grouped under one DocSection -->
        <DocSection id="admin-modules" title="Admin & Utility Modules">
          <p class="mb-4 text-sm text-muted-foreground">
            These modules share the core application stack and follow the same routing,
            permission, and persistence patterns as the full modules above.
          </p>
          <div class="space-y-8">
            <DocModule
              v-for="m in briefModules"
              :key="m.id"
              :module="m"
              show-title
              compact
            />
          </div>
        </DocSection>

        <!-- ============================================================ -->
        <!-- C) DEPLOYMENT                                                  -->
        <!-- ============================================================ -->

        <DocSection id="deployment" title="Deployment">
          <p class="text-sm leading-relaxed text-foreground/90">
            Mostisko deploys as a single Docker container packaging both the static frontend
            and the .NET API on one port. SQLite lives on a persistent volume so data
            survives container restarts and redeployments.
          </p>

          <div class="space-y-3">
            <div v-for="item in [
              { label: 'Container', detail: 'Single Docker image — frontend (Vite build) + API (.NET 8), one port, no reverse proxy needed.' },
              { label: 'Database', detail: 'SQLite on a PERSISTENT VOLUME. No connection string, no separate service. Data directory is mounted from the host.' },
              { label: 'Platform', detail: 'Fly.io with a mounted volume. Any platform with persistent filesystem works (VPS, bare metal, etc.).' },
            ]" :key="item.label"
              class="rounded-lg border border-border bg-card p-4"
            >
              <p class="text-xs font-semibold uppercase tracking-wide text-muted-foreground">{{ item.label }}</p>
              <p class="mt-1 text-sm text-foreground/90">{{ item.detail }}</p>
            </div>
          </div>

          <DocCallout variant="note" title="Why not Vercel or Netlify?">
            Both are serverless/edge platforms that don't support writable SQLite. The
            database needs a persistent filesystem — which serverless functions don't
            provide. Fly.io with a mounted volume, a VPS, or your own server are the
            supported deployment targets.
          </DocCallout>

          <DocCodeBlock filename="docker-compose.yml (simplified)" language="yaml" :code="`services:
  mostisko:
    build: .
    ports:
      - '8080:8080'
    volumes:
      - mostisko-data:/app/data   # SQLite persistent volume
    environment:
      - OLLAMA_URL=http://host.docker.internal:11434

volumes:
  mostisko-data:`" />

          <DocCodeBlock filename="fly.toml (key sections)" language="toml" :code="`[build]

[mounts]
  source = 'mostisko_data'
  destination = '/app/data'

[http_service]
  internal_port = 8080
  force_https = true`" />
        </DocSection>

        <!-- ============================================================ -->
        <!-- D) FAQ                                                         -->
        <!-- ============================================================ -->

        <DocSection id="faq" title="Frequently Asked Questions">
          <div class="space-y-4">
            <details
              v-for="(item, i) in FAQ_ITEMS"
              :key="i"
              class="group rounded-lg border border-border bg-card"
            >
              <summary
                class="flex cursor-pointer items-center gap-3 px-4 py-3 text-sm font-medium text-foreground transition-colors hover:bg-muted/50 focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring [&::-webkit-details-marker]:hidden"
              >
                <ChevronDown
                  class="h-4 w-4 shrink-0 text-muted-foreground transition-transform group-open:rotate-180"
                />
                {{ item.q }}
              </summary>
              <div class="border-t px-4 py-3 text-sm leading-relaxed text-muted-foreground">
                {{ item.a }}
              </div>
            </details>
          </div>
        </DocSection>

        <!-- Bottom spacer -->
        <div class="h-16" aria-hidden="true" />
      </div>
    </div>
  </div>
</template>
