# Final settlement rules — CoreERP

**Status: draft for HR review.** Written from the database code on 14 Oct 2026:
`ProcessFinalSettelment`, `ProcessSupplementaryFinalSetelment`, `GetSalaryDetails`, `GetDateDurationYear`.
It describes what the system **does**, which may differ from policy. Section 11 lists the places where the two may not match. HR corrects this file; the chat answers from the corrected version.

Terms used below: **basic** is the monthly basic salary, **gross** the monthly gross salary, **yrs** the completed years of service, **day's basic** = basic ÷ 30.

---

## 1. Codes

### Employee type (`EmpTypeID`)

| ID | Type | Main rules that apply |
|---|---|---|
| 1 | Management | Gratuity (until 27 Jun 2026), then service benefit; earned leave by month or calendar day; leave paid on basic |
| 2 | Non-Management | Service benefit; earned leave 1 per 18 worked days, capped at 40 days |
| 3 | Schedule-A | Service benefit; earned leave 1 per 18 worked days; overtime |
| 4 | Schedule-B | As Schedule-A |
| 5 | Day Labour | Earned leave and unpaid salary only; no service-type benefit (except retirement, discharge and death) |

### Separation status (`EmpStatusID`)

| ID | Status | ID | Status |
|---|---|---|---|
| 1 | Active | 9 | Dismiss |
| 2 | Retirement | 10 | Discharge |
| 3 | Resignation | 11 | Retrenchment |
| 4 | Termination | 12 | Contract Expiration |
| 5 | Left | 13 | Removal |
| 6 | Suspension | 14 | Tongi Team Payroll |
| 7 | Unauthorized Absence (UA) | 15 | Discontinuation |
| 8 | Death | 16 | Extra |

Only separations with `IsPartial = 0` are settled.

### Employee category (`EmpCategoryID`)
Every benefit except earned leave, retirement, discharge and death needs **category 1**. *(HR: what is category 1?)*

### Attendance codes counted as worked days
For earned leave: `P, LT, LTP, SD, TL, EE, NOP, EXL`. For the last part-year of service, `SL` also counts.
`AL` (annual leave taken) is subtracted from the earned-leave balance.
*(HR: please add the meaning of each code.)*

---

## 2. Salary used for settlement

### 2.1 Basic from gross (`GetSalaryDetails`)
The gross comes from the employee's increment history at the salary date. The basic is then worked out from the salary structure (`CalculationFormulaId`):

| Structure | Name | Basic |
|---|---|---|
| 1, 5, 6, 12 | Non-Management GMT | From Dec 2023: (gross − 2,450) ÷ 1.5. Medical 750, conveyance 450, food 1,250, house rent 50% of basic. Before: (gross − 1,850) ÷ 1.5 |
| 3 | Non-Management Textile | From Mar 2024: (gross − 2,000) ÷ 1.55, house rent 55%. Before: (gross − 850) ÷ 1.35 |
| 10 | Plastic (gazette) | (gross − 1,000) ÷ 1.4 |
| 2 | Management, old 60% | Joined before Mar 2024: (gross − 2,500) × 60%. Joined from Mar 2024: 50% of gross |
| 9 | Management, new | 50% of gross |
| 4, 7 | Management Factory / Textile Factory | Joined before Mar 2024: (gross − 500) × 60%. Joined from Mar 2024: 50% of gross |
| 11 | Management C&F | Joined before Mar 2024: (gross − 500) × 60%. Joined from Mar 2024: 50% of gross |
| 13 | Management 60% | (gross − 500) × 60% |

Management house rent is capped at 25,000 and medical at 10,000.

**Units 1, 27 and 45 use "basic (original)" for settlement.** For structure 2 before March 2024 that is (gross − 500) × 60%, which is higher than the payroll basic.

Gross in a foreign currency is converted at the latest rate on file.

### 2.2 Salary month
- Units 1, 27, 45 and 111 run the month from the 27th to the 26th. A separation on the 27th–31st or the 1st counts in the **next** month.
- Other units use the calendar month; a separation on the 1st counts in the previous month.

### 2.3 Contract start date
When a new contract starts in the settlement month:
- the salary in force on the contract start date is used, not the one at the start of the month;
- attendance for the unpaid salary is counted only **from the contract start date**.

---

## 3. Length of service
Years, months and days from the joining date to the separation date. A year counts only when the anniversary has passed (`GetDateDurationYear`).

"Fraction days" = days worked from the last completed anniversary until the day before separation (the worked-day codes plus `SL`). Several benefits add one extra year when fraction days reach **240**.

---

## 4. Earned leave (annual leave) at settlement

**Service under 365 days:** no leave is earned. Leave already taken and leave deductions are subtracted from zero, so the balance can be negative. A negative balance becomes a deduction.

**Non-Management, Schedule-A, Schedule-B, Day Labour:** **1 day for every 18 days worked.**
- Worked days are counted after the last annual-leave payment. The system checks `Payroll_AnnualLeave_Payment_JoinYear_Fraction`, `_JoinDate` and `_Monthly`. If leave was never paid out, it counts from the joining date.
- Unit 36 counts from 1 January.
- Units 46, 47, 48 and 91 add migrated days for separations before 2025.
- When leave was paid by join-year or join-date, the count restarts after the last payment end date. A date on or before 30 Nov 2024 moves to the 2024/2025 anniversary of joining.
- Subtracted: days taken (`AL`), `LeaveDeduction` rows after that date, and `Earned_Leave_Deduction_By_Date` rows (mode 1).

**Management:**
- Units 1, 27, 45: days since 1 January × 16 ÷ 360.
- Units 54, 55, 61, 90, 46, 47, 48, 91: 1.33 days per full month this year, plus a share of the last month by days attended.
- Other units: yearly allocation ÷ 12 × months. The separation month counts only after the 15th.

**Cap:** 40 days for Non-Management; 60 days for everyone else.

**Amount:**
- Management: day's basic × balance.
- Others: gross ÷ 30 × balance when the balance is positive; day's basic × balance when it is negative.

**Late attendance** (unpaid-salary step), in units 1, 27, 45 and 19 only:
- **When it applies:** the month's lates exceed the allowance — 2 in units 1, 27 and 45 and for Management in unit 19; 3 for others in unit 19.
- **How much:** every 3 lates in the month, counting all of them, cost one day.
- **Where it comes from:** earned leave if the balance covers it, otherwise salary.

Other units have no late deduction.

---

## 5. Benefits by separation status

Applies to category 1 unless the table says otherwise. "+1 yr" means the extra year described in each row.

### Non-Management, Schedule-A, Schedule-B (types 2, 3, 4)

| Status | Benefit |
|---|---|
| **Resignation (3), Left (5), UA (7), Dismiss (9), Contract Expiration (12), Discontinuation (15), others** | **Service benefit**, at least 1 year of service, separation from 1 Mar 2026:<br>• exactly 3 years 0 months 0 days: 7 days' basic × 3<br>• 3–9 yrs: 15 days' basic × yrs; +1 yr when there are 6+ extra months **and** 240+ fraction days<br>• 10+ yrs: basic × yrs, with the same +1 yr rule<br>Before 1 Mar 2026: at least 3 years; 3–9 yrs 15 days' basic × yrs; 10+ yrs basic × yrs |
| **Left (5)** | Not settled until 28 days after the separation date |
| **Termination (4)** | Basic × yrs **plus** 120 days' basic (at least 1 year) |
| **Retrenchment (11)** | Basic × yrs, +1 yr with 6+ months and 240+ fraction days (at least 1 year), **plus notice pay**: day's basic × notice-period days |
| **Removal (13)** | Half basic × yrs (at least 1 year). No service benefit |
| **Retirement (2)** | Basic × yrs; +1 yr when fraction days ≥ 240. No service benefit |
| **Discharge (10)** | Basic × yrs (any type or category), in addition to the service benefit above |
| **Death (8)** | Death benefit basic × yrs (+1 yr with 6+ months and over 1 year of service), **plus** retirement benefit basic × yrs. No service benefit |

### Management (type 1)

| Status | Benefit |
|---|---|
| Any except Retirement, separation **before 28 Jun 2026**, 5+ yrs | **Gratuity**: 5–9 yrs basic × (yrs ÷ 10) × yrs; 10+ yrs basic × yrs. 5 yrs 6 months 1 day or more counts as one more year |
| Any, separation **from 28 Jun 2026** | Service benefit as for Non-Management above (no gratuity) |
| Retirement (2) | Basic × yrs; 5 yrs 6 months or more adds a year |
| Death (8) | Death and retirement benefits as above; with 5+ yrs also expiration benefit: 5–9 yrs basic ÷ 10 × yrs, 10+ yrs basic × yrs (and gratuity before 28 Jun 2026) |

### Day Labour (type 5) and other categories
Earned leave, unpaid salary, and the retirement, discharge and death benefits only.

---

## 6. Provident fund
Unit 66 only: the employee's PF contributions are paid back. They are doubled when PF membership started at least 2 years before separation.

## 7. Notice period
- **Short notice** (a negative notice period, not waived): day's basic × days short, at most 60 days, deducted.
- **Notice pay** to the employee: retrenchment only (section 5).
- A waiver, or a death, sets the notice period to 0.

## 8. Other deductions
- **Loan:** the installments due in the settlement month, not on hold.
- **Stamp fee:** only when the net amount is over 1,000 and the payment mode is 2. It is 20, or 50 when the net is over 50,000; 10 before July 2022.

## 9. Unpaid salary (`ProcessSupplementaryFinalSetelment`)
The salary for the days worked in the settlement month that were not paid yet, up to the separation date:
- **Per-day rate:** gross ÷ days in month. Most units use basic ÷ 30 for basic-only days.
- **Overtime:** Schedule-A and Schedule-B without a fixed rate get basic ÷ 104 per hour; Management and Non-Management only with a fixed rate.
- **Festival holiday worked:** Management at their allowance rate; others 2 × gross ÷ days in month.
- **Allowances:** tiffin, iftar, holiday, night, lunch, mobile and key allowances are added for the days not already paid.
- **Locked month:** if the payroll month is locked, this step is skipped.

## 10. Net amount
Unpaid salary + earned leave + all benefits + PF + notice pay − loan − short-notice deduction − stamp fee.

## 11. Questions for HR (where code and policy may differ)

1. **The 240-day extra year never applied before 1 Mar 2026**, because an `ELSE` is missing (`ProcessFinalSettelment` lines 689–705). Was that intended?
2. **Dismiss (9) and UA (7) get the same service benefit as resignation.** Is that the policy?
3. **Discharge (10) gets both** the discharge benefit and the service benefit. Correct?
4. **Death pays death benefit plus retirement benefit**, and for Management also expiration benefit and gratuity, and the extra year can be added twice. Correct?
5. **Gratuity and expiration use different 5–9-year formulas**: basic × yrs ÷ 10 × yrs against basic ÷ 10 × yrs. Which is right?
6. **The death rule depends on the date the settlement is run** (`GETDATE()`, line 897), not the separation date. The special-allowance adjustment in `GetSalaryDetails` (line 258) does the same.
7. **Currency conversion** looks up `SalaryMonth <= month AND SalaryYear <= year`, so in January–November it skips the previous December's rate (`GetSalaryDetails` line 265).
8. **Re-running a settlement on a different day** adds a second unpaid-salary row (only the same day's row is deleted), and the settlement then reads one of them.
9. **Running the settlement changes leave deductions** (`sp_ManageLeaveDeduction` in the late rule).
10. **Special cases fixed in code:** employees 015405 and 041870 (earned leave), 005457 and 065011 (unpaid salary set to 0), EmpId 1371, 79117 and 333127 (salary). The chat must say these are special cases, not explain them from the general rules.
11. What is category 1, and what does each attendance code mean?
