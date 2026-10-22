#!/usr/bin/env node
// Matrix fixtures — synthetic workbooks with the structure and quirks of the
// real files (docs/MATRIX_PROMPT.md §2.2, rule 4).
//
//   node scripts/matrix/make-fixtures.mjs [outDir]
//
// Every name, ID, phone number and amount here is invented by a seeded random
// generator; nothing is read from a real file. The planted quirks are counted
// as they are made and written to manifest.json, which the eval compares the
// server's findings against.

import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import { writeWorkbook, colLetter } from './xlsx-lite.mjs'

// ─────────────────────────────── helpers ───────────────────────────────

function rng(seed) {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6d2b79f5) >>> 0
    let t = a
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}
const R = rng(20260519)
const int = (lo, hi) => lo + Math.floor(R() * (hi - lo + 1))
const pick = (arr) => arr[Math.floor(R() * arr.length)]
const chance = (p) => R() < p

// Invented syllables — not real names.
const SYL1 = ['Ar', 'Be', 'Ka', 'Do', 'Fi', 'Gu', 'Ha', 'Jo', 'Lu', 'Mi', 'No', 'Pa', 'Ra', 'Sa', 'Ta', 'Vi', 'Za', 'Qe', 'Xo', 'Yu']
const SYL2 = ['ron', 'lin', 'dar', 'mesh', 'nal', 'bir', 'sul', 'tan', 'vex', 'quor', 'zim', 'pell']
const BN1 = ['অর', 'বেল', 'কান', 'দোর', 'ফিল', 'গুম', 'হার', 'জোন', 'লুক', 'মিল']
const BN2 = ['ভান', 'জিত', 'সুম', 'তার', 'নীল', 'রুপ']
const fakeName = () => `${pick(SYL1)}${pick(SYL2)} ${pick(SYL1)}${pick(SYL2)}`
const fakeNameBn = () => `${pick(BN1)}${pick(BN2)} ${pick(BN1)}${pick(BN2)}`
const digits = (n) => Array.from({ length: n }, () => int(0, 9)).join('')

const MON = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const day = (y, m, d) => new Date(Date.UTC(y, m, d))
const randDate = (y0, y1) => day(int(y0, y1), int(0, 11), int(1, 28))
const ddMMMyyyy = (d) => `${String(d.getUTCDate()).padStart(2, '0')}-${MON[d.getUTCMonth()]}-${d.getUTCFullYear()}`
const isoText = (d) => `${d.toISOString().slice(0, 10)} 00:00:00`
const iso = (d) => d.toISOString().slice(0, 10)

/**
 * Synthetic employee IDs, six digits. Most are 5xxxxx–9xxxxx (no leading zero);
 * a few hundred are 0004xx–0006xx (with leading zeros). Real IDs use
 * neither band (the eval confirms there is no overlap without printing any), so
 * a fixture can never carry a real person's number even by chance.
 */
const usedIds = new Set()
let zeroIds = 0
function newId() {
  for (;;) {
    const zeros = zeroIds < 280 && chance(0.04)
    const id = zeros ? String(int(400, 699)).padStart(6, '0') : String(int(500000, 999999))
    if (usedIds.has(id)) continue
    usedIds.add(id)
    if (zeros) zeroIds++
    return id
  }
}

// ─────────────────────────────── the organisation ───────────────────────────────

const UNITS = ['FX-Unit-1', 'FX-Unit-2', 'FX-Unit-3', 'FX-Unit-4']
const LINES = Array.from({ length: 53 }, (_, i) => 101 + i)           // "LINE - 101" … "LINE - 153"
const SEWING_DESIGNATIONS = ['Sewing Operator', 'Sr. Sewing Operator', 'Helper', 'Quality Inspector', 'Line Supervisor', 'Line Chief']
const OTHER_SECTIONS = {
  Cutting: ['Cutter', 'Cutting Helper', 'Cutting Supervisor'],
  Finishing: ['Ironman', 'Finishing Helper', 'Folder', 'Quality Controller'],
  Packing: ['Packer', 'Packing Helper'],
  'Button Stitch': ['Button Operator', 'Helper'],
  'House Keeping': ['Cleaner'],
  Maintenance: ['Mechanic', 'Electrician'],
  Admin: ['Officer', 'Sr. Officer', 'Executive', 'Manager'],
}
// Two designations no lookup row covers (the "missing from the equivalency lookup" plant).
const MISSING_FROM_LOOKUP = ['Mechanic Asst', 'Line Iron Man']
// The employee types of the real files: the equivalency lookup covers the first two (staff and management).
const EMP_TYPES = ['Management', 'Non-Management', 'Schedule-A', 'Schedule-B']

// ─────────────────────────────── master file ───────────────────────────────

const MASTER_HEADERS = [
  'EmpID', 'PunchNo', 'Name', 'NameBan', 'CompanyName', 'UnitId', 'UnitName', 'DepartmentName', 'SectionName',
  'WingName', 'TeamName', 'DesignationName', 'DesignationNameBan', 'GradeId', 'EmpTypeName', 'EmpCategoryName',
  'EmpStatusName', 'Gender', 'JoiningDate', 'ConfirmStatus', 'ProbationDate', 'DateOfBirth', 'GrossSalary',
  'Basic', 'HouseRent', 'Medical', 'Conveyance', 'Food', 'PaymentMode', 'NIDNo', 'BirthCertificateNo', 'BGMEAID',
  'BankAccountNo', 'BankName', 'BranchName', 'Mobile', 'Email', 'PresentAddress', 'PermanentAddress',
  'PresentVillage', 'FathersName', 'MothersName', 'SpouseName', 'ReligionName', 'BloodGroupName', 'EmergContact',
  'EmergContactName', 'MachineCode', 'LineChiefRemarks',
]

function masterPerson(i) {
  const sewing = i % 10 < 7
  let section, wing, designation
  if (sewing) {
    section = 'Sewing'
    wing = `LINE - ${LINES[i % LINES.length]}`
    designation = SEWING_DESIGNATIONS[i % 9 < 5 ? 0 : i % 9 < 7 ? 2 : (i % 9) - 4]
  } else {
    section = pick(Object.keys(OTHER_SECTIONS))
    wing = `${section} Floor-${int(1, 3)}`
    designation = pick(OTHER_SECTIONS[section])
  }
  const id = newId()
  const joining = randDate(2008, 2025)
  const gross = int(9, 40) * 500 + 2450
  const basic = Math.round(((gross - 2450) / 1.5) * 100) / 100
  return {
    EmpID: id, PunchNo: Number(id), Name: fakeName(), NameBan: fakeNameBn(), CompanyName: 'FX Apparels Ltd.',
    UnitId: UNITS.indexOf(pick(UNITS)) + 1, UnitName: null, DepartmentName: sewing ? 'Production' : pick(['Production', 'Admin', 'Maintenance']),
    SectionName: section, WingName: wing, TeamName: pick(['A', 'B', 'C']), DesignationName: designation,
    DesignationNameBan: `${pick(BN1)}${pick(BN2)}`, GradeId: int(1, 9), EmpTypeName: sewing ? (i % 20 === 0 ? 'Schedule-B' : 'Schedule-A') : pick(EMP_TYPES),
    EmpCategoryName: pick(['Permanent', 'Probation']), EmpStatusName: 'Active', Gender: chance(0.62) ? 'Female' : 'Male',
    JoiningDate: ddMMMyyyy(joining), ConfirmStatus: pick(['Confirmed', 'Pending']),
    ProbationDate: ddMMMyyyy(new Date(joining.getTime() + 180 * 86400000)), DateOfBirth: ddMMMyyyy(randDate(1970, 2005)),
    GrossSalary: gross, Basic: basic, HouseRent: Math.round(basic * 0.5), Medical: 750, Conveyance: 450, Food: 1250,
    PaymentMode: pick(['Bank', 'Cash', 'MFS']), NIDNo: digits(10), BirthCertificateNo: chance(0.1) ? '0' + digits(16) : null,
    BGMEAID: chance(0.4) ? 'FXB' + digits(6) : null, BankAccountNo: digits(13), BankName: 'FX Bank', BranchName: 'FX Branch',
    Mobile: '01' + digits(9), Email: chance(0.3) ? `${pick(SYL1).toLowerCase()}${digits(3)}@example.invalid` : null,
    PresentAddress: `House ${int(1, 99)}, Road ${int(1, 20)}, FX Town`, PermanentAddress: `Village ${pick(SYL1)}${pick(SYL2)}, FX District`,
    PresentVillage: `${pick(SYL1)}${pick(SYL2)}pur`, FathersName: fakeName(), MothersName: fakeName(),
    SpouseName: chance(0.5) ? fakeName() : null, ReligionName: pick(['Religion A', 'Religion B']),
    BloodGroupName: pick(['A+', 'B+', 'O+', 'AB+']), EmergContact: '01' + digits(9), EmergContactName: fakeName(),
    MachineCode: sewing ? `MC-${int(100, 999)}` : null, LineChiefRemarks: chance(0.05) ? 'ok' : null,
  }
}

function makeMaster(dir, M) {
  const N = 8514
  const people = Array.from({ length: N }, (_, i) => masterPerson(i))
  for (const p of people) p.UnitName = UNITS[p.UnitId - 1]

  // Plants, each counted.
  // 4 rows repeat an existing EmpID (duplicate emp_id).
  for (let k = 0; k < 4; k++) { const src = people[100 + k]; people[N - 1 - k].EmpID = src.EmpID; people[N - 1 - k].PunchNo = src.PunchNo }
  // 2 rows write an existing ID without its leading zeros ("000512" written "512").
  const withZeros = people.filter((p, i) => i > 200 && i < 1000 && p.EmpID.startsWith('00')).slice(0, 2)
  const stripped = people.slice(2000, 2002)
  stripped.forEach((p, k) => { p.EmpID = String(Number(withZeros[k].EmpID)); p.PunchNo = Number(withZeros[k].EmpID) })
  // Mixed date formats: 5 joining dates as "2021-10-05 00:00:00".
  people.slice(300, 305).forEach((p) => { p.JoiningDate = isoText(randDate(2015, 2022)) })
  // Near-duplicate categories: 3 rows "Housekeeping" beside "House Keeping".
  const hk = people.filter((p) => p.SectionName === 'House Keeping')
  hk.slice(0, 3).forEach((p) => { p.SectionName = 'Housekeeping' })
  // Trailing spaces: 7 designations "Helper ".
  people.filter((p) => p.DesignationName === 'Helper').slice(0, 7).forEach((p) => { p.DesignationName = 'Helper ' })
  // Empty required fields: 3 units, 2 statuses, 1 emp_id.
  people.slice(400, 403).forEach((p) => { p.UnitName = null })
  people.slice(410, 412).forEach((p) => { p.EmpStatusName = null })
  people[420].EmpID = null
  // Designations missing from the lookup: two titles, 6 rows.
  // On Management rows: the lookup check covers Management and Non-Management only.
  people.slice(500, 503).forEach((p) => { p.DesignationName = MISSING_FROM_LOOKUP[0]; p.EmpTypeName = 'Management' })
  people.slice(503, 506).forEach((p) => { p.DesignationName = MISSING_FROM_LOOKUP[1]; p.EmpTypeName = 'Non-Management' })
  // A worker designation missing from the lookup AND without a GradeId: the lookup check
  // ignores it (workers are out of its scope); the worker-grade check counts it, once.
  people.slice(600, 603).forEach((p) => { p.DesignationName = 'Thread Cutter'; p.EmpTypeName = 'Schedule-A'; p.GradeId = null })
  // One grade written two ways (the real master mixes "3" and "Grade 3"): 9 cells as text "Grade N", 2 as "Grade 0N".
  people.slice(700, 709).forEach((p) => { p.GradeId = `Grade ${p.GradeId}` })
  people.slice(709, 711).forEach((p) => { p.GradeId = `Grade 0${p.GradeId}` })

  const row = (p) => MASTER_HEADERS.map((h) => p[h] ?? null)
  const hrm = [MASTER_HEADERS, ...people.map(row)]

  // HRM (2): an older copy, the last 29 people missing.
  const hrm2 = [MASTER_HEADERS, ...people.slice(0, N - 29).map(row)]

  // For corrcetion: HRM plus 537 more people and two unnamed columns.
  const extra = Array.from({ length: 537 }, (_, i) => masterPerson(N + i)).map((p) => { p.UnitName = UNITS[p.UnitId - 1]; return p })
  const corrHeaders = [...MASTER_HEADERS, 'Column1', 'Column2']
  const corr = [corrHeaders, ...[...people, ...extra].map((p, i) => [...row(p), i % 50 === 0 ? 'x' : null, null])]

  // unit : 4 units, trailing space in the sheet name.
  const unit = [['UnitId', 'UnitName', 'UnitAddress'], ...UNITS.map((u, i) => [i + 1, u, `${i + 1} FX Road, FX Town`])]

  // Sheet1: 30 columns, Current Salary in R, Basic = (R-2450)/1.5 with cached values; 3 cached values wrong.
  const S1 = ['EmpID', 'Name', 'DesignationName', 'SectionName', 'WingName', 'Present Days', 'Absent Days', 'Leave Days',
    'Holidays', 'Weekend', 'Payable Days', 'OT Hours', 'OT Rate', 'OT Amount', 'Attendance Bonus', 'Stamp', 'Advance',
    'Current Salary', 'Basic', 'House Rent', 'Medical', 'Conveyance', 'Food', 'Gross', 'Net Pay', 'Bank Account',
    'Payment Mode', 'Remarks', 'Signature', 'Month']
  const salarySheet = [S1]
  const wrongRows = new Set([5, 77, 600])
  people.slice(0, 1168).forEach((p, i) => {
    const r = i + 2
    const basic = Math.round(((p.GrossSalary - 2450) / 1.5) * 100) / 100
    salarySheet.push([
      p.EmpID, p.Name, p.DesignationName, p.SectionName, p.WingName, int(20, 26), int(0, 3), int(0, 2), int(0, 2), 4, 26,
      int(0, 40), 50, null, 500, 10, 0,
      p.GrossSalary,
      { f: `(R${r}-2450)/1.5`, v: wrongRows.has(i) ? basic + 100 : basic },
      { f: `S${r}*0.5`, v: Math.round(basic * 0.5 * 100) / 100 },
      750, 450, 1250, { f: `R${r}`, v: p.GrossSalary }, p.GrossSalary - 10, p.BankAccountNo, p.PaymentMode, null, null, 'May-2026',
    ])
  })

  writeWorkbook(path.join(dir, 'fx-master.xlsx'), [
    { name: 'HRM', rows: hrm }, { name: 'HRM (2)', rows: hrm2 }, { name: 'For corrcetion', rows: corr },
    { name: 'unit ', rows: unit }, { name: 'Sheet1', rows: salarySheet },
  ])

  const sewing = people.filter((p) => p.SectionName === 'Sewing')
  M.master = {
    file: 'fx-master.xlsx',
    sheets: { HRM: { rows: N, cols: MASTER_HEADERS.length }, 'HRM (2)': { rows: N - 29, cols: MASTER_HEADERS.length },
      'For corrcetion': { rows: N + 537, cols: corrHeaders.length }, 'unit ': { rows: 4, cols: 3 }, Sheet1: { rows: 1168, cols: 30 } },
    copies: { 'HRM (2)': 'HRM', 'For corrcetion': 'HRM' },
    sewingLines: new Set(sewing.map((p) => p.WingName)).size,
    sensitiveHeaders: ['NIDNo', 'BirthCertificateNo', 'BGMEAID', 'BankAccountNo', 'BankName', 'BranchName', 'Mobile', 'Email',
      'PresentAddress', 'PermanentAddress', 'PresentVillage', 'FathersName', 'MothersName', 'SpouseName', 'ReligionName',
      'BloodGroupName', 'EmergContact', 'EmergContactName', 'DateOfBirth'],
    unknownHeaders: ['MachineCode', 'LineChiefRemarks'],
    plants: {
      dup_emp_id: 4, id_leading_zeros: 2, mixed_date_formats: 5, near_duplicate_values: 1, trailing_spaces: 7,
      empty_required: { unit: 3, status: 2, emp_id: 1 }, designation_not_in_lookup: 2, worker_designation_no_grade: 1, grade_forms_unified: 11, formula_mismatch: 3,
      // After the newer lookup (fx-designation-2.xlsx) adds one of the two: the re-check leaves 1.
      designation_after_recheck: 1,
      cross_sheet_person: (() => {
        const a = new Set(people.map((p) => p.EmpID).filter(Boolean))
        const b = new Set(people.slice(0, N - 29).map((p) => p.EmpID).filter(Boolean))
        return [...a].filter((x) => b.has(x)).length
      })(),
    },
    // Sample cells for the plants, for the eval to compare refs against.
    refs: {
      dup_emp_id: [N - 2, N - 1, N, N + 1].map((r) => `HRM!A${r}`),
      trailing_spaces: people.map((p, i) => (p.DesignationName === 'Helper ' ? `HRM!${colLetter(MASTER_HEADERS.indexOf('DesignationName'))}${i + 2}` : null)).filter(Boolean),
      formula_mismatch: [...wrongRows].map((i) => `Sheet1!S${i + 2}`),
    },
    // For the PunchNo normaliser: every row's PunchNo padded to six digits is its EmpID (the stripped plants excepted).
    punchPadsToEmpId: people.filter((p) => p.EmpID && String(p.PunchNo).padStart(6, '0') === p.EmpID).length,
    punchRows: people.filter((p) => p.EmpID).length,
  }
  return people
}

// ─────────────────────────────── attendance ───────────────────────────────

function makeAttendance(dir, M, people, name, dateText, seedShift = 0) {
  const ATT = ['SL.', 'Emp ID', 'Name', 'Designation', 'Department', 'Shift ', 'In Time', 'Out Time', 'OT Hours', 'Status', 'Deduction OT ']
  const pool = people.filter((p) => p.EmpID && p.EmpID.length === 6)
  const chosen = pool.slice(seedShift, seedShift + 650)
  const outsiders = Array.from({ length: 26 }, () => ({ EmpID: newId(), Name: fakeName(), DesignationName: 'Helper', DepartmentName: 'Production' }))
  const all = [...chosen, ...outsiders]
  const time = (h, m, s) => {
    const ap = h >= 12 ? 'PM' : 'AM'
    const hh = h % 12 === 0 ? 12 : h % 12
    return `${String(hh).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')} ${ap} `
  }
  let late = 0, otOver2 = 0, otAsDate = 0
  const rows = [['Daily Attendance Details Report'], [dateText], ATT]
  all.forEach((p, i) => {
    const isLate = i % 17 === 0
    if (isLate) late++
    const inH = isLate ? 8 : 7
    const ot = [0, 0, 1, 2, 2, 3, 4][i % 7]
    if (ot > 2) otOver2++
    const asDate = i === 10 || i === 20
    if (asDate) otAsDate++
    rows.push([
      i + 1, p.EmpID, p.Name.padEnd(p.Name.length + (i % 2), ' '), p.DesignationName.trim(), p.DepartmentName,
      ['G(WKR)', 'GPC', 'ABN'][i % 3], time(inH, int(0, 59), int(0, 59)), time(17 + (ot > 0 ? ot : 0), int(0, 59), int(0, 59)),
      asDate ? { number: ot, asDate: true } : ot, isLate ? 'LT' : 'P', 0,
    ])
  })
  writeWorkbook(path.join(dir, name), [{ name: 'rptDailyAttDetailsReport_Factor', rows, merges: ['A1:K1', 'A2:K2'] }])
  return { file: name, rows: all.length, cols: ATT.length, headerRow: 3, late, present: all.length, otOver2,
    numbersAsDates: otAsDate, notInMaster: outsiders.length }
}

// ─────────────────────────────── designation lookup ───────────────────────────────

function makeDesignation(dir, M) {
  const known = new Set([...SEWING_DESIGNATIONS, ...Object.values(OTHER_SECTIONS).flat()])
  const rows = [['DesignationName', 'EquivalencyName', 'EquivalencyGradeName']]
  const names = [...known]
  // Pad to 165 rows with invented titles, two with typos, four with trailing spaces.
  for (let i = 0; names.length < 163; i++) names.push(`${pick(['Asst.', 'Jr.', 'Sr.', 'Deputy', 'Chief'])} ${pick(['Planner', 'Merchandiser', 'Designer', 'Architect', 'Analyst', 'Engineer'])} ${i}`)
  names.push('Asssistant Planner', 'Trainee Designr')
  let trailing = 0
  names.forEach((n, i) => {
    const grade = `M${1 + (i % 14)}`
    const withSpace = i % 41 === 7 && trailing < 4
    if (withSpace) trailing++
    rows.push([withSpace ? n + ' ' : n, `Equivalent ${grade}`, grade])
  })
  writeWorkbook(path.join(dir, 'fx-designation.xlsx'), [{ name: 'Sheet1', rows }])
  M.designationRows = rows
  M.designation = { file: 'fx-designation.xlsx', rows: names.length, cols: 3, trailing_spaces: trailing, grades: 14 }
}

// ─────────────────────────────── HR basic details (separations) ───────────────────────────────

function makeHrBasic(dir, M) {
  const H = ['NAME', 'EmpCode', 'UnitName', 'DepartmentName', 'DesignationName', 'SectionName', 'EmpTypeName', 'JoiningDate',
    'EmpStatusName', 'EffectDate', 'GrossSalary', 'SALARY PAID COMPANY']
  const STATUS = ['Active', 'Left', 'Resignation', 'Removal', 'Death', 'Retirement', 'Unauthorized Absence (UA)', 'Dismissal', 'Termination']
  const rows = [H]
  let nullText = 0, sepBefore = 0
  for (let i = 0; i < 345; i++) {
    const joining = randDate(2012, 2024)
    const status = i < 133 ? 'Active' : STATUS[1 + (i % 8)]
    let effect = status === 'Active' ? 'NULL' : { date: new Date(joining.getTime() + int(30, 900) * 86400000) }
    if (status !== 'Active' && sepBefore < 4 && i % 37 === 0) { effect = { date: new Date(joining.getTime() - 40 * 86400000) }; sepBefore++ }
    const section = i < 112 ? 'NULL' : pick(['Sewing', 'Cutting', 'Finishing', 'Packing'])
    const gross = i >= 342 ? 'NULL' : int(9, 40) * 500 + 2450
    const payer = i >= 342 ? 'NULL' : 'FX Apparels Ltd.'
    for (const v of [effect, section, gross, payer]) if (v === 'NULL') nullText++
    rows.push([fakeName() + ' ', 1000 + i, pick(UNITS), 'Production', pick(SEWING_DESIGNATIONS), section,
      pick(EMP_TYPES), { date: joining }, status, effect, gross, payer])
  }
  writeWorkbook(path.join(dir, 'fx-hr-basic.xlsx'), [{ name: 'Sheet1', rows }])
  M.hrBasic = { file: 'fx-hr-basic.xlsx', rows: 345, cols: H.length, null_text: nullText, separation_before_joining: sepBefore, statuses: STATUS.length }
}

// ─────────────────────────────── manpower (Bengali pairs) ───────────────────────────────

// The real file's 59 headers, in order (column names only).
const MANPOWER_HEADERS = ['CompanyName', 'CompanyNameBan', 'UnitName', 'UnitNameBan', 'UnitTypeName', 'Employee ID', 'Name', 'NameBan',
  'DesignationName', 'DesignationNameBan', 'FathersName', 'MothersName', 'DepartmentName', 'DepartmentNameBan', 'SectionName',
  'SectionNameBan', 'WingName', 'WingNameBan', 'TeamName', 'TeamNameBan', 'DesignationSpecification', 'PositionName', 'GradeId',
  'JobLocationName', 'JoiningDate', 'ProbationDate', 'DateOfBirth', 'WorkDurationYears', 'WorkDurationDetails', 'AgeYears',
  'AgeDetails', 'Gender', 'EmpTypeName', 'EmpCategoryName', 'EmpStatusName', 'SeparationEffectDate', 'IsApproved', 'BGMEAID',
  'BirthCertificateNo', 'NIDNo', 'DegreeName', 'Institute', 'BloodGroupName', 'ReligionName', 'Mobile', 'Phone', 'PABX', 'Email',
  'EmailOffice', 'EmergContact', 'EmergContactAddress', 'EmergContactName', 'IsFixed_A', 'SalaryCompany', 'LastDesignationName',
  'LastIncEffectDate_A', 'LastSplAllIncEffectDate_A', 'CurrentFixedShift', 'WeekOffDay']

function makeManpower(dir, M, file, reportDate, count = 1173) {
  const TYPES = ['Corporate', 'Central (G)', "Garment's", 'Washing', 'Woven', 'Knit', 'Yarn']
  const rows = [MANPOWER_HEADERS]
  for (let i = 0; i < count; i++) {
    const joining = randDate(2000, 2025)
    const dob = randDate(1965, 2004)
    const years = Math.floor((reportDate - joining) / (365.25 * 86400000))
    const section = pick(['Sewing', 'Cutting', 'Finishing', 'Accounts', 'HR', null])
    const v = {
      CompanyName: 'FX Group', CompanyNameBan: chance(0.8) ? 'এফএক্স গ্রুপ' : null, UnitName: pick(UNITS), UnitNameBan: 'এফএক্স ইউনিট',
      UnitTypeName: pick(TYPES) + (chance(0.1) ? ' ' : ''), 'Employee ID': newId(), Name: fakeName() + ' ', NameBan: fakeNameBn(),
      DesignationName: pick(['Officer', 'Executive', 'Manager', 'Sewing Operator']), DesignationNameBan: 'পদবি',
      FathersName: fakeName(), MothersName: fakeName(), DepartmentName: pick(['Production', 'Admin', 'Finance', 'HR']),
      DepartmentNameBan: 'বিভাগ', SectionName: section, SectionNameBan: section ? 'শাখা' : null,
      WingName: section === 'Sewing' ? `LINE - ${pick(LINES)}` : null, WingNameBan: null, TeamName: null, TeamNameBan: null,
      DesignationSpecification: null, PositionName: null, GradeId: int(1, 8), JobLocationName: pick(['FX Town', 'FX Port']),
      JoiningDate: { date: joining }, ProbationDate: { date: new Date(joining.getTime() + 180 * 86400000) }, DateOfBirth: { date: dob },
      WorkDurationYears: years, WorkDurationDetails: `${years} Year(s)-${int(0, 11)} Month(s)-${int(0, 29)} Day(s)`,
      AgeYears: Math.floor((reportDate - dob) / (365.25 * 86400000)), AgeDetails: `${int(18, 60)} Year(s)`, Gender: pick(['Male', 'Female']),
      EmpTypeName: pick(EMP_TYPES), EmpCategoryName: pick(['Permanent', 'Probation', 'Contractual']), EmpStatusName: 'Active',
      SeparationEffectDate: null, IsApproved: 'Yes', BGMEAID: chance(0.3) ? 'FXB' + digits(6) : null,
      BirthCertificateNo: chance(0.1) ? '0' + digits(16) : null, NIDNo: digits(10), DegreeName: chance(0.5) ? 'B.A.' : null,
      Institute: chance(0.5) ? 'FX College' : null, BloodGroupName: pick(['A+', 'B+', 'O+']), ReligionName: pick(['Religion A', 'Religion B']),
      Mobile: '01' + digits(9), Phone: null, PABX: chance(0.2) ? digits(3) : null, Email: null, EmailOffice: null,
      EmergContact: '01' + digits(9), EmergContactAddress: 'FX Town', EmergContactName: fakeName(), IsFixed_A: chance(0.5),
      SalaryCompany: 'FX Apparels Ltd.', LastDesignationName: null, LastIncEffectDate_A: { date: randDate(2020, 2025) },
      LastSplAllIncEffectDate_A: null, CurrentFixedShift: pick(['General', 'Morning', 'Evening']), WeekOffDay: pick(['Friday', 'Saturday']),
    }
    rows.push(MANPOWER_HEADERS.map((h) => v[h] ?? null))
  }
  writeWorkbook(path.join(dir, file), [{ name: 'Sheet1', rows }])
  return { file, rows: count, cols: MANPOWER_HEADERS.length, sheetRows: rows }
}

// ─────────────────────────────── a two-row header ───────────────────────────────

function makeMultiHeader(dir, M) {
  const rows = [
    ['Line', 'Total Machine', null, 'Operators'],
    [null, 'Hours', 'Count', null],
    ['LINE - 101', 120, 12, 30], ['LINE - 102', 96, 10, 26], ['LINE - 103', 108, 11, 28],
  ]
  writeWorkbook(path.join(dir, 'fx-multiheader.xlsx'), [{ name: 'Machines', rows, merges: ['B1:C1', 'A1:A2', 'D1:D2'] }])
  M.multiHeader = { file: 'fx-multiheader.xlsx', headers: ['Line', 'Total Machine Hours', 'Total Machine Count', 'Operators'], rows: 3 }
}

// ─────────────────────────────── main ───────────────────────────────

export function makeFixtures(dir) {
  fs.mkdirSync(dir, { recursive: true })
  const M = {}
  const people = makeMaster(dir, M)
  M.attendance = makeAttendance(dir, M, people, 'fx-attendance.xlsx', '(19-May-2026 To 19-May-2026)')
  M.attendance.reportDate = '2026-05-19'
  M.attendance2 = makeAttendance(dir, M, people, 'fx-attendance-2.xlsx', '(20-May-2026 To 20-May-2026)', 40)
  M.attendance2.reportDate = '2026-05-20'
  // 21 May: no one here is in the master, so attendance % cannot be computed (the real files' case).
  const strangers = Array.from({ length: 40 }, () => ({ EmpID: String(100000 + int(0, 99999)), Name: fakeName(), DesignationName: 'Operator', DepartmentName: 'Production' }))
  M.attendance3 = makeAttendance(dir, M, strangers, 'fx-attendance-3.xlsx', '(21-May-2026 To 21-May-2026)', 0)
  M.attendance3.reportDate = '2026-05-21'
  makeDesignation(dir, M)
  makeHrBasic(dir, M)
  M.manpower = makeManpower(dir, M, 'fx-manpower.xlsx', day(2026, 4, 1))
  makeMultiHeader(dir, M)
  // A newer designation lookup of the same layout that adds one missing designation (no random draws: the others stay as they were).
  writeWorkbook(path.join(dir, 'fx-designation-2.xlsx'), [{ name: 'Sheet1', rows: [...M.designationRows, [MISSING_FROM_LOOKUP[0], 'Equivalent M3', 'M3']] }])
  M.designation2 = { file: 'fx-designation-2.xlsx', rows: M.designationRows.length }
  delete M.designationRows

  // A month later (M2's snapshots): 40 people have left, 25 have joined, the rest are the same people.
  // Made last, so its random draws change no other fixture.
  const may = M.manpower.sheetRows
  delete M.manpower.sheetRows
  const idCol = MANPOWER_HEADERS.indexOf('Employee ID'), nameCol = MANPOWER_HEADERS.indexOf('Name')
  const leaving = new Set(may.slice(1).map((r) => r[idCol]).filter((_, i) => i % 29 === 3).slice(0, 40))
  const june = [may[0], ...may.slice(1).filter((r) => !leaving.has(r[idCol]))]
  for (let i = 0; i < 25; i++) {
    const r = [...may[1 + i * 7]]
    r[idCol] = newId()
    r[nameCol] = fakeName() + ' '
    june.push(r)
  }
  writeWorkbook(path.join(dir, 'fx-manpower-2.xlsx'), [{ name: 'Sheet1', rows: june }])
  M.manpower2 = { file: 'fx-manpower-2.xlsx', rows: june.length - 1, leavers: leaving.size, joiners: 25,
    reportDate: '2026-06-01', previousReportDate: '2026-05-01' }
  fs.writeFileSync(path.join(dir, 'manifest.json'), JSON.stringify(M, null, 2))
  return M
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const dir = path.resolve(process.argv[2] ?? path.join(os.tmpdir(), 'matrix-eval', 'fixtures'))
  const M = makeFixtures(dir)
  console.log(`fixtures in ${dir}: ${Object.values(M).map((m) => m.file).join(', ')}`)
}
