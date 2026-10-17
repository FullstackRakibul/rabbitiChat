# Final settlement — quick rules (CoreERP `ProcessFinalSettelment`)

Source: the stored procedure as of 17 Oct 2026. It describes what the system calculates. Lines marked ⚠ are under HR review. Full steps and worked examples are in `settlement-calculation-guide.md`.

## Codes
- **Employee type (EmpTypeID):** 1 Management · 2 Non-Management · 3 Schedule-A · 4 Schedule-B · 5 Day Labour (6 Test, inactive).
- **Employee category (EmpCategoryID):** 1 Permanent · 2 Contractual · 3 Expatriate · 4 Project Staff · 5 Hour Basis · 6 Day Basis. Most benefits need category 1 Permanent.
- **Separation status (EmpStatusID):** 1 Active · 2 Retirement · 3 Resignation · 4 Termination · 5 Left · 6 Suspension · 7 Unauthorized Absence (UA) · 8 Death · 9 Dismiss · 10 Discharge · 11 Retrenchment · 12 Contract Expiration · 13 Removal · 14 Tongi Team Payroll · 15 Discontinuation · 16 Extra.
- **Payment mode (PaymentModeId):** 1 Bank · 2 Cash · 3 Cheque.

## Inputs a calculation needs
Employee type, category, separation status, unit, joining date, separation date, gross and salary structure (or basic), earned-leave balance (or worked days since the last leave payment), notice period and IsWaive, payment mode, loan installments for the settlement month, unpaid salary for the settlement month.

## Which parts apply (Permanent employees)

| Status | Non-Mgmt / Sch-A / Sch-B (2, 3, 4) | Management (1) |
|---|---|---|
| 3 Resignation, 5 Left, 6, 7 UA, 9 Dismiss, 12, 15, 16 | Service benefit | Gratuity if separated before 28 Jun 2026; service benefit from 28 Jun 2026 |
| 2 Retirement | Retirement benefit | Retirement benefit |
| 4 Termination | Basic × years + termination benefit (120 days' basic) | Gratuity before 28 Jun 2026; no benefit from 28 Jun 2026 ⚠ |
| 11 Retrenchment | Basic × years (+1) + notice pay | Notice pay + gratuity before 28 Jun 2026; only notice pay from 28 Jun 2026 ⚠ |
| 13 Removal | Removal benefit (half basic × years) only | Gratuity before 28 Jun 2026; no benefit from 28 Jun 2026 ⚠ |
| 8 Death | Death benefit + retirement benefit | Death + retirement + expiration (5+ yrs) + gratuity (before 28 Jun 2026) |
| 10 Discharge | Discharge benefit + service benefit | Discharge benefit + gratuity before 28 Jun 2026; discharge + service benefit from 28 Jun 2026 |

Every status also gets: unpaid salary + earned-leave amount − loan − short-notice deduction − stamp fee. Day Labour and non-Permanent categories get only these, plus retirement (Permanent only), discharge and death.

## Formulas (basic = monthly basic; yrs = completed years)
- **Service benefit:** 3–9 yrs: basic ÷ 30 × 15 × yrs. 10+ yrs: basic × yrs. From 1 Mar 2026: exactly 3 yrs 0 m 0 d → basic ÷ 30 × 7 × 3; 6+ extra months **and** 240+ fraction days → one more year. Before 1 Mar 2026: needs 3+ yrs.
- **Gratuity (Management, 5+ yrs, before 28 Jun 2026):** 5–9 yrs: basic × (yrs ÷ 10) × yrs. 10+ yrs: basic × yrs. 6+ extra months and 1+ day → one more year.
- **Termination benefit:** round(basic ÷ 30) × 120.
- **Removal benefit:** basic ÷ 2 × yrs.
- **Retirement:** basic × yrs; +1 yr (Management: 5 yrs 6 months; others: 240+ fraction days).
- **Death:** basic × (yrs + 1) with 6+ extra months and more than 1 year, else basic × yrs; plus retirement basic × yrs.
- **Expiration (Management death, 5+ yrs):** 5–9 yrs basic ÷ 10 × yrs; 10+ yrs basic × yrs.
- **Discharge:** basic × yrs (1+ yr).
- **Earned leave days:** 1 day per 18 worked days (types 2–5); Management by month or calendar day. Cap 40 (Non-Management), 60 (others). Under 365 days of service: no leave earned.
- **Earned leave amount:** Management basic ÷ 30 × days; others gross ÷ 30 × days (basic ÷ 30 if negative).
- **Notice pay (Retrenchment):** basic ÷ 30 × notice days.
- **Short-notice deduction:** basic ÷ 30 × days short, at most 60, only when IsWaive = 0. ⚠
- **Stamp fee:** payment mode 2 (Cash) and net over 1,000: 20, or 50 when net is over 50,000.
- **Amounts** are rounded to whole taka.
