# Mostisko — PO Studio: Workspace + Documents merged, buyer profiles that learn

### Evaluation-based build prompt for Claude Code

> **How to start.** Tell Claude Code:
>
> *"Follow `docs/PO_STUDIO_PROMPT.md`. Do **Phase 1** only, then stop and give me the Phase 1 report."*
>
> After each report: *"Continue with Phase 2."* Never let it run several phases in one go.

---

## 0. Rules of engagement

1. **One phase at a time.** Each phase ends with the report in §10. Stop and wait.
2. **A gate is PASS only with evidence**: eval output, a command and its output, a file path and line, or a screenshot. "Should work" is not evidence. After three failed attempts on one gate, stop and report the blocker.
3. **Evals first.** Each phase adds its checks to `scripts/po-eval.mjs` before the code. Run them once while they fail and paste that line. A check asserts what the fake Ollama received, what the API returned and what the database holds, never the code's own account of itself.
4. **Reuse what exists. Never re-create it.** The extraction pipeline (`ExtractionDispatcher`, `IPoParser`, `LlmPoFallback`, `ExtractionValidator`, `ExtractionResult`), `ExtractionRuns` with `ConfirmedJson` and `EditsJson`, `BuyerProfiles` and its API, `ExtractionReview.vue`, document upload and workspace chat already work. A second extractor, validator, review screen or profile table is an automatic FAIL.
5. **Accuracy is measured against what a person confirmed, never against what a model said.** A score computed from model output alone is a FAIL.
6. **No new packages**, NuGet or npm. Training (Phase 3) runs outside the API with Python tools on the Mac; the app only prepares data, registers the result and evaluates it.
7. **Do not touch the context engine** (`docs/CONTEXT_ENGINE.md`, `ContextPlanner`, `ContextAssembler`, `ModelSettings*`, `ContextReceipt`) or any branch named `context-engine`. If you find its files, leave them as they are.
8. **§2 was verified at commit `cc95a82` of `origin/main`. Your working copy is ahead of it.** Re-confirm every fact and report what changed before building on it.

---

## 1. What we are building

Today a merchandiser uses two tabs: **Documents** to upload and manage POs, and **Workspace** to chat with one and extract JSON. Both feed the same pipeline. This build merges them into one tab, **PO Studio**, and closes the loop that turns confirmed extractions into better extractions:

```
Upload PDF ─► text ─► buyer profile decides: model, schema, rules, glossary, examples
   ─► model writes JSON (format = schema, temperature 0, thinking off)
   ─► validator checks the arithmetic ─► one repair attempt with the failures
   ─► person reviews and confirms ─► confirmed JSON stored
   ─► accuracy per buyer and per field ─► dataset export
   ─► (outside the app) LoRA training ─► candidate model registered
   ─► A/B on held-out confirmed POs ─► promote only if better
```

| Phase | Work | Why in this order |
|---|---|---|
| **1** | Merged PO Studio UI; profiles carry model, schema, rules, glossary and examples; schema-forced output; one repair round | Most of the accuracy gain, with no training |
| **2** | Accuracy board and dataset export | You cannot improve what you do not measure, and training needs clean data |
| **3** | Training script, candidate registration, A/B evaluation, promotion | Last, and only when Phase 2 shows a plateau |

**Not in this build:** OCR for scanned PDFs, training started from the web UI, GPU training inside the API process, automatic promotion without a person pressing the button, multi-document batch extraction.

---

## 2. Established facts (verified at `cc95a82` — re-confirm, do not assume)

| # | Fact | Evidence |
|---|---|---|
| F1 | Two tab keys exist: `workspace` (gates `/api/documents/workspace-chat`) and `docs` (gates `/api/documents/*` and `/api/buyer-profiles/*`). General has neither; System has both | `backend/Middleware/TabAccessMiddleware.cs:43-44, 61`, `backend/Rbac/TabCatalog.cs:111-124` |
| F2 | `DocumentsTab.vue` (113 lines) lists and uploads; `WorkspaceTab.vue` (236 lines) uploads, chats and extracts; `ExtractionReview.vue` (584 lines) is the review dialog. All state lives in `App.vue` | `frontend/src/components/tabs/DocumentsTab.vue`, `WorkspaceTab.vue`, `frontend/src/components/ExtractionReview.vue`, `frontend/src/App.vue:490-653, 889-901, 958-960` |
| F3 | The frontend's buyer list is hard-coded (`['Kohls', 'Walmart', 'Target', 'H&M', 'Generic']`) while the backend has a `BuyerProfiles` table with full CRUD | `frontend/src/App.vue:554`, `backend/MinimalApis/BuyerProfileApis.cs:20-104` |
| F4 | `BuyerProfile` has `Name` (unique), `DetectPattern`, `ParserKey` (default `GenericLlm`), `ChunkingStrategy`, `IsActive`. Profiles are install-wide, not per user | `backend/Models/BuyerProfile.cs:8-29`, `backend/Data/AppDbContext.cs:160` |
| F5 | The dispatcher detects a profile, runs a registered deterministic parser if the profile names one, otherwise falls back to the model; the validator runs on both | `backend/Extraction/ExtractionDispatcher.cs:42-115` |
| F6 | The model fallback sends one fixed schema and rules, `format: "json"` (not the schema itself), temperature 0, and **cuts the document at 60,000 characters** without telling anyone. At 16K tokens of context that cut is already too late, so long POs lose their end silently | `backend/Extraction/LlmPoFallback.cs:13-77` |
| F7 | The fallback uses the `ollama` HttpClient, whose timeout is 10 minutes, without streaming | `backend/Extraction/LlmPoFallback.cs:84`, `backend/Program.cs:86-90` |
| F8 | `ExtractionRuns` stores `ResultJson` (pipeline output), `Status`, `FailedChecks`, `ConfirmedJson`, `ConfirmedByUserId`, `ConfirmedAt`, `EditsJson`. It does **not** store which model or which profile version produced the result | `backend/Models/BuyerProfile.cs:33-51`, `backend/Data/SchemaUpgrade.cs:372-420` |
| F9 | Confirming saves the edited JSON and the edit list on the latest run of a document | `backend/MinimalApis/DocumentApis.cs:313-352` |
| F10 | `ExtractionResult` is a typed tree: `Header`, `OrderLine`, `PrepackComponents`, `Derived`, `PurchaseOrderTotal`, `Validation` | `backend/Extraction/ExtractionResult.cs:8-49` |
| F11 | Uploads are kept at `data/uploads/{id}{ext}` and extracted per page with PdfPig | `backend/MinimalApis/DocumentApis.cs:23, 512-523` |

If the model-policy build is present (`backend/Rbac/ModelPolicy.cs`), every model a profile names must pass `ModelPolicy` for the caller, like any other model call.

---

## 3. The design contract

### 3.1 Tab and routing

- One tab, label **PO Studio**, key `workspace`, route `/workspace`. `/documents` redirects to `/workspace#library`.
- The `docs` key stays in `TabCatalog` and `TabAccessMiddleware` exactly as today, so existing grants and API gates do not change. The tab shows for a user who has `workspace` **or** `docs`. A section whose API the user cannot call is shown disabled with one line saying which access it needs.
- `ALL_TABS` and `MODULE_ROUTES` lose the separate Documents entry; the command palette keeps "Upload a purchase order".

### 3.2 PO Studio layout

The feel to aim for is a guided studio: clear steps, generous space, one main action per screen, live progress, nothing that needs the manual. Use the current theme tokens and the `components/ui` kit only.

```
┌ PO Studio ─────────────────────────────────────────────────────────────┐
│ Library │ Review │ Profiles │ Accuracy │ Datasets │ Training           │  ← sub-navigation
├────────────────────────────────────────────────────────────────────────┤
│ Library   drop zone (drag a PDF, or click) · buyer: auto-detect ▾      │
│           table: file · buyer · status chip · last run · accuracy ·    │
│           confirmed ✓ · actions (Review, Chat, Delete)                 │
│ Review    split view: PDF page (pdfjs) left │ JSON form right          │
│           failed checks pinned on top, the fields they name outlined;  │
│           "Ask about this PO" chat drawer; Confirm button              │
│ Profiles  one card per buyer: model, rules, glossary, examples, fields │
│ Accuracy  per buyer and per field (Phase 2)                            │
│ Datasets  export (Phase 2)                                             │
│ Training  candidates and A/B results (Phase 3)                         │
└────────────────────────────────────────────────────────────────────────┘
```

- **Upload flow as a stepper**: Uploading → Reading text → Detecting buyer → Extracting → Checking → Ready for review. Each step shows its state; a failure stops at that step with a plain message and a retry.
- **Review** reuses `ExtractionReview.vue`'s logic; move it from a dialog into the Review section. `pdfjs-dist` is already installed. Never render extracted text or model output with `v-html`.
- **Status chips**: `needs review`, `checks failed`, `confirmed`. Colours only from the status classes already used in `LogsTab.vue`.
- Works at 360 px wide and in light and dark.

### 3.3 Profiles that carry their own knowledge

Add to `BuyerProfiles` (in `SchemaUpgrade`, idempotent):

| Column | Meaning |
|---|---|
| `ExtractionModel` | The Ollama model for this buyer. Empty means the install default `Extraction:DefaultModel` (start with `gemma4:12b`) |
| `Rules` | Buyer-specific instructions, plain text ("Quantities on Kohls POs are packs; pieces = packs × pack size") |
| `Glossary` | `term = meaning` lines (prepack, assortment, ex-factory, ...) |
| `ExampleRunIds` | Up to 2 confirmed `ExtractionRuns` used as worked examples, ordered |
| `Version` | Integer, +1 on every save |

**Prompt assembly** (one pure function, `PoPromptBuilder`, unit-testable without a model):

```
system:  base rules (today's LlmPoFallback rules)
         ## Buyer rules           <Rules>
         ## Glossary              <Glossary>
         ## Output schema         <the JSON schema of ExtractionResult, without Derived and Validation>
         ## Worked example 1      <first 1,500 tokens of that PO's text>  →  <its ConfirmedJson>
         ## Worked example 2      …
user:    <the document text, with "--- page N ---" markers>
```

- The schema is sent **both** in the prompt and as Ollama's `format` (a JSON schema object, not the string `"json"`), with `temperature: 0` and `think: false`.
- **Budget, no silent cuts** (replaces the 60,000-character cut of F6): estimate tokens as `ceil(ASCII ÷ 3.5) + non-ASCII`. Window = `Extraction:NumCtx` (16384), minus 4,096 reserved for the JSON. If it does not fit: drop example 2, then example 1, then glossary. If the document alone still does not fit, the run fails with `"Document is N tokens; the window holds M. Split it or raise Extraction:NumCtx."` Never cut the document.
- The extraction call streams and uses a client with no fixed timeout and the request's cancellation token (F7).

### 3.4 One repair round

After the validator, if any check failed **and** the result came from the model: send one more request with the same prompt plus the model's JSON and the failed checks ("sizes sum to 1,750 but the PO total is 1,800"), asking for corrected JSON only. Keep the second answer only if it fails **fewer** checks; otherwise keep the first. Record both in the run.

### 3.5 Runs remember how they were made

Add to `ExtractionRuns`: `Model`, `ProfileVersion`, `PromptHash` (SHA-256 of the assembled system prompt), `RepairUsed` (bool), `FirstResultJson` (before repair), `PromptTokens`, `OutputTokens`, `DurationMs`. Without these, accuracy cannot be compared over time.

### 3.6 Accuracy (Phase 2)

**Field list** (fixed, in code, one place): header `OrderNumber, PoType, PoDate, CancelDate, DeliveryWindow.Start/End, ShippingWindow.Start/End, ExitFactoryEndDate, Incoterms, Vendor.Name`; line `StyleNumber, Sku, Upc, UnitPrice`; `PrepackComponents` (as a set of size → qty pairs); `PurchaseOrderTotal`.

**Comparison** of a run's `ResultJson` against its `ConfirmedJson`:

- Strings: trim, collapse inner spaces, exact case. Numbers: numeric equality (money to 2 decimals). Dates: same ISO date. `null` equals `null`.
- `PrepackComponents`: correct only if the set of pairs is identical.
- A field present in neither counts as correct and is excluded from the "fields present" count.

**Scores**: per run (fields correct ÷ fields present), per field across runs, per buyer, and "fully correct POs %". Only runs with `ConfirmedJson` count.

**Held-out set**: a confirmed run belongs to the test set when `DocumentId % 5 == 0`. It never changes, never appears in an export's training file, and is never chosen as a worked example.

### 3.7 Dataset export (Phase 2)

`GET /api/po-studio/datasets/export?buyer=&split=train|test` returns JSONL, one line per confirmed run, in the chat format MLX and Unsloth both read:

```json
{"messages":[{"role":"system","content":"<assembled system prompt without examples>"},{"role":"user","content":"<document text>"},{"role":"assistant","content":"<ConfirmedJson>"}]}
```

A summary first: counts per buyer and split, and runs skipped with the reason (no confirmation, document text missing, over the window).

### 3.8 Training, candidates and A/B (Phase 3)

- `scripts/train-po-lora.sh` (Mac, Python): installs `mlx-lm` into a local venv, runs LoRA on the exported train file, fuses, converts to GGUF, and creates the Ollama model `po-extract-<buyer>-v<N>` from a Modelfile. Every step prints what it does and stops on the first error. A short `docs/PO_TRAINING.md` explains it and the alternative of Unsloth on a rented GPU.
- **Candidates**: `POST /api/po-studio/candidates { buyer, model }` registers an installed model as a candidate. `POST …/candidates/{id}/evaluate` runs the current profile model and the candidate on the **held-out** set with the same prompt, stores both scores per field, and never overwrites any `ExtractionRun` (evaluation runs live in their own table).
- **Promotion rule** (shown in the UI, enforced by the server): promote only if overall field accuracy is at least **2 points** higher, no single field drops more than **5 points**, and validator pass rate is not lower. The button is disabled with the reason otherwise. Promotion sets `ExtractionModel` and bumps `Version`; the previous model is kept for one-click rollback.

### 3.9 Files

```
backend/Extraction/PoPromptBuilder.cs          PURE: profile + document text + examples + budget → messages + report
backend/Extraction/PoAccuracy.cs               PURE: ResultJson vs ConfirmedJson → field scores
backend/Extraction/LlmPoFallback.cs            uses the builder, schema format, repair round, streaming
backend/Models/BuyerProfile.cs                 new columns; ExtractionRun new columns; EvaluationRun entity (Phase 3)
backend/Data/SchemaUpgrade.cs, AppDbContext.cs
backend/MinimalApis/BuyerProfileApis.cs        new fields; example picking limited to confirmed, non-test runs
backend/MinimalApis/PoStudioApis.cs            /api/po-studio/accuracy, /datasets/export, /candidates*  (gated by tab docs)
backend/Middleware/TabAccessMiddleware.cs      one prefix: /api/po-studio → docs
frontend/src/components/tabs/PoStudioTab.vue   sub-navigation only
frontend/src/components/po-studio/            Library, Review, Profiles, Accuracy, Datasets, Training, UploadStepper
frontend/src/services/po-studio.service.js
frontend/src/app/router.js, App.vue            merged tab, /documents redirect, remove hard-coded buyer list
scripts/po-eval.mjs, scripts/po-fixtures/, scripts/train-po-lora.sh
docs/PO_TRAINING.md
```

---

## 4. Phase 1 — Merged studio and smarter profiles

**Do**
- Re-confirm §2. Write `scripts/po-eval.mjs` (fake Ollama, API on a throwaway database, fixtures) and this phase's checks first.
- Fixtures: three small **text** POs (`.txt`, accepted by upload) for two buyers, with their correct JSON written by hand. One real PDF from KAKAROT if available (§11 Q1).
- §3.1–§3.5.

**Gates**

| Gate | Check |
|---|---|
| G1.1 | One PO Studio tab; `/documents` lands on its Library; a user with only `docs` and a user with only `workspace` both see it, each with the other's sections disabled and explained. Screenshots |
| G1.2 | The buyer list comes from `/api/buyer-profiles`; `grep -n "'Walmart'" frontend/src` → no matches |
| G1.3 | Upload shows the six-step stepper; a forced failure at each of "Reading text" and "Extracting" stops there with its message and a working retry |
| G1.4 | `PoPromptBuilder` determinism: the same inputs give byte-identical messages 10 times; the layout matches a hand-written expected string in the eval |
| G1.5 | The fake Ollama received `format` as the schema **object**, `temperature: 0`, `think: false`, and the profile's model (or the default when empty) |
| G1.6 | Budget: with a small `Extraction:NumCtx`, examples are dropped before the glossary, and a document that cannot fit fails with the message of §3.3. The fake never received a cut document. `grep -n "60_000" backend/Extraction` → no matches |
| G1.7 | Repair: a fake answer that fails one check, followed by a corrected one, ends with the corrected JSON and `RepairUsed = true`; a worse second answer is discarded. Both cases recorded |
| G1.8 | A run stores `Model`, `ProfileVersion`, `PromptHash`, tokens and duration; editing a profile bumps `Version` and changes `PromptHash` |
| G1.9 | Worked examples: only confirmed runs can be chosen; a held-out run (`DocumentId % 5 == 0`) is refused with a reason |
| G1.10 | Review: split view with the PDF page and the JSON form; failed checks pinned and their fields outlined; Confirm still writes `ConfirmedJson` and `EditsJson`. `grep -rn "v-html" frontend/src/components/po-studio` → no matches |
| G1.11 | 360 px, light and dark: screenshots of Library, Review and Profiles. No hard-coded colours in new files |
| G1.12 | Workspace chat with a pinned PO still streams as before |
| G1.13 | `dotnet build`, `npm run build` clean; `node scripts/po-eval.mjs --phase 1` summary, and the red line from before the code |

---

## 5. Phase 2 — Accuracy board and dataset export

**Do**
- `PoAccuracy` (pure) and its unit checks first, against hand-written pairs covering every comparison rule in §3.6.
- `/api/po-studio/accuracy?buyer=&from=&to=` and the **Accuracy** section: per-buyer cards (field accuracy, fully-correct %, confirmed runs, trend by week), a per-field table sorted worst first, and a list of the runs behind any cell.
- `/datasets/export` and the **Datasets** section: counts per buyer and split, skipped runs with reasons, download buttons for train and test.

**Gates**

| Gate | Check |
|---|---|
| G2.1 | Every rule in §3.6 has an eval case (string spacing, case, money rounding, dates, null vs missing, prepack set equality, order-insensitive pairs) and passes |
| G2.2 | On the fixtures, the board's numbers equal the eval's own arithmetic, per field, per buyer and overall |
| G2.3 | Only confirmed runs count; an unconfirmed run changes nothing on the board |
| G2.4 | Clicking a field's cell lists exactly the runs that got it wrong, each opening in Review |
| G2.5 | Export: valid JSONL (every line parses), the chat format of §3.7, the held-out runs only in `split=test`, never in `train`. Skipped runs listed with reasons |
| G2.6 | The trend uses `ProfileVersion`: changing a profile shows a new point, so you can see whether the change helped |
| G2.7 | Screenshots of Accuracy and Datasets, light and dark, 360 px. Each chart also shows its numbers as text |
| G2.8 | Builds clean; `--phase 2` summary line |

---

## 6. Phase 3 — Training and A/B evaluation

**Before starting, check the board.** If a buyer has fewer than 100 confirmed training runs, say so in the report: training on fewer is unlikely to help, and the gates below use the fixtures only to prove the mechanics.

**Do**
- `scripts/train-po-lora.sh` and `docs/PO_TRAINING.md` (§3.8).
- `EvaluationRuns` table, `/candidates` routes, the **Training** section: candidates per buyer, an Evaluate button with progress, a side-by-side table (current vs candidate, per field, with the difference), the promotion rule's verdict, Promote and Roll back.

**Gates**

| Gate | Check |
|---|---|
| G3.1 | The script runs end to end on the Mac on the fixture export and creates an Ollama model; paste its output and `ollama list`. If the Mac cannot run it, say why and stop at this gate |
| G3.2 | Registering a model that is not installed is refused with a clear message |
| G3.3 | Evaluation uses only held-out runs, the same prompt for both models, and writes nothing to `ExtractionRuns`. Show row counts before and after |
| G3.4 | With the fake Ollama, a candidate that is better by 3 points with no field drop over 5 is promotable; one better by 1 point, or with a 6-point field drop, is not, and the UI states which rule failed |
| G3.5 | Promote sets the profile model and bumps `Version`; Roll back restores the previous model in one click. Both appear in `AuditLogs` |
| G3.6 | Live, on the Mac: evaluate `gemma4:12b` against `qwen3.8-27b-q3` (or a trained candidate) on the real held-out set. Paste the side-by-side table and the time taken |
| G3.7 | Builds clean; the whole `po-eval` suite passes |

---

## 7. Eval harness

`scripts/po-eval.mjs`, Node 18+, no dependencies. If `scripts/oddity-eval.mjs` or `scripts/model-policy-eval.mjs` exists, reuse its fake Ollama and API start-up instead of writing a third copy.

- The fake Ollama records every request and answers `/api/chat` from a script keyed by the fixture name, so a test can say "first answer fails the size check, second fixes it".
- The API runs against a throwaway SQLite file, never the dev database. The admin login comes from environment variables, never written in the script.
- Output: `id | check | PASS/FAIL | detail`, a summary line, non-zero exit on any FAIL, `--phase N`, `--only <id>`.

---

## 8. Do not

- Do not cut a document to fit. Fail with the size instead.
- Do not compute accuracy from anything other than `ConfirmedJson`.
- Do not let a held-out run become a worked example or a training line.
- Do not overwrite `ExtractionRuns` during evaluation, or promote without the rule passing and a person pressing the button.
- Do not run training inside the API process.
- Do not keep two separate tabs for documents and workspace, or a hard-coded buyer list.
- Do not use `v-html` on extracted text or model output, or colours outside the theme tokens.
- Do not touch the context engine (rule 7).
- Do not fix unrelated things you notice; report them.

---

## 9. Phase order is fixed

Phase 2 depends on the run columns of Phase 1, and Phase 3 depends on the held-out split and the export of Phase 2. Do not start a phase whose predecessor has any FAIL.

---

## 10. Report format (end of every phase)

```
## Phase N report — <title>
Summary: <2–3 lines>
Eval: <N/M passed>   first run before the code: <N/M passed>

| Gate | Check | Result | Evidence |
|------|-------|--------|----------|

Facts from §2 that changed: <list, or "none">
Files changed: <list>
Decisions made (and why): <list>
Noticed, not changed: <list>
Needs KAKAROT: <questions, or "none">
```

A phase with any FAIL is not complete. Say so plainly.

---

## 11. Open questions for KAKAROT

Each has a default the build follows until you say otherwise.

1. **Real POs.** Send two or three real POs per buyer (PDF) with the correct JSON for each. Default: text fixtures only until they arrive; G3.6 waits for real data.
2. **Default extraction model.** Default `gemma4:12b` (fast, best on the last test). The 27B q3 is the alternative for buyers whose POs it handles better; the A/B screen will show it.
3. **Who may edit profiles and promote models.** Default: anyone with the `docs` tab edits profiles; only SystemAdmin promotes or rolls back.
4. **Held-out share.** Default 20% (`DocumentId % 5 == 0`). Say if you want more data kept for testing.
5. **Where training runs.** Default: on the Mac with `mlx-lm`. If you prefer Unsloth on a rented GPU, the export is the same; only the script changes.
