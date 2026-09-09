/**
 * JSON → table model.
 *
 * Pure functions, no Vue: the shape of a table is a data question, and keeping
 * it out of the component makes the rowspan arithmetic — the part that is easy
 * to get subtly wrong — testable on its own.
 *
 * The vocabulary used throughout:
 *   record     one entry in the source array
 *   scalar     a leaf field on the record (string/number/bool/null, or an
 *              object flattened to dotted paths)
 *   group      a nested array of objects, e.g. Education or Experience
 *   rowHeight  how many table rows one record occupies:
 *              max(length of each of its groups, 1)
 */

import { formatDateValue, dateSortKey } from './jsondate.js'

/** UTF-8 BOM. Files exported from Excel and many Windows tools start with it. */
const BOM = '﻿'

/**
 * JSON.parse with the two failures that actually happen in this workflow handled:
 * a leading BOM (which JSON.parse rejects) and an error message that says
 * nothing about *where* the problem is.
 *
 * @param {string} text
 * @returns {{ok: true, value: any} | {ok: false, error: string}}
 */
export function parseJson(text) {
  const src = typeof text === 'string' ? text.replace(/^﻿/, '') : ''

  if (!src.trim()) return { ok: false, error: 'Nothing to parse — paste or upload some JSON.' }

  try {
    return { ok: true, value: JSON.parse(src) }
  } catch (e) {
    return { ok: false, error: describeParseError(e, src) }
  }
}

/**
 * Turns "Unexpected token } in JSON at position 412" into a line/column the user
 * can actually navigate to. Falls back to the raw message when the engine gives
 * no position (message wording differs across browsers).
 */
function describeParseError(e, src) {
  const raw = e?.message || 'Invalid JSON.'

  // V8 and Firefox both surface a character offset, in different phrasings.
  const posMatch = /position (\d+)/i.exec(raw)
  const lineColMatch = /line (\d+) column (\d+)/i.exec(raw)

  if (lineColMatch) return `Invalid JSON at line ${lineColMatch[1]}, column ${lineColMatch[2]}. ${raw}`

  if (posMatch) {
    const pos = Number(posMatch[1])
    const before = src.slice(0, pos)
    const line = before.split('\n').length
    const column = pos - before.lastIndexOf('\n')
    const snippet = src.slice(Math.max(0, pos - 30), pos + 30).replace(/\s+/g, ' ').trim()
    return `Invalid JSON at line ${line}, column ${column}: ${raw}\n…${snippet}…`
  }

  return `Invalid JSON: ${raw}`
}

/**
 * Every array-of-objects the parsed value could be read as rows.
 *
 * A bare array is the whole answer. An object may hold several candidate arrays
 * (a common export shape is `{ employees: [...], meta: {...} }`), so the caller
 * is offered the choice rather than the first one being guessed at.
 *
 * @returns {Array<{path: string, label: string, count: number}>}
 */
export function findRowSets(value) {
  if (Array.isArray(value)) {
    return [{ path: '', label: '(root array)', count: value.length }]
  }

  if (!isPlainObject(value)) return []

  const sets = []
  for (const [key, v] of Object.entries(value)) {
    if (Array.isArray(v) && v.length && v.some(isPlainObject)) {
      sets.push({ path: key, label: key, count: v.length })
    }
  }

  // Nothing at the top level — look one level down, which covers
  // `{ data: { employees: [...] } }`.
  if (!sets.length) {
    for (const [key, v] of Object.entries(value)) {
      if (!isPlainObject(v)) continue
      for (const [k2, v2] of Object.entries(v)) {
        if (Array.isArray(v2) && v2.length && v2.some(isPlainObject)) {
          sets.push({ path: `${key}.${k2}`, label: `${key}.${k2}`, count: v2.length })
        }
      }
    }
  }

  return sets.sort((a, b) => b.count - a.count)
}

/** Reads a dotted path out of a parsed value. An empty path means the value itself. */
export function selectRowSet(value, path) {
  if (!path) return Array.isArray(value) ? value : []
  const found = path.split('.').reduce((acc, k) => (acc == null ? acc : acc[k]), value)
  return Array.isArray(found) ? found : []
}

/**
 * Builds the column schema by inspecting every record.
 *
 * Every record is scanned, not just the first: real exports omit fields on some
 * rows, and a schema taken from record 0 would silently drop those columns.
 *
 * @param {object[]} records
 * @returns {{scalars: Column[], groups: Group[]}}
 */
export function buildSchema(records) {
  const scalarKeys = new Map()   // path -> insertion order
  const groupKeys = new Map()    // name -> Map(subPath -> order)

  // Pass 1: which keys are nested arrays anywhere in the data?
  //
  // This has to be decided across the whole set before any record is classified.
  // Nested arrays are frequently null or empty on individual records, and judging
  // a key record-by-record would classify the same field as a group on one record
  // and as a scalar on another — which is how a raw JSON blob ends up in a cell.
  const groupNames = new Set()
  for (const record of records) {
    if (!isPlainObject(record)) continue
    for (const [key, value] of Object.entries(record)) {
      if (Array.isArray(value) && value.length > 0) groupNames.add(key)
    }
  }

  // A key that is null or [] on every record is still an array field, not a
  // scalar. Registering it keeps the column present (and blank) rather than
  // letting a stray value fall through to the scalar branch later.
  for (const record of records) {
    if (!isPlainObject(record)) continue
    for (const [key, value] of Object.entries(record)) {
      if (Array.isArray(value)) groupNames.add(key)
    }
  }

  for (const name of groupNames) {
    if (!groupKeys.has(name)) groupKeys.set(name, new Map())
  }

  // Pass 2: collect the columns.
  for (const record of records) {
    if (!isPlainObject(record)) continue

    for (const [key, value] of Object.entries(record)) {
      // Group membership is decided by the key, not by this record's value, so a
      // null Education here cannot turn Education into a scalar column.
      if (groupNames.has(key)) {
        const sub = groupKeys.get(key)
        // Sub-columns are the UNION of keys across every record's rows, so a
        // ragged array (Department present on some rows only) still yields one
        // aligned column, blank where the key is absent.
        for (const item of (Array.isArray(value) ? value : [])) {
          if (!isPlainObject(item)) {
            // An array of bare values still deserves a column.
            if (!sub.has('value')) sub.set('value', sub.size)
            continue
          }
          for (const subKey of flattenKeys(item)) {
            if (!sub.has(subKey)) sub.set(subKey, sub.size)
          }
        }
        continue
      }

      // Nested plain objects become dotted scalar columns rather than their own
      // group: they contribute one value per record, so they never add rows.
      if (isPlainObject(value)) {
        for (const subKey of flattenKeys(value)) {
          const path = `${key}.${subKey}`
          if (!scalarKeys.has(path)) scalarKeys.set(path, scalarKeys.size)
        }
        continue
      }

      if (!scalarKeys.has(key)) scalarKeys.set(key, scalarKeys.size)
    }
  }

  const scalars = [...scalarKeys.keys()].map((path) => ({
    id: `s:${path}`,
    path,
    label: humanize(path),
    kind: 'scalar',
  }))

  const groups = [...groupKeys.entries()]
    .filter(([, sub]) => sub.size > 0)   // null on every record → nothing to show
    .map(([name, sub]) => ({
    id: `g:${name}`,
    name,
    label: humanize(name),
    columns: [...sub.keys()].map((subPath) => ({
      id: `g:${name}:${subPath}`,
      path: subPath,
      label: humanize(subPath),
      kind: 'group',
      group: name,
    })),
  }))

  return { scalars, groups }
}

/** All leaf paths of a plain object, dotted. Arrays inside are stringified, not expanded. */
function flattenKeys(obj, prefix = '') {
  const out = []
  for (const [key, value] of Object.entries(obj)) {
    const path = prefix ? `${prefix}.${key}` : key
    if (isPlainObject(value)) out.push(...flattenKeys(value, path))
    else out.push(path)
  }
  return out
}

/**
 * The row model both views render from.
 *
 * Each entry describes one record: its scalar values, its group rows, and how
 * many table rows it occupies. Report view reads `rowHeight` for its rowspans;
 * Data view just emits `rowHeight` rows. One model, two renderings — so the two
 * views can never disagree about what the data is.
 *
 * @returns {Array<{index: number, rowHeight: number, scalars: object, groups: object, searchText: string}>}
 */
export function buildRows(records, schema) {
  return records.map((record, index) => {
    const scalars = {}
    for (const col of schema.scalars) {
      scalars[col.path] = readPath(record, col.path)
    }

    const groups = {}
    let rowHeight = 1

    for (const group of schema.groups) {
      // null, undefined and [] all mean "no rows here" — the group's cells render
      // blank for this record rather than the column vanishing or shifting.
      const items = Array.isArray(record?.[group.name]) ? record[group.name] : []
      // Each group contributes its own rows; the record is as tall as its
      // tallest group. A record with no groups is still one row.
      rowHeight = Math.max(rowHeight, items.length)

      groups[group.name] = items.map((item) => {
        const row = {}
        for (const col of group.columns) {
          row[col.path] = isPlainObject(item) ? readPath(item, col.path) : item
        }
        return row
      })
    }

    return {
      index,
      rowHeight,
      scalars,
      groups,
      // Precomputed once so global search is a substring test per record rather
      // than a re-walk of the object on every keystroke.
      searchText: buildSearchText(scalars, groups).toLowerCase(),
    }
  })
}

function buildSearchText(scalars, groups) {
  const parts = Object.values(scalars).map(toText)
  for (const rows of Object.values(groups)) {
    for (const row of rows) parts.push(...Object.values(row).map(toText))
  }
  return parts.join('  ')
}

/** Reads a dotted path off a record. Missing intermediate objects yield undefined, never a throw. */
export function readPath(obj, path) {
  if (obj == null) return undefined
  if (!path.includes('.')) return obj[path]
  return path.split('.').reduce((acc, k) => (acc == null ? undefined : acc[k]), obj)
}

/**
 * A cell's display text.
 *
 * A table cell is for reading, so a structured value is summarised rather than
 * dumped: an object becomes "key: val; key: val" and an array becomes a short
 * joined list. Raw JSON and "[object Object]" are both unreadable, and neither
 * ever reaches a cell. null/undefined render as empty so a blank looks blank
 * instead of saying "null".
 */
export function toText(value) {
  if (value == null) return ''

  // Date-shaped strings become house style here, at the single point every
  // surface reads through — table, HTML export and CSV alike. Anything that is
  // not a date passes through untouched; nothing is ever coerced.
  if (typeof value === 'string') return formatDateValue(value)
  if (typeof value === 'number' || typeof value === 'boolean') return String(value)

  if (Array.isArray(value)) {
    if (value.length === 0) return ''
    // An array reaching a cell means it was not modelled as a column group —
    // summarise it rather than printing its JSON.
    return value.map((v) => toText(v)).filter(Boolean).join('; ')
  }

  if (typeof value === 'object') {
    const parts = []
    for (const [k, v] of Object.entries(value)) {
      if (v == null || v === '') continue
      // One level deep only: a nested object inside a cell is summarised by its
      // own values, never expanded into more "key: {…}" noise.
      const text = isPlainObject(v) || Array.isArray(v)
        ? Object.values(v).map((x) => toText(x)).filter(Boolean).join(' ')
        : toText(v)
      if (text) parts.push(`${humanize(k)}: ${text}`)
    }
    return parts.join('; ')
  }

  return String(value)
}

/** "EmpCode" → "Emp Code", "joining_date" → "Joining Date", "a.b" → "A › B". */
export function humanize(path) {
  return path
    .split('.')
    .map((seg) => seg
      .replace(/[_-]+/g, ' ')
      .replace(/([a-z\d])([A-Z])/g, '$1 $2')
      .replace(/\s+/g, ' ')
      .trim()
      .replace(/\b\w/g, (c) => c.toUpperCase()))
    .join(' › ')
}

/**
 * Applies search, per-column filters and sort. Returns the surviving rows in
 * display order.
 *
 * Filtering is per RECORD, not per sub-row: a record matches if any of its
 * values match, and it then keeps all of its rows. Dropping individual sub-rows
 * would silently misrepresent a person's history — showing 2 of someone's 7 jobs
 * without saying so is worse than showing all 7.
 *
 * @param {object} opts
 * @param {string} opts.search              global term
 * @param {Record<string,string>} opts.filters  columnId -> contains term
 * @param {{columnId: string, dir: 'asc'|'desc'} | null} opts.sort
 */
export function applyView(rows, schema, { search = '', filters = {}, sort = null } = {}) {
  const term = search.trim().toLowerCase()
  const activeFilters = Object.entries(filters)
    .map(([id, value]) => [id, String(value ?? '').trim().toLowerCase()])
    .filter(([, value]) => value.length > 0)

  let out = rows

  if (term) out = out.filter((r) => r.searchText.includes(term))

  if (activeFilters.length) {
    out = out.filter((row) => activeFilters.every(([id, value]) => cellMatches(row, id, value)))
  }

  if (sort) {
    const col = schema.scalars.find((c) => c.id === sort.columnId)
    if (col) {
      const dir = sort.dir === 'desc' ? -1 : 1
      // Sorting a copy: `rows` is the caller's array and must not be reordered
      // underneath it.
      out = [...out].sort((a, b) => dir * compareValues(a.scalars[col.path], b.scalars[col.path]))
    }
  }

  return out
}

/** Does this record match a filter on one column? Group columns match on any of their rows. */
function cellMatches(row, columnId, needle) {
  if (columnId.startsWith('s:')) {
    return toText(row.scalars[columnId.slice(2)]).toLowerCase().includes(needle)
  }

  // "g:Education:Degree"
  const rest = columnId.slice(2)
  const sep = rest.indexOf(':')
  if (sep === -1) return false

  const groupName = rest.slice(0, sep)
  const path = rest.slice(sep + 1)
  const groupRows = row.groups[groupName] || []
  return groupRows.some((r) => toText(r[path]).toLowerCase().includes(needle))
}

/**
 * Orders two cell values. Numbers compare numerically, dates chronologically,
 * everything else by locale — so a Salary column sorts 9 before 100, and an
 * ISO-ish date column sorts by time rather than by string.
 */
function compareValues(a, b) {
  const aEmpty = a == null || a === ''
  const bEmpty = b == null || b === ''
  if (aEmpty && bEmpty) return 0
  if (aEmpty) return 1          // blanks sink, in both directions
  if (bEmpty) return -1

  // Dates sort by their own Y/M/D/H/M, never by the formatted string — "02 Jul
  // 2011" sorts alphabetically nowhere near chronologically.
  const ka = dateSortKey(a)
  const kb = dateSortKey(b)
  if (ka !== null && kb !== null) return ka - kb

  if (typeof a === 'number' && typeof b === 'number') return a - b

  const na = Number(a)
  const nb = Number(b)
  if (!Number.isNaN(na) && !Number.isNaN(nb) && String(a).trim() !== '' && String(b).trim() !== '') {
    return na - nb
  }

  return String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' })
}

/**
 * Flattens the row model for Data view and CSV: one entry per sub-row, scalars
 * repeated, groups aligned by index.
 *
 * @returns {Array<{recordIndex: number, subIndex: number, cells: Record<string, any>}>}
 */
export function flatten(rows, schema) {
  const out = []
  for (const row of rows) {
    for (let i = 0; i < row.rowHeight; i++) {
      const cells = {}
      for (const col of schema.scalars) cells[col.id] = row.scalars[col.path]
      for (const group of schema.groups) {
        const item = (row.groups[group.name] || [])[i]
        for (const col of group.columns) cells[col.id] = item ? item[col.path] : undefined
      }
      out.push({ recordIndex: row.index, subIndex: i, cells })
    }
  }
  return out
}

/** Total table rows the current selection would render — the paging trigger. */
export function countRows(rows) {
  return rows.reduce((n, r) => n + r.rowHeight, 0)
}

/**
 * CSV of the flat model. Hand-rolled rather than pulled from a library: the
 * whole format here is quoting, and the rule is small enough to state exactly.
 *
 * Fields containing a quote, comma, CR or LF are wrapped in quotes with inner
 * quotes doubled (RFC 4180). CRLF line endings and a UTF-8 BOM are emitted so
 * Excel opens it with the right encoding instead of mangling non-ASCII names.
 */
export function toCsv(columns, flatRows) {
  const esc = (value) => {
    const s = toText(value)
    return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s
  }

  const header = columns.map((c) => esc(c.group ? `${c.group} - ${c.label}` : c.label)).join(',')
  const body = flatRows.map((r) => columns.map((c) => esc(r.cells[c.id])).join(','))
  return BOM + [header, ...body].join('\r\n')
}

export function isPlainObject(v) {
  return typeof v === 'object' && v !== null && !Array.isArray(v)
}
