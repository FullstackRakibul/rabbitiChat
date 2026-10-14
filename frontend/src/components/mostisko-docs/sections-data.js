/**
 * sections-data.js — Single source of truth for the Mostisko documentation page.
 *
 * Everything the sidebar, scroll-spy, and section wrappers need lives here.
 * Adding a section = one entry in SECTIONS; adding a module = one entry in
 * MODULES. Nothing else to edit.
 *
 * @module mostisko-docs/sections-data
 */

import {
  BookOpen, Layers, Server, Shield, ShieldCheck,
  MessageSquare, Brain, Globe, ListTodo, Table,
  GraduationCap, BadgeDollarSign, Settings, Rocket, CircleHelp, Users,
} from '@lucide/vue'

// ─────────────────────────────────────────────────────────────────────────────
// JSDoc type definitions
// ─────────────────────────────────────────────────────────────────────────────

/**
 * A sidebar / scroll-spy section entry.
 * @typedef {Object} SectionDef
 * @property {string}                          id    — unique anchor ID
 * @property {string}                          title — display label
 * @property {import('vue').Component}         icon  — Lucide icon component
 * @property {'architecture'|'modules'|'other'} group — sidebar group
 */

/**
 * A concrete user-guide step.
 * @typedef {Object} GuideStep
 * @property {string} step   — short action label
 * @property {string} detail — expanded description
 */

/**
 * An engineering-decision callout inside the Technical register.
 * @typedef {Object} TechNote
 * @property {string}                     title   — decision headline
 * @property {string}                     detail  — explanation
 * @property {'tip'|'security'|'note'}    [variant] — DocCallout variant
 */

/**
 * A code / config snippet.
 * @typedef {Object} CodeSnippet
 * @property {string} [filename] — shown in the header bar
 * @property {string} [language] — fallback header label
 * @property {string} code       — the source text
 */

/**
 * A module documented with the three-register pattern.
 * @typedef {Object} ModuleDef
 * @property {string}        id        — must match a SECTIONS id (group 'modules')
 * @property {string}        title     — display heading
 * @property {boolean}       [brief]   — true for compact admin/utility modules
 * @property {string}        purpose   — what it's for (one paragraph)
 * @property {GuideStep[]}   guide     — ordered user steps
 * @property {string[]}      stack     — technologies / libraries used
 * @property {TechNote[]}    decisions — key engineering decisions
 * @property {CodeSnippet[]} snippets  — code examples
 */

/**
 * FAQ question/answer pair.
 * @typedef {Object} FaqItem
 * @property {string} q — the question
 * @property {string} a — the answer
 */

// ─────────────────────────────────────────────────────────────────────────────
// SECTIONS — drives sidebar nav, scroll-spy targets, and section rendering
// ─────────────────────────────────────────────────────────────────────────────

/** @type {SectionDef[]} */
export const SECTIONS = [
  // ── Architecture (system-wide, not per-module) ──
  { id: 'overview',             title: 'Overview',             icon: BookOpen,        group: 'architecture' },
  { id: 'system-architecture',  title: 'System Architecture',  icon: Layers,          group: 'architecture' },
  { id: 'model-sources',        title: 'Model Sources',        icon: Server,          group: 'architecture' },
  { id: 'security',             title: 'Security & Trust',     icon: Shield,          group: 'architecture' },
  { id: 'rbac-overview',        title: 'RBAC',                 icon: ShieldCheck,     group: 'architecture' },

  // ── Modules (each documented with Purpose / User Guide / Technical) ──
  { id: 'chat',                 title: 'Chat & Sources',       icon: MessageSquare,   group: 'modules' },
  { id: 'workspace',            title: 'Workspace Studio',     icon: Brain,           group: 'modules' },
  { id: 'hr',                   title: 'মানব সম্পদ',            icon: Users,           group: 'modules' },
  { id: 'search',               title: 'Search',               icon: Globe,           group: 'modules' },
  { id: 'todorabbit',           title: 'todorabbit',           icon: ListTodo,        group: 'modules' },
  { id: 'json-table',           title: 'JSON → Table',         icon: Table,           group: 'modules' },
  { id: 'ielts',                title: 'IELTS Trainer',        icon: GraduationCap,   group: 'modules' },
  { id: 'five-lakhs',           title: '5Lakhs',               icon: BadgeDollarSign, group: 'modules' },
  { id: 'admin-modules',        title: 'Admin & Utility',      icon: Settings,        group: 'modules' },

  // ── Other ──
  { id: 'deployment',           title: 'Deployment',           icon: Rocket,          group: 'other' },
  { id: 'faq',                  title: 'FAQ',                  icon: CircleHelp,      group: 'other' },
]

// ─────────────────────────────────────────────────────────────────────────────
// MODULES — drives DocModule three-register rendering
// ─────────────────────────────────────────────────────────────────────────────

/** @type {ModuleDef[]} */
export const MODULES = [
  // ═══════════════════════════════════════════════════════════════════════════
  // FULL MODULES
  // ═══════════════════════════════════════════════════════════════════════════

  {
    id: 'chat',
    title: 'Chat & Connected Sources',
    purpose:
      'AI chat using locally-hosted models with document context. The ContextSource ' +
      'system unifies Connected Sources, buyer profiles, and search profiles into one ' +
      'togglable mechanism — they are injected into the system prompt when active, so ' +
      'adding a new context type means implementing one interface, not wiring three ' +
      'separate injection paths.',
    guide: [
      { step: 'Select a model',            detail: 'The picker reads from /api/models, aggregating every registered IChatModelSource.' },
      { step: 'Toggle desired sources',     detail: 'Each source is a switch — on means its content is injected into the system prompt for this chat.' },
      { step: 'Type your message and send', detail: 'Text is streamed to the backend; the reply arrives as SSE chunks.' },
      { step: 'Watch the streaming reply',  detail: 'Content, thinking tokens, and tool calls (web search) render in real-time.' },
      { step: 'Optionally enable web search', detail: 'A one-shot toggle that lets the model call a web_search tool for grounded answers.' },
    ],
    stack: ['Vue 3', 'SSE (fetch + ReadableStream)', 'ContextSource API', '.NET 8 minimal API'],
    decisions: [
      {
        title: 'Connected Sources are ONE system',
        detail:
          'Sources, buyer profiles, and search profiles all implement the same ContextSource ' +
          'interface. They are toggled per-chat via activeSourceIds and injected into the system ' +
          'prompt. Unifying them avoids three separate injection mechanisms and makes adding new ' +
          'context types trivial — implement the interface, register it, done.',
        variant: 'note',
      },
      {
        title: 'Backend owns every model key',
        detail:
          'The browser never sees API keys. The frontend sends a model name and source identifier; ' +
          'the backend resolves the key from its own config. This is the local-first privacy contract.',
        variant: 'security',
      },
    ],
    snippets: [
      {
        filename: 'services/index.js',
        language: 'javascript',
        code:
`// SSE streaming — the fundamental chat pattern
const id = await streamChat(
  {
    conversationId: currentId,
    source: modelSource,     // 'local' | 'cloud' | 'openai'
    model: model,
    message: text,
    webSearch: useWebSearch,  // one-shot toggle
    think: think,
    activeSourceIds,          // which ContextSources are on
  },
  {
    onContent:  (chunk) => { reply.content += chunk },
    onThinking: (chunk) => { reply.thinking += chunk },
    onTool:     (t)     => { /* web_search status card */ },
  },
  controller.signal,
)`,
      },
    ],
  },

  // ───────────────────────────────────────────────────────────────────────────

  {
    id: 'workspace',
    title: 'Workspace (PO Extraction)',
    purpose:
      'Extract purchase-order data from PDF uploads into structured records for ERP ' +
      'integration. Built for the RMG (ready-made garment) buyer-PO pipeline, where ' +
      'accuracy on quantities and amounts matters more than speed — a wrong number in ' +
      'the ERP is costlier than a slow extraction.',
    guide: [
      { step: 'Select a buyer profile',     detail: 'The profile routes the PDF to the right column-band configuration for that buyer\'s PO format.' },
      { step: 'Upload the PDF',             detail: 'Drag-and-drop or file picker. The system reads the PDF server-side.' },
      { step: 'Review positional extraction', detail: 'PdfPig extracts data using x-coordinate column bands. Results appear in an editable table.' },
      { step: 'Check flagged mismatches',    detail: 'The arithmetic validation gate highlights any row where quantities or amounts don\'t reconcile.' },
      { step: 'Confirm and push',            detail: 'Human review is required — the model assists extraction but a human confirms before data enters the ERP.' },
    ],
    stack: ['PdfPig (.NET)', 'Validation gate', 'Buyer-profile routing', 'LLM fallback', 'ExtractionReview UI'],
    decisions: [
      {
        title: 'Positional parsing, not LLM-on-flat-text',
        detail:
          'Flattening PDF text destroys column alignment and merges adjacent table cells. ' +
          'PdfPig\'s positional approach reads each word\'s x-coordinate and assigns it to ' +
          'the correct column band, preserving the table structure that flat-text extraction loses.',
        variant: 'note',
      },
      {
        title: 'Prepack qty is PACKS not pieces',
        detail:
          'A common source of ERP errors: prepack quantities represent packs (each containing ' +
          'a fixed assortment), not individual pieces. The system enforces this distinction ' +
          'throughout the extraction and validation pipeline.',
        variant: 'tip',
      },
      {
        title: 'Validation gate blocks unreconciled data',
        detail:
          'The model formats and selects — it never authors numbers. Every extracted figure ' +
          'is cross-checked arithmetically (row totals, column sums, grand totals) before it ' +
          'can proceed. The LLM fallback is held to the same gate: its output is validated ' +
          'identically to the positional parser\'s output.',
        variant: 'security',
      },
      {
        title: 'Human review before ERP push',
        detail:
          'The ExtractionReview component presents every extracted field for human confirmation. ' +
          'No data enters the system of record without explicit approval. This is a policy ' +
          'decision, not a technical limitation.',
        variant: 'security',
      },
    ],
    snippets: [
      {
        filename: 'PO extraction (conceptual)',
        language: 'csharp',
        code:
`// PdfPig positional extraction — column bands, not flattened text
var words = page.GetWords()
    .Where(w => w.BoundingBox.Left >= colStart
             && w.BoundingBox.Left <  colEnd)
    .OrderBy(w => w.BoundingBox.Bottom);

// Each column band maps to a PO field (style, color, qty, price)
// The x-coordinate decides the column — not the word order in the stream`,
      },
    ],
  },

  // ───────────────────────────────────────────────────────────────────────────

  {
    id: 'hr',
    title: 'মানব সম্পদ (HR Assistant)',
    purpose:
      'Answers HR\'s questions about final settlement and earned leave from HR\'s own ' +
      'library — the HR-approved rules document, the CoreERP procedure text and the code ' +
      'tables — with a citation after each statement. It has no database connection and ' +
      'runs nothing: procedure files are text for the model to read. Where the library ' +
      'is silent it says so rather than guessing company policy.',
    guide: [
      { step: 'Open মানব সম্পদ',            detail: 'hr-assistant users land here; anyone else needs the tab granted in Access.' },
      { step: 'Keep the library current',  detail: 'লাইব্রেরি: paste text or upload .md, .txt, .sql or .csv. Rules and code tables are sent whole with every question; procedures are split and searched.' },
      { step: 'Sign items off',            detail: 'Set "approved by" on an item; the name is shown next to its citations.' },
      { step: 'Check what will be searched', detail: 'Open a procedure\'s chunks to see exactly which sections and line ranges the assistant can quote.' },
    ],
    stack: ['ContextSource (Kind = hr)', 'HrSplitter', 'nomic-embed-text', 'VectorIndex (kind gate)', 'ModelPolicy'],
    decisions: [
      {
        title: 'Commented-out code is removed before splitting',
        detail:
          'Old formulas such as the "Ager Formula" block stay in the procedure files as comments. ' +
          'They are removed before a procedure is split, so the model can never quote a formula ' +
          'that no longer runs.',
        variant: 'note',
      },
      {
        title: 'Shared, not personal',
        detail:
          'HR items are company knowledge: anyone with the tab reads them, and only hr-assistant ' +
          'and SystemAdmin users change them. Every other kind of source stays scoped to its owner.',
        variant: 'note',
      },
    ],
  },

  // ───────────────────────────────────────────────────────────────────────────

  {
    id: 'search',
    title: 'Search',
    purpose:
      'Agentic web search — the model decides what to search, evaluates results, and ' +
      'may refine the query across multiple rounds. For when you want AI-synthesized ' +
      'answers grounded in web content, not just a list of links.',
    guide: [
      { step: 'Enter your question',        detail: 'Natural language — the agent decides the search terms.' },
      { step: 'Watch the agent work',        detail: 'A dotted-map backdrop shows search activity. The model may search multiple times.' },
      { step: 'Read the synthesized answer', detail: 'Results are combined into a coherent response with source attribution.' },
      { step: 'Browse history',              detail: 'Queries are saved with soft-delete. Repeating a query bumps its count instead of creating a duplicate.' },
    ],
    stack: ['Tool loop (web_search)', 'WorldMapBackdrop', 'SearchHistoryPanel', 'Soft-delete + dedupe'],
    decisions: [
      {
        title: 'Bounded ≤4 tool rounds',
        detail:
          'The agent can call web_search up to 4 times per query. This prevents infinite ' +
          'search loops while still allowing multi-step research (search → evaluate → ' +
          'refine → search again).',
        variant: 'note',
      },
      {
        title: 'History is query-based, not session-based',
        detail:
          'Each unique query gets one history entry. Repeating the same query bumps its ' +
          'count rather than creating a duplicate row. Soft-delete hides entries without ' +
          'losing them.',
        variant: 'tip',
      },
    ],
    snippets: [],
  },

  // ───────────────────────────────────────────────────────────────────────────

  {
    id: 'todorabbit',
    title: 'todorabbit (Kanban)',
    purpose:
      'Kanban board for personal task management. Boards → Columns → Cards with ' +
      'drag-and-drop reordering. Designed for individual use within the workspace, ' +
      'not enterprise project management.',
    guide: [
      { step: 'Create a board',              detail: 'Each board is an independent workspace (e.g. "Work", "Personal").' },
      { step: 'Add columns',                 detail: 'Typical setup: Todo → In Progress → Done, but any column set works.' },
      { step: 'Add cards to columns',        detail: 'Each card has a title and optional description.' },
      { step: 'Drag cards between columns',  detail: 'Drop to reorder within a column or move across columns. Changes persist automatically.' },
    ],
    stack: ['vuedraggable', 'SQLite persistence', 'Optimistic update pattern', 'Board→Column→Card hierarchy'],
    decisions: [
      {
        title: 'Optimistic update with rollback',
        detail:
          'The UI moves the card immediately on drop. The API call fires in the background. ' +
          'If the server rejects the move, the card reverts to its original position. This ' +
          'makes drag-and-drop feel instant without sacrificing data integrity.',
        variant: 'tip',
      },
    ],
    snippets: [
      {
        filename: 'TodoTab.vue',
        language: 'vue',
        code:
`<!-- vuedraggable — cards within a column -->
<draggable
  v-model="column.cards"
  group="cards"
  item-key="id"
  @end="onCardMoved"
>
  <template #item="{ element }">
    <Card :card="element" @click="openCard(element)" />
  </template>
</draggable>`,
      },
    ],
  },

  // ───────────────────────────────────────────────────────────────────────────

  {
    id: 'json-table',
    title: 'JSON → HTML Table',
    purpose:
      'Paste JSON and get a formatted HTML table with merged rowspans for nested arrays, ' +
      'column filters, drag-to-resize columns, and CSV export. For when you have API ' +
      'response data and need it in a shareable, scannable format.',
    guide: [
      { step: 'Paste JSON',                 detail: 'The editor accepts any valid JSON array or object.' },
      { step: 'View the rendered table',     detail: 'Nested arrays get merged rowspans — parent cells span their children.' },
      { step: 'Filter and resize',           detail: 'Per-column filters narrow rows. Drag column borders to resize.' },
      { step: 'Export as CSV',               detail: 'Flattened output with UTF-8 BOM for correct Excel rendering.' },
    ],
    stack: ['Dynamic table renderer', 'Rowspan merging', 'Column resize (drag)', 'CSV generator + BOM'],
    decisions: [
      {
        title: 'Merged rowspan for nested arrays',
        detail:
          'When a JSON object has an array field, the parent row spans all child rows in ' +
          'the rendered table. This preserves the hierarchical structure that a flat table ' +
          'would lose.',
        variant: 'note',
      },
      {
        title: 'UTF-8 BOM in CSV export',
        detail:
          'Without the BOM (byte-order mark), Excel opens CSV files as Latin-1 and mangles ' +
          'any non-ASCII characters. The export prepends \\uFEFF so Excel correctly detects ' +
          'UTF-8 encoding.',
        variant: 'tip',
      },
      {
        title: 'Midnight timestamps → date-only display',
        detail:
          'If a date value has a time component of exactly 00:00:00, the table renders it as ' +
          'a date without the time. This heuristic matches the common pattern where "2024-03-15T00:00:00" ' +
          'means "March 15", not "midnight on March 15".',
        variant: 'note',
      },
    ],
    snippets: [],
  },

  // ───────────────────────────────────────────────────────────────────────────

  {
    id: 'ielts',
    title: 'IELTS Trainer',
    purpose:
      'Practice IELTS Reading and Listening with mistake tracking and spaced review. ' +
      'Every wrong answer becomes a reviewable row — the Mistake table is the spine of ' +
      'the learning flow. Scoring is deterministic: band scores come from the official ' +
      'raw-score conversion table, not an AI approximation.',
    guide: [
      { step: 'Start a practice section',    detail: 'Choose Reading or Listening, then a passage or audio set.' },
      { step: 'Answer all questions',         detail: 'Standard IELTS question types: multiple choice, fill-in, True/False/Not Given, etc.' },
      { step: 'Submit and see your score',    detail: 'Raw score → band via the official conversion table. No AI guesswork.' },
      { step: 'Review mistakes',              detail: 'Wrong answers are added to the Mistake table automatically with metadata for spaced review.' },
      { step: 'Revisit for spaced review',    detail: 'The Mistake table surfaces items due for review based on your error history.' },
    ],
    stack: ['Scoring tables (official IELTS)', 'Mistake persistence', 'Review queue', 'Deterministic band calculation'],
    decisions: [
      {
        title: 'Band from the raw-score TABLE, not an AI guess',
        detail:
          'IELTS publishes official raw-score-to-band conversion tables. The trainer uses ' +
          'these tables directly — deterministic arithmetic, not LLM output. A score of 30/40 ' +
          'in Reading is always band 7.0, regardless of what any model might say.',
        variant: 'security',
      },
      {
        title: 'Mistake table as spine',
        detail:
          'The entire learning flow is built around the Mistake table. Every wrong answer → ' +
          'a row with the question, the wrong answer, the correct answer, and metadata for ' +
          'spaced review. Everything else (reports, progress, review scheduling) reads from ' +
          'this table.',
        variant: 'note',
      },
    ],
    snippets: [
      {
        filename: 'IELTS scoring (conceptual)',
        language: 'javascript',
        code:
`// Deterministic band calculation — raw score → official table
const READING_BANDS = {
  // raw: band (Academic Reading)
  39: 9.0, 37: 8.5, 35: 8.0, 33: 7.5,
  30: 7.0, 27: 6.5, 23: 6.0, 19: 5.5,
  15: 5.0, 13: 4.5, 10: 4.0,
}

function getBand(rawScore, table) {
  const thresholds = Object.keys(table)
    .map(Number)
    .sort((a, b) => b - a)
  for (const threshold of thresholds) {
    if (rawScore >= threshold) return table[threshold]
  }
  return 0
}`,
      },
    ],
  },

  // ───────────────────────────────────────────────────────────────────────────

  {
    id: 'five-lakhs',
    title: '5Lakhs — Novelty Check Generator',
    purpose:
      'Generate check images for demonstration and training purposes. Every generated ' +
      'check carries a permanent, unmistakable NOVELTY watermark baked into the image — ' +
      'it cannot be cropped, layer-separated, or otherwise removed. This is a safety ' +
      'feature, not a limitation.',
    guide: [
      { step: 'Fill in payee details',       detail: 'Name, amount (in figures and words), date.' },
      { step: 'Generate the check image',    detail: 'Canvas rendering produces the check with the NOVELTY watermark.' },
      { step: 'Download or print',           detail: 'PDF output via jsPDF. The watermark is permanent in all output formats.' },
    ],
    stack: ['HTML Canvas', 'jsPDF', 'Permanent watermark rendering'],
    decisions: [
      {
        title: 'Permanent NOVELTY watermark',
        detail:
          'The watermark is rendered directly into the canvas bitmap, not as a CSS overlay ' +
          'or a separate layer. It cannot be removed by cropping, editing CSS, or separating ' +
          'PDF layers. This is deliberate — checks are unmistakably fake by design.',
        variant: 'security',
      },
    ],
    snippets: [],
  },

  // ═══════════════════════════════════════════════════════════════════════════
  // BRIEF MODULES (grouped under "Admin & Utility")
  // ═══════════════════════════════════════════════════════════════════════════

  {
    id: 'rbac-admin',
    title: 'RBAC Admin',
    brief: true,
    purpose:
      'Manage user roles and per-tab permissions. SystemAdmin-only — the API refuses ' +
      'non-admin requests regardless of what the frontend shows.',
    guide: [
      { step: 'View the user list',          detail: 'Shows all registered users with their current role.' },
      { step: 'Assign a role',               detail: 'General / System / SystemAdmin. Roles are additive — higher roles gain tabs.' },
      { step: 'Toggle tab access',           detail: 'Per-tab switches let you grant or revoke individual module access.' },
    ],
    stack: ['.NET 8 RBAC API', 'Additive permission model', 'Last-admin guardrail'],
    decisions: [
      {
        title: 'Last-admin guardrail',
        detail:
          'The API prevents removing the only SystemAdmin. You cannot demote yourself if ' +
          'no other admin exists — this avoids an unrecoverable lockout.',
        variant: 'security',
      },
    ],
    snippets: [],
  },

  {
    id: 'sources-admin',
    title: 'Sources',
    brief: true,
    purpose:
      'Configure context sources that can be toggled into chat sessions. Sources are ' +
      'the persistent configuration; activeSourceIds is the per-chat selection.',
    guide: [
      { step: 'Add or edit a source',        detail: 'Define the source content and metadata.' },
      { step: 'Set the default active state', detail: 'Controls whether the source is on by default in new chats.' },
      { step: 'Toggle in chat',              detail: 'Sources appear in the chat sidebar for per-conversation toggling.' },
    ],
    stack: ['ContextSourceManager.vue', 'ContextSource API', 'SQLite persistence'],
    decisions: [],
    snippets: [],
  },

  {
    id: 'settings-module',
    title: 'Settings',
    brief: true,
    purpose:
      'Session and workspace defaults — model selection, dark/light theme, ' +
      'connection status, and account management.',
    guide: [
      { step: 'Toggle dark mode',            detail: 'Persisted via the useTheme composable.' },
      { step: 'Select your default model',    detail: 'Applies to new chats. Existing chats keep their model.' },
      { step: 'View connection status',       detail: 'Shows API base URL and whether Ollama is reachable.' },
      { step: 'Log out',                      detail: 'Clears the session token and all in-memory state.' },
    ],
    stack: ['useTheme composable', 'shadcn-vue Card/Select', 'Session token management'],
    decisions: [],
    snippets: [],
  },

  {
    id: 'dashboard-module',
    title: 'Dashboard',
    brief: true,
    purpose:
      'Landing page and navigation hub. Shows accessible modules as cards and provides ' +
      'quick-start actions (new chat, command palette).',
    guide: [
      { step: 'Click any module card',       detail: 'Navigates directly to that tab.' },
      { step: 'Use the command palette',      detail: 'Cmd/Ctrl+K opens search across all tabs and actions.' },
    ],
    stack: ['Tab permissions (filtered catalog)', 'Command palette integration', 'vue-router'],
    decisions: [],
    snippets: [],
  },
]

// ─────────────────────────────────────────────────────────────────────────────
// FAQ
// ─────────────────────────────────────────────────────────────────────────────

/** @type {FaqItem[]} */
export const FAQ_ITEMS = [
  {
    q: 'Why local-first?',
    a: 'Privacy. Buyer PO data, chat history, and source content never leave the machine. ' +
       'The browser talks only to your backend, which talks only to your local Ollama instance ' +
       '(or an opt-in cloud provider you explicitly configure).',
  },
  {
    q: 'Can I use GPT-4, Claude, or other cloud models?',
    a: 'Yes — via the OpenAI-compatible model source. Configure OpenRouter, Groq, or Together ' +
       'in the backend. Keys stay server-side; the browser never sees them. Cloud models are ' +
       'metered and opt-in; the default is always local Ollama.',
  },
  {
    q: 'Why SQLite instead of Postgres?',
    a: 'One fewer service to run. SQLite handles the workload (single-user to small-team), ' +
       'deploys alongside the app on the same persistent volume, and needs no connection ' +
       'string management. The trade-off: no concurrent writers, which is fine for this use case.',
  },
  {
    q: 'Why positional PDF parsing instead of sending the whole PDF to an LLM?',
    a: 'Flattening PDF text merges adjacent table cells. A value in column 3 can appear next ' +
       'to a label from column 2 in the flat stream, which the LLM has no way to disambiguate. ' +
       'PdfPig\'s positional approach reads each word\'s x-coordinate and assigns it to the ' +
       'correct column band — the table structure is preserved, not inferred.',
  },
  {
    q: 'Can multiple users share one instance?',
    a: 'Yes, with RBAC. Each user logs in with their own credentials, sees only the tabs their ' +
       'role permits, and owns their own conversations. The SystemAdmin manages roles and tab ' +
       'assignments via the RBAC admin panel.',
  },
  {
    q: 'Why does the validation gate exist if the LLM is doing the extraction?',
    a: 'The model formats and selects — it never authors numbers. The validation gate ' +
       'cross-checks every extracted figure arithmetically (row totals, column sums, grand ' +
       'totals). If the numbers don\'t reconcile, the extraction is flagged regardless of ' +
       'whether it came from the positional parser or the LLM fallback.',
  },
  {
    q: 'Can I deploy on Vercel or Netlify?',
    a: 'No. Both are serverless/edge platforms that don\'t support writable SQLite. The database ' +
       'needs a persistent filesystem — Fly.io with a mounted volume, a VPS, or your own server.',
  },
]
