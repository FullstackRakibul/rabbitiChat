/**
 * Date detection and formatting for the JSON → table module.
 *
 * Kept separate from the table model because it is a self-contained decision —
 * "is this string a date, and how should it read?" — and because the exported
 * standalone HTML needs the same rules re-emitted as vanilla JS. One definition,
 * two consumers.
 *
 * Everything here parses the string's OWN digits. `new Date("2011-07-02T00:00:00")`
 * is interpreted as local time by some engines and UTC by others, and reading it
 * back with getHours()/getDate() then shifts by the runtime's offset — which is
 * how a midnight date becomes the previous day for anyone west of UTC. Splitting
 * the numerals out avoids that entirely: no Date object is ever constructed for
 * display.
 */

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
  'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** Whole-string only: a date inside a sentence stays part of that sentence. */
const DATE_ONLY = /^(\d{4})-(\d{2})-(\d{2})$/
const DATE_TIME = /^(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})(?::(\d{2}))?/

/**
 * Classifies a value and returns its parts, or null when it is not a date.
 *
 * A midnight time is treated as no time at all: this export glues `T00:00:00`
 * onto fields that are conceptually pure dates (JoiningDate, DateOfBirth), and
 * showing "12:00 AM" on all of them would be noise asserting a precision the
 * data does not have.
 *
 * @returns {{kind:'date'|'datetime', y,m,d,hh,mi} | null}
 */
export function classifyDate(value) {
  if (typeof value !== 'string') return null

  const s = value.trim()
  if (!s) return null

  const dOnly = DATE_ONLY.exec(s)
  if (dOnly) {
    const [, y, m, d] = dOnly
    return isValidYmd(+y, +m, +d) ? { kind: 'date', y: +y, m: +m, d: +d, hh: 0, mi: 0 } : null
  }

  const dt = DATE_TIME.exec(s)
  if (dt) {
    const [, y, m, d, hh, mi, ss] = dt
    if (!isValidYmd(+y, +m, +d)) return null
    if (+hh > 23 || +mi > 59) return null

    const midnight = +hh === 0 && +mi === 0 && (ss === undefined || +ss === 0)
    return {
      kind: midnight ? 'date' : 'datetime',
      y: +y, m: +m, d: +d, hh: +hh, mi: +mi,
    }
  }

  return null
}

/** Rejects 2011-13-45 and 2011-02-30 — a shape match is not a real date. */
function isValidYmd(y, m, d) {
  if (m < 1 || m > 12 || d < 1 || d > 31) return false
  // Day count per month, with a leap-year February.
  const days = [31, (y % 4 === 0 && y % 100 !== 0) || y % 400 === 0 ? 29 : 28,
    31, 30, 31, 30, 31, 31, 30, 31, 30, 31]
  return d <= days[m - 1]
}

/**
 * The display form of a value: "02 Jul 2011", "01 Jan 2026 03:07 PM", or the
 * value unchanged when it is not a date.
 *
 * Anything unrecognised — null, "", "null", a number, ordinary text — is returned
 * untouched rather than coerced, so "Invalid Date" can never reach a cell.
 */
export function formatDateValue(value) {
  const parts = classifyDate(value)
  if (!parts) return value

  const day = String(parts.d).padStart(2, '0')
  const month = MONTHS[parts.m - 1]
  const base = `${day} ${month} ${parts.y}`

  if (parts.kind === 'date') return base

  // 12-hour clock: 0 and 12 both read as 12.
  const period = parts.hh >= 12 ? 'PM' : 'AM'
  const hour12 = parts.hh % 12 === 0 ? 12 : parts.hh % 12
  return `${base} ${String(hour12).padStart(2, '0')}:${String(parts.mi).padStart(2, '0')} ${period}`
}

/**
 * A sortable key for a date value: numeric, chronological, timezone-free.
 * Non-dates return null so the caller can fall back to its usual comparison.
 */
export function dateSortKey(value) {
  const p = classifyDate(value)
  if (!p) return null
  // Zero-padded concatenation compares identically to real chronology and needs
  // no Date object.
  return p.y * 1e8 + p.m * 1e6 + p.d * 1e4 + p.hh * 1e2 + p.mi
}

/**
 * The same rules as a self-contained source string, for embedding in the
 * exported standalone HTML. It has no build step and no imports, so the
 * functions travel with it rather than being re-implemented and drifting.
 */
export const DATE_RUNTIME_JS = `
var MONTHS = ${JSON.stringify(MONTHS)};
var DATE_ONLY = /^(\\d{4})-(\\d{2})-(\\d{2})$/;
var DATE_TIME = /^(\\d{4})-(\\d{2})-(\\d{2})[T ](\\d{2}):(\\d{2})(?::(\\d{2}))?/;
function isValidYmd(y, m, d) {
  if (m < 1 || m > 12 || d < 1 || d > 31) return false;
  var days = [31, ((y % 4 === 0 && y % 100 !== 0) || y % 400 === 0) ? 29 : 28,
    31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  return d <= days[m - 1];
}
function classifyDate(value) {
  if (typeof value !== 'string') return null;
  var s = value.trim();
  if (!s) return null;
  var a = DATE_ONLY.exec(s);
  if (a) {
    return isValidYmd(+a[1], +a[2], +a[3])
      ? { kind: 'date', y: +a[1], m: +a[2], d: +a[3], hh: 0, mi: 0 } : null;
  }
  var b = DATE_TIME.exec(s);
  if (b) {
    if (!isValidYmd(+b[1], +b[2], +b[3])) return null;
    if (+b[4] > 23 || +b[5] > 59) return null;
    var midnight = +b[4] === 0 && +b[5] === 0 && (b[6] === undefined || +b[6] === 0);
    return { kind: midnight ? 'date' : 'datetime',
      y: +b[1], m: +b[2], d: +b[3], hh: +b[4], mi: +b[5] };
  }
  return null;
}
function formatDateValue(value) {
  var p = classifyDate(value);
  if (!p) return value;
  var base = String(p.d).padStart(2, '0') + ' ' + MONTHS[p.m - 1] + ' ' + p.y;
  if (p.kind === 'date') return base;
  var period = p.hh >= 12 ? 'PM' : 'AM';
  var h = p.hh % 12 === 0 ? 12 : p.hh % 12;
  return base + ' ' + String(h).padStart(2, '0') + ':' + String(p.mi).padStart(2, '0') + ' ' + period;
}
function dateSortKey(value) {
  var p = classifyDate(value);
  if (!p) return null;
  return p.y * 1e8 + p.m * 1e6 + p.d * 1e4 + p.hh * 1e2 + p.mi;
}
`
