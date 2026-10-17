# Mostisko — মানব সম্পদ · প্রসিডিউর বিশ্লেষণ (Procedure Analyzer): rules and a setup list from SQL

### Evaluation-based build prompt for Claude Code

> **Before you start.** Put `hr-lookups.csv` at `docs/hr/hr-lookups.csv`. Make sure `docs/hr/procedures/` holds `ProcessFinalSettelment.sql`, `ProcessSupplementaryFinalSetelment.sql` and `GetSalaryDetails.sql`.
>
> **How to start.** Put this file at `docs/HR_PROCEDURE_ANALYZER_PROMPT.md` and tell Claude Code:
>
> *"Follow `docs/HR_PROCEDURE_ANALYZER_PROMPT.md`. Do **Phase A1** only, then stop and give me the Phase A1 report."*
>
> After each report: *"Continue with Phase A2."* Never let it run several phases in one go.

---

## 0. Rules of engagement

1. **One phase at a time.** Each phase ends with the report in §8. Stop and wait.
2. **A gate is PASS only with evidence**: eval output, a command and its output, a file path and line, or a screenshot. After three failed attempts on one gate, stop and report the blocker.
3. **Evals first.** Add this build's checks to `scripts/hr-eval.mjs` (`--phase A1`, `A2`, `A3`) before the code. Run them once while they fail and paste that line.
4. **SQL is text, never executed.** No connection to any SQL Server, no package that runs or parses SQL against a database. The analyzer reads the file. Do not add `Microsoft.Data.SqlClient`, ScriptDom or any other package.
5. **Facts before the model.** Everything that can be found by reading the code is found by deterministic C#: tables, procedure calls, conditions, literal values, dates and hard-coded employees. The model only turns code sections into plain-language rules, and every rule it writes is checked against those facts.
6. **Unknown values are never guessed.** A value compared in the code (`@EmpTypeID IN (2,3,4)`, `LeaveId=3`, `AttStatus='EXL'`) gets its meaning from the **lookup registry** (§3.3). If the registry doesn't know it, the analyzer opens a **review question** instead of inventing a meaning.
7. **Nothing becomes a rule without a person.** Extracted rules are proposals. A person accepts them, and only accepted rules can be sent to the Rulebook, as a **draft** page, which then follows the Rulebook's confirm flow.
8. **Reuse what exists.**
   - The HR tab and its sections; the Library's splitter, hybrid search and glossary.
   - The Rulebook's draft pages, versions and confirm flow.
   - `ModelPolicy`, tab gates and the shared fake Ollama.
   - The schema-forced JSON pattern from PO Studio (`format` = schema object, temperature 0, `think: false`, one repair round).

   A second Rulebook, glossary or JSON-repair loop is an automatic FAIL.
9. **No new packages.** Do not push, merge to `main` or open a pull request. Work on `hr-knowledge`; commit at the end of each phase.
10. Re-confirm every fact in §2 and report what changed.

---

## 1. What we are building

A fourth section in the মানব সম্পদ tab, **প্রসিডিউর · Procedures**, next to Rulebook. DevOps uploads a stored procedure or function as a `.sql` file, and the app produces three things:

1. **Fact sheet** (deterministic):
   - parameters;
   - tables read and written;
   - procedures called;
   - every condition on a known parameter column, with its values and lines;
   - date cut-offs;
   - uses of `GETDATE()`;
   - hard-coded employees;
   - commented-out code that was ignored.
2. **Rules list:** plain-language rules grouped by **Salary, Attendance, Leave, Settlement, Other**, each with the code lines it came from, who it applies to (types, categories, statuses, units, dates) and its formula.
3. **Process setup list:** what must be set up for the procedure to give right answers. It covers:
   - master data and parameters it depends on, for example "LeaveAllocationMaster needs a row for every category × type with LeaveID 3" or "Payroll_CurrencyConversion needs a rate for every month";
   - values the code relies on that have no meaning recorded yet;
   - risks such as hard-coded employees or date-dependent logic.

**When context is missing, the analyzer opens a review question instead of guessing.** Examples:
- "What does `LeaveId = 3` mean?"
- "What is attendance code `EXL`?"
- "Unit 19 has special late rules; what is unit 19?"

DevOps or HR answers in the app. The answer goes into the lookup registry, and the affected rules are re-checked.

```
DevOps uploads .sql (version kept, append-only)
   │
   ▼  1. Deterministic scan (C#)
Fact sheet: sections, tables R/W, EXEC calls, conditions + literals, dates, GETDATE, hard-coded IDs
   │           │
   │           └─ literal not in lookup registry ─► Review question (open)
   ▼  2. Model, one section at a time (schema JSON)
Proposed rules ─► validator: every literal and line must exist in that section's facts
   │
   ▼  3. People
Review: accept / edit / reject rules · answer questions (answers fill the registry)
   │
   ├─► Process setup list (checklist, exportable)
   └─► "Send to Rulebook" → draft page per category → Rulebook confirm flow
```

| Phase | Work | Why in this order |
|---|---|---|
| **A1** | Upload, versions, deterministic fact sheet, lookup registry, review questions, setup list (from facts only) | Facts are the ground truth that the model's rules are checked against |
| **A2** | Model rule extraction per section, the validator, the review screen, send to Rulebook, version comparison | Needs A1's facts and registry |
| **A3** | Live run on the three real procedures, measured against `HR_SETTLEMENT_RULES` v3 | Proves coverage and that nothing is invented |

**Not in this build:** running SQL, connecting to CoreERP, rewriting or fixing procedures, analysing views or triggers (functions and procedures only), languages other than T-SQL.

---

## 2. Established facts (re-confirm, do not assume)

| # | Fact | Evidence |
|---|---|---|
| A-F1 | The HR tab has three sections: Ask, Library, Rulebook | `HrTab.vue` |
| A-F2 | The HR splitter removes commented-out code (whole `--` lines with SQL keywords, and `/* … */`) and splits SQL on `--//… Start` section comments | `backend/Hr/HrSplitter.cs`, HR module G1.5 |
| A-F3 | Rulebook pages have append-only versions, a draft/confirmed/retired status and a confirm step with a note | `backend/Hr/HrRulebook.cs`, R1 report |
| A-F4 | The glossary is a Library item of type `glossary`, re-read on every question | R2 follow-ups report |
| A-F5 | PO Studio forces JSON with a schema object, temperature 0, `think: false`, and one repair round when checks fail | `backend/Extraction/PoPromptBuilder.cs`, `LlmPoFallback.cs` |
| A-F6 | Known parameter columns in these procedures: `EmpTypeID`, `EmpCategoryID`, `EmpStatusID`, `UnitId`, `PaymentModeId`, `CalculationFormulaId` (`@SalaryStractureId`), `LeaveId`, `AttStatus`, `ModeId`, `CurrencyId`. Values are compared through variables such as `@EmpTypeID`, `@UnitId`, `@EmpStatusID` | `docs/hr/procedures/*.sql` |
| A-F7 | `docs/hr/hr-lookups.csv` has 227 known values (`Column,Value,Label,Source`) for EmpTypeID, EmpCategoryID, EmpStatusID, PaymentModeId, CalculationFormulaId, SalaryHeadId, ZoneId, CompanyId and UnitId. It deliberately has **no** labels for `LeaveId`, `AttStatus`, `ModeId` or `CurrencyId` | the file |
| A-F8 | The Mac's models: `gemma4:12b` (default), `qwen3.8-27b-q3` | KAKAROT's setup |

---

## 3. The design contract

### 3.1 Uploads and versions
- **Accepted files:** `.sql`, at most 2 MB. The object name and kind (procedure or function) are read from `CREATE`/`ALTER PROCEDURE|FUNCTION [schema].[name]`; a file without one is refused with that reason.
- **Tables:**
  - `HrProcedures(Id, ObjectName, Kind, CurrentVersion, CreatedAtUtc)`;
  - `HrProcedureVersions(ProcedureId, Version, Sql, Sha256, UploadedByUserId, AtUtc, Note)`, **append-only**, guarded by the same SQLite trigger pattern as the Rulebook versions.
- **Re-uploading the same object** adds a version. An identical file (same SHA-256) is refused as "no change".
- **Each version is analysed once,** and its results are stored with the version. Re-analysis happens only when the lookup registry changes, and only for the rules the change affects.
- **Library:** the uploaded SQL also becomes a Library `procedure` item (Search). The Library copy is updated on each new version, so Ask can cite the latest code.

### 3.2 Deterministic scan (the fact sheet)
Written in C# without packages:
1. A small tokenizer that understands strings (`'…'`, `N'…'`), comments (`--`, `/* */`), brackets (`[…]`) and line numbers.
2. Pattern rules over the tokens, not regex over raw text.

Per version, the scan produces:

| Fact | Found from | Kept with |
|---|---|---|
| Sections | `--//… Start` / `--//… End` comments; otherwise top-level `IF … BEGIN … END` blocks | name, line range |
| Parameters | the header's `@name type` list | name, type |
| Tables read | `FROM`, `JOIN`, `APPLY` | table, lines |
| Tables written | `INSERT INTO`, `UPDATE`, `DELETE FROM`, `MERGE` | table, operation, lines |
| Calls | `EXEC`/`EXECUTE`; functions `[dbo].[Name](` and `dbo.Name(` | name, lines |
| Conditions on parameters | `@Var = n`, `@Var <> n`, `@Var IN (…)`, `@Var NOT IN (…)`, `@Var BETWEEN a AND b`, `@Var > n`, and `Column = n` in `WHERE`, where the variable or column maps to a known parameter column (A-F6; the mapping comes from the `SELECT @Var = Column` assignments in the same file, plus a small alias table in config) | column, operator, values, lines, section |
| Date cut-offs | date string literals compared with dates (`< '2026-03-01'`) | date, operator, lines |
| Run-date dependence | `GETDATE()`, `SYSDATETIME()`, `CURRENT_TIMESTAMP` outside comments | lines |
| Hard-coded employees | `EmpCode='…'`, `dbo.REmpId('…')`, `ReturnEmpCode(@EmpId) IN (…)`, `@EmpId = n`, `@EmpId NOT IN (…)` | value, lines |
| Thresholds | numeric literals in comparisons with variables that are not parameter columns (`@FractionWorkingDay>=240`, `@LengthYear>=5`, `@ELBalance>60`) | variable, operator, value, lines |
| Ignored code | commented-out code removed before the scan | line ranges |

### 3.3 Lookup registry and review questions
- **Registry table:** `HrLookups(Column, Value, Label, Note, Source, UpdatedByUserId, UpdatedAtUtc)`, unique on (Column, Value).
  - Seeded once from `docs/hr/hr-lookups.csv`, only for rows not present.
  - Editable in the UI.
  - Every change is logged in `HrLookupLog`, append-only.
- **Review questions** are opened by the scan for:
  - every literal compared with a parameter column that has no registry label (for example `LeaveId = 3`, `AttStatus IN ('P','LT',…,'EXL')`, `ModeId = 1`);
  - every hard-coded employee ("Why is employee 015405 set by hand?");
  - every `GETDATE()` in a business condition ("Should this depend on the run date or the separation date?");
  - every table written that the Rulebook has no rule for.
- **One question per distinct item,** listing every place it occurs across all procedures, with lines. Asking the same question twice is a FAIL.
- **Answering:**
  - A **label** answer writes the registry, closes the question everywhere, and marks affected rules "re-check".
  - A **note** answer (for the why questions) is stored and shown on the rules it affects.
  - A question can also be marked "not a business value" (for example a loop counter); it is closed and remembered.

### 3.4 Process setup list (from facts, Phase A1)
A checklist per procedure, generated by deterministic rules. Every line names its evidence lines.

- **Master data it reads.** For each table read, one line: "Must be maintained: `<table>`". For tables used with a filter on parameter columns, the line names the combination, for example "`LeaveAllocationMaster`: a row for each EmpCategoryID × EmpTypeID with LeaveID 3, latest by ActionDate".
- **Parameters it depends on.** Each parameter column with the values the code uses. Values without a registry label are marked "unknown — see question #n".
- **Units with special handling.** Each unit list (`@UnitId IN (…)`) is shown with the unit names from the registry.
- **Date cut-offs**, sorted by date, with the section each belongs to.
- **Risks:**
  - hard-coded employees;
  - run-date dependence;
  - tables written, with the operation;
  - procedures it calls, and whether those are uploaded too ("`ProcessSupplementaryFinalSetelment` — uploaded v1" or "not uploaded").

The list can be exported as Markdown and printed, with the same header and footer style as the Rulebook.

### 3.5 Rule extraction by the model (Phase A2)
- **One request per section,** model through `ModelPolicy` with config `Hr:AnalyzerModel`, default `gemma4:12b`; `think: false`, temperature 0; `format` = the schema below.
- **Prompt:** the section's code with original line numbers, that section's facts, and only the registry labels for values that occur in the section.
- **Schema, per rule:**
  ```json
  {
    "category": "salary | attendance | leave | settlement | other",
    "title": "short name",
    "applies_to": { "emp_types": [], "emp_categories": [], "statuses": [], "units": [], "from_date": null, "to_date": null },
    "condition": "plain words",
    "formula": "plain words, using the variable names",
    "lines": [from, to],
    "literals": ["values used, exactly as in the code"],
    "unresolved": ["values whose meaning was not given"]
  }
  ```
- **Validator,** run after each section:
  - `lines` lie inside the section;
  - every value in `literals` and `applies_to` occurs in the facts for those lines;
  - every number in `formula` occurs in those lines;
  - a value with no registry label must be listed in `unresolved`.
  - A rule that fails gets **one** repair round with the failures listed. A rule that still fails is kept, marked "invalid", with the reasons, and is never sent to the Rulebook.
- **Long procedures** run in the background with progress ("section 14 of 41"). One failed section doesn't stop the rest, and Retry runs only that section.
- **Re-checking:** when a registry answer arrives, only the rules whose `unresolved` list contains that value are regenerated.

### 3.6 Review and send to the Rulebook
- **Each rule has a status:** proposed, accepted, edited, rejected, invalid, or re-check.
- **Accepting or editing** needs `HrAssistant` or `SystemAdmin`; DevOps can upload and answer questions but not accept rules (§3.7).
- **"Send to Rulebook"** takes the accepted rules of one category and creates or updates a **draft** page `"<Category> — from <ObjectName> v<N>"`:
  - each rule becomes a numbered line with its condition, formula and `[<ObjectName>.sql L<a>-<b>]`;
  - unresolved items go under Under review.

  The Rulebook's own confirm flow does the rest. Nothing is confirmed here.
- **Compare versions:** after a new upload, show rules added, removed and changed by matching their sections and literals, plus the fact-sheet differences (new tables, new dates, new hard-coded IDs).

### 3.7 Roles and the UI
- **A new role `DevOps`:**
  - tab baseline `hr` and `settings`, with model-policy row `gemma4:12b`, seeded like `HrAssistant`;
  - DevOps sees only the Procedures section of the HR tab;
  - DevOps can upload, see results, answer questions and edit the registry.
- **`HrAssistant`** sees Procedures read-only, plus answering questions and accepting rules.
- **`SystemAdmin`** can do everything.
- **Section bar:** Ask · Library · Rulebook · **প্রসিডিউর Procedures**.
- **Procedures list:** object, kind, version, uploaded by and when, analysis status, open questions count, rules by status.
- **Procedure page,** four tabs:
  - **Facts:** tables with the lines.
  - **Rules:** grouped by category, each rule beside its code lines. The code pane highlights the lines and the literals.
  - **Questions:** open first; answer inline.
  - **Setup list:** the checklist and its export.
- **Registry page:** search and edit lookups, with the change log.
- **Style:** Bengali labels with English in smaller text. Theme tokens only, light and dark, usable at 360 px.

---

## 4. Phase A1 — Upload, facts, registry, questions, setup list

**Do:** §3.1–§3.4 and the Facts, Questions and Setup tabs of §3.7. No model calls in this phase.

Use **content checks, not fixed line numbers**: the procedure files may be the 14 Oct or the 17 Oct version.

**Gates**

| Gate | Check |
|---|---|
| GA1.1 | **Upload and versions.** Upload `ProcessFinalSettelment.sql` → v1. The same file again → "no change". An edited copy → v2. Direct `UPDATE`/`DELETE` on `HrProcedureVersions` fail with the trigger error. A file with no `CREATE`/`ALTER PROCEDURE|FUNCTION` is refused |
| GA1.2 | **Ignored code.** The facts from `ProcessFinalSettelment.sql` contain nothing from the commented "Ager Formula" block or the commented `UnitId IN (81)` notice block; both appear under Ignored code |
| GA1.3 | **Tables and calls.** Writes: `FinalSattlementPayable` (DELETE ×2, INSERT ×1). Calls: `ProcessSupplementaryFinalSetelment`, `GetSalaryDetails`, `GetDateDurationYear`, `GetDateDurationMonth`, `GetDateDurationDay`. Reads include `HumanResource_EmployeeBasic`, `HumanResource_EmployeeSeparation`, `LeaveAllocation`, `LeaveAllocationMaster`, `Attendance_DailyAttendance`, `Employee_ContractInfo` and the three `Payroll_AnnualLeave_Payment_*` tables. Paste the full lists |
| GA1.4 | **Conditions.** At least these are found, with the right lines: `UnitId IN (1,27,45,111)`; `UnitId IN (1,27,45)`; `UnitId IN (54,55,61,90,46,47,48,91)`; `UnitId IN (36)`; `UnitId IN (66)`; `EmpStatusID = 5`; `EmpStatusID = 8`; `EmpStatusID NOT IN (2,4,11,13)`; `EmpTypeID IN (2,3,4)`; `EmpTypeID = 1`; `EmpCategoryID = 1`; `PaymentModeId = 2`; `LeaveId = 3`; `AttStatus IN ('P','LT','LTP','SD','TL','EE','NOP','EXL')` |
| GA1.5 | **Dates, run date, employees, thresholds.** Dates `2026-03-01`, `2026-06-28`, `2025-04-01`, `2025-01-01`, `2024-11-30`, `2022-07-01`. `GETDATE()` in the Left-28-days rule and in the death rule. Employees `015405` and `041870`. Thresholds include `@FractionWorkingDay >= 240`, `@ELBalance > 60`, `@ELBalance > 40`, `@NoticePeriodDeductionDay > 60`, `@NetPayableAmount > 50000` |
| GA1.6 | **Registry and questions.** After seeding, 227 rows. Questions open for `LeaveId 3`, `ModeId 1`, each unlabelled `AttStatus` code (once each, across all three procedures), each hard-coded employee, and each `GETDATE()` business use. Uploading `ProcessSupplementaryFinalSetelment.sql` adds its occurrences to the **same** `AttStatus` questions instead of opening duplicates |
| GA1.7 | **Answering.** Answering `LeaveId 3` = "Earned leave" writes the registry and the log, closes the question, and the setup list now shows "LeaveId 3 (Earned leave)". "Not a business value" closes a question and it does not reopen on re-upload |
| GA1.8 | **Setup list.** For `ProcessFinalSettelment.sql` it contains:<br>• the `LeaveAllocationMaster` line naming EmpCategoryID × EmpTypeID and LeaveID 3;<br>• the unit lists with unit names from the registry (1 Corporate Office, 27 Textile Head Office, 45 Corporate Office Tongi Zone, 111 Corporate Office Tongi-Merchant);<br>• the date cut-offs sorted;<br>• risks for the hard-coded employees and `GETDATE()`;<br>• `ProcessSupplementaryFinalSetelment` marked uploaded or not uploaded correctly.<br>The Markdown export matches the screen |
| GA1.9 | **Roles.** A `DevOps` user sees only Procedures in the HR tab and can upload and answer; `HrAssistant` can answer but not upload (403); a user without `hr` gets 403. Screenshots of each, light and dark, 360 px |
| GA1.10 | **No SQL runs.** `git grep` shows no `SqlConnection`, `SqlCommand` or SQL package; the eval runs with no database server present |
| GA1.11 | `dotnet build` 0 warnings, `npm run build` clean; `hr-eval --phase A1` summary and the red line; the other suites unchanged |

---

## 5. Phase A2 — Rules from the model, review, send to Rulebook

**Do:** §3.5, §3.6, and the Rules tab.

**Gates**

| Gate | Check |
|---|---|
| GA2.1 | **Schema and settings.** For one section, the fake received `format` = the schema object, temperature 0, `think: false`, the section's code with line numbers, its facts, and registry labels only for values in that section |
| GA2.2 | **Validator.** A scripted rule citing lines outside its section, one using a value not in the code (`EmpTypeID 6`), and one with an unlabelled value missing from `unresolved`: each fails with its reason, gets one repair round, and is marked invalid if the repair fails too. A valid rule passes |
| GA2.3 | **Background and retry.** A 40-section fixture shows progress; a section that fails (fake returns an error) is marked and Retry runs only that section (the fake's call count proves it) |
| GA2.4 | **Re-check on answer.** Answering `ModeId 1` regenerates only the rules whose `unresolved` held it (fake call count equals the number of those rules) |
| GA2.5 | **Review.** Accept, edit and reject work with the role rules in §3.6; an edit is kept as a version of the rule with who and when |
| GA2.6 | **Send to Rulebook.** Sending the accepted Settlement rules creates one **draft** page with numbered rules, line citations and an Under review list of unresolved items. The page is not confirmed, and the Library and Ask are unchanged until a person confirms it in the Rulebook |
| GA2.7 | **Compare versions.** v2 of a fixture procedure, with one new unit in a list, one changed date and one removed hard-coded employee, shows exactly those as fact differences and the affected rules as changed |
| GA2.8 | **UI.** The Rules tab shows each rule beside highlighted code lines and literals; invalid and re-check rules are visibly marked. Screenshots, light and dark, 360 px |
| GA2.9 | Builds clean; `hr-eval --phase A2`; the other suites unchanged |

---

## 6. Phase A3 — Live on the real procedures

On the Mac, with the real files and the seeded registry.

| Gate | Check |
|---|---|
| GA3.1 | Analyse `ProcessFinalSettelment.sql`, `ProcessSupplementaryFinalSetelment.sql` and `GetSalaryDetails.sql` with `gemma4:12b`. Paste for each: sections, rules by status, open questions, and time per procedure |
| GA3.2 | **Coverage.** Compare the valid rules for `ProcessFinalSettelment.sql` with `HR_SETTLEMENT_RULES` v3 §5–§10: list each rule there as found, partly found or missing. At least 80% found or partly found |
| GA3.3 | **Nothing invented.** No valid rule uses a value, date or number absent from its cited lines (the validator's log, plus a hand check of 10 random rules, pasted) |
| GA3.4 | **The same with `qwen3.8-27b-q3`** on `ProcessFinalSettelment.sql` only. Coverage, invalid count and time for both models; recommend one for `Hr:AnalyzerModel` |
| GA3.5 | **The questions list** for KAKAROT and HR to answer, grouped (lookup values, hard-coded employees, run-date use, tables written), as a printable Markdown export |

---

## 7. Do not

- Do not execute SQL, connect to a database, or add a SQL package.
- Do not let the model decide what a code value means; only the registry does.
- Do not send invalid rules, or rules that are not accepted, to the Rulebook, and never confirm a Rulebook page from here.
- Do not update or delete procedure versions or registry log rows.
- Do not open the same review question twice.
- Do not push, merge to `main` or open a pull request.

---

## 8. Report format (end of every phase)

```
## Phase AN report — <title>
Summary: <2–3 lines>
Eval: <N/M passed>   first run before the code: <N/M passed>
Other suites: hr-eval (1–2, R1–R2) <N/M>, po-eval <N/M>, oddity-eval <N/M>, model-policy-eval <N/M>

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

1. **DevOps role.** Default: a new role `DevOps` with tabs `hr` (Procedures only) and `settings`. Say if DevOps should instead be a grant on existing users.
2. **Who accepts rules.** Default: `HrAssistant` and `SystemAdmin`; DevOps proposes and answers questions only.
3. **Analyzer model.** Default: `gemma4:12b`; GA3.4 decides whether `qwen3.8-27b-q3` is worth the extra time.
4. **Library copy.** Default: each uploaded procedure also updates its Library `procedure` item, so Ask cites the newest code. Say if Ask should keep the old copy until HR approves.
