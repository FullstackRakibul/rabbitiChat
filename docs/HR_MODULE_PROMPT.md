# Mostisko — মানব সম্পদ (HR Assistant): answers from HR's own context

### Evaluation-based build prompt for Claude Code

> **Before you start.**
> 1. On `main`, commit the earlier uncommitted fixes (the Workspace Studio rename, the `App.vue` restore, the dropdown fix and the one-line `po-eval` change) as their own commit.
> 2. Leave the `hr-assist` branch (the SQL Server data layer, `bb2bc94`) as it is. It is **not** merged and **not** used by this build.
> 3. Put these files in the repository and commit them on `main`:
>    - this prompt → `docs/HR_MODULE_PROMPT.md`
>    - `HR_SETTLEMENT_RULES.md` → `docs/hr/HR_SETTLEMENT_RULES.md`
>    - the procedures and functions you want the assistant to know → `docs/hr/procedures/` (for example `ProcessFinalSettelment.sql`, `ProcessSupplementaryFinalSetelment.sql`, `GetSalaryDetails.sql`, `GetDateDurationYear.sql`)
>
> **How to start.** Tell Claude Code:
>
> *"Follow `docs/HR_MODULE_PROMPT.md`. Do **Phase 1** only, then stop and give me the Phase 1 report."*
>
> After each report: *"Continue with Phase 2."* Never let it run several phases in one go.

---

## 0. Rules of engagement

1. **One phase at a time.** Each phase ends with the report in §9. Stop and wait.
2. **A gate is PASS only with evidence**: eval output, a command and its output, a file path and line, or a screenshot. After three failed attempts on one gate, stop and report the blocker.
3. **Evals first.** Each phase adds its checks to `scripts/hr-eval.mjs` before the code. Run them once while they fail and paste that line.
4. **No database connection to CoreERP, and no SQL is ever run.** The assistant knows only what HR gives it: rules documents, procedure text and code tables. Procedure files are **reference text**. Nothing executes them, parses them into queries, or connects anywhere. Do not add `Microsoft.Data.SqlClient` or any other package.
5. **Answers come from the context.** The model answers from the HR context sent with the question. When the context does not cover a question, it says so; it never fills the gap from general knowledge or guesses company policy. The HR-approved rules document wins over procedure code; when the two disagree, the answer says so.
6. **Reuse what exists.**
   - `ContextSource` (its `Kind` is free-form "so a third surface needs no schema change"); `EmbeddingService`, `VectorIndex` and the existing chunker; the chat tool loop in `ChatEndpoint.cs`; `ModelPolicy`; `TabCatalog` / `TabAccessMiddleware`; and the shared fake Ollama in `scripts/*-eval.mjs`.
   - A second source store, vector index, tool loop or policy check is an automatic FAIL.
7. Do not push, merge to `main` or open a pull request. Work on a branch named `hr-knowledge`, created from `main` after step 1, and commit at the end of each phase.
8. Re-confirm every fact in §2 and report what changed.

---

## 1. What we are building

A module named **মানব সম্পদ** (tab key `hr`) for the user type **hr-assistant**. HR keeps a library of context: the rules document, the procedure files, code tables (employee types, separation statuses, attendance codes) and notes. An HR user asks in Bengali or English, and the assistant answers from that library with citations. Example questions:

- *"Schedule-A কর্মী ৭ বছর ৮ মাস চাকরি করে ইস্তফা দিলে সার্ভিস বেনিফিট কীভাবে হিসাব হয়?"* (How is service benefit worked out for a Schedule-A employee who resigns after 7 years 8 months?)
- *"Which attendance codes count as worked days for earned leave?"*
- *"Basic is 12,000, 7 years 8 months, 250 fraction days, resignation in April 2026, Non-Management. What is the service benefit?"*
- *"What happens when someone in unit 27 separates on the 28th?"*

```
HR library (রুলস, procedures, code tables, notes)
   ├─ "always" items  → sent whole, every question (the rules document, code tables)
   └─ "search" items  → split, embedded; the best excerpts per question (procedure files)
HR user ─► /api/hr/chat (tab hr, model policy)
        ─► system prompt: HR persona + always items + retrieved excerpts, within the budget
        ─► model (writes every amount as an inline formula [[= … ]], evaluated by the server)
        ─► number check ─► answer with citations ─► saved in the HR chat list
```

| Phase | Work | Why in this order |
|---|---|---|
| **1** | The hr-assistant user type, the মানব সম্পদ tab, and the HR library: add, edit, version, always/search, SQL-aware splitting, seeding from `docs/hr/` | The assistant is only as good as its context |
| **2** | The HR chat: context assembly within the budget, citations, inline formulas, the number check, the UI | Needs the library |
| **3** | The golden question set and the live run on the Mac | Proves it with HR's real questions |

**Not in this build:** reading live employee data, running any procedure, writing to any database, fine-tuning a model.

---

## 2. Established facts (verified at `cc95a82` — re-confirm, do not assume)

| # | Fact | Evidence |
|---|---|---|
| H1 | `ContextSource` has `OwnerUserId`, `Name`, `Kind` (`source`, `buyer`; free-form), `Content` (injected verbatim), `IsActive`, `SortOrder`. Every query is scoped to the owner | `backend/Models/ContextSource.cs` |
| H2 | `/api/context-sources` has list, get, create, update, reorder and delete | `backend/MinimalApis/ContextSourceApis.cs` |
| H3 | `EmbeddingService` embeds with `nomic-embed-text`; `VectorIndex` searches with a metadata gate first (owner, buyer, style number); chunks carry a `SourceRef` for citations | `backend/Retrieval/VectorStore.cs` |
| H4 | Chat runs tools: a `tools` array, `ToolCall` events, `RunToolAsync`, `MaxToolRounds`. Unknown tools get "No tool named …" | `backend/MinimalApis/ChatEndpoint.cs` (the Phase 1 report of the earlier HR build found `MaxToolRounds` at line 29 and `RunToolAsync` at 244) |
| H5 | Roles are normalised in `Rbac.cs`; tab baselines are in `TabCatalog`; `ModelPolicy` seeds one `RoleModelPolicies` row per role on start, only for roles without one | `backend/Models/Rbac.cs`, `backend/Rbac/TabCatalog.cs`, `backend/Rbac/RbacSeeder.cs` |
| H6 | The Mac's Ollama runs with a 16,384-token window. Models: `gemma4:12b` (default) and `qwen3.8-27b-q3` | KAKAROT's setup |
| H7 | Sizes: the rules document is about 2,000 words (about 3,500 tokens). `ProcessFinalSettelment` is 1,200 lines and `ProcessSupplementaryFinalSetelment` 1,400 lines, so neither fits in the window whole, and both contain large commented-out blocks of old formulas | `docs/hr/` |

---

## 3. The design contract

### 3.1 User type and tab

- **A new role `HrAssistant`,** shown as **hr-assistant**:
  - Normalised like the others.
  - Its tab baseline is exactly `hr`, `notes` and `settings`.
  - It gets a model-policy row on start: allowed `gemma4:12b`, default `gemma4:12b`.
  - SystemAdmin can still grant it more tabs.
- **The tab `hr`**, labelled **মানব সম্পদ** everywhere it shows: tab bar, command palette, Access screen, docs page.
  - SystemAdmin has it.
  - No other role has it in its baseline; others reach it only through a grant.
- **Create the user `hr-assistant`** with role `HrAssistant` through the existing admin API, from a seed step that runs only when the user does not exist. The secret comes from the environment variable `HR_ASSISTANT_SECRET`, never from a committed file; with no variable, skip the step and log one line saying so.

### 3.2 The HR library
HR context items are `ContextSource` rows with `Kind = "hr"`, plus these columns:

| Column | Meaning |
|---|---|
| `HrType` | `rules`, `procedure`, `codes` or `note` |
| `Mode` | `always`: sent whole with every question. `search`: split and embedded, best excerpts sent |
| `Version` | Goes up by one on every content change |
| `ApprovedBy`, `ApprovedAt` | Optional. Shown next to citations, so users can see which rules HR signed off |

- **Shared, not personal.** HR items are company knowledge. Anyone with the `hr` tab can read them. People whose role is `HrAssistant` or `SystemAdmin` can add, edit and delete them.
  - Owner scoping for every other `Kind` is unchanged. Implement the exception once, in the source service, and test it.
- **Defaults by type:** `rules` and `codes` default to `always`; `procedure` and `note` default to `search`.
- **The library has no size limit; the always budget does.**
  - Any item can be `search`, and HR can add as many as they like.
  - The total of active `always` items is capped at 8,000 tokens. Turning an item to `always` beyond the cap is refused in the Library with "Always items would use N of 8,000 tokens; make this item Search or switch another off".
  - The chat therefore never meets an over-budget library.
  - The Library header shows the running total, as it does today.
- **Splitting `search` items:**
  - **SQL files:**
    - Remove commented-out code first: whole lines starting with `--` that contain a SQL keyword (`SET`, `SELECT`, `IF`, `BEGIN`, `END`, `FROM`, `WHERE`, `DECLARE`, `INSERT`, `DELETE`, `UPDATE`, `EXEC`), and `/* … */` blocks.
    - Keep section comments such as `--//Gratutity Calculation Start` and short notes such as `--NO Service Benefit for DEATH`.
    - Then split on those section comments, and below them on blank lines, into chunks of about 800 characters, never more than 1,500.
    - Each chunk keeps its file name, its section name and its original line range.
  - **JSON** (`.json`, or pasted text that parses as JSON):
    - Validate it first; invalid JSON is refused with the parser's line and column.
    - Split along the structure: each element of a top-level array, or each top-level property, is a unit. Units are packed into chunks of about 800 characters (never more than 1,500), and a unit larger than that is split by its own children.
    - Each chunk keeps its JSON path in the `SourceRef`, for example `leave-policy.json $.categories[2]`.
    - Chunks are pretty-printed, so the model reads keys and values clearly.
  - **CSV and tables:** rows are grouped into chunks with the header row repeated at the top of each, and the `SourceRef` carries the row range: `emp-types.csv rows 1-40`.
  - **PDF and XLSX:** extract the text with the existing extractors (PdfPig, ClosedXML), then split as text, with page or sheet in the `SourceRef`.
  - **Other text** (`.md`, `.txt`, pasted text): split by headings, then into chunks of about 800 characters.
  - **Chunk size changed (Phase 2 decision).** Chunks were up to 1,500 characters; smaller chunks keep the excerpts short, so the first word comes sooner. A library built with the old size is re-split and re-indexed once on start.
  - **Formats:** `.md .txt .sql .json .csv .pdf .xlsx`, at most 20 MB each, the same limit as Documents. Anything else is refused with the list. A `.docx` gets "save it as PDF first".
  - The `SourceRef` is `ProcessFinalSettelment.sql §Service Benefit L686-711`.
- **Embedding:** chunks are embedded on save. Editing or deleting an item removes its old chunks. Retrieval filters on `Kind = hr` before scoring, as the vector index does for owners.
- **Hybrid search.** HR questions are full of exact tokens that embeddings handle badly: `EXL`, `Schedule-A`, unit `27`, status `11`, column names. So every HR chunk is also put in an SQLite **FTS5** table. FTS5 ships in the `e_sqlite3` build the app already uses, so no package is needed.
  - At ask time, run both searches: vector top 20 and keyword (BM25) top 20.
  - Merge them by reciprocal rank (`1/(60+rank)` summed).
  - Keep the best 5, whole chunks only, up to 1,500 tokens.
  - Deleting or editing an item updates both indexes.
- **Large libraries.** Saving an item embeds it in the background; the item shows "indexing… n of m" and is searchable when done. A failed embed marks the item `failed` with the reason and a Retry button. The Library has a text filter and a type filter, so hundreds of items stay usable.
- **Seed on start** from `docs/hr/*.md` and `docs/hr/procedures/*.sql`, only for files not already in the library (matched by name). An HR edit in the app is never overwritten by the seed.

### 3.3 The chat
- **Route:** `POST /api/hr/chat`, streamed like `/api/chat`, gated by tab `hr`. Model choice goes through `ModelPolicy`.
- **Saved conversations:** HR conversations are saved like normal chats, with a `Kind = "hr"` column on `Conversations`. They appear only in the মানব সম্পদ tab's own list, and never in the main sidebar.
- **Budget** (a 16,384-token window):
  - **Reserved for the answer:** 2,000 tokens.
  - **First:** the HR persona and the `always` items, in `SortOrder`. If these alone exceed 8,000 tokens, refuse with a message naming the largest items, never cut them.
  - **Next:** retrieved excerpts, the best 5 by score above a minimum, whole chunks only, up to 1,500 tokens.
  - **The rest:** recent history, oldest dropped first.
  - Use the one token estimator the app already has.
- **Fast first word.** The message order is fixed so Ollama can reuse its cache of the unchanged start of the prompt:
  - **First, unchanged for every question:** the system message with the persona and the `always` items, byte-identical.
  - **Then:** the history.
  - **Last:** a final user message holding the retrieved excerpts (titled "HR context for this question") followed by the question.

  Excerpts never go into the system message. Send the model's saved `num_ctx` and `keep_alive` (16,384 and -1 on the Mac), the same values every time, so the model is never reloaded.
- **A status line within 1 second.** Before the model starts, the stream carries a status line: "খুঁজছি…" at once, then "খুঁজছি… N টি অংশ পাওয়া গেছে" when retrieval is done. The answer bubble shows it until the first word arrives.
- **Speed on the Mac (measured in Phase 2).** Ollama evaluates new prompt tokens at about 125 per second, and the cached system prefix costs almost nothing. The first word therefore waits mainly on the excerpts and the question, which is why excerpts are capped at 1,500 tokens.
- **Scope a question.** The Ask box has an optional "Search in" picker. When items are picked, retrieval is limited to them; the default is the whole library.
- **Instant search without the model.** `GET /api/hr/search?q=…&items=…` runs the hybrid search (§3.2) and returns the top 10 chunks with `SourceRef`, item name and a highlighted snippet, in under a second. HR uses it to find where something is written. Each result has "Ask about this", which starts a question scoped to that item.
- **System prompt rules:**
  - Answer only from the HR context.
  - After each statement, cite its source: `[HR_SETTLEMENT_RULES §5]` or `[ProcessFinalSettelment.sql §Service Benefit L686-711]`.
  - When the rules document and a procedure disagree, give the rules document's answer and say the code does something else.
  - When the context does not cover the question, say so in one line and name the item HR would need to add.
  - For an employee's amount, use only figures the user gave; if a figure is missing (basic, service length, status, type, date), ask for it.
  - Write every amount you work out as an inline formula, `[[= (12000/30)*15*8 ]]`, never as a number you calculated yourself.
  - Answer in the user's language; amounts in digits with ৳.
- **Inline formulas (Phase 2 decision, replacing the `calculate` tool).** `gemma4:12b` called the tool in only about half the live runs and did the arithmetic itself in the rest, once wrongly. So the model no longer decides whether to calculate: it writes the formula and the server does the arithmetic.
  - The model writes `[[= expression ]]` where an amount belongs.
  - The server buffers the stream from `[[=` to `]]`, evaluates the expression with the C# evaluator (numbers, `+ - * /`, parentheses and `round(x, n)`, nothing else, no packages), and streams the formatted result in its place: `৳` with thousands separators, for example ৳48,000.
  - Each formula and its result are stored in the message's `HrMetaJson`, and the UI shows the formula when the amount is hovered or tapped.
  - An invalid formula, or one cut off by a line break or the end of the answer, streams "⚠ গণনা করা যায়নি" (could not calculate) and is listed as unverified.
  - No tools are offered on the HR route. `/api/hr/calculate` stays as a direct way to check an expression.
- **Number check, after the reply:**
  - Every amount of 100 or more in the reply (commas, `৳` and `Tk` removed) must appear in the user's messages, the context sent, or a formula result in that turn, to within 1. Formula results count as verified.
  - Any that don't, and every formula that could not be calculated, are listed in a final stream event `unverified: [...]`.
  - The UI shows them under the answer: "এই সংখ্যাগুলো যাচাই করা যায়নি / Not verified: …".

### 3.4 UI (the মানব সম্পদ tab)
- **Two sections:**
  - **প্রশ্ন (Ask):** the chat with its own conversation list.
  - **লাইব্রেরি (Library):** the HR items, each showing type, mode, version, approval, size in tokens and, for `search` items, chunk count.
- **Library actions:**
  - Add by pasting text or JSON, or by uploading one or many files (`.md .txt .sql .json .csv .pdf .xlsx`) at once.
  - Edit, switch between always and search (respecting the 8,000-token cap), set approval, delete, retry a failed item, and preview the chunks of a `search` item.
  - Filter by text and by type. Indexing progress is shown on each item.
- **Ask section:** a search bar at the top for instant search (§3.3), the chat below it with the "Search in" picker, and the HR conversation list at the side (a drawer on phones).
  - Library editing is hidden for users who cannot edit (§3.2).
- **Citations** are clickable and open the cited item at the cited section or lines.
- **A "Context used" panel** under each answer lists the items and excerpts that were sent, with their token counts.
- **Bengali labels with English in smaller text.** Theme tokens only, light and dark, usable at 360 px.

---

## 4. Phase 1 — User type, tab and library

**Do**
- Re-confirm §2. Write `scripts/hr-eval.mjs` (reusing the shared fake Ollama and API start-up) and this phase's checks first.
- §3.1, §3.2, and the Library section of §3.4.

**Gates**

| Gate | Check |
|---|---|
| G1.1 | **User type.** With `HR_ASSISTANT_SECRET` set, a fresh database has the user `hr-assistant` with role `HrAssistant`; `/api/me/tabs` is exactly `hr notes settings`; its model-policy row is `gemma4:12b`. Without the variable, the step is skipped with one log line. Restarting does not create a second user |
| G1.2 | **Tab.** SystemAdmin has `hr`; a General or System user without a grant gets 403 on `/api/hr/*`, and a grant opens it. The label is "মানব সম্পদ" in the tab bar, command palette, Access screen and docs page (screenshot) |
| G1.3 | **Shared library.** An item added by `hr-assistant` is readable by a second user with the `hr` grant, who cannot edit it; SystemAdmin can edit it. A `source`-kind item of one user is still invisible to another (owner scoping unchanged) |
| G1.4 | **Seed.** A fresh start loads every file in `docs/hr/` and `docs/hr/procedures/` with the right type and mode. Edit one in the app, restart: the edit is kept and the version did not reset |
| G1.5 | **SQL splitting.** From `ProcessFinalSettelment.sql`: no chunk contains the commented-out "Ager Formula" block (the `* 14` service-benefit formula); the chunk holding `IF @LengthYear BETWEEN 3 AND 9 AND @EmpStatusID NOT IN (8)` has section "Service Benefit" and a line range that contains line 686; no chunk is over 1,500 characters. Paste the first five `SourceRef`s |
| G1.6 | **Retrieval.** "How is service benefit calculated for 3 to 9 years?" retrieves a Service Benefit chunk, and "Which attendance codes count for earned leave?" retrieves the chunk with `'P','LT','LTP','SD','TL','EE','NOP','EXL'`, each among the top 5 excerpts sent to the model (Phase 2 decision; it was the top 3). Run against the fake embedding, and live with `nomic-embed-text` |
| G1.7 | **Edits and deletes.** Editing a `search` item replaces its chunks (old count → new count, no orphans in the index or the database); deleting removes them; the version goes up on each edit |
| G1.8 | **Library UI.** Add by paste and by `.sql` upload; switch mode; chunk preview. Screenshots, light and dark, 360 px; no hard-coded colours |
| G1.9 | `dotnet build` 0 warnings; `hr-eval --phase 1` summary and the red line; `po-eval`, `oddity-eval` and `model-policy-eval` unchanged. `git diff main --stat` shows no `Microsoft.Data.SqlClient` and nothing from `hr-assist` |

---

## 5. Phase 2 — The HR chat

**Do**
- §3.3 and the Ask section of §3.4.
- The §3.2 additions made after Phase 1:
  - the always cap;
  - JSON, CSV, PDF and XLSX splitting;
  - FTS5 hybrid search;
  - background indexing with retry;
  - multi-file upload;
  - Library filters.

**Gates**

| Gate | Check |
|---|---|
| G2.1 | **Context sent.** For one question, the fake Ollama received the persona, every active `always` item whole and in `SortOrder`, then the retrieved excerpts with their `SourceRef`s. Inactive items were not sent. The receipt's token counts match §3.3's limits |
| G2.2 | **Never cut.** With `always` items totalling more than 8,000 tokens, the request is refused with the message naming the largest items, and the fake received nothing. With a long history, the oldest turns are dropped first and the `always` items stay byte-identical |
| G2.3 | **Inline formulas.** `round((12000/30)*15*7, 0)` returns `42000`; `12000*` and `system("x")` are refused with a reason. A scripted reply containing `[[= round((12000/30)*15*7, 0) ]]`, split across stream chunks, streams ৳42,000 and stores the formula in `HrMetaJson`; `[[= 12000* ]]` streams "⚠ গণনা করা যায়নি" and is listed as unverified. The fake received no tools on the HR route, while `/api/chat` still offers only `web_search`. On `/api/chat`, after a tool runs, only the final round's text is shown and saved |
| G2.4 | **Number check.** A scripted reply containing a formula result, an amount from the user's message, and one invented: the final event lists only the invented one, and the UI shows the warning line |
| G2.5 | **Saved, separately.** HR conversations have `Kind = hr`, appear in the HR list and not in the main sidebar; a normal chat does not appear in the HR list |
| G2.6 | **Access.** The chat refuses without the `hr` tab; `ModelPolicy` applies (`hr-assistant` naming `qwen3.8-27b-q3` gets 403 while its row allows only `gemma4:12b`) |
| G2.7 | **UI.** Ask section with clickable citations that open the right item and lines, the status line, formula results that show their formula on hover, and the "Context used" panel. Screenshots, light and dark, 360 px |
| G2.8 | **Live.** On the Mac with `gemma4:12b`, ask the four questions in §1 three times each (12 runs). Every answer cites a source. At least 10 runs use formulas for every amount (nothing unverified, and the third question's amount comes from a formula), and 0 runs show a wrong amount without the warning. Paste the answers |
| G2.9 | **JSON.** A pasted array of 50 objects becomes chunks of about 800 characters (at most 1,500), each with a `$[i]` path. A question about one object's value retrieves it in the top 3 and the answer cites the path. Invalid JSON is refused with line and column |
| G2.10 | **Other formats.** One `.csv` (header repeated in every chunk, row ranges in `SourceRef`), one `.pdf` (page in `SourceRef`), one `.xlsx` (sheet in `SourceRef`), uploaded together in one action. `.docx` is refused with "save it as PDF first" |
| G2.11 | **Hybrid search.** The query `EXL` finds the attendance-code chunk at rank 1 through the keyword side even when the vector side ranks it below 10 (show both ranks). Editing and deleting an item updates the FTS5 table (row counts before and after) |
| G2.12 | **Always cap.** Switching an item to `always` that would take the total over 8,000 tokens is refused with the message; the chat is never refused for budget with a library built through the UI |
| G2.13 | **Scale.** Upload 200 generated items (about 2 MB of text). The Library stays responsive while they index in the background (progress shown); `/api/hr/search` stays under 1 second (median of 20 queries); a failed embed shows Retry and recovers |
| G2.14 | **Speed and cache.** Live on the Mac with `gemma4:12b` warm: the status line arrives within 1 second, and the second question in a conversation reaches the first word in under 15 seconds (median of 5; Phase 2 decision, it was 5). The always prefix was reused, judged by evaluation time: Ollama's MLX runner reports the whole prompt in `prompt_eval_count` even when it reused the cache, so the tokens actually evaluated are `prompt_eval_duration` × the cold rate. The fake shows the system message byte-identical across turns, the excerpts only in the last user message, and at most 5 excerpts within 1,500 tokens |
| G2.15 | **Instant search and scoped ask.** `/api/hr/search?q=gratuity` returns ranked chunks with snippets; "Ask about this" sends a question whose retrieval used only that item (check at the fake) |
| G2.16 | Builds clean; `hr-eval --phase 2`; the other suites unchanged |

---

## 6. Phase 3 — Golden set and live run

KAKAROT and HR supply `scripts/hr/golden.json`: 30 questions, each with the facts a correct answer must contain (amounts, a rule, the right source) or the expected "not in the context" reply. Half are in Bengali.

| Kind | Count | Example |
|---|---|---|
| Rules | 10 | "Termination benefit for Non-Management after 6 years?" |
| Worked calculations from given figures | 10 | "Basic 15,000, 11 years 7 months, 250 fraction days, retrenchment, notice 30 days: total?" |
| Procedure details | 5 | "From which date are earned-leave days counted after a leave payment?" |
| Not covered or must refuse | 5 | "What is employee 115978's settlement?" (no data: ask for the figures); "Change his joining date"; a policy the library does not contain |

`scripts/hr/run-golden.mjs` posts each question to `/api/hr/chat` as `hr-assistant`, checks the facts, and writes `results/hr-golden-<model>.md` and a summary line.

| Gate | Check |
|---|---|
| G3.1 | `golden.json` has HR's sign-off name and date in its header |
| G3.2 | With `gemma4:12b`: at least 26 of 30 correct, all 5 "not covered" cases correct, **0 unverified amounts**. Paste the summary and every failure |
| G3.3 | The same with `qwen3.8-27b-q3`; recommend one, with time to first word and total time per answer for both |
| G3.4 | Each failure is classified: missing or wrong context (an edit list for HR), retrieval missed the right chunk, or model error. Context problems are fixed in the library, not in code |

---

## 7. Do not

- Do not connect to CoreERP or any SQL Server, or add a package for it.
- Do not execute, parse into queries, or "simulate" a procedure file. It is text for the model to read.
- Do not let the model answer company policy from general knowledge when the library is silent.
- Do not cut an `always` item to fit the window; refuse instead.
- Do not show HR conversations in the main sidebar, or other users' chats in the HR list.
- Do not put the `hr-assistant` secret in a committed file.
- Do not push, merge to `main` or open a pull request.

---

## 8. Eval harness

`scripts/hr-eval.mjs`, Node 18+, no dependencies.
- Reuse the shared fake Ollama (chat, embeddings, tags) and API start-up, with a throwaway SQLite file.
- Fixtures: a short rules document, a code table, and a trimmed copy of `ProcessFinalSettelment.sql` that keeps the Ager Formula comment and the Service Benefit section.
- `--live` uses the real Ollama for G1.6, G2.8, G2.14 and Phase 3.
- Output `id | check | PASS/FAIL | detail`, a summary line, non-zero exit on any FAIL, `--phase N`, `--only <id>`.

---

## 9. Report format (end of every phase)

```
## Phase N report — <title>
Summary: <2–3 lines>
Eval: <N/M passed>   first run before the code: <N/M passed>
Other suites: po-eval <N/M>, oddity-eval <N/M>, model-policy-eval <N/M>

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

## 10. Open questions for KAKAROT

Each has a default the build follows until you say otherwise.

1. **One account or a user type.** Default: a user type `HrAssistant` plus one seeded account `hr-assistant`. More HR people get their own accounts with the same type.
2. **Who edits the library.** Default: `HrAssistant` users and SystemAdmin.
3. **Approval.** Default: approval is a label only; unapproved items are still used. Say if only approved items should be sent.
4. **History.** Default: HR chats are saved, in their own list.
5. **Questions in the rules document §11.** Default: the assistant answers what the rules document says and, where §11 lists a doubt, adds "HR is reviewing this rule".