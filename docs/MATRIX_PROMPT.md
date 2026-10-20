# Mostisko — Matrix: RMG manpower and attendance analytics from Excel, with dashboards and chat

### Evaluation-based build prompt for Claude Code

> **Before you start.**
> 1. Create a folder `matrix-samples/` at the repository root, add it to `.gitignore`, and copy the five real files into it:
>    - `ADL-MASTER_FILE-HRM-Extended.xlsx`
>    - `DailyAttDetailsReport_24.xlsx`
>    - `Designation_Equivalency_List.xlsx`
>    - `HR-basic-details-from-2021.xlsx`
>    - `Manpower-HR-AF-PE.xlsx`
>
>    **They contain personal data** (names, NID numbers, phone numbers, bank accounts, salaries). They must never be committed, pasted into a report, or used as eval fixtures.
> 2. Put this file at `docs/MATRIX_PROMPT.md` and commit it.
>
> **How to start.** Tell Claude Code:
>
> *"Follow `docs/MATRIX_PROMPT.md`. Do **Phase M1** only, then stop and give me the Phase M1 report."*
>
> After each report: *"Continue with Phase M2."* Never let it run several phases in one go.

---

## 0. Rules of engagement

1. **One phase at a time.** Each phase ends with the report in §10. Stop and wait.
2. **A gate is PASS only with evidence**: eval output, a command and its output, a file path and line, or a screenshot. After three failed attempts on one gate, stop and report the blocker.
3. **Evals first.** Write `scripts/matrix-eval.mjs` and each phase's checks before the code. Run them once while they fail and paste that line. Reuse the shared fake Ollama and API start-up from the other `scripts/*-eval.mjs`.
4. **Synthetic fixtures only.** `scripts/matrix/make-fixtures.mjs` generates workbooks with the **same structure and quirks** as the real files (§2), filled with invented names, IDs and numbers. Evals run on these. Real files are used only for the `--live` gates, from `matrix-samples/`, and reports show **counts and column names only, never a person's data**.
5. **Code does the numbers; the model explains.**
   - Every count, sum, average, ratio and percentage is computed by the server (SQL built by code from a validated query, §3.6).
   - The model may propose a column mapping, a query, a summary or an answer, and is never trusted for arithmetic.
   - Every number in a model's text is checked against the computed results (the HR number check pattern).
6. **Personal data stays out of the model.**
   - Columns classified **sensitive** (§3.4) are dropped at import by default.
   - **Personal** columns (names) are never sent to the model.
   - **Salary** columns are visible only with permission.
   - The model receives aggregates and, at most, masked sample rows.
7. **Follow the project's layers** (§3.1): Models → Repositories → Services (interface + implementation) → Minimal API group → DTOs; frontend service → tab component → components. Mirror existing files rather than inventing a new style.
8. **Reuse what exists:**
   - ClosedXML for reading workbooks;
   - EF Core and SQLite;
   - `ModelPolicy`;
   - the role and tab system (`AccessPolicy` and the permission catalog if `docs/RBAC_ROLES_PROMPT.md` has been built; otherwise `TabCatalog` and the tab middleware);
   - the HR patterns: number check, `[[= … ]]` formulas with `HrFormulas`, fixed prompt order for cache reuse, saved conversations with a `Kind`, status line within 1 s, `reactive()` streaming bubble;
   - PO Studio's schema-forced JSON with one repair round.

   A second evaluator, number check or chat loop is an automatic FAIL; move shared code to a common place instead.
9. **Packages:** exactly one new npm package is allowed, `echarts` (Apache ECharts), used through a small wrapper component of our own. Nothing else, NuGet or npm. Do not push, merge to `main` or open a pull request. Work on a branch `matrix` from the current integration branch; commit at the end of each phase.
10. Re-confirm every fact in §2 and report what changed.

---

## 1. What we are building

**Matrix** is a new module (tab key `matrix`, label **ম্যাট্রিক্স · Matrix**). HR and production managers upload the Excel files they already produce: manpower lists, master files, daily attendance reports, designation lists. Matrix turns them into:

- **Clean datasets.** Tables are found inside messy sheets, columns are mapped to known fields, types are fixed, personal data is handled, and quality problems are listed.
- **Garment manpower analytics:**
  - headcount by unit, department, section and line;
  - sewing, cutting, finishing, wash and packing lines;
  - operators and helpers per line;
  - gender mix, grade structure, tenure;
  - new joiners and separations, attrition;
  - attendance and lateness;
  - overtime;
  - salary totals (with permission).
- **Dashboards.** Created automatically for each kind of report, with filters, drill-down to the rows, an editor, export and history over time.
- **A report card and summary** for every import, worked out by code and explained by the model.
- **Chat with the data,** in Bengali or English. Answers come from computed queries, and **every number cites its source**: clicking a citation opens the dataset viewer with the exact rows, columns or cells highlighted, or the KPI definition used.

```
Upload .xlsx/.csv ─► Profile (sheets, tables, header rows, types, quirks)
                  ─► Template (column → field mapping; remembered for the next file)
                  ─► Import  (per-dataset SQLite table; sensitive columns dropped; derived fields)
                  ─► Quality report
                  ─► Report card (KPIs by code) ─► Summary (model, numbers checked)
                  ─► Dashboards (ECharts)         ─► Chat: plan → query (code) → answer + citations
Snapshots by report date ─► trends, comparisons
```

| Phase | Work | Why in this order |
|---|---|---|
| **M1** | Foundation: module, permissions, layers; upload, workbook profiling, field catalog, templates, personal-data policy, import, quality report, dataset viewer with highlight | Clean, trusted data is the base of everything |
| **M2** | Analytics engine: derived fields, relations between datasets, the query language and its SQL builder, the RMG KPI library, report cards | Dashboards and chat both run on this |
| **M3** | Dashboards: automatic dashboards per report kind, filters, drill-down, editor, export, snapshots and trends | Needs M2's queries and KPIs |
| **M4** | Summary and chat: summary, plan → query → answer, citations with highlight, definitions, saved conversations, live golden set | Needs everything above |

**Not in this build:** connecting to CoreERP or any database, writing back to Excel files, scheduled e-mails, payroll calculation (Matrix reports salary figures from files; it does not compute pay), forecasting models.

---

## 2. Established facts (re-confirm, do not assume)

### 2.1 The project

| # | Fact | Evidence |
|---|---|---|
| X-F1 | Layers: `backend/Models`, `backend/Repositories`, `backend/Services` (interface + class, for example `IContextSourceService`/`ContextSourceService`), `backend/MinimalApis/*Apis.cs` (static classes mapped from `Middleware/MiddlewareDependencyInversion.cs`), `backend/DTOs`, `backend/Data/AppDbContext.cs` + `SchemaUpgrade.cs` (no migrations) | the repo |
| X-F2 | Frontend: `frontend/src/services/*.service.js` exported from `services/index.js`, one `components/tabs/<Name>Tab.vue` per module, the `components/ui` kit, theme tokens, the module switcher and System menu fed by the user's tabs | the repo |
| X-F3 | ClosedXML reads `.xlsx`; PdfPig and ClosedXML are the only document packages | `backend/RabbitIC.Api.csproj` |
| X-F4 | HR chat patterns to reuse: `HrPrompt` (fixed message order), `HrFormulas`/`HrCalculator` (`[[= … ]]`), `HrNumberCheck`, `Conversations.Kind`, status line, `HrMetaJson` on messages | `backend/Hr/*`, `HrChatEndpoint.cs` |
| X-F5 | Upload limit used elsewhere is 20 MB; the largest real file here is about 10 MB | `DocumentApis.cs`, `matrix-samples/` |
| X-F6 | The Mac runs `gemma4:12b` (default) and `qwen3.8-27b-q3`, prompt evaluation about 125–135 tokens/s, window 16,384 | earlier HR reports |

### 2.2 The real files (structure only)

| File | Sheets and shape | Quirks the importer must handle |
|---|---|---|
| **ADL-MASTER_FILE-HRM-Extended.xlsx** (≈10 MB) | `HRM` (8,514 rows × 99 columns), `HRM (2)` (8,485 × 99, an older copy), `For corrcetion` (9,051 × 101, with unnamed `Column1`/`Column2`), `unit ` (4 units: id, name, address; note the trailing space), `Sheet1` (1,168 × 30, with live formulas) | Duplicate and near-duplicate sheets of the same people; dates as text (`01-Apr-2021`); IDs as text with leading zeros in `EmpID` next to the same number without the zeros in `PunchNo`; sewing line in `WingName` (`LINE - 143`, 53 lines) under `SectionName = Sewing`; 60 sections (Sewing, Finishing, Cutting, Packing, Button Stitch…); formulas in `Sheet1` such as `=(R2-2450)/1.5` for Basic; ~40 sensitive columns (NID, birth certificate, bank account, mobile, addresses, parents' and spouse names, date of birth, religion, e-mail) |
| **DailyAttDetailsReport_24.xlsx** | one sheet, 676 people × 11 columns | Row 1 title and row 2 date range (`(19-May-2026 To 19-May-2026)`), both merged A:K; header on row 3; Emp ID as text with leading zeros; times as text with a trailing space (`07:54:20 AM `); status codes `P`, `LT`; shift codes (`G(WKR)`, `GPC`, `ABN`); OT hours and "Deduction OT" as numbers |
| **Designation_Equivalency_List.xlsx** | one sheet, 165 rows × 3 columns: designation → equivalency name → grade `M1`…`M14` | A lookup table, not a report; typos and trailing spaces in names (for example "Asssistant Architect", "Trainee Designer ") |
| **HR-basic-details-from-2021.xlsx** | one sheet, 345 rows × 12 columns | The text `NULL` for empty cells; status names including "Unauthorized Absence (UA)"; some separation effect dates are earlier than the joining date |
| **Manpower-HR-AF-PE.xlsx** | one sheet, 1,173 rows × 59 columns | English and Bengali column pairs (`UnitName`/`UnitNameBan`, …); `UnitTypeName` (Corporate, Central (G), Garment's, Washing, Woven, Knit, Yarn…); `WorkDurationDetails` as text ("25 Year(s)-1 Month(s)-10 Day(s)"); sensitive columns as in the master file; all rows Active |

**Re-profile each file yourself in M1** with the profiler you build, and report any difference from this table: counts and column names only.

---

## 3. The design contract

### 3.1 Layers and files
**Backend:**

```
backend/Models/Matrix.cs                     entities (§3.2)
backend/Repositories/MatrixRepository.cs     EF access for the entities; dataset-table DDL/DML through one class
backend/Matrix/WorkbookProfiler.cs           sheets → table blocks → header rows → column types → quirks
backend/Matrix/FieldCatalog.cs               canonical fields, synonyms, sensitivity (§3.3)
backend/Matrix/TemplateMatcher.cs            fingerprint → template; mapping suggestions
backend/Matrix/DatasetImporter.cs            typed rows → per-dataset table; derived fields; quality checks
backend/Matrix/QualityChecks.cs
backend/Matrix/MatrixQuery.cs                query model + validator + SQL builder (§3.6)
backend/Matrix/KpiLibrary.cs                 RMG KPIs as named queries (§3.7)
backend/Matrix/ReportCardBuilder.cs
backend/Matrix/MatrixChat.cs                 plan → query → answer (§3.9)
backend/Services/IMatrixService.cs, MatrixService.cs
backend/MinimalApis/MatrixApis.cs            route group /api/matrix (§3.10)
backend/DTOs/MatrixDtos.cs
```

**Frontend:**

```
frontend/src/services/matrix.service.js
frontend/src/components/tabs/MatrixTab.vue
frontend/src/components/matrix/  MatrixUpload.vue, MatrixMapping.vue, MatrixDatasets.vue, MatrixViewer.vue,
                                 MatrixQuality.vue, MatrixDashboard.vue, MatrixWidget.vue, MatrixChart.vue (ECharts wrapper),
                                 MatrixAsk.vue, MatrixDefinitions.vue
frontend/src/lib/matrix-format.js            numbers, lakh grouping, dates, Bengali digits
```

### 3.2 Data
| Entity | Key columns |
|---|---|
| `MxUpload` | file name, size, SHA-256, uploaded by and when, report date (detected or entered), status, profile JSON |
| `MxTemplate` | name, report kind (`manpower`, `master`, `attendance-daily`, `designation-lookup`, `separation-list`, `custom`), sheet and header fingerprint, column → field mapping JSON, version, created and updated by |
| `MxDataset` | upload, template, sheet, table range, row count, physical table name `mx_ds_<id>`, column list JSON (name, field, type, sensitivity, unit), report date, `IsCurrent` (latest snapshot of its kind and scope) |
| `MxQualityIssue` | dataset, check, severity, count, sample cell refs, suggested fix, state (open/fixed/ignored) |
| `MxRelation` | left dataset kind and field, right dataset kind and field, normaliser (for example "Emp ID as 6-digit text") |
| `MxDashboard` | name, report kind or dataset, layout JSON (widgets: type, query, options), owner, shared flag |
| `MxReportCard` | dataset, JSON, created at; `MxSummary`: dataset, language, text, meta JSON (formulas, citations, checks) |
| `MxKpiDefinition` | key, name (bn/en), description, formula in words, query JSON, unit, thresholds; seeded from `KpiLibrary`, editable |
| Conversations | `Kind = "matrix"`, optional dataset scope; messages keep `MetaJson` (queries, citations, checks) |

**Dataset tables:**
- Each dataset table `mx_ds_<id>` has one column per kept field, typed (`TEXT`, `REAL`, `INTEGER`, ISO date `TEXT`), plus `_row` (the source row number) and `_sheet`. That way every value can be traced back to its cell.
- Indexes are created on the dimension fields (unit, section, line, designation, status, date).
- Create and drop happen only through `MatrixRepository`. Table and column names come from code, never from user or model text.

### 3.3 Field catalog (the RMG vocabulary)
`FieldCatalog` declares canonical fields with type, role (`id`, `dimension`, `measure`, `date`, `time`, `text`), sensitivity, Bengali and English labels, and header synonyms. At least these:

| Group | Fields (synonyms in brackets) |
|---|---|
| Identity | `emp_id` (EmpID, Employee ID, Emp ID, EmpCode, PunchNo*), `apparel_emp_id`, `track_id` |
| Organisation | `company`, `unit` (UnitName, Unit Name), `unit_id`, `unit_type`, `department`, `section`, `wing`, `team`, `job_location`, `salary_company` |
| Line | `line_no` (from `wing` matching `LINE - <n>`), `line_type` (derived from section: Sewing, Cutting, Finishing, Washing, Packing, Button Stitch, Other) |
| Job | `designation`, `designation_bn`, `designation_spec`, `grade_id`, `position`, `emp_type` (EmpTypeName), `emp_category`, `equivalency`, `equivalency_grade` (from the designation lookup) |
| Status and dates | `status` (EmpStatusName), `joining_date`, `effect_date`/`separation_date`, `probation_date`, `confirm_status` |
| Person (personal) | `name`, `name_bn`, `gender`, `title` |
| Derived bands | `tenure_years`, `tenure_band` (<1, 1–3, 3–5, 5–10, 10+), `age_band` (from date of birth at import; the date itself is then dropped) |
| Attendance | `att_date`, `shift`, `in_time`, `out_time`, `ot_hours`, `ot_deduction`, `att_status` (P, LT, A, …) |
| Salary (salary) | `gross`, `basic`, `house_rent`, `medical`, `conveyance`, `food`, `other_allow`, `current_salary`, `increment_amount`, `increment_date`, `ot_rate`, `payment_mode` |
| Sensitive (dropped) | NID, birth certificate, BGMEA ID, bank account, bank/branch, mobile/phone/contact, e-mail, addresses and their parts, father's/mother's/spouse's name, date of birth (after deriving `age_band`), religion, blood group, emergency contacts, village |

`*` `PunchNo` maps to `emp_id` only through the normaliser (pad to 6 digits), and only when no `EmpID` column exists in the same table.

**Unknown headers:** suggestions use synonyms first. The model is asked only for headers left unmatched; it is given the header, 5 masked sample values and the catalog list, and answers in a schema-forced JSON. A person confirms every mapping before it is saved.

### 3.4 Personal-data policy
- **Each column gets a sensitivity:** `public`, `personal`, `salary` or `sensitive`, from the catalog. An unmapped column is `personal` until a person classifies it.
- **`sensitive` columns are not imported.** The upload record keeps only that they existed and were dropped. An admin can allow one for a template (with a note, logged); even then it is never sent to the model and never shown in dashboards.
- **`personal`** (names) is shown in the viewer only with `action:matrix.view-personal` and never sent to the model. Chat answers name groups (lines, sections), not people. A request for a person's details is answered with a link to the filtered rows, if the user may see them.
- **`salary`** is shown and queryable only with `action:matrix.view-salary`. Without it, queries touching salary fields are refused with the reason.
- **The original uploaded file is not stored after import.** Only its hash, name and profile are kept, so the master file's NID numbers never sit in the app. Re-import needs a re-upload.

### 3.5 Profiling, templates and import
- **Table blocks:** found per sheet by contiguous non-empty regions. Title rows (merged across the table, or a single text cell above the header) become report details; a date-range line becomes the report date.
- **Header row:** the first row whose cells are mostly short distinct texts above typed values. Multi-row headers are joined ("Total Machine" + "Hours").
- **Types:** decided per column from all values:
  - numbers with units or thousand separators → number + unit;
  - `NULL`, empty and `-` → null;
  - text dates (`01-Apr-2021`, `2021-10-05 00:00:00`) and Excel serials → date;
  - text times with spaces → time;
  - IDs with leading zeros stay text;
  - formula cells use their cached values, and the formula is kept in the profile.
- **Duplicate sheets:** a sheet whose rows mostly match another sheet's (by `emp_id`) is flagged "possible copy of `<sheet>`", and the user chooses which to import. Default: the one with the newest name or most rows, with `HRM` over `HRM (2)`.
- **Template fingerprint:** sheet names + normalised header texts. A matching template imports with no questions and shows "Imported with template `<name>` v<N>".
- **Quality checks** (each with count, sample cell refs and a suggested fix):
  - duplicate `emp_id` in a dataset;
  - the same person in two sheets of one upload;
  - IDs that differ only by leading zeros;
  - `NULL` text;
  - mixed date formats;
  - separation date before joining date;
  - designations missing from the equivalency lookup;
  - trailing spaces and near-duplicate category values ("House Keeping"/"Housekeeping");
  - numbers formatted as dates;
  - empty required fields (`emp_id`, `unit`, `status`);
  - formula results that disagree with their formula's inputs (for example `Basic` ≠ (`Current Salary` − 2,450) ÷ 1.5).
- **Fixes:** a fix creates a corrected column in the dataset (the raw value is kept in `_raw_<field>`) and is logged; nothing is changed silently.

### 3.6 The query language (`MatrixQuery`)
The only way anything — dashboards, KPIs, report cards, chat — reads data:

```json
{
  "dataset": "current:manpower" | "<datasetId>" | "snapshots:attendance-daily",
  "join":    [{ "dataset": "current:master", "on": "emp_id" }],
  "filter":  [["section", "=", "Sewing"], ["status", "in", ["Active"]], ["att_date", "between", "2026-05-01", "2026-05-31"]],
  "group_by": ["unit", "line_no"],
  "measures": [["count", "*", "headcount"], ["sum", "ot_hours", "ot"], ["avg", "gross", "avg_gross"],
               ["ratio", ["count_if", ["designation", "contains", "Operator"]], ["count_if", ["designation", "contains", "Helper"]], "op_helper_ratio"]],
  "sort": [["headcount", "desc"]],
  "limit": 50
}
```

- **Validator:** datasets, fields and operators come from fixed lists; fields must exist in the dataset (or its join); permissions for salary and personal fields are applied; `limit` is at most 500; at most 2 joins and 6 group-by fields.
- **SQL builder:** turns a valid query into parameterised SQLite SQL. Identifiers come only from the catalog and the dataset's column list, never from the query's text.
- **Every result carries its provenance:** the query, the datasets with their report dates, row counts, and, for each output row, a **citation token** that reopens the underlying rows in the viewer (the same filter plus the group's values).

### 3.7 RMG KPI library (seeded `MxKpiDefinition`s, editable)

| Area | KPIs |
|---|---|
| Manpower | Active headcount; by unit, department, section, line type, line, designation, grade, emp type, gender; female %; Management:worker ratio |
| Lines | Sewing lines per unit; operators per line; helpers per line; operator:helper ratio; QC (Quality Inspector, Quality Controller, Checker) per line; supervisors per line; lines below a set operator count |
| Joiners and separations | New joiners in period; separations in period by status (Left, Resignation, Removal, Death, Retirement…); attrition % = separations ÷ average active headcount; separations by tenure band |
| Tenure and grade | Tenure-band distribution; equivalency grade distribution (M1–M14); headcount without a grade mapping |
| Attendance (daily) | Present (P + LT), late (LT), late %, attendance % against active manpower of the same unit (join), OT hours total and average, people with OT above 2 h, above 4 h; shift mix; earliest in, latest out |
| Salary (permission) | Gross payroll by unit, section and line; average gross by designation and grade; increment totals |
| Quality | Open quality issues by check |

Each KPI has bn/en names, a plain-words formula (shown in Definitions), its query, unit and thresholds (green/amber/red, set by managers).

### 3.8 Report card, summary, dashboards
- **Report card** (per dataset, made at import):
  - the report kind, date and scope;
  - the main KPIs for that kind, with their values and citation tokens;
  - the top and bottom 3 of key breakdowns (for example lines with the fewest operators);
  - comparison with the previous snapshot of the same kind, if any;
  - quality issues.

  Code builds it, at 1,000–3,000 tokens.
- **Summary:** the model writes it from the card in Bengali or English:
  - 5–8 short points, each citing its card items;
  - every amount in an `[[= … ]]` formula or copied from the card;
  - the number check runs on it;
  - it is saved with the dataset.
- **Automatic dashboards,** one per report kind:
  - **Manpower overview:** KPI tiles, headcount by unit, a section donut, gender, grades, tenure.
  - **Line board:** a grid of sewing lines with operators, helpers, QC, ratio and status colour.
  - **Attendance today:** tiles, present/late by section and line, OT distribution, shift mix.
  - **Joiners and separations:** by month, by status, attrition.
  - **Data quality.**
- **Widgets:** KPI tile (with trend arrow and threshold colour), bar (target vs actual), stacked bar, line (trend), donut, gauge, heatmap (line × hour or section × day), table (conditional colours), timeline.
- **Every widget is a `MatrixQuery`** plus display options, and clicking a bar, segment or cell opens the viewer with those rows.
- **Editor:** add, remove, move and resize widgets; change chart type; rename. Bengali and English labels; light and dark; phones stack widgets in one column.
- **Filters** apply to the whole dashboard: date range, unit, section, line, emp type, status, shift.
- **Snapshots and trends:** datasets of the same kind and scope with different report dates form a series. Trend widgets and "compare with previous" use it.
- **Export:** dashboard as PNG (ECharts' own export per chart, plus a print view) and PDF through browser print; cleaned dataset as `.xlsx` (ClosedXML) and `.csv`, without sensitive columns, and without personal or salary columns unless the user may see them.

### 3.9 Chat (Ask) — plan, query, answer, cite
- **Route:** `POST /api/matrix/chat`, streamed. Saved as conversations with `Kind = "matrix"`, optionally scoped to a dataset or dashboard. Model through `ModelPolicy`.
- **Step 1, plan.** One schema-forced call returns one of:
  - `{"answer_from_card": true}`;
  - `{"queries": [MatrixQuery, …]}` (at most 3);
  - `{"definition": "<kpi key>"}`;
  - `{"need": "<what is missing>"}`.

  The plan prompt holds the persona, the field catalog of the datasets in scope, KPI names, the report card, and the question. Invalid queries get one repair round with the validator's errors.
- **Step 2, run.** The server runs the queries and applies permissions. Results are capped at 50 rows; the model gets the rows (names never included) and their citation tokens.
- **Step 3, answer.** Streamed, Bengali or English as asked:
  - every number comes from the results or an `[[= … ]]` formula;
  - every claim cites `[R1]`, `[R2]` … (result rows), `[KPI: <name>]` or `[Card]`;
  - the number check flags anything unverified.
- **Citations highlight the source.** Clicking `[R2]` opens the dataset viewer filtered to that group with the cells used highlighted. Clicking `[KPI: Attrition %]` opens its definition with the formula.
- **"Data used" panel** under each answer: the queries in plain words, datasets and report dates, row counts.
- **Chart in the answer:** when a result is a breakdown, the answer can include a chart (same widget code) with "Add to dashboard".
- **Speed:** the status line within 1 s ("খুঁজছি… ২টি প্রশ্ন চালানো হচ্ছে"); the persona and catalog are a stable prefix for cache reuse; target first word under 15 s warm.

### 3.10 API (route group `/api/matrix`, tab `matrix`)
| Route | Purpose | Permission |
|---|---|---|
| `POST /uploads` | upload and profile | `action:matrix.upload` |
| `GET /uploads/{id}/profile` | sheets, tables, columns, suggestions, quirks | section `matrix.data` |
| `POST /uploads/{id}/import` | confirm mapping and template, import | `action:matrix.upload` |
| `GET/PUT /templates…` | list and edit templates | `action:matrix.templates` |
| `GET /datasets…`, `GET /datasets/{id}/rows` | list; rows with filter and paging (the viewer), honouring sensitivity | section `matrix.data` |
| `GET /datasets/{id}/quality`, `POST /quality/{id}/fix` | issues and fixes | `action:matrix.fix` |
| `POST /query` | run a `MatrixQuery` | section `matrix.dashboards` |
| `GET/POST/PUT/DELETE /dashboards…` | dashboards | `action:matrix.dashboards.edit` for changes |
| `GET /datasets/{id}/card`, `GET /datasets/{id}/summary` | report card, summary | section `matrix.dashboards` |
| `GET/PUT /kpis…` | KPI definitions and thresholds | `action:matrix.kpis.edit` for changes |
| `POST /chat` | chat | section `matrix.ask` |
| `GET /export/{kind}/{id}` | dataset or dashboard export | section `matrix.data` |

**Permission keys** for the catalog (or, before it exists, role checks in one Matrix policy class):
- `tab:matrix`
- `section:matrix.data`, `section:matrix.dashboards`, `section:matrix.ask`
- `action:matrix.upload`, `action:matrix.templates`, `action:matrix.fix`, `action:matrix.dashboards.edit`, `action:matrix.kpis.edit`, `action:matrix.view-personal`, `action:matrix.view-salary`

**Seeds:**
- SystemAdmin: all.
- System and HrAssistant: tab and all sections, upload, dashboards edit, view-personal; not view-salary.
- Other roles: none.

### 3.11 UI (Matrix tab)
- **Sections:** **ডেটা Data** (uploads, datasets, quality), **ড্যাশবোর্ড Dashboards**, **প্রশ্ন Ask**, **সংজ্ঞা Definitions** (KPIs and fields).
- **Upload flow:**
  1. drop files (several at once);
  2. a profile screen per file: sheets, detected tables, header row, columns with field suggestions, sensitivity chips and quirks;
  3. confirm the mapping (template saved);
  4. import progress;
  5. quality report;
  6. the automatic dashboard and summary.
- **Dataset viewer:** a virtualised grid with sticky header, column filters and a **highlight mode** for citations (rows outlined, cited cells filled with the theme's accent). Sensitive columns are absent; personal and salary columns are masked without permission.
- **Style:** Bengali labels with English in smaller text, theme tokens only, light and dark, usable at 360 px, no horizontal page scroll (the grid scrolls inside its frame).

---

## 4. Phase M1 — Foundation, import, quality, viewer

**Do:**
- the module and tab, with the permissions (§3.10);
- the layers (§3.1);
- §3.2–§3.5;
- the Data section (upload, mapping, datasets, quality, viewer);
- the fixture generator.

**Gates**

| Gate | Check |
|---|---|
| G-M1.1 | **Layers.** The files of §3.1 exist and follow the existing style (show one service interface and its registration, one route group mapping beside the existing ones). The `matrix` tab appears for SystemAdmin, is absent for General, and its routes return 403 without it |
| G-M1.2 | **Fixtures.** `make-fixtures.mjs` produces five workbooks reproducing every quirk in §2.2: merged title rows, text dates, leading zeros, `NULL`, duplicate sheets, `LINE - n` in `WingName`, formulas with cached values, Bengali columns, sensitive columns. The script contains no real names or IDs (show a grep for three real employee IDs from the samples → no match) |
| G-M1.3 | **Profiling** (fixtures, then live on all five real files): sheets, table ranges, header rows and row counts match §2.2; the attendance report's title and date range become report details with report date 2026-05-19; `Sheet1`'s formula columns use cached values and list their formulas. Paste the live profile table (counts and column names only) |
| G-M1.4 | **Mapping.** On each real file at least 90% of columns are mapped by synonyms; the rest are model suggestions (schema JSON, sample values masked, checked at the fake) awaiting confirmation. A second upload of the same layout matches the saved template and imports with no questions |
| G-M1.5 | **Personal data.** After importing the real master file: no dataset table has an NID, birth certificate, bank, mobile, address, parents' or spouse's name, date-of-birth, religion or e-mail column (show `PRAGMA table_info`); `age_band` exists; the original file is not on disk (search the data folders for its SHA-256 name and size); a user without view-personal sees names masked in the viewer and the rows API |
| G-M1.6 | **Types and IDs.** `emp_id` keeps leading zeros; `PunchNo` joins to `EmpID` through the normaliser; `NULL` becomes null; `01-Apr-2021` and `2021-10-05 00:00:00` become ISO dates; `07:54:20 AM ` becomes `07:54:20`; `line_no` and `line_type` are derived (the master's sewing sections give 53 distinct lines live) |
| G-M1.7 | **Duplicate sheets.** The master's `HRM` and `HRM (2)` are flagged as copies, with overlap percentage; `HRM` is the default choice; `For corrcetion` is flagged as a near-copy with 2 extra columns |
| G-M1.8 | **Quality checks.** Each check in §3.5 fires on the fixture planted for it, with correct counts and sample cell refs; live counts are listed per real file (no personal data). A fix creates the corrected column, keeps `_raw_`, and is logged |
| G-M1.9 | **Viewer and highlight.** The viewer opens a dataset with 8,500 rows smoothly (virtualised; show the frame time or row render count); a highlight token outlines the right rows and cells. Screenshots, light and dark, 360 px |
| G-M1.10 | `dotnet build` 0 warnings, `npm run build` clean; `matrix-eval --phase M1` summary and the red line; the other suites unchanged |

---

## 5. Phase M2 — Analytics engine

**Do:** §3.6, §3.7, relations, and report cards.

**Gates**

| Gate | Check |
|---|---|
| G-M2.1 | **Validator.** Unknown dataset, unknown field, a salary field without permission, a forbidden operator, `limit` 10,000, an injection attempt in a value (`'; DROP TABLE`) and in a field name: each refused with its reason; the generated SQL for a valid query uses parameters only (show it) |
| G-M2.2 | **Correctness.** For 15 KPI queries on the fixtures, results equal an independent computation in the eval script (written separately in JS over the fixture rows) |
| G-M2.3 | **Joins.** Attendance joined to the master by `emp_id` gives present/late per sewing line; people in attendance missing from the master are reported as a count |
| G-M2.4 | **Live KPIs** on the real files (counts only): active headcount by unit, sewing lines per unit, operators per line (min, median, max), female %, grade distribution, attendance % and late % for 19 May 2026, people with OT above 2 h |
| G-M2.5 | **Report card** per dataset kind: built by code, under 3,000 tokens, every value with a citation token that reopens the right rows |
| G-M2.6 | **Snapshots.** Two fixture manpower files dated a month apart give joiners, separations and attrition % equal to the eval's independent computation |
| G-M2.7 | Builds clean; `matrix-eval --phase M2`; the other suites unchanged |

---

## 6. Phase M3 — Dashboards

**Do:** §3.8 dashboards, filters, drill-down, editor, export, trends; the Definitions section.

**Gates**

| Gate | Check |
|---|---|
| G-M3.1 | **Automatic dashboards.** Importing each fixture kind creates its dashboard (Manpower overview, Line board, Attendance today, Joiners and separations, Data quality); each widget's numbers equal its query result |
| G-M3.2 | **Filters and drill-down.** A unit filter changes every widget; clicking a line's bar opens the viewer with exactly that line's rows highlighted |
| G-M3.3 | **Editor.** Add, move, resize, change type, rename, save, reload; a non-editor sees no edit controls and gets 403 on save |
| G-M3.4 | **Thresholds and trends.** KPI tiles colour by thresholds; trend widgets use the snapshot series; "compare with previous" shows the change |
| G-M3.5 | **Export.** PNG of each chart, print view of a dashboard, `.xlsx` and `.csv` of a dataset without sensitive columns, and without salary columns for a user without view-salary (open the files and list their headers) |
| G-M3.6 | **Live screenshots** of the Manpower overview and Line board on the real master file, and Attendance today on the real attendance file; light and dark, desktop and 360 px. **Blur or crop anything personal before pasting** |
| G-M3.7 | No hard-coded colours (chart colours come from theme tokens read at runtime); builds clean; suites unchanged |

---

## 7. Phase M4 — Summary and chat

**Do:** §3.8 summary and §3.9 chat; the Ask section.

**Gates**

| Gate | Check |
|---|---|
| G-M4.1 | **Summary.** For each fixture kind, the summary's numbers all pass the number check; each point cites card items; Bengali and English versions |
| G-M4.2 | **Plan.** The fake received `format` = the plan schema, temperature 0, `think: false`; an invalid query gets one repair round with the validator's errors; a still-invalid plan returns "I couldn't form a query for that" with the reason, not a guess |
| G-M4.3 | **No personal data to the model.** Across all chat checks, the fake never received a value from a `personal`, `salary` (without permission) or `sensitive` column (scan every request body against the fixture's names and IDs) |
| G-M4.4 | **Citations.** `[R1]`-style citations map to result rows; clicking opens the viewer filtered and highlighted; `[KPI: …]` opens the definition; an answer citing a token that wasn't returned is marked "unknown source" |
| G-M4.5 | **Permissions in chat.** A user without view-salary asking for payroll gets a refusal naming the permission; with it, the answer is computed |
| G-M4.6 | **Golden set (live).** `scripts/matrix/golden.json`: 25 questions on the real files, half in Bengali, with expected numbers computed by the eval's independent code. The questions:<br>• headcount by unit;<br>• sewing lines in ADL-Unit-1;<br>• the line with the fewest operators;<br>• operator:helper ratio for line 143;<br>• female % in Finishing;<br>• attendance % and late count on 19 May 2026;<br>• people with OT above 2 h by section;<br>• grade M3 headcount;<br>• separations by status;<br>• one "not in the data" question;<br>• one personal-data request that must be refused.<br>With `gemma4:12b`: at least 21 of 25 correct, 0 unverified numbers, 0 personal data leaked; paste the summary and failures, plus time to first word. The same with `qwen3.8-27b-q3`; recommend one |
| G-M4.7 | **UI.** Ask with the status line, streamed answer, inline chart with "Add to dashboard", Data used panel, citations. Screenshots (personal data blurred), light and dark, 360 px |
| G-M4.8 | Builds clean; the whole `matrix-eval`; the other suites unchanged |

---

## 8. Eval harness
`scripts/matrix-eval.mjs`, Node 18+, no dependencies beyond what the repo already has for evals. Workbook fixtures are generated by `scripts/matrix/make-fixtures.mjs`; if writing `.xlsx` needs a library, generate the fixtures from the backend in a test-only endpoint enabled by `Matrix:FixtureEndpoint=true` (off by default), using ClosedXML.

- The API runs on a throwaway SQLite file with the shared fake Ollama.
- `--live` uses `matrix-samples/` and the real Ollama, and **prints counts only**.
- Output `id | check | PASS/FAIL | detail`, a summary line, non-zero exit on any FAIL, `--phase N`, `--only <id>`.

---

## 9. Do not
- Do not commit, paste or log real personal data; never use the real files as fixtures.
- Do not send names, salaries without permission, or sensitive columns to the model.
- Do not store the original uploaded files after import.
- Do not let the model write SQL or compute the numbers shown to users.
- Do not build table or column names from user or model text.
- Do not add packages beyond `echarts`; do not push, merge to `main` or open a pull request.

---

## 10. Report format (end of every phase)

```
## Phase MN report — <title>
Summary: <2–3 lines>
Eval: <N/M passed>   first run before the code: <N/M passed>
Other suites: hr-eval <N/M>, po-eval <N/M>, oddity-eval <N/M>, model-policy-eval <N/M>, rbac-eval <N/M if present>

| Gate | Check | Result | Evidence |
|------|-------|--------|----------|

Facts from §2 that changed: <list, or "none">
Files changed: <list>
Decisions made (and why): <list>
Noticed, not changed: <list>
Needs KAKAROT: <questions, or "none">
```

A phase with any FAIL is not complete. Say so plainly. Never include a person's name, ID, phone number or salary in a report.

---

## 11. Open questions for KAKAROT

Each has a default the build follows until you say otherwise.

1. **Chart library.** Default: Apache ECharts (one npm package). Say if you'd rather have hand-made SVG charts and no new package.
2. **Who sees salary.** Default: only SystemAdmin, plus anyone given `action:matrix.view-salary`.
3. **Keeping uploaded files.** Default: not kept after import (only the cleaned dataset without sensitive columns). Say if a copy must be kept for audit, and where.
4. **Line naming.** Default: a sewing line is `WingName` matching `LINE - <n>` within section Sewing. Say if cutting, finishing and wash lines are named the same way in other files.
5. **Attendance status codes.** Default: `P` = present, `LT` = late (counted as present), others shown as they are. Send the full code list (A, L, LWP, …) and I'll add it to the catalog.
6. **Attrition formula.** Default: separations in the period ÷ average of opening and closing active headcount × 100.
