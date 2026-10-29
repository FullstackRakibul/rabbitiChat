# Mostisko — Executive HRIS Workspace: one simple module on top of মানব সম্পদ and Matrix

### Evaluation-based build prompt for Claude Code

> **How to start.** Put this file at `docs/HRIS_WORKSPACE_PROMPT.md` and tell Claude Code:
>
> *"Follow `docs/HRIS_WORKSPACE_PROMPT.md`. Do **Phase W1** only, then stop and give me the Phase W1 report."*
>
> After each report: *"Continue with Phase W2."* Never let it run several phases in one go.

---

## 0. Rules of engagement

1. **This module is a new front door, not a new engine.** Everything it does is done by code that already exists in মানব সম্পদ (`hr`) and Matrix (`matrix`). The new code is only:
   - a tab;
   - a question router;
   - pinned items;
   - a few thin endpoints.
2. **Do not damage what works.**
   - The `hr` and `matrix` tabs, their routes and their behaviour stay exactly as they are.
   - `hr-eval` and `matrix-eval` (and every other suite) must give the **same results before and after** each phase.
   - When existing logic must be called from a new place, move it into a service **without changing what it does**, and prove it with those suites.
3. **Reuse, never copy.**
   - Vue components from the HR and Matrix tabs are **imported**, not duplicated.
   - These are reused, never written a second time: the chat loop, number check, `[[= … ]]` formulas, `MatrixQuery`, the KPI library, ECharts wrapper, viewer, exports, RBAC catalog and `ModelPolicy`.
   - A second copy of any of these is an automatic FAIL.
4. **No new packages,** NuGet or npm. ECharts and ClosedXML are already there; PDF comes from the browser's print.
5. **Stay on .NET 8.** No framework upgrade.
6. **Evals first.** Write `scripts/hris-eval.mjs` and each phase's checks before the code, using the shared fake Ollama. Run them once while they fail and paste that line.
7. **A gate is PASS only with evidence:** eval output, a command and its output, a file and line, or a screenshot. After three failed attempts on one gate, stop and report the blocker.
8. **No real personal data** in fixtures, reports or commits. Matrix fixtures stay synthetic; real files stay in `matrix-samples/`.
9. **Branching.** Work on a branch `hris` from the current integration branch, and commit at the end of each phase. Do not push, merge to `main` or open a pull request.
10. **Feature flag.** `Hris:Enabled` (default `true` in development, `false` until KAKAROT turns it on in production). When off, the tab and `/api/hris/*` don't exist.

---

## 1. What we are building

A module named **Executive HRIS Workspace** (tab key `hris`; label **এইচআর ওয়ার্কস্পেস · HRIS Workspace**) for DMD Sir, HR managers and admins. It has three tabs:

| Tab | For | Built from |
|---|---|---|
| **রিপোর্ট · Reports** | Meetings and quick reviews | Matrix dashboards, widgets, report cards and summaries, shown as a gallery of **pinned** items with one filter bar |
| **প্রশ্ন · Ask** | Any question, in Bengali or English | One input box. A **router** sends policy questions to the HR chat and data questions to the Matrix chat, both unchanged |
| **ডেটা ও নিয়ম · Data & Rules** | HR keeping things up to date | The Matrix upload and quality screens and the HR Rulebook, embedded as they are |

```
                    ┌──────────── HRIS Workspace (tab hris) ────────────┐
                    │  Reports        Ask                  Data & Rules  │
                    └────┬────────────┬──────────────────────┬──────────┘
   pins (MatrixQuery,    │            │ router               │ embedded components
   card, summary, answer)│     policy ┌┴┐ data               │
                         ▼            ▼  ▼                    ▼
                 Matrix query/KPIs  HR chat  Matrix chat   Matrix Data · HR Rulebook
                 (unchanged)        (unchanged services, called through interfaces)
```

| Phase | Work | Why in this order |
|---|---|---|
| **W1** | Tab, roles, permissions, flag; the Reports tab with pins from existing Matrix dashboards, the filter bar and exports | DMD Sir gets value from day one, from data that already exists |
| **W2** | Ask: the router, both chats behind one box, quick actions, the settlement form, "Pin to Reports", the Data used drawer | Needs W1's pins and permissions |
| **W3** | Data & Rules hub (embedded), polish, mobile, speed | Reuses finished screens; mostly wiring |
| **W4** | UAT with HR and DMD Sir | Proves it with the real people |

**Not in this build:** new KPIs, new HR rules logic, a new chat model, Procedure Analyzer changes, scheduled e-mails, answers that mix policy and data in one reply (see §3.4).

---

## 2. Established facts (re-confirm, do not assume)

| # | Fact | Where |
|---|---|---|
| W-F1 | HR chat: `POST /api/hr/chat`, `HrPrompt` (fixed order), `HrFormulas`/`HrCalculator`, `HrNumberCheck`, `Conversations.Kind = hr`, citation chips, Context used panel | `backend/Hr/*`, `HrChatEndpoint.cs` |
| W-F2 | Matrix chat: `POST /api/matrix/chat`, `MatrixChat` plan → query → answer, `[R1]`/`[C3]`/`[KPI:]` chips, Data used panel, `Kind = matrix` | `backend/Matrix/MatrixChat.cs`, `MatrixApis.cs` |
| W-F3 | `MatrixQuery` validator and SQL builder; `MxDashboard` widgets are `MatrixQuery` + display options; `MxReportCard`, `MxSummary` exist | `backend/Matrix/*` |
| W-F4 | Exports: chart PNG (ECharts), dashboard print view, dataset `.xlsx`/`.csv` (ClosedXML), honouring sensitivity | Matrix M3 |
| W-F5 | RBAC: permission catalog with `tab:`, `section:`, `action:` keys; role presets editable in the UI; `AccessPolicy` | RBAC P1 |
| W-F6 | HR glossary item (type `glossary`, Bengali ↔ English), `FieldCatalog` synonyms and KPI names (bn/en) | HR library, `FieldCatalog.cs`, `KpiLibrary.cs` |
| W-F7 | Rulebook pages and Matrix upload/quality are Vue components inside `HrTab` and `MatrixTab` | `frontend/src/components/hr/*`, `components/matrix/*` |

If HR chat logic lives inside the endpoint rather than a service, W1's first task is to extract `IHrChatService` with **zero behaviour change**, proven by an unchanged `hr-eval`.

---

## 3. The design contract

### 3.1 Roles and permissions

**A new role `Executive`** for DMD Sir and other top management:
- tab baseline `hris` and `settings`;
- model policy `gemma4:12b`.

| Permission key | Executive (DMD Sir) | HrAssistant (HR manager) | SystemAdmin |
|---|---|---|---|
| `tab:hris` | ✔ | ✔ | ✔ |
| `section:hris.reports` | ✔ | ✔ | ✔ |
| `section:hris.ask` | ✔ | ✔ | ✔ |
| `section:hris.hub` | — (read-only status card only) | ✔ | ✔ |
| `action:hris.pin` (add, remove and arrange pins on the shared board) | — (personal pins only) | ✔ | ✔ |
| `action:hris.export` | ✔ | ✔ | ✔ |
| `action:hris.debug` (timings, tokens, route reasons) | — | — | ✔ |
| `action:matrix.view-salary` | **owner decides** (§9) | — | ✔ |
| `action:matrix.view-personal` | — | ✔ | ✔ |

- **Under the hood, the existing permissions still apply.** `hris` sections never widen access. A data question still goes through Matrix's salary and personal checks, and the Hub's upload still needs `action:matrix.upload`; the Rulebook's edit rules still apply.
- **Presets:** add `Executive` to the RBAC presets; the presets are editable in the UI as today.
- **The Procedure Analyzer** stays in the `hr` tab. SystemAdmin gets a link to it from the Hub.

### 3.2 Reports tab (রিপোর্ট)

**A pin** is a saved reference, not a picture, so it re-renders live with the filters.

| Pin kind | Stores | Shows |
|---|---|---|
| `chart` | a `MatrixQuery` + display options (same shape as an `MxDashboard` widget) | live chart with the current filters |
| `kpi` | a KPI key + scope | a KPI tile with threshold colour and change from the previous snapshot |
| `card` | dataset kind (`current:manpower`, `current:attendance-daily`…) | the latest report card of that kind |
| `summary` | dataset kind + language | the latest saved summary, with its citations |
| `answer` | a frozen copy of a chat answer (text, citations, Data used, date) | the answer **as of its date**, marked "উত্তরের তারিখ · Answered on <date>" |

- **Table:** `HrisPins(Id, Board ('shared' or 'personal'), OwnerUserId, Kind, Title (bn/en), PayloadJson, Order, Size, CreatedBy, CreatedAt, UpdatedAt)`.
- **Two boards:**
  - **Shared board** (default view), curated by HR (`action:hris.pin`). This is what DMD Sir opens.
  - **My pins**, personal to each user.
- **Seeding:** on first enable, the shared board is filled from the existing automatic Matrix dashboards. There are 8 starter pins:
  - active headcount;
  - headcount by unit;
  - sewing lines and operators per line;
  - female %;
  - attrition trend;
  - today's attendance and late %;
  - the latest manpower card;
  - the latest summary.

  HR can then change them.
- **Filter bar** (sticky at the top) with unit, date range, department and section, line type, and line.
  - Filters are added to each `chart` and `kpi` pin's query **through the existing validator**.
  - If a pin's dataset has no such field, the pin shows a small "এই ফিল্টার প্রযোজ্য নয় · filter not applicable" badge and stays unfiltered.
  - Filters are kept in the URL, so a filtered view can be shared.
- **Each pin** shows its title, "as of <report date>" and its source dataset. Clicking a chart part opens the existing viewer with the rows highlighted.
- **Unavailable data:** if a pin's KPI can't be computed (for example attendance % without a matching master), the pin says so with the reason from the report card. It never shows a substitute number.
- **Exports** (`action:hris.export`, all existing code):
  - one pin → `.xlsx` (the rows behind it) and PNG;
  - the whole board → PDF through a print view (header: board name, filters, date, "Mostisko · HRIS Workspace");
  - the whole board → `.xlsx` with one sheet per pin.

  Sensitivity rules are unchanged: no sensitive columns, and no salary or personal columns without permission.
- **Layout:** a responsive grid (1 column on phones, 2 on tablets, 3 on desktops). Drag to reorder (editors only); sizes S, M and L. DMD Sir's phone view is the first design target.

### 3.3 Ask tab (প্রশ্ন): one box, two engines

**Router** (`HrisRouter`, backend), deciding **policy**, **data** or **unclear** in two steps:

1. **Deterministic first** (no model, under 10 ms), using vocabulary that already exists:
   - *policy* terms: the HR glossary (both languages), Rulebook page titles and categories, HR library item names;
   - *data* terms: `FieldCatalog` synonyms, KPI names (bn/en), unit, section and line names from the current datasets, and words like "কতজন / how many / % / trend / today".
   - Score both sides. A clear winner (score ≥ 2 and at least twice the other side) decides.
2. **Otherwise one small schema-forced call** (temperature 0, `think: false`, `format` = `{"route": "policy|data|unclear", "reason": "…"}`). `unclear` → ask the user with two buttons: "নিয়ম/নীতি · Policy" and "ডেটা · Data".

**Answering:** the chosen route calls the **existing** service: `IHrChatService` or `MatrixChat`, with the same prompt, formulas, number check and citations. The conversation is saved with `Kind = hris` and each message keeps its route.

- **Route chip** on every answer: "উত্তর: HR নিয়ম থেকে · Answered from HR rules", or "… Matrix ডেটা থেকে · from Matrix data". Tapping **"অন্যভাবে উত্তর দিন · Answer from the other side"** re-asks through the other route. Wrong routes are logged for tuning.
- **Mixed questions** (policy and data in one, for example "settlement total for all Left employees in line 143") are **not merged in v1**. The router picks the main side, and the answer ends with a one-line offer to ask the other part. This keeps every number traceable to one engine.
- **Quick actions:** chips under the box from `backend/config/hris-actions.json` (label bn/en, route, prompt or form). Starter set:
  - "আজকের লেট % · Today's late %" (data);
  - "ইউনিটভিত্তিক জনবল · Headcount by unit" (data);
  - "সবচেয়ে কম অপারেটর যে লাইনে · Line with fewest operators" (data);
  - "এই মাসের অ্যাট্রিশন · Attrition this month" (data);
  - "সার্ভিস বেনিফিট নিয়ম · Service benefit rule" (policy);
  - **"সেটেলমেন্ট হিসাব · Settlement calculator"** (form).
- **Settlement calculator form:**
  - **Fields:** employee type, category, separation status, basic (or gross + structure), joining and separation dates (or years, months and fraction days), notice days and IsWaive, earned-leave balance.
  - **Submitting** sends one composed question with all the figures to the HR route, so the existing `[[= … ]]` formulas and number check do the maths.
  - **Missing required fields** are asked for in the form, not by the model.
  - **No employee lookup:** it takes typed figures only.
- **Answers can carry charts:** a data answer with a breakdown shows the existing inline chart.
- **"রিপোর্টে পিন করুন · Pin to Reports"** works on any chart (as a `chart` pin with its query) or answer (as an `answer` pin).
- **Data used drawer** (collapsible) uses the existing panels: for data, the queries in plain words, datasets, report dates and row counts; for policy, the library excerpts and Rulebook pages with their versions. Citations open the viewer or the library item as they do today.
- **Speed:**
  - the status line within 1 s;
  - a warm-up request (existing pattern) when Ask opens;
  - measure the time to the first word when switching between routes (the two prompt prefixes may evict each other's cache) and report it (§4, G-W2.7).

### 3.4 Data & Rules tab (ডেটা ও নিয়ম)

- **The existing components, embedded:** Matrix upload → profile → mapping → import → quality, and the HR Rulebook list, editor, confirm and history.
- **Same permissions, same APIs, no forks.**
- **Data status card** at the top, also shown read-only to Executives:
  - the latest report date per dataset kind;
  - open quality issues;
  - KPIs currently unavailable and why;
  - the last Rulebook confirmation.
- **"Three clicks" path** for a known layout: drop the file → import (template matched automatically) → the shared board's pins update. Show "Board updated with <file>, report date <date>".
- **SystemAdmin only:** links to the Procedure Analyzer and the HR Library in the `hr` tab.

### 3.5 Backend surface (new, thin)

| Route | Purpose | Permission |
|---|---|---|
| `GET /api/hris/pins?board=` | List pins | `section:hris.reports` |
| `POST/PUT/DELETE /api/hris/pins…`, `PUT /api/hris/pins/order` | Manage pins | shared: `action:hris.pin`; personal: owner |
| `POST /api/hris/pins/render` | Run pins with filters (batch; reuses `MatrixQuery`) | `section:hris.reports` |
| `POST /api/hris/ask` | Route, then stream from the existing chat service | `section:hris.ask` + the route's own checks |
| `GET /api/hris/status` | Data status card | `tab:hris` |
| `GET /api/hris/export/{kind}` | Board or pin export (reuses the existing exporters) | `action:hris.export` |

Everything else is called through existing services. Add **no** new routes under `/api/hr` or `/api/matrix`.

### 3.6 UI style
- Bengali labels with English in smaller text.
- Theme tokens only, light and dark; usable at 360 px; no horizontal page scroll.
- The module switcher shows "HRIS Workspace" for roles that have it.

---

## 4. Phases and gates

### W1 — Shell, RBAC and Reports

| Gate | Check |
|---|---|
| G-W1.1 | **No damage.** `hr-eval`, `matrix-eval`, `rbac-eval`, `po-eval`, `oddity-eval` and `model-policy-eval` give identical pass counts before and after (paste both runs). `git diff` shows no change to existing route handlers' behaviour; any extracted service is listed with the suite that proves it unchanged |
| G-W1.2 | **Flag.** With `Hris:Enabled=false`, the tab is absent and `/api/hris/*` returns 404 |
| G-W1.3 | **RBAC.** An Executive sees Reports and Ask, and only the status card in the Hub; their pin-edit attempts on the shared board get 403. HrAssistant and SystemAdmin match §3.1. A General user gets 403 on every `/api/hris/*` route |
| G-W1.4 | **Seeding.** First enable creates the 8 starter pins on the shared board; a restart doesn't duplicate them |
| G-W1.5 | **Live pins.** Every chart and KPI pin's numbers equal the same query run through `/api/matrix/query` |
| G-W1.6 | **Filters.** A unit filter changes every pin that has `unit`; a pin without the field shows the "not applicable" badge; the URL restores the filters |
| G-W1.7 | **Unavailable data.** With the fixture attendance file sharing no IDs with the master, the attendance % pin shows the reason and no number |
| G-W1.8 | **Exports.** Pin `.xlsx` and PNG, board PDF (print preview screenshot) and board `.xlsx` (sheet list). No sensitive columns; no salary for a user without view-salary (list the headers) |
| G-W1.9 | **UI.** Screenshots of the Reports tab, light and dark, at 360 px and desktop, as Executive and as HrAssistant |
| G-W1.10 | `dotnet build` 0 warnings, `npm run build` clean; `hris-eval --phase W1` and the red line |

### W2 — Ask

| Gate | Check |
|---|---|
| G-W2.1 | **Router accuracy.** `scripts/hris/router-set.json`: 60 labelled questions (30 policy, 30 data, half Bengali, 10 deliberately vague). Deterministic step alone: report its decided share and accuracy. With the model step (live): ≥ 95% correct on the clear ones; every vague one is `unclear` or correct |
| G-W2.2 | **Same engines.** For 5 policy and 5 data questions, the fake Ollama receives byte-identical prompts through `/api/hris/ask` and through `/api/hr/chat` or `/api/matrix/chat` |
| G-W2.3 | **Checks intact.** The number check, formulas and citation chips behave exactly as in the source tabs (one planted invented number per route is flagged) |
| G-W2.4 | **Permissions follow the route.** An Executive without view-salary asking for payroll gets Matrix's existing refusal; a policy question is never refused for lack of Matrix permissions |
| G-W2.5 | **Switch route.** "Answer from the other side" re-asks through the other engine, and the switch is logged |
| G-W2.6 | **Settlement form.** The form refuses missing required fields. A filled form gives the same amounts as the worked examples in the calculation guide (50,947; 210,817; 327,333) through the HR route, with 0 unverified numbers |
| G-W2.7 | **Speed (live, Mac, `gemma4:12b`).** Time to first word, median of 5 each: policy after policy, data after data, policy after data, data after policy. Report all four. If switching is over 2× the same-route time, report it and propose the fix; don't change Ollama settings without KAKAROT |
| G-W2.8 | **Pin from Ask.** A chart from a data answer pinned to the shared board renders live in Reports; a pinned policy answer shows "Answered on <date>" and doesn't change |
| G-W2.9 | **UI.** Ask with chips, route chip, Data used drawer and the settlement form; screenshots, light and dark, 360 px |
| G-W2.10 | Builds clean; `hris-eval --phase W2`; all other suites unchanged |

### W3 — Hub, polish, speed

| Gate | Check |
|---|---|
| G-W3.1 | **Embedded, not copied.** The Hub imports the existing Matrix Data and Rulebook components (show the imports; `git diff` shows no duplicated component files) |
| G-W3.2 | **Three clicks.** With a known fixture layout: drop → import → the shared board shows the new report date. Count the clicks and record the time |
| G-W3.3 | **Status card.** Report dates, open quality issues, unavailable KPIs with reasons, last Rulebook confirmation; read-only for Executives |
| G-W3.4 | **Mobile.** The Reports gallery, Ask and the status card are usable at 360 px; on a phone the board loads its first pins in under 3 s on the office network (median of 5, warm server) |
| G-W3.5 | Builds clean; the whole `hris-eval`; all other suites unchanged |

### W4 — UAT with HR and DMD Sir

Run on real data in the app. Screenshots in reports must have personal data blurred.

| Gate | Check |
|---|---|
| G-W4.1 | **DMD Sir's checklist**, done unassisted on a phone, with time and any confusion recorded:<br>1. open the board;<br>2. filter by unit;<br>3. export the board as PDF;<br>4. ask one data question by chip;<br>5. ask one policy question by typing;<br>6. pin an answer |
| G-W4.2 | **HR's checklist:** upload a new month's file (three clicks), fix one quality issue, confirm one Rulebook page, rearrange the shared board, run the settlement form |
| G-W4.3 | **Golden mix:** 20 questions (10 policy from the HR golden set, 10 data from the Matrix golden set) through Ask: at least 18 right, 0 unverified numbers, 0 personal data in answers |
| G-W4.4 | **Feedback list** from HR and DMD Sir, sorted into "fix before launch" and "later". Launch = KAKAROT sets `Hris:Enabled=true` in production |

---

## 5. Eval harness

`scripts/hris-eval.mjs`, Node 18+, no new dependencies.
- It reuses the shared fake Ollama, the API start-up with a throwaway SQLite file, and the Matrix fixture generator.
- `--live` uses the Mac's Ollama and prints counts only.
- Output is `id | check | PASS/FAIL | detail`, a summary line, and a non-zero exit on any FAIL. Options: `--phase W1…W4`, `--only <id>`.

---

## 6. Do not

- Do not change the behaviour of `/api/hr/*`, `/api/matrix/*` or their tabs.
- Do not copy a component, chat loop, number check, formula evaluator, query builder or exporter. Import or call it.
- Do not merge policy and data into one model answer in v1.
- Do not let the router or any model compute a number.
- Do not widen access: `hris` permissions never bypass Matrix's salary or personal checks or the Rulebook's edit rules.
- Do not show a substitute number when a KPI is unavailable.
- Do not add packages or upgrade .NET.
- Do not put real personal data in fixtures, reports or commits.
- Do not push, merge to `main` or open a pull request.

---

## 7. Report format (end of every phase)

```
## Phase WN report — <title>
Summary: <2–3 lines>
Eval: <N/M passed>   first run before the code: <N/M passed>
Other suites (before → after): hr-eval, matrix-eval, rbac-eval, po-eval, oddity-eval, model-policy-eval

| Gate | Check | Result | Evidence |
|------|-------|--------|----------|

Facts from §2 that changed: <list, or "none">
Existing code touched (and the suite that proves no change): <list>
Files changed: <list>
Decisions made (and why): <list>
Needs KAKAROT: <questions, or "none">
```

A phase with any FAIL is not complete. Say so plainly.

---

## 8. Shipping checklist (KAKAROT)

- [ ] Create DMD Sir's account with role `Executive`
- [ ] Decide salary visibility for Executives (§9.1)
- [ ] HR curates the shared board after W1 (keep, remove or add pins)
- [ ] Load a current manpower file and a **matching** attendance and master file, so attendance % works on the board
- [ ] Run W4 with HR and DMD Sir
- [ ] Set `Hris:Enabled=true` in production

---

## 9. Open questions for KAKAROT

Each has a default the build follows until you say otherwise.

1. **Salary for DMD Sir.** Default: **off**. Executives see headcount, attendance and attrition, not payroll. Say if `Executive` should get `action:matrix.view-salary`.
2. **Bengali tab name.** Default: "এইচআর ওয়ার্কস্পেস".
3. **Board ownership.** Default: HR curates one shared board, and everyone also has personal pins. Say if DMD Sir should also edit the shared board.
4. **Mixed questions.** Default: one engine per answer in v1. Say if merged answers should be planned for v2.
5. **Quick actions.** Default: the 6 in §3.3, editable in `hris-actions.json`. Send any others DMD Sir asks for often.
