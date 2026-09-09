/**
 * Export helpers for the JSON → table module.
 *
 * The HTML export is built from the row model rather than scraped from the live
 * DOM: the rendered table may be paginated, and lifting innerHTML would export
 * only the visible page plus a payload of Tailwind classes and Vue attributes.
 * Generating it means the file is self-contained, styled inline, and contains
 * every row — with the rowspans that make it a report.
 *
 * The exported page also carries its own filtering. It has no framework and no
 * network, so the toolbar, styles and script below are emitted inline as vanilla
 * JS and must keep working from a file:// double-click.
 */
import { toText } from './jsontable.js'
import { dateSortKey } from './jsondate.js'

const escapeHtml = (v) => toText(v)
  .replace(/&/g, '&amp;')
  .replace(/</g, '&lt;')
  .replace(/>/g, '&gt;')
  .replace(/"/g, '&quot;')

/** Escapes a value destined for an HTML attribute. */
const escapeAttr = (s) => String(s)
  .replace(/&/g, '&amp;')
  .replace(/</g, '&lt;')
  .replace(/>/g, '&gt;')
  .replace(/"/g, '&quot;')

/**
 * Everything one record's text amounts to, lowercased, for the global search.
 *
 * Built from the FORMATTED values so a search for "Jul 2011" matches what the
 * reader can actually see, and precomputed into an attribute so filtering is one
 * `includes()` per record rather than a DOM walk.
 */
function recordSearchText(row, schema) {
  const parts = []
  for (const c of schema.scalars) parts.push(toText(row.scalars[c.path]))
  for (const g of schema.groups) {
    for (const item of (row.groups[g.name] || [])) {
      for (const c of g.columns) parts.push(toText(item[c.path]))
    }
  }
  return parts.filter(Boolean).join(' ').toLowerCase()
}

/**
 * A standalone HTML document of the merged (report) table, with a working
 * filter toolbar.
 *
 * Styles and script are inline so the file opens correctly anywhere — a browser,
 * an email, or Excel via "Open as web page", which is the usual way a merged
 * table gets into a spreadsheet.
 *
 * @param {Array} rows      row model (already filtered/sorted)
 * @param {object} schema   visible columns: { scalars, groups }
 * @param {string} title
 */
export function toHtmlTable(rows, schema, title = 'JSON Table') {
  const groupColumns = schema.groups.flatMap((g) => g.columns)
  const totalCols = schema.scalars.length + groupColumns.length

  // ---- head: group spans, leaf headers, per-column filter row ----
  const head = []
  head.push('<tr>')
  if (schema.scalars.length) {
    head.push(`<th colspan="${schema.scalars.length}" class="grp">Record</th>`)
  }
  for (const g of schema.groups) {
    head.push(`<th colspan="${g.columns.length}" class="grp">${escapeHtml(g.label)}</th>`)
  }
  head.push('</tr><tr>')
  schema.scalars.forEach((c, i) => {
    // data-col drives column show/hide; data-sort marks it sortable.
    head.push(`<th class="sc" data-col="${i}" data-sort="${i}" title="Click to sort">`
      + `${escapeHtml(c.label)}<span class="arrow"></span></th>`)
  })
  groupColumns.forEach((c, i) => {
    head.push(`<th data-col="${schema.scalars.length + i}">${escapeHtml(c.label)}</th>`)
  })
  head.push('</tr><tr class="filters">')
  schema.scalars.forEach((c, i) => {
    head.push(`<th data-col="${i}"><input data-fcol="${i}" placeholder="Filter…"></th>`)
  })
  groupColumns.forEach((c, i) => {
    // Nested columns are not filtered individually: a nested value belongs to one
    // sub-row, not to the record, so filtering on it would misrepresent the rest.
    head.push(`<th data-col="${schema.scalars.length + i}" class="nofilter"`
      + ` title="Nested columns aren't filtered individually — use the search box">—</th>`)
  })
  head.push('</tr>')

  // ---- body: one group of <tr>s per record, tagged with its index ----
  const body = []
  rows.forEach((row, recordIndex) => {
    const search = recordSearchText(row, schema)

    for (let i = 0; i < row.rowHeight; i++) {
      // data-record ties every <tr> of a record together, so the filter can
      // hide the whole group as a unit and never shear a rowspan.
      const attrs = [`data-record="${recordIndex}"`]
      if (i === 0) {
        attrs.push('class="rec"')
        attrs.push(`data-search="${escapeAttr(search)}"`)
        // Per-column filter values and sort keys live on the record's first row.
        schema.scalars.forEach((c, ci) => {
          attrs.push(`data-c${ci}="${escapeAttr(toText(row.scalars[c.path]).toLowerCase())}"`)
        })
        schema.scalars.forEach((c, ci) => {
          const v = row.scalars[c.path]
          const key = dateSortKey(v)
          // A date sorts by its numeric key; everything else by its own text.
          attrs.push(`data-s${ci}="${escapeAttr(key === null ? toText(v).toLowerCase() : key)}"`)
        })
      }
      body.push(`<tr ${attrs.join(' ')}>`)

      if (i === 0) {
        schema.scalars.forEach((c, ci) => {
          const span = row.rowHeight > 1 ? ` rowspan="${row.rowHeight}"` : ''
          body.push(`<td${span} data-col="${ci}">${escapeHtml(row.scalars[c.path])}</td>`)
        })
      }

      groupColumns.forEach((c, gi) => {
        const item = (row.groups[c.group] || [])[i]
        body.push(`<td data-col="${schema.scalars.length + gi}">${item ? escapeHtml(item[c.path]) : ''}</td>`)
      })

      body.push('</tr>')
    }
  })

  const columnList = [
    ...schema.scalars.map((c) => c.label),
    ...groupColumns.map((c) => `${c.group} › ${c.label}`),
  ]

  return `<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(title)}</title>
<style>
  :root { --line:#999; --head:#eee; --grp:#ddd; --bg:#fff; --fg:#111; --muted:#666; }
  * { box-sizing: border-box; }
  body { font-family: system-ui, -apple-system, "Segoe UI", sans-serif; margin: 0;
         color: var(--fg); background: var(--bg); }
  .bar { position: sticky; top: 0; z-index: 60; background: var(--bg);
         border-bottom: 1px solid var(--line); padding: 10px 16px;
         display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
  .bar h1 { font-size: 14px; margin: 0 8px 0 0; font-weight: 600; }
  .bar input[type=search] { flex: 1 1 260px; min-width: 200px; padding: 6px 10px;
         font: inherit; font-size: 13px; border: 1px solid var(--line); border-radius: 6px; }
  .bar button { padding: 6px 12px; font: inherit; font-size: 13px; cursor: pointer;
         border: 1px solid var(--line); border-radius: 6px; background: var(--bg); }
  .bar button:hover { background: var(--head); }
  #count { font-size: 12px; color: var(--muted); margin-left: auto; white-space: nowrap; }
  .cols { position: relative; }
  .cols-menu { display: none; position: absolute; right: 0; top: 30px; z-index: 70;
         background: var(--bg); border: 1px solid var(--line); border-radius: 6px;
         padding: 6px; max-height: 320px; overflow: auto; min-width: 220px;
         box-shadow: 0 4px 16px rgba(0,0,0,.15); }
  .cols-menu.open { display: block; }
  .cols-menu label { display: flex; gap: 6px; align-items: center; font-size: 12px;
         padding: 2px 4px; cursor: pointer; white-space: nowrap; }
  .cols-menu label:hover { background: var(--head); }
  .wrap { overflow: auto; max-height: calc(100vh - 52px); }
  table { border-collapse: collapse; font-size: 12px; }
  th, td { border: 1px solid var(--line); padding: 4px 8px; text-align: left;
           vertical-align: top; background: var(--bg); }
  thead th { position: sticky; background: var(--head); font-weight: 600; white-space: nowrap; }
  thead tr:nth-child(1) th { top: 0; z-index: 30; }
  thead tr:nth-child(2) th { top: 27px; z-index: 30; }
  thead tr:nth-child(3) th { top: 54px; z-index: 30; }
  th.grp { background: var(--grp); text-align: center; }
  th.sc { cursor: pointer; user-select: none; }
  th.sc:hover { background: var(--grp); }
  .arrow { font-size: 9px; color: var(--muted); margin-left: 3px; }
  tr.filters th { padding: 2px; }
  tr.filters input { width: 100%; min-width: 90px; font: inherit; font-size: 11px;
         padding: 2px 4px; border: 1px solid var(--line); border-radius: 3px; }
  th.nofilter { text-align: center; color: var(--muted); font-weight: 400; }
  tr.rec > td { border-top: 2px solid #666; }
  tr.hidden { display: none; }
  .hidecol { display: none; }
  #empty { display: none; padding: 32px; text-align: center; color: var(--muted); font-size: 13px; }
</style>
</head>
<body>
<div class="bar">
  <h1>${escapeHtml(title)}</h1>
  <input type="search" id="q" placeholder="Search all columns…" autocomplete="off">
  <div class="cols">
    <button type="button" id="colsBtn">Columns ▾</button>
    <div class="cols-menu" id="colsMenu"></div>
  </div>
  <button type="button" id="clear">Clear</button>
  <span id="count"></span>
</div>

<div class="wrap">
<table id="t">
<thead>${head.join('')}</thead>
<tbody>${body.join('')}</tbody>
</table>
<div id="empty">No records match the current filters.</div>
</div>

<script>
(function () {
  'use strict';
  // Cell text is already formatted at generation time and sort keys are baked
  // into data-s* attributes, so this page needs no date logic of its own.

  var table = document.getElementById('t');
  var tbody = table.tBodies[0];
  var q = document.getElementById('q');
  var countEl = document.getElementById('count');
  var emptyEl = document.getElementById('empty');
  var SCALARS = ${schema.scalars.length};
  var TOTAL_COLS = ${totalCols};
  var COLUMN_LABELS = ${JSON.stringify(columnList)};

  // Group every <tr> by the record it belongs to. Filtering then shows or hides
  // whole groups, which is what keeps the rowspans intact — hiding a single
  // sub-row would leave its record's merged cells spanning rows that no longer
  // exist, and the layout shears.
  var groups = [];
  var byIndex = {};
  Array.prototype.forEach.call(tbody.rows, function (tr) {
    var i = tr.getAttribute('data-record');
    if (!byIndex[i]) {
      byIndex[i] = { rows: [], search: '', cols: [], sort: [] };
      groups.push(byIndex[i]);
    }
    var g = byIndex[i];
    g.rows.push(tr);
    if (tr.hasAttribute('data-search')) {
      g.search = tr.getAttribute('data-search') || '';
      for (var c = 0; c < SCALARS; c++) {
        g.cols.push(tr.getAttribute('data-c' + c) || '');
        g.sort.push(tr.getAttribute('data-s' + c) || '');
      }
    }
  });
  var TOTAL = groups.length;

  // Accent-insensitive where the browser supports it, so "Jose" finds "José".
  function norm(s) {
    s = String(s == null ? '' : s).toLowerCase().trim();
    return s.normalize ? s.normalize('NFD').replace(/[\\u0300-\\u036f]/g, '') : s;
  }

  var colFilters = {};

  function apply() {
    var term = norm(q.value);
    var active = [];
    for (var k in colFilters) {
      if (colFilters[k]) active.push([Number(k), colFilters[k]]);
    }

    var shown = 0;
    for (var i = 0; i < groups.length; i++) {
      var g = groups[i];
      var ok = !term || norm(g.search).indexOf(term) !== -1;
      for (var j = 0; ok && j < active.length; j++) {
        // Multiple column filters AND together, and AND with the search.
        if (norm(g.cols[active[j][0]]).indexOf(active[j][1]) === -1) ok = false;
      }
      for (var r = 0; r < g.rows.length; r++) {
        g.rows[r].classList.toggle('hidden', !ok);
      }
      if (ok) shown++;
    }

    countEl.textContent = 'Showing ' + shown + ' of ' + TOTAL + ' records';
    emptyEl.style.display = shown ? 'none' : 'block';
    table.style.display = shown ? '' : 'none';
  }

  // Debounced so typing stays smooth on a few hundred records.
  var timer = null;
  function debounced() { clearTimeout(timer); timer = setTimeout(apply, 150); }

  q.addEventListener('input', debounced);

  Array.prototype.forEach.call(document.querySelectorAll('input[data-fcol]'), function (input) {
    input.addEventListener('input', function () {
      colFilters[input.getAttribute('data-fcol')] = norm(input.value);
      debounced();
    });
  });

  document.getElementById('clear').addEventListener('click', function () {
    q.value = '';
    colFilters = {};
    Array.prototype.forEach.call(document.querySelectorAll('input[data-fcol]'), function (i) {
      i.value = '';
    });
    apply();
  });

  // ---- column show/hide ----
  var menu = document.getElementById('colsMenu');
  COLUMN_LABELS.forEach(function (label, i) {
    var l = document.createElement('label');
    var cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = true;
    cb.addEventListener('change', function () {
      Array.prototype.forEach.call(
        table.querySelectorAll('[data-col="' + i + '"]'),
        function (cell) { cell.classList.toggle('hidecol', !cb.checked); });
    });
    l.appendChild(cb);
    l.appendChild(document.createTextNode(' ' + label));
    menu.appendChild(l);
  });
  document.getElementById('colsBtn').addEventListener('click', function (e) {
    e.stopPropagation();
    menu.classList.toggle('open');
  });
  document.addEventListener('click', function () { menu.classList.remove('open'); });
  menu.addEventListener('click', function (e) { e.stopPropagation(); });

  // ---- sorting ----
  // Whole record groups are reordered and their rows re-appended together, so a
  // record's merged cells stay attached to their own sub-rows.
  var sortCol = -1;
  var sortDir = 1;

  Array.prototype.forEach.call(table.querySelectorAll('th[data-sort]'), function (th) {
    th.addEventListener('click', function () {
      var col = Number(th.getAttribute('data-sort'));
      if (sortCol === col) sortDir = -sortDir; else { sortCol = col; sortDir = 1; }

      groups.sort(function (a, b) {
        var x = a.sort[col], y = b.sort[col];
        if (x === '' && y === '') return 0;
        if (x === '') return 1;            // blanks sink either way
        if (y === '') return -1;
        var nx = Number(x), ny = Number(y);
        // Date keys and plain numbers both compare numerically; text falls back
        // to a natural-order string compare.
        if (!isNaN(nx) && !isNaN(ny)) return (nx - ny) * sortDir;
        return String(x).localeCompare(String(y), undefined, { numeric: true }) * sortDir;
      });

      var frag = document.createDocumentFragment();
      groups.forEach(function (g) {
        g.rows.forEach(function (r) { frag.appendChild(r); });
      });
      tbody.appendChild(frag);

      Array.prototype.forEach.call(table.querySelectorAll('.arrow'), function (a) {
        a.textContent = '';
      });
      th.querySelector('.arrow').textContent = sortDir > 0 ? '\\u25B2' : '\\u25BC';
    });
  });

  apply();
})();
</script>
</body>
</html>`
}

/** Triggers a download of `text` as `filename`, then releases the object URL. */
export function downloadText(filename, text, mime = 'text/plain;charset=utf-8') {
  const blob = new Blob([text], { type: mime })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = filename
  document.body.appendChild(a)
  a.click()
  a.remove()
  URL.revokeObjectURL(url)
}

/** Clipboard write with a boolean result, so the caller can report success honestly. */
export async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text)
    return true
  } catch {
    return false
  }
}
