# Final settlement rules — CoreERP (reference)

**Version 2, 17 Oct 2026. Status: what the system does, for HR review.**
Written from the database code: `ProcessFinalSettelment`, `ProcessSupplementaryFinalSetelment`, `GetSalaryDetails`, `GetDateDuration*`, and the salary formula table. Where the code and HR policy may differ, the rule is marked ⚠ and listed in §12.

**How to use this file:**
- **§1** has the codes.
- **§2** gives the order to work out any settlement.
- **§3–§10** give one rule per heading.
- **§11** is the full table of which benefits apply.

Short version: `settlement-quick-rules.md`. Worked examples: `settlement-calculation-guide.md`.

**Terms:**
- **basic** and **gross**: the monthly basic and gross salary.
- **yrs**: completed years of service.
- **extra months**: the months after the last completed year.
- **day's basic**: basic ÷ 30.

All amounts are rounded to whole taka, as the system stores them.

---

## 1. Codes

### 1.1 Employee type (`EmpTypeID`)
| ID | Type |
|---|---|
| 1 | Management |
| 2 | Non-Management |
| 3 | Schedule-A |
| 4 | Schedule-B |
| 5 | Day Labour |
| 6 | Test (inactive) |

### 1.2 Employee category (`EmpCategoryID`)
| ID | Category |
|---|---|
| 1 | Permanent |
| 2 | Contractual |
| 3 | Expatriate |
| 4 | Project Staff |
| 5 | Hour Basis |
| 6 | Day Basis |

Service benefit, gratuity, termination, retrenchment, removal and retirement are paid only to **Permanent (1)** employees. Death and discharge are paid to every category.

### 1.3 Separation status (`EmpStatusID`)
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

Only active separation records whose status is not partial (`IsPartial = 0`) are settled.

### 1.4 Payment mode (`PaymentModeId`)
| ID | Mode |
|---|---|
| 1 | Bank |
| 2 | Cash |
| 3 | Cheque |

### 1.5 Attendance codes
- **Worked days for earned leave:** `P, LT, LTP, SD, TL, EE, NOP, EXL`.
- **Fraction days** (last part-year of service): the same codes plus `SL`.
- **Annual leave taken:** `AL`, subtracted from the earned-leave balance.

*(HR: add the meaning of each code.)*

---

## 2. How to work out a settlement (order of steps)
1. Find the **settlement month** (§3.3).
2. Find **basic and gross** for that month (§3).
3. Find **length of service**: yrs, extra months, days and fraction days (§4).
4. Work out **earned-leave days and amount** (§5).
5. Look up which **benefits** apply to the status, type and category (§11), and work each one out (§6–§8).
6. Add **notice pay** or subtract the **short-notice deduction** (§9).
7. Add **unpaid salary** and **provident fund**; subtract **loan** (§10).
8. Subtract the **stamp fee** (§10.4). The result is the **net payable**.

If any input is missing (type, category, status, unit, dates, gross or basic, leave, notice, payment mode), ask for it before calculating.

---

## 3. Salary used for settlement

### 3.1 Basic from gross, by salary structure (`CalculationFormulaId`)
| Structure | Name | Basic | House rent | Fixed parts |
|---|---|---|---|---|
| 1, 6, 12 | Non-Management GMT / Worker-A / Non-Management C&F | From Dec 2023: (gross − 2,450) ÷ 1.5. Before: (gross − 1,850) ÷ 1.5 | 50% of basic | Medical 750, food 1,250, conveyance 450 |
| 3 | Non-Management (Denim / Textile) | From Mar 2024: (gross − 2,000) ÷ 1.55. Before: (gross − 850) ÷ 1.35 | 55% of basic | Medical 750, conveyance 400, food 850 |
| 10 | Plastic Gazette | (gross − 1,000) ÷ 1.4 | 40% of basic | Medical 500, food 200, conveyance 300 |
| 2 | Management (old 60%) | Joined before Mar 2024: (gross − 2,500) × 0.60. Joined from Mar 2024: gross × 0.50 | 50% of basic, max 25,000 | Medical 10% of basic, max 10,000; conveyance 2,500 |
| 9 | Management (50%) | gross × 0.50 | 50% of basic, max 25,000 | Medical 10% of basic, max 10,000; conveyance 2,500 |
| 4, 7, 11 | Management Factory / Denim / C&F | Joined before Mar 2024: (gross − 500) × 0.60. Joined from Mar 2024: gross × 0.50 | (gross − 500) × 0.40, or 50% of basic | Medical 500 |
| 13 | Management (60%) | (gross − 500) × 0.60 | (gross − 500) × 0.40 | Medical 500 |
| 5 | Non-Management (SPL) | ⚠ The system uses (gross − 2,450) ÷ 1.5; the formula table says (gross − 1,000) ÷ 1.4 | | |
| 8 | Hourly / Labour | ⚠ The system has no rule for 8, so basic comes out **0**; the formula table says basic = gross | | |

### 3.2 Which basic the settlement uses
- **Units 1, 27, 45:** "basic original". For structure 2 before March 2024 that is (gross − 500) × 0.60, which is higher than the payroll basic.
- **All other units:** the payroll basic.
- **Gross** comes from the increment history in force on the salary date, or on the contract start date if a contract started in the settlement month. A foreign-currency gross is converted at the latest rate on file.

### 3.3 Settlement month
- **Units 1, 27, 45, 111** (27th–26th month): a separation on the 27th–31st or the 1st counts in the **next** month.
- **Other units** (calendar month): a separation on the 1st counts in the previous month; any other day counts in its own month.

### 3.4 Contract start date
When a new contract starts in the settlement month:
- the salary in force on the contract start date is used;
- unpaid-salary attendance is counted only from the contract start date.

---

## 4. Length of service
- **Years** are completed anniversaries from the joining date to the separation date. Extra months and days are what is left after the last anniversary.
- **Fraction days** are the days worked (§1.5, with `SL`) from the last anniversary to the day before separation.
- **The "extra year" rules** add one year to the years used in a formula. Each benefit has its own condition (§6–§8).

---

## 5. Earned leave

### 5.1 Days earned
- **Under 365 days of service:** no leave is earned. Leave already taken and leave deductions make the balance negative, and a negative balance becomes a deduction.
- **Non-Management, Schedule-A, Schedule-B, Day Labour:** **1 day for every 18 worked days**, counted after the last annual-leave payment. If leave was never paid out, they are counted from the joining date.
- **Management, units 1, 27, 45:** days from 1 January to the separation date × 16 ÷ 360.
- **Management, units 54, 55, 61, 90 (AGL) and 46, 47, 48, 91 (ADL):** 1.33 × full months this year + 1.33 ÷ days in month × days attended in the separation month.
- **Other Management:** yearly allocation ÷ 12 × months. The separation month counts only after the 15th.

### 5.2 Where the count starts (types 2–5)
- **After a leave payment:** the count starts after the last payment's end date (tables `Payroll_AnnualLeave_Payment_JoinYear_Fraction`, `_JoinDate` and `_Monthly`). A date on or before 30 Nov 2024 moves to the 2024/2025 anniversary of joining.
- **Unit 36:** counts from 1 January.
- **ADL units 46, 47, 48, 91:** add migrated days for separations before 2025.
- **Subtracted:** `AL` days taken, `LeaveDeduction` rows, and `Earned_Leave_Deduction_By_Date` rows (mode 1) after the start date.

### 5.3 Cap and amount
- **Cap:** 40 days for Non-Management (type 2); 60 days for everyone else. Days are kept to 2 decimals.
- **Amount, Management:** day's basic × days.
- **Amount, others:** gross ÷ 30 × days when positive; day's basic × days when negative.

---

## 6. Service benefit
**Who:** Permanent, types 2–4; Management too for separations from 28 Jun 2026.
**Statuses:** Resignation (3), Left (5), Suspension (6), UA (7), Dismiss (9), Discharge (10), Contract Expiration (12), Discontinuation (15), Extra (16). Retirement, Termination, Retrenchment, Removal and Death use their own rules.

### 6.1 Separation from 1 Mar 2026 (needs 1+ year)
- **1–2 yrs:** 0.
- **Exactly 3 yrs 0 months 0 days:** day's basic × 7 × 3.
- **3–9 yrs:** day's basic × 15 × yrs.
- **10+ yrs:** basic × yrs.
- **Extra year:** 6+ extra months **and** 240+ fraction days → use yrs + 1 (3–9 and 10+).

### 6.2 Separation before 1 Mar 2026 (needs 3+ years)
- **3–9 yrs:** day's basic × 15 × yrs.
- **10+ yrs:** basic × yrs.
- ⚠ No extra year was ever given (§12.1).

---

## 7. Gratuity (Management)
**Who:** Management (1), Permanent, 5+ yrs, separation **before 28 Jun 2026**, any status except Retirement.
- **Extra year:** 6+ extra months and 1+ extra day → yrs + 1.
- **5–9 yrs:** basic × (yrs ÷ 10) × yrs.
- **10+ yrs:** basic × yrs.

From 28 Jun 2026 Management gets the service benefit (§6) instead.

---

## 8. Benefits by separation status

### 8.1 Termination (4)
**Types 2–4, Permanent:**
- basic × yrs;
- plus a termination benefit of round(day's basic) × 120, with 1+ year.

⚠ Management gets gratuity before 28 Jun 2026 and nothing from that date (§12.3).

### 8.2 Retrenchment (11)
**Types 2–4, Permanent, 1+ yr:**
- basic × yrs, or basic × (yrs + 1) with 6+ extra months and 240+ fraction days;
- plus notice pay (§9.2), for any type.

⚠ Management gets notice pay, plus gratuity before 28 Jun 2026 (§12.3).

### 8.3 Removal (13)
**Types 2–4, Permanent, 1+ yr:** basic ÷ 2 × yrs. No service benefit.

⚠ Management: gratuity before 28 Jun 2026, nothing after (§12.3).

### 8.4 Retirement (2)
**Permanent, any type, 1+ yr:** basic × yrs.
- **Management extra year:** 5+ yrs and 6+ extra months → yrs + 1.
- **Others' extra year:** 240+ fraction days → yrs + 1.

No service benefit and no gratuity.

### 8.5 Death (8)
**Any type and category:**
- **Death benefit:** basic × (yrs + 1) with 6+ extra months and more than 1 year (⚠ §12.5); otherwise basic × yrs.
- **Plus a retirement benefit:** basic × yrs.
- **Management with 5+ yrs also gets the expiration benefit:** 5–9 yrs basic ÷ 10 × yrs; 10+ yrs basic × yrs. Before 28 Jun 2026 gratuity is also paid.

Service benefit is 0, and the notice period is 0.

### 8.6 Discharge (10)
**Any type and category, 1+ yr:** basic × yrs, plus the service benefit (§6) for those who qualify.

### 8.7 Left (5)
Treated like Resignation (§6), but it is **not settled until 28 days** after the separation date.

### 8.8 Day Labour and non-Permanent categories
Only these are paid:
- unpaid salary;
- earned leave;
- death (§8.5);
- discharge (§8.6).

Retirement needs Permanent.

---

## 9. Notice period

### 9.1 IsWaive and short notice
- **IsWaive = 1:** the notice period is set to 0. There is no short-notice deduction, and no notice pay.
- **IsWaive = 0 and a negative notice period** (notice short): the deduction is day's basic × days short, at most 60 days, for every employee type.
- **Death:** the notice period is always 0.
- ⚠ HR has described IsWaive the other way round: "0 = no deduction; 1 = deduct gross or basic by employee type". The system does what is written above (§12.2).

### 9.2 Notice pay
Retrenchment (11) with 1+ yr: day's basic × notice-period days, unless IsWaive = 1.

---

## 10. Unpaid salary, provident fund, loan and stamp fee

### 10.1 Unpaid salary
The days worked in the settlement month that were not paid yet, up to the separation date (`ProcessSupplementaryFinalSetelment`).
- **Per-day rate:** gross ÷ days in the month; in most units, basic ÷ 30 for basic-only days.
- **Overtime:** Schedule-A and Schedule-B without a fixed rate get basic ÷ 104 per hour; other types only with a fixed rate.
- **Festival holiday worked:** Management at their allowance rate; others 2 × gross ÷ days in the month.
- **Allowances:** tiffin, iftar, holiday, night, lunch, mobile and key allowances for days not already paid.
- **Locked payroll month:** this step is skipped.

### 10.2 Late attendance (units 1, 27, 45 and 19 only)
- **When it applies:** the month's lates exceed the allowance: 2 in units 1, 27 and 45 and for Management in unit 19; 3 for others in unit 19.
- **How much:** every 3 lates in the month, counting all of them, cost 1 day.
- **Where it comes from:** earned leave if the balance covers it, otherwise salary.

### 10.3 Provident fund and loan
- **Provident fund:** unit 66 only. Employee contributions are paid back, doubled when PF membership started 2+ years before separation.
- **Loan:** installments dated in the settlement month, not on hold, are deducted.

### 10.4 Stamp fee
Only when the payment mode is **2 (Cash)** and the net is over 1,000:
- **Net over 50,000:** 50.
- **Net 1,000–50,000:** 20.
- **Before July 2022:** 10.

Bank (1) and Cheque (3) pay no stamp fee.

### 10.5 Net payable
Net = unpaid salary + earned-leave amount + gratuity + service + termination + removal + retirement + expiration + discharge + death + PF + notice pay − loan − short-notice deduction − stamp fee.

---

## 11. Which benefits apply (Permanent employees)

| Status | Non-Mgmt / Schedule-A / Schedule-B | Management, before 28 Jun 2026 | Management, from 28 Jun 2026 |
|---|---|---|---|
| 3 Resignation, 5 Left, 6, 7 UA, 9 Dismiss, 12, 15, 16 | Service benefit | Gratuity | Service benefit |
| 2 Retirement | Retirement | Retirement | Retirement |
| 4 Termination | Basic × yrs + 120 days' basic | Gratuity | None ⚠ |
| 11 Retrenchment | Basic × yrs (+1) + notice pay | Gratuity + notice pay | Notice pay only ⚠ |
| 13 Removal | Half basic × yrs | Gratuity | None ⚠ |
| 8 Death | Death + retirement | Death + retirement + expiration + gratuity | Death + retirement + expiration |
| 10 Discharge | Discharge + service benefit | Discharge + gratuity | Discharge + service benefit |

Every row also includes unpaid salary + earned leave − loan − short-notice deduction − stamp fee.

---

## 12. Under HR review (⚠)
1. **Extra year before 1 Mar 2026.** The 240-day extra year never applied, because an `ELSE` is missing (`ProcessFinalSettelment` lines 689–705).
2. **IsWaive.** The system deducts short notice only when IsWaive = 0, and always on basic. HR's description is the opposite.
3. **Management from 28 Jun 2026.** Termination, Retrenchment and Removal get no gratuity and no service benefit.
4. **Dismiss and UA.** Dismiss (9) and UA (7) get the same service benefit as Resignation.
5. **Death.** It pays death plus retirement benefit, and the death rule depends on the day the settlement is **run** (`GETDATE()`, line 895), not the separation date. For Management it also pays expiration and gratuity, and the extra year can be added twice.
6. **Discharge.** It pays both the discharge benefit and the service benefit.
7. **Gratuity and expiration** use different 5–9-year formulas: basic × (yrs ÷ 10) × yrs against basic ÷ 10 × yrs.
8. **Salary structures 5 and 8** (§3.1).
9. **Currency rate.** The lookup skips the previous December's rate from January to November (`GetSalaryDetails` line 265). The special-allowance adjustment also depends on the run date (line 258).
10. **Re-running a settlement on another day** adds a second unpaid-salary row, and running it changes leave deductions (`sp_ManageLeaveDeduction`).
11. **Special cases fixed in code:** employees 015405 and 041870 (earned leave), 005457 and 065011 (unpaid salary set to 0), EmpId 1371, 79117 and 333127 (salary). Explain these as special cases, never from the general rules.
