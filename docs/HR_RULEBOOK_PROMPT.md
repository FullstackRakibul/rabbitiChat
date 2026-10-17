# Mostisko — মানব সম্পদ · নিয়মাবলি (Rulebook): HR's own rule pages with a paper trail

### Evaluation-based build prompt for Claude Code

> **How to start.** Put this file at `docs/HR_RULEBOOK_PROMPT.md` and tell Claude Code:
>
> *"Follow `docs/HR_RULEBOOK_PROMPT.md`. Do **Phase 1** only, then stop and give me the Phase 1 report."*
>
> After each report: *"Continue with Phase 2."* Never let it run several phases in one go.

---

## 0. Rules of engagement

1. **One phase at a time.** Each phase ends with the report in §8. Stop and wait.
2. **A gate is PASS only with evidence**: eval output, a command and its output, a file path and line, or a screenshot. After three failed attempts on one gate, stop and report the blocker.
3. **Evals first.** Add this build's checks to `scripts/hr-eval.mjs` (`--phase R1`, `R2`, `R3`) before the code. Run them once while they fail and paste that line.
4. **The paper trail is append-only.** A saved version is never updated or deleted, by any route, for any role. Retiring a page hides it from use; it never erases history.
5. **Nothing a model writes is a rule until a person confirms it.** Generated text is always a draft. Only confirmed pages reach the Library and the Ask chat.
6. **Reuse what exists.**
   - The HR tab and its sections (`HrTab.vue`, `HrAsk.vue`, `HrLibrary.vue`, `HrItemViewer.vue`).
   - The HR library (`ContextSource` with `Kind = hr`, `HrType`, `Mode`, `Version`, `ApprovedBy`, `ApprovedAt`) and its splitter, indexer and hybrid search.
   - `HrPrompt`, `HrChatEndpoint` and the excerpt selection (`SelectExcerptsAsync`).
   - The `[[= … ]]` formulas (`HrFormulas`, `HrCalculator`) and the number check.
   - `ModelPolicy`, tab gates, the shared fake Ollama.
   - The Notes module's editor, if it fits a plain-text page; say what you reused.

   A second library, splitter, formula evaluator or chat loop is an automatic FAIL.
7. **No new packages**, NuGet or npm. The diff view is written by hand (a line-based LCS is enough). Do not push, merge to `main` or open a pull request. Work on `hr-knowledge`, and commit at the end of each phase.
8. Re-confirm every fact in §2 and report what changed.

---

## 1. What we are building

A third section in the মানব সম্পদ tab, **নিয়মাবলি · Rulebook**, next to প্রশ্ন (Ask) and লাইব্রেরি (Library).

**What it holds:** plain, human-readable rule pages, one per topic, written the way HR would write them on paper. Categories:
- Settlement
- Salary
- Company Policy
- Leave & Approval
- Attendance
- Other

**How a page is made:**
1. **Written by hand**, like a note.
2. Or **drafted from the Library**: HR names a topic, the model writes a draft from the Library's rules and procedures, with sources.
3. HR reads it, edits it, and presses **নিশ্চিত করুন · Confirm**, with a short note saying why.

**What confirming does:**
- The confirmed page becomes the **approved rule**. It is added to the Library automatically, and the Ask chat uses it **before** the raw procedure code.
- Every save, generation, confirmation and retirement is kept as a version with who, when and why. That is the paper trail. HR can compare any two versions, restore an old one as a new draft, print, or download.

```
Library (rules docs, procedures, codes)
      │  "Draft from library"  (model, sources listed)
      ▼
Rulebook page — draft v1 ─► HR edits (v2, v3…) ─► Confirm (v4, note, who, when)
      │                                              │
      │                                              ▼
      │                         Library item "Rulebook · <title>" (rules, search, approved)
      ▼                                              │
History: every version, diff, restore, print         ▼
                                    Ask cites [Rulebook: <title> v4] first
```

| Phase | Work | Why in this order |
|---|---|---|
| **R1** | Rulebook pages written by hand: categories, editor, versions, confirm, retire, history, diff, restore, print and download; confirmed pages flow into the Library | The paper trail and the approval step are the core; generation only feeds them |
| **R2** | Draft from the Library: generation with sources, review, confirm; Ask prefers confirmed pages | Needs R1's draft and confirm flow |
| **R3** | HR acceptance: real pages written, confirmed and asked about | Proves it with HR's own rules |

**Not in this build:** multi-step approval chains, e-signatures, e-mail notifications, rules that change payroll or settlement data, Word export.

---

## 2. Established facts (re-confirm, do not assume)

| # | Fact | Evidence |
|---|---|---|
| R-F1 | The HR tab has two sections, প্রশ্ন (Ask) and লাইব্রেরি (Library), with a section bar under the module switcher | `frontend/src/components/hr/HrTab.vue` (or where it lives) |
| R-F2 | Library items are `ContextSource` rows with `Kind = hr` and the columns `HrType` (rules, procedure, codes, note), `Mode` (always, search), `Version`, `ApprovedBy`, `ApprovedAt`, `SplitVersion` | `backend/Models/ContextSource.cs`, `backend/Hr/HrLibrary.cs` |
| R-F3 | Library editors are `HrAssistant` and `SystemAdmin`; anyone with tab `hr` can read | `backend/Hr/HrLibrary.cs`, the Phase 1 report of the HR module |
| R-F4 | Ask builds its prompt with `HrPrompt` (persona + always items, then history, then excerpts in the last user message) and evaluates `[[= … ]]` formulas server-side, with a number check | `backend/Hr/HrPrompt.cs`, `backend/MinimalApis/HrChatEndpoint.cs`, `backend/Hr/HrFormulas.cs` |
| R-F5 | The always budget is 8,000 tokens including the persona; excerpts are the best 5 within 1,500 tokens | `docs/HR_MODULE_PROMPT.md` §3.3 |
| R-F6 | Notes exist as a module with their own editor and API | `backend/MinimalApis/NoteEndpoints.cs`, the Notes tab component |
| R-F7 | HR answers stream through a `reactive()` bubble | commit `26539b5` |

---

## 3. The design contract

### 3.1 Data

**`HrRulePages`**

| Column | Notes |
|---|---|
| `Id` | |
| `Title` | Required, unique among pages that are not retired |
| `Category` | One of `settlement`, `salary`, `company-policy`, `leave-approval`, `attendance`, `other` |
| `Language` | `bn` or `en`; the language the page is written in |
| `Status` | `draft`, `confirmed`, `retired` |
| `CurrentVersion` | The latest version number |
| `ConfirmedVersion` | The version the Library and Ask use; null until first confirmed |
| `LibraryItemId` | The Library item created on first confirmation |
| `CreatedByUserId`, `CreatedAtUtc`, `UpdatedAtUtc` | |

**`HrRulePageVersions`** (append-only)

| Column | Notes |
|---|---|
| `PageId`, `Version` | Primary key together; versions start at 1 and go up by one |
| `Kind` | `written`, `generated`, `edited`, `confirmed`, `retired`, `restored` |
| `Body` | The full page text (Markdown, plain and readable) |
| `ChangeNote` | Required for `confirmed`, `retired` and `restored`; optional otherwise |
| `AuthorUserId`, `AtUtc` | |
| `SourcesJson` | For `generated`: the Library items and `SourceRef`s used. Otherwise empty |
| `ModelJson` | For `generated`: model, prompt hash, token counts, duration |

- **Append-only enforcement:** there is no update or delete route for versions. The repository exposes only `AddVersion`. An SQLite trigger also raises an error on `UPDATE` or `DELETE` of `HrRulePageVersions`; create it in `SchemaUpgrade` and in the EF model path for a fresh database.
- **Status changes always add a version:**
  - Confirming copies the current body into a new `confirmed` version, with the note.
  - Retiring adds a `retired` version.
  - Status changes are never silent.
- **Editing a confirmed page:** this creates a draft version on top. The page shows "Confirmed v4 · draft v5 in progress", and Ask keeps using v4 until v5 is confirmed.

### 3.2 Permissions
- **Read:** anyone with tab `hr` can read confirmed and retired pages and their history.
- **Drafts:** visible only to Rulebook editors.
- **Editors:** `HrAssistant` and `SystemAdmin`. They create, edit, generate, confirm, retire and restore.
- **Server-side:** every route checks this; the UI only hides buttons.

### 3.3 Confirmed pages flow into the Library
- **First confirmation:** creates one Library item:
  - `Kind = hr`, `HrType = rules`, `Mode = search`;
  - name `Rulebook · <Title>`;
  - `ApprovedBy` and `ApprovedAt` from the confirmation.
- **Its content** is the confirmed body with a header line: `Rulebook · <Title> · v<N> · confirmed by <user> on <date> · category <Category>`.
- **Each new confirmation** replaces the item's content, re-splits and re-indexes it, and raises its `Version`.
- **Retiring** deactivates the item, so it is never sent again; the page and history stay.
- **Drafts never reach the Library.**
- **The Library shows these items with a "Rulebook" badge and no edit button.** They are edited only from the Rulebook, so the paper trail can't be bypassed.

### 3.4 Generation from the Library (Phase R2)
`POST /api/hr/rulebook/generate`, editors only, model through `ModelPolicy`.

**Input:** title, category, language, an optional list of Library items to draw from, and an optional instruction ("focus on Non-Management").

**Retrieval:** the existing hybrid search, with a larger budget for drafting: the best 12 excerpts within 5,000 tokens, plus every active `always` item. If the request names Library items, only those are searched.

**The draft always uses this template, in the chosen language:**
```
# <Title>
**বিভাগ / Category:** …   **প্রযোজ্য / Applies to:** employee types, categories, statuses

## উদ্দেশ্য · Purpose
## নিয়ম · Rules            (numbered, one rule per line)
## হিসাব · Calculation      (formula in words, then a worked example with [[= … ]])
## ব্যতিক্রম · Exceptions
## পর্যালোচনাধীন · Under review   (every ⚠ item from the sources, kept as ⚠)
## উৎস · Sources            (the SourceRefs used)
```

**Rules for the model:**
- Use only the excerpts.
- Never invent a rule, date or number.
- Write "এই বিষয়ে লাইব্রেরিতে তথ্য নেই · Not in the library" under a heading the sources do not cover.
- Every ⚠ item in the sources stays ⚠.

**After generation:**
- The draft streams into the editor with the status line, exactly as Ask does.
- The number check runs on it, and unverified amounts are highlighted in the editor.
- It is saved as a `generated` version with `SourcesJson` and `ModelJson`.
- It is never confirmed automatically.

### 3.5 Ask uses confirmed pages first
- **Precedence** in the persona: confirmed Rulebook pages over rules documents over procedure code. When a Rulebook page and the code disagree, the answer gives the Rulebook rule and says the code does something else.
- **Citation format:** `[Rulebook: <Title> v<N>]`. Clicking one opens the page at that version.
- **Pinned retrieval:** when a confirmed page's title or category matches the question (keyword search on titles), its best chunk is always among the 5 excerpts.

### 3.6 UI — নিয়মাবলি · Rulebook
- **Section bar:** প্রশ্ন Ask · লাইব্রেরি Library · **নিয়মাবলি Rulebook**, in the empty space to the right of Library.
- **List:**
  - grouped by category;
  - each page shows title, status chip (খসড়া Draft, নিশ্চিত Confirmed, বাতিল Retired), confirmed version, who confirmed it and when;
  - search by title and text; filter by category and status.
- **Page view:** reads like a printed page, with large readable type, numbered rules, and the worked example shown with its formula on hover, as in Ask.
- **Editor:** plain Markdown in a text area with a live preview. Save asks for an optional note. "Unsaved changes" warns before leaving.
- **Actions** (editors only):
  - নতুন নিয়ম New;
  - লাইব্রেরি থেকে খসড়া Draft from library;
  - সংরক্ষণ Save;
  - নিশ্চিত করুন Confirm, which asks for a required note and shows a summary of what changed since the last confirmation;
  - বাতিল Retire, which asks for a required note;
  - পুনরুদ্ধার Restore, on a version in History.
- **History panel:** every version with kind, author, time and note.
  - Selecting two versions shows a line diff: added lines highlighted, removed lines struck through, using theme tokens.
  - "Restore" creates a new draft from the chosen version.
- **Print and download:**
  - A print view (browser print) with a header: title, category, version, confirmed by and date, and a footer: "Mostisko · মানব সম্পদ · printed <date>".
  - A Markdown download of the page or of a chosen version.
  - "Export all confirmed" downloads one Markdown file with every confirmed page, grouped by category.
- **Style:** Bengali labels with English in smaller text. Theme tokens only, light and dark, usable at 360 px.

---

## 4. Phase R1 — Hand-written pages, paper trail, Library sync

**Do:** §3.1–§3.3 and §3.6, without generation.

**Gates**

| Gate | Check |
|---|---|
| GR1.1 | **Section.** The Rulebook section shows next to Library; the list is grouped by category with status chips; search and filters work. Screenshots, light and dark, 360 px |
| GR1.2 | **Append-only.** Create a page and save it 3 times: 3 versions, each body exactly as saved. A direct `UPDATE` and `DELETE` on `HrRulePageVersions` fail with the trigger's error, on a fresh database and on an upgraded copy. No route updates or deletes a version (list the routes) |
| GR1.3 | **Confirm.** Confirm without a note → 400. With a note: a `confirmed` version is added, the page shows Confirmed vN, and one Library item `Rulebook · <Title>` exists with the header line, `HrType = rules`, `Mode = search`, approved fields set, and chunks indexed (vector and keyword counts) |
| GR1.4 | **Edit after confirm.** Editing adds a draft version; the Library item and Ask still use the confirmed version (its content is unchanged byte for byte) until the next confirmation, which updates the item and raises its version |
| GR1.5 | **Retire.** Requires a note; adds a `retired` version; the Library item is inactive and no longer retrieved (search for its unique phrase → 0 hits); the history is still readable |
| GR1.6 | **History, diff, restore.** The diff of v1 and v3 marks exactly the changed lines; restoring v1 adds a `restored` draft whose body equals v1's |
| GR1.7 | **Permissions.** A user with tab `hr` but not an editor sees confirmed and retired pages only, and gets 403 on create, save, confirm, retire, restore and on reading drafts. Editors can do all of them. The Rulebook's Library item has no edit button and its edit route returns 409 "edit it in the Rulebook" |
| GR1.8 | **Print and download.** The print view has the header and footer (screenshot of the print preview); the Markdown download equals the version's body plus the header line; "Export all confirmed" contains every confirmed page and no draft |
| GR1.9 | **Ask uses it.** After confirming a page with a unique rule ("Fixture rule: a test allowance of 777 Tk"), Ask answers a question about it citing `[Rulebook: <Title> v<N>]` (fake and live) |
| GR1.10 | `dotnet build` 0 warnings, `npm run build` clean; `hr-eval --phase R1` summary and the red line; the other suites unchanged |

---

## 5. Phase R2 — Draft from the Library

**Do:** §3.4 and §3.5.

**Gates**

| Gate | Check |
|---|---|
| GR2.1 | **Draft only.** Generating "Retrenchment benefits" streams a draft with every template heading in order; it is saved as a `generated` version with `SourcesJson` and `ModelJson`; the page stays Draft; the Library's item count and content are unchanged |
| GR2.2 | **Sources.** The fake received the always items and at most 12 excerpts within 5,000 tokens. With two Library items picked, it received excerpts from those two only. The Sources heading lists the `SourceRef`s used, and each opens the Library item at its lines |
| GR2.3 | **Not invented.** With a fixture library missing a topic, the draft writes "Not in the library" under that heading instead of a rule. Every ⚠ line in the excerpts appears under Under review |
| GR2.4 | **Numbers.** `[[= … ]]` formulas in the draft are evaluated; an invented amount is highlighted as unverified in the editor and listed before Confirm |
| GR2.5 | **Precedence.** With a fixture where a confirmed page and a procedure excerpt disagree, Ask gives the page's rule, cites the page, and says the code differs |
| GR2.6 | **Pinned retrieval.** A question naming a confirmed page's title gets that page's best chunk among the 5 excerpts, even when vector and keyword ranks alone would leave it out |
| GR2.7 | **Live.** On the Mac with `gemma4:12b` and the real library, generate "রিট্রেঞ্চমেন্ট সুবিধা" in Bengali and "Earned leave at settlement" in English. Paste both drafts, their source lists, and the time to the first word and to the end. No invented rule (compare against `HR_SETTLEMENT_RULES` §8.2 and §5) |
| GR2.8 | Builds clean; `hr-eval --phase R2`; the other suites unchanged |

---

## 6. Phase R3 — HR acceptance

KAKAROT and HR do this with Claude Code's help; Claude Code records the results.

| Gate | Check |
|---|---|
| GR3.1 | HR writes or generates and confirms at least 5 pages: Service benefit, Retrenchment, Earned leave at settlement, Stamp fee and payment mode, and one hand-written page the Library cannot supply (for example a Leave Approval rule) |
| GR3.2 | Each confirmed page has a confirmation note and at least one edit by HR after generation (the history shows it) |
| GR3.3 | The test questions in `scripts/hr/golden.json` (or the 15 test questions used so far) are asked again: answers on those topics cite the Rulebook pages, with no unverified amounts |
| GR3.4 | "Export all confirmed" produces the HR rulebook file; KAKAROT confirms it reads well printed |
| GR3.5 | A list of what HR changed from the drafts, as a guide to improve the generation template |

---

## 7. Do not

- Do not update or delete a version, or add a route that could.
- Do not let a generated draft reach the Library or Ask without a person confirming it.
- Do not let the Rulebook's Library items be edited from the Library.
- Do not store the rule pages in Notes or in `ContextSource` alone. The Rulebook tables are the source of truth, and the Library item is a copy.
- Do not add packages, push, merge to `main` or open a pull request.

---

## 8. Report format (end of every phase)

```
## Phase RN report — <title>
Summary: <2–3 lines>
Eval: <N/M passed>   first run before the code: <N/M passed>
Other suites: hr-eval (phases 1–2) <N/M>, po-eval <N/M>, oddity-eval <N/M>, model-policy-eval <N/M>

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

## 9. Open questions for KAKAROT

Each has a default the build follows until you say otherwise.

1. **Who confirms.** Default: any editor, including the page's author. Say if a second person must confirm (two-person approval).
2. **Categories.** Default: the six in §1, fixed in config. Say if HR should be able to add categories.
3. **Language.** Default: one language per page, chosen when it is created. Say if a page needs both Bengali and English side by side.
4. **Old rules documents.** Default: `HR_SETTLEMENT_RULES` and the guides stay in the Library. Once Rulebook pages cover their topics, HR may switch them off.
