# Mostisko — HR Assist: settlement answers from CoreERP, read-only

### Evaluation-based build prompt for Claude Code

> **Before you start.**
> 1. Copy `HR_SETTLEMENT_RULES.md` to `docs/hr/HR_SETTLEMENT_RULES.md`, and this file to `docs/HR_MODULE_PROMPT.md`. Commit both on `model-policy` (or on whatever branch has model policy and PO Studio).
> 2. Have a **copy** of CoreERP to point at (UAT or a restored backup). Never production in Phases 1–2.
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
4. **Read-only, three times over.**
   - The database login can only read the `ai` schema (§3.1).
   - The SQL text is fixed in one file, never built from model output.
   - A static check fails the build if that file contains a write or `EXEC` keyword.

   The chat must **never** run `ProcessFinalSettelment` or `ProcessSupplementaryFinalSetelment`: both delete and insert, and the second also changes leave deductions.
5. **The model never invents a number.** Every amount in a reply must come from a tool result. The server checks this (§3.5). Rules come from `docs/hr/HR_SETTLEMENT_RULES.md`, which HR owns.
6. **Reuse what exists.** The chat tool loop in `ChatEndpoint.cs` (`MaxToolRounds`, `RunToolAsync`, `ToolDefinition`), `ModelPolicy`, `TabCatalog` / `TabAccessMiddleware`, and the existing fake Ollama in `scripts/*-eval.mjs`. A second tool loop, policy check or fake is an automatic FAIL.
7. **One new package only:** `Microsoft.Data.SqlClient` (a 5.2 release). Nothing else, NuGet or npm. Do not push, merge to `main` or open a pull request. Work on a branch named `hr-assist`, created from the branch in "Before you start", and commit at the end of each phase.
8. **§2 was verified at `cc95a82` plus the CoreERP code KAKAROT shared on 14 Oct 2026.** Re-confirm every fact you use and report what changed.

---

## 1. What we are building

An **HR** tab where an HR user asks, in English or Bengali, things like:
- *"What is 115978's final settlement and why is the service benefit that amount?"*
- *"How much earned leave does 041122 have, and from which date is it counted?"*
- *"What is Schedule-A's service benefit after 10 years?"*

The model answers by calling a few named, read-only lookups against CoreERP, then explains the numbers using the rules document, with citations.

```
HR user ─► /api/hr/chat (tab hr, model policy)
        ─► model + rules doc + hr_* tools
        ─► tool call ─► HrToolRunner ─► fixed SQL on the ai schema (read-only login, unit filter)
        ─► results back to the model ─► answer
        ─► number check: each amount must appear in a tool result ─► stream to the browser
        ─► audit row
```

| Phase | Work | Why in this order |
|---|---|---|
| **1** | The read-only data layer: `ai` schema script, query catalog, tool runner, unit scope, masking, audit; a test route, no chat | Correct data first; nothing else matters if a number is wrong |
| **2** | The HR tab and chat: tools wired into the existing loop, the rules document as context, number check, citations | Needs Phase 1's tools |
| **3** | The golden set from HR, and the live run on the Mac | Proves it with real questions and the real model |

**Not in this build:** running or re-running a settlement, a what-if calculator, any write to CoreERP, fine-tuning a model, payroll questions outside final settlement and earned leave.

---

## 2. Established facts (re-confirm, do not assume)

| # | Fact | Evidence |
|---|---|---|
| H1 | Chat already runs tools: it offers a `tools` array, collects `ToolCall` events, runs them in `RunToolAsync`, and stops after `MaxToolRounds = 2`. Unknown tool names get a "No tool named …" reply | `backend/MinimalApis/ChatEndpoint.cs:30, 202-234, 346-365` |
| H2 | A tool is a `ToolDefinition(name, description, jsonSchema)`; `web_search` is the only one today | `backend/ModelSources/ChatStreamEvent.cs:55`, `backend/Services/WebSearchTool.cs` |
| H3 | `OllamaChatStream.BuildPayload` sends the `tools` array to Ollama | `backend/ModelSources/OllamaChatStream.cs:108-110` |
| H4 | No SQL Server client is referenced today | `backend/RabbitIC.Api.csproj:13-24` |
| H5 | Tabs are keyed in `TabCatalog`; `TabAccessMiddleware` gates routes by key; grants are additive | `backend/Rbac/TabCatalog.cs`, `backend/Middleware/TabAccessMiddleware.cs` |
| H6 | `ModelPolicy.Check` refuses a model the role cannot run (model-policy branch) | `backend/Rbac/ModelPolicy.cs` |
| C1 | `ProcessFinalSettelment @EmpID, @p_UserId` deletes and re-inserts the employee's `FinalSattlementPayable` row, and calls `ProcessSupplementaryFinalSetelment` | CoreERP |
| C2 | `ProcessSupplementaryFinalSetelment` inserts `Payroll_MonthlySalarySupplementary` rows and calls `sp_ManageLeaveDeduction` (writes `LeaveDeduction`) | CoreERP |
| C3 | `dbo.GetSalaryDetails(@EmpId, @Month, @Year, @IsCurrentSalary, @ContStartDate)` is a table-valued function returning gross, basic, basic (original), house rent, medical, conveyance, food, other, special allowance; read-only | CoreERP |
| C4 | `dbo.GetDateDurationYear/Month/Day(@from, @to)` are scalar functions; the year counts whole anniversaries | CoreERP |
| C5 | `FinalSattlementPayable` holds one row per settled employee with every component (unpaid salary, EL balance and amount, gratuity, service, termination + removal, retirement, expiration, discharge, death, PF, notice, loan, stamp fee, net) and `BankAccount` | CoreERP |
| C6 | Lookups: `Common_EmploymentType` (1 Management, 2 Non-Management, 3 Schedule-A, 4 Schedule-B, 5 Day Labour), `Common_EmploymentStatus` (16 statuses, see the rules doc §1) | CoreERP |
| C7 | Earned-leave inputs: `LeaveAllocation` (LeaveId 3), `LeaveDeduction`, `Earned_Leave_Deduction_By_Date`, `Attendance_DailyAttendance`, and the leave-payment tables `Payroll_AnnualLeave_Payment_JoinYear_Fraction`, `_JoinDate`, `_Monthly` | CoreERP |

The names of the unit, department and designation lookup tables were not shared. Find them in the CoreERP copy, or list them under "Needs KAKAROT".

---

## 3. The design contract

### 3.1 The `ai` schema (CoreERP side)

Write `scripts/hr/coreerp-ai-schema.sql`, run by KAKAROT on the copy:
- `CREATE SCHEMA ai AUTHORIZATION dbo`, so ownership chaining lets its views read `dbo` tables without granting them.
- **Views**, columns limited to what §3.3 returns:
  - `ai.Employee`: code, name, unit, department, designation, type, category, status, joining date, separation date, salary structure id, gross. No bank, phone, NID or address.
  - `ai.Settlement`: `FinalSattlementPayable` without `BankAccount` or `BankID`.
  - `ai.LeavePayments`: the three payment tables, one row per payment with its source.
  - `ai.Attendance`: `EmpID`, `WorkDay`, `AttStatus` only.
- **Inline functions:**
  - `ai.SalaryDetails(@EmpId, @Month, @Year)`, wrapping `dbo.GetSalaryDetails(@EmpId, @Month, @Year, 1, <the contract start date the settlement uses>)`.
  - `ai.ServiceLength(@EmpId, @AsOf)`, returning years, months and days from the three `GetDateDuration*` functions.
- **A login and user `mostisko_hr_ro`** with `GRANT SELECT ON SCHEMA::ai` and nothing else. No `EXECUTE`, no `dbo` access.

The script is idempotent (`CREATE OR ALTER`) and has a matching `coreerp-ai-schema-drop.sql`.

### 3.2 Connection
`ConnectionStrings:CoreErpReadOnly` from user-secrets or an environment variable, never `appsettings.json`. Use `ApplicationIntent=ReadOnly`, a command timeout of 15 s, and at most 400 rows per result.

### 3.3 Tools (the whole catalog)

All SQL lives in `backend/Hr/HrQueryCatalog.cs` as constants.

| Tool | Parameters | Returns |
|---|---|---|
| `hr_employee` | `emp_code` | `ai.Employee` row: names, not ids |
| `hr_settlement` | `emp_code` | The `ai.Settlement` row with labelled components, plus type, status, joining and separation dates, and `special_case: true` for the employees listed in the rules doc §11.10 |
| `hr_service_length` | `emp_code`, optional `as_of` (default: separation date, else today) | Years, months, days, and fraction days worked since the last anniversary |
| `hr_salary_breakdown` | `emp_code`, `month`, `year` | `ai.SalaryDetails` row, and which basic the settlement uses (units 1, 27, 45: basic original) |
| `hr_earned_leave` | `emp_code` | Last leave-payment end date and its table; worked days since then (the codes in the rules doc §1); AL taken; deductions; `estimate = worked ÷ 18` for types 2–5, labelled **estimate**; the cap; and the `ELBalance` stored in `ai.Settlement` if there is one |
| `hr_attendance_summary` | `emp_code`, `from`, `to` (at most 400 days) | Count per attendance code |

**Parameter rules:**
- `emp_code` matches `^\d{6}$`.
- Dates are ISO; month is 1–12; year is 2015 to next year.
- Anything else is refused before SQL runs, with the reason handed back to the model.

### 3.4 Who sees what
- **Tab** `hr`: in no role's baseline except SystemAdmin; given through grants.
- **Unit scope:** a SQLite table `HrUnitAccess(UserId, UnitId)`. Every query filters `UnitID IN (SELECT value FROM STRING_SPLIT(@Units, ','))`. A user with no rows sees nothing; SystemAdmin sees all. An employee outside the user's units gets the same reply as an unknown code: "No employee 123456 in your units." That way the reply doesn't reveal whether the employee exists.
- **Audit:** a SQLite table `HrQueryLog(UserId, Tool, EmpCode, ParamsJson, Rows, DurationMs, Refused, Reason, AtUtc)`. One row per tool call, including refused ones. No salary values are stored in SQLite.

### 3.5 Chat contract
- **Route:** `POST /api/hr/chat`, streamed like `/api/chat`. Model choice goes through `ModelPolicy`.
- **Stateless:** the client sends at most the last 10 turns, and nothing about the HR conversation is saved except the audit rows.
- **System prompt:** an HR persona, plus the rules document whole (it is about 3,500 tokens; fail with a clear message if the estimate exceeds 6,000), plus the answer rules:
  - Amounts only from tool results.
  - Cite the tool after each amount, `[hr_settlement]`, and the rule section after each explanation, `[Rules §5]`.
  - Say "special case, set by hand in the system" when `special_case` is true.
  - Never guess an employee code; ask for it.
  - Answer in the user's language.
- **Tools offered:** the six `hr_*` tools (no `web_search`). `MaxToolRounds` stays at 2 for normal chat; the HR route may use 3, set where the constant lives.
- **Number check, after the reply:**
  - Every amount of 100 or more in the reply (after removing commas and `৳`/`Tk`) must equal a value in that turn's tool results, to within 1.
  - Any that don't are listed in a final stream event `unverified: [...]`, and the UI shows them under the answer as "Not from the database: …".
- **UI:** the HR tab has an employee-code box, a chat pane, and a card showing the last `hr_employee` and `hr_settlement` results. Theme tokens only, light and dark, usable at 360 px.

---

## 4. Phase 1 — Read-only data layer

**Do**
- Re-confirm §2. Write `scripts/hr-eval.mjs` and this phase's checks first.
- Add `Microsoft.Data.SqlClient`.
- Write the `ai` schema script (§3.1), `HrQueryCatalog`, `HrToolRunner`, `HrUnitAccess`, `HrQueryLog`, and an `IHrDataSource` with two implementations: SQL Server, and a JSON fixture used by the eval (`Hr:DataSource = Fixture`).
- Add a test route, `POST /api/hr/tools/{name}`, tab `hr`, which runs one tool and returns its JSON.

**Gates**

| Gate | Check |
|---|---|
| G1.1 | **Static read-only check.** `hr-eval` fails if `HrQueryCatalog.cs` or any `ai` view contains `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `TRUNCATE`, `DROP`, `ALTER`, `EXEC`, `sp_` or `dbo.Process`. Shown passing, and shown failing once on a planted `EXEC` |
| G1.2 | **Login permissions.** On the copy, as `mostisko_hr_ro`: `SELECT * FROM fn_my_permissions(NULL,'DATABASE')` lists only `CONNECT`; `SELECT TOP 1 * FROM dbo.FinalSattlementPayable` fails; `EXEC dbo.ProcessFinalSettelment 1,0` fails; `SELECT TOP 1 * FROM ai.Settlement` works. Paste all four |
| G1.3 | **Parameters.** `12345`, `1234567`, `abc123`, `115978; DROP`, month 13, a 401-day range: each refused with its reason, no SQL run (fixture call count unchanged), audit row with `Refused = 1` |
| G1.4 | **Masking.** No tool result, in fixture or live, contains a key matching `bank|account|nid|phone|mobile|address` (case-insensitive) |
| G1.5 | **Unit scope.** A user with unit 27 only: an employee of unit 27 is found; one of unit 36 gives exactly the unknown-code message. A user with no units finds nobody. SystemAdmin finds both |
| G1.6 | **Settlement matches the source.** Live, three settled employees (a Non-Management resignation, a Schedule-A termination, a Management separation): every component of `hr_settlement` equals the `FinalSattlementPayable` row read directly as `dbo`. Paste the side-by-side with codes masked to the last 3 digits |
| G1.7 | **Salary matches the source.** For the same three, `hr_salary_breakdown` for the separation month equals `SELECT * FROM dbo.GetSalaryDetails(...)` run directly |
| G1.8 | **Earned-leave estimate.** For 10 settled employees of types 2–4, compare `estimate` (capped) with the stored `ELBalance`. Report the table. Every difference over 0.05 days needs a one-line cause (special case, deduction rule, migrated days…). This gate is about explanation, not equality |
| G1.9 | **Audit.** Every call in G1.3–G1.7 has one `HrQueryLog` row; no salary value appears in SQLite (`grep` the database file for one of the G1.6 net amounts → no match) |
| G1.10 | `dotnet build` 0 warnings; `hr-eval --phase 1` summary and the red line; `po-eval`, `oddity-eval`, `model-policy-eval` unchanged |

---

## 5. Phase 2 — HR tab and chat

**Do**
- Tab key `hr` in `TabCatalog`, the route group gated by it, and the HR tab UI (§3.5).
- Register the six tools with the existing loop for the HR route only.
- The system prompt from the rules document, the number check, and the citations.

**Gates**

| Gate | Check |
|---|---|
| G2.1 | **Tool use.** With the fake Ollama scripted to call `hr_settlement` with `{"emp_code":"115978"}`, the server runs exactly that query, returns the result to the model, and streams the answer. The fake received the six tools and no `web_search` |
| G2.2 | **Bad calls.** Scripted calls to `hr_delete_all`, to `hr_settlement` with `"emp_code":"x"`, and a fourth tool round: each is refused or cut off as specified, and the user still gets an answer |
| G2.3 | **Number check.** A scripted reply with one amount from the tool result and one invented: the stream ends with `unverified` listing only the invented one, and the UI shows the warning line |
| G2.4 | **Rules in context.** The fake received the rules document in the system message, byte-identical to `docs/hr/HR_SETTLEMENT_RULES.md`, and the token estimate is in the log line |
| G2.5 | **Access.** No `hr` grant → 403 on `/api/hr/*`. `hr` grant but General role → `ModelPolicy` applies (forbidden model 403). Unit scope applies through chat as in G1.5 |
| G2.6 | **Nothing saved.** After five HR chat turns, `Conversations` and `Messages` row counts are unchanged; only `HrQueryLog` grew |
| G2.7 | **UI.** Employee card, answer with tool and rule citations, and the unverified line. Screenshots, light and dark, 360 px; no hard-coded colours (`grep` as in other prompts) |
| G2.8 | **Live, tools.** On the Mac with `gemma4:12b`, ask the three G1.6 employees' settlements in English and one in Bengali. Each answer calls `hr_settlement`, and every amount is verified. If `gemma4:12b` does not call tools reliably (fewer than 4 of 4), repeat with `qwen3.8-27b-q3` and report both |
| G2.9 | Builds clean; `hr-eval --phase 2`; the other suites unchanged |

---

## 6. Phase 3 — Golden set and live run

KAKAROT and HR supply `scripts/hr/golden.json`: 30 questions with the facts a correct answer must contain (amounts, dates, a rule section) and, for refusals, the expected refusal. The mix:

| Kind | Count | Example |
|---|---|---|
| Settlement amounts and why | 10 | "Why is 115978's service benefit 84,000?" |
| Earned leave | 6 | "How many EL days does 041122 have and from which date?" |
| Rules only, no employee | 6 | "Retrenchment benefit for Schedule-B after 7 years?" |
| Salary breakdown | 3 | "Basic of 101499 in January 2026?" |
| Must refuse | 5 | Another unit's employee; a bank account number; "run the settlement for 115978"; "change his joining date"; a code that doesn't exist |

`scripts/hr/run-golden.mjs` posts each question to `/api/hr/chat` as an HR test user, checks the facts, and writes `results/hr-golden-<model>.md` and a summary line.

| Gate | Check |
|---|---|
| G3.1 | `golden.json` exists with HR's sign-off name and date in its header |
| G3.2 | With `gemma4:12b`: at least 27 of 30 correct, all 5 refusals correct, **0 unverified amounts** across the run. Paste the summary and every failure |
| G3.3 | The same run with `qwen3.8-27b-q3`; report both, recommend one |
| G3.4 | Each failure is classified: wrong tool, wrong rule in the doc, data issue, or model error. Rule-doc problems become an edit list for HR, not code changes |
| G3.5 | Median time to the first word, and total per answer, for both models |

---

## 7. Do not

- Do not call, wrap or re-create `ProcessFinalSettelment`, `ProcessSupplementaryFinalSetelment` or any `sp_`.
- Do not build SQL from model output, or add a "run any query" tool.
- Do not grant the login anything outside `ai`, or put its secret in a committed file.
- Do not store salary values, bank details or HR chat text in SQLite.
- Do not let the model state an amount the number check can't find without the warning showing.
- Do not change the rules document's meaning. If it is wrong, report it for HR.
- Do not push, merge to `main` or open a pull request.

---

## 8. Eval harness

`scripts/hr-eval.mjs`, Node 18+, no dependencies.
- Reuse the shared fake Ollama and API start-up. The API runs with `Hr:DataSource=Fixture` and `scripts/hr/fixtures/*.json`: six employees across units 27 and 36, types 1–4, with settlement, salary, leave and attendance rows.
- `--live` switches to `ConnectionStrings:CoreErpReadOnly` for the live gates.
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

A phase with any FAIL is not complete. Say so plainly. Mask employee codes to the last 3 digits and never paste salary amounts of real people beyond what a gate asks for.

---

## 10. Open questions for KAKAROT

Each has a default the build follows until you say otherwise.

1. **Which database.** Default: a UAT copy for Phases 1–2. Production only after Phase 3 passes, with the same read-only login.
2. **Unit scope.** Default: each HR user sees only the units granted in `HrUnitAccess`; SystemAdmin sees all.
3. **Management salaries.** Default: HR users see Management employees like everyone else in their units. Say if Management (type 1) should need a separate grant.
4. **Answer language.** Default: the user's language; amounts always in digits with ৳.
5. **The questions in the rules doc §11.** Default: the chat explains what the system does today and adds "HR is reviewing this rule" where §11 lists it.
