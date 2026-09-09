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

  // <colgroup> is what makes resizing work through the rowspans: a width set on
  // a <col> applies to the whole column regardless of which cell spans what, so
  // a merged scalar cell and its sub-rows can never disagree about the width.
  const colgroup = Array.from({ length: totalCols }, (_, i) => `<col data-cg="${i}">`).join('')

  // Pivot operates on the FLAT model — one entry per sub-row — because a merged
  // record has no single value for a nested field. Scalars are marked so the UI
  // can offer them as dimensions by default and warn about the rest.
  const pivotFields = [
    ...schema.scalars.map((c, i) => ({ i, label: c.label, scalar: true })),
    ...groupColumns.map((c, gi) => ({
      i: schema.scalars.length + gi,
      label: `${c.group} › ${c.label}`,
      scalar: false,
    })),
  ]

  // Pivot source rows, one per sub-row, tagged with the record they came from.
  //
  // The record tag matters for correctness: a scalar like GrossSalary is a
  // property of the RECORD, but the flat model repeats it once per sub-row, so
  // summing it naively multiplies each salary by that employee's dependant
  // count. Aggregation therefore de-duplicates by record whenever no nested
  // field is involved, and only counts sub-rows when one actually is.
  const pivotData = []
  rows.forEach((row, recordIndex) => {
    for (let i = 0; i < row.rowHeight; i++) {
      const cells = [recordIndex]
      for (const c of schema.scalars) cells.push(toText(row.scalars[c.path]))
      for (const c of groupColumns) {
        const item = (row.groups[c.group] || [])[i]
        cells.push(item ? toText(item[c.path]) : '')
      }
      pivotData.push(cells)
    }
  })

  // Stable per-table key so saved widths follow this report, not every export.
  const tableKey = `jsontable:${title}:${columnList.join('|')}`
    .replace(/\s+/g, ' ')
    .slice(0, 200)

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

  /* ---- column resize ----
     table-layout:fixed makes <col> widths authoritative; without it the browser
     re-negotiates widths from content and a drag would be undone on every
     filter. Autofit measures first, so nothing is lost by fixing the layout. */
  table { table-layout: fixed; width: max-content; min-width: 100%; }
  th, td { overflow: hidden; text-overflow: ellipsis; }
  /* Wrap is the default so long values stay readable; the toggle switches a
     column to single-line truncation. */
  th, td { white-space: normal; overflow-wrap: anywhere; }
  thead th { white-space: nowrap; }
  col.nowrap-col { }
  td.nowrap, th.nowrap { white-space: nowrap; overflow-wrap: normal; }

  th.sc, thead tr:nth-child(2) th { position: sticky; }
  /* Anchored fully inside the header: an overhanging handle shifts as columns
     relayout, which makes the second click of a double-click miss it. */
  .rz { position: absolute; top: 0; right: 0; width: 9px; height: 100%;
        cursor: col-resize; user-select: none; z-index: 40; }
  .rz:hover, .rz.active { background: #3b82f6; opacity: .55; }
  thead tr:nth-child(2) th { position: sticky; }
  th[data-col] { position: relative; }
  body.resizing { cursor: col-resize; user-select: none; }

  /* ---- frozen columns ----
     Left offsets are assigned by script once real widths are known; the class
     only supplies the stickiness and the seam. */
  .froz { position: sticky; z-index: 25; background: var(--bg); }
  thead .froz { z-index: 45; background: var(--head); }
  thead tr:nth-child(1) .froz { background: var(--grp); }
  .froz-last { border-right: 2px solid #666; }

  /* ---- rows ---- */
  tbody tr:not(.hidden):nth-of-type(even) > td { background: #fafafa; }
  tbody tr:hover > td { background: #eef4ff; }
  tbody tr:hover > td.froz { background: #eef4ff; }
  td.copied { outline: 2px solid #3b82f6; outline-offset: -2px; }

  /* ---- pivot ---- */
  #pivot { display: none; padding: 12px 16px; }
  #pivot.on { display: block; }
  .pv-controls { display: flex; flex-wrap: wrap; gap: 10px; align-items: flex-end;
        margin-bottom: 12px; }
  .pv-controls label { display: flex; flex-direction: column; gap: 3px;
        font-size: 11px; color: var(--muted); }
  .pv-controls select { font: inherit; font-size: 13px; padding: 5px 8px;
        border: 1px solid var(--line); border-radius: 6px; background: var(--bg); min-width: 150px; }
  #pvNote { font-size: 12px; color: #92400e; background: #fef3c7;
        border: 1px solid #fcd34d; border-radius: 6px; padding: 6px 10px; margin-bottom: 10px;
        display: none; }
  #pvNote.on { display: block; }
  #pvOut table { table-layout: auto; width: auto; }
  #pvOut th { background: var(--head); }
  #pvOut .tot { font-weight: 600; background: var(--grp); }
  #pvOut td.num { text-align: right; font-variant-numeric: tabular-nums; }
  .hidden-view { display: none !important; }
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
  <label style="font-size:12px;color:var(--muted);display:flex;gap:4px;align-items:center">
    Freeze
    <select id="freeze" style="font:inherit;font-size:12px;padding:4px 6px;border:1px solid var(--line);border-radius:6px;background:var(--bg)"></select>
  </label>
  <button type="button" id="resetCols" title="Restore default column widths and wrapping">Reset columns</button>
  <button type="button" id="viewBtn">Pivot view</button>
  <button type="button" id="clear">Clear</button>
  <span id="count"></span>
</div>

<div class="wrap" id="tableView">
<table id="t">
<colgroup>${colgroup}</colgroup>
<thead>${head.join('')}</thead>
<tbody>${body.join('')}</tbody>
</table>
<div id="empty">No records match the current filters.</div>
</div>

<div id="pivot">
  <div class="pv-controls">
    <label>Rows<select id="pvRow"></select></label>
    <label>Columns<select id="pvCol"></select></label>
    <label>Values<select id="pvVal"></select></label>
    <label>Aggregation<select id="pvAgg">
      <option value="count">Count</option>
      <option value="sum">Sum</option>
      <option value="avg">Average</option>
      <option value="min">Min</option>
      <option value="max">Max</option>
    </select></label>
  </div>
  <div id="pvNote"></div>
  <div id="pvOut"></div>
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
  var PIVOT_FIELDS = ${JSON.stringify(pivotFields)};
  var PIVOT_DATA = ${JSON.stringify(pivotData)};
  var STORE_KEY = ${JSON.stringify(tableKey)};

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

  // ================= column widths =================
  // Widths live on <col> elements. That is the whole reason resizing survives
  // the rowspans: a merged cell spanning six rows and the sub-row cells beside
  // it all belong to the same column, so one <col> width governs them equally.

  var MIN_W = 48;
  var MAX_AUTOFIT = 400;
  var cols = table.querySelectorAll('col[data-cg]');
  var headerCells = [];        // the leaf header for each column index
  var state = { w: {}, nowrap: {}, freeze: 0 };

  Array.prototype.forEach.call(table.querySelectorAll('thead tr:nth-child(2) th'), function (th) {
    headerCells[Number(th.getAttribute('data-col'))] = th;
  });

  function saveState() {
    // Persistence is a convenience: a browser that blocks storage (file:// in
    // some configurations, private mode) still resizes fine, just not across
    // reopens — so every access is guarded rather than assumed.
    try { localStorage.setItem(STORE_KEY, JSON.stringify(state)); } catch (e) {}
  }

  function loadState() {
    try {
      var raw = localStorage.getItem(STORE_KEY);
      if (!raw) return;
      var parsed = JSON.parse(raw);
      if (parsed && typeof parsed === 'object') {
        state.w = parsed.w || {};
        state.nowrap = parsed.nowrap || {};
        state.freeze = Number(parsed.freeze) || 0;
      }
    } catch (e) {}
  }

  function setWidth(i, px) {
    var w = Math.max(MIN_W, Math.round(px));
    state.w[i] = w;
    if (cols[i]) cols[i].style.width = w + 'px';
  }

  function applyWidths() {
    for (var i = 0; i < TOTAL_COLS; i++) {
      if (state.w[i]) { if (cols[i]) cols[i].style.width = state.w[i] + 'px'; }
      else if (cols[i]) cols[i].style.width = '';
    }
  }

  function applyWrap() {
    for (var i = 0; i < TOTAL_COLS; i++) {
      var off = !!state.nowrap[i];
      Array.prototype.forEach.call(
        table.querySelectorAll('[data-col="' + i + '"]'),
        function (cell) { cell.classList.toggle('nowrap', off); });
    }
  }

  /**
   * Widest rendered content in a column, measured on a canvas rather than by
   * mutating the DOM — no reflow per cell, and it stays accurate for a few
   * hundred rows. Hidden rows are skipped: autofit should size to what is
   * actually on screen.
   */
  var measurer = document.createElement('canvas').getContext('2d');
  function autofit(i) {
    var probe = headerCells[i] || table.querySelector('[data-col="' + i + '"]');
    if (!probe) return;
    var cs = getComputedStyle(probe);
    measurer.font = cs.fontWeight + ' ' + cs.fontSize + ' ' + cs.fontFamily;

    var widest = 0;
    var cells = table.querySelectorAll('[data-col="' + i + '"]');
    for (var n = 0; n < cells.length; n++) {
      var cell = cells[n];
      var tr = cell.parentNode;
      if (tr && tr.classList && tr.classList.contains('hidden')) continue;
      var text = cell.textContent || '';
      // Multi-line content: the widest single line decides the column.
      var lines = text.split('\\n');
      for (var li = 0; li < lines.length; li++) {
        var w = measurer.measureText(lines[li].trim()).width;
        if (w > widest) widest = w;
      }
    }
    // Padding + border + a little slack for the sort arrow.
    setWidth(i, Math.min(widest + 26, MAX_AUTOFIT));
    saveState();
  }

  // Drag handles sit on each leaf header's right edge.
  var drag = null;
  var lastDownAt = 0;      // timestamp of the previous handle press
  var lastDownCol = -1;    // and which column it was on
  for (var ci = 0; ci < TOTAL_COLS; ci++) {
    (function (i) {
      var th = headerCells[i];
      if (!th) return;
      var handle = document.createElement('div');
      handle.className = 'rz';
      handle.title = 'Drag to resize · double-click to autofit';

      handle.addEventListener('mousedown', function (e) {
        e.preventDefault();
        e.stopPropagation();          // never let the header's sort fire

        // Autofit is detected on the second press rather than by a dblclick
        // listener alone: the first click starts a drag and its mouseup ends it,
        // and that sequence does not reliably produce a dblclick on the handle.
        // e.detail is the click ordinal where the browser supplies one; the
        // timestamp fallback covers inputs that always report detail = 1.
        var now = Date.now();
        var quickSecond = (now - lastDownAt) < 400 && lastDownCol === i;
        lastDownAt = now;
        lastDownCol = i;

        if (e.detail > 1 || quickSecond) {
          lastDownAt = 0;               // a third click starts a fresh pair
          autofit(i);
          layoutFrozen();
          return;
        }

        var startX = e.clientX;
        var startW = (cols[i] && cols[i].offsetWidth) || th.offsetWidth;
        drag = { i: i, startX: startX, startW: startW };
        handle.classList.add('active');
        document.body.classList.add('resizing');
      });

      // Kept so a real dblclick event (some input devices synthesise one without
      // a preceding second mousedown) also autofits rather than doing nothing.
      handle.addEventListener('dblclick', function (e) {
        e.preventDefault();
        e.stopPropagation();
        autofit(i);
        layoutFrozen();
      });

      // The handle is 7px wide and the header shifts slightly as columns are
      // laid out, so the second click of a double-click can land just off it —
      // on the sort arrow or the header itself. Treating a double-click near
      // the right edge as an autofit makes the gesture forgiving instead of
      // pixel-perfect, and it never competes with sorting, which is a single
      // click.
      th.addEventListener('dblclick', function (e) {
        var r = th.getBoundingClientRect();
        if (r.right - e.clientX > 14) return;   // not the resize zone
        e.preventDefault();
        e.stopPropagation();
        autofit(i);
        layoutFrozen();
      });

      th.appendChild(handle);
    })(ci);
  }

  document.addEventListener('mousemove', function (e) {
    if (!drag) return;
    var dx = e.clientX - drag.startX;
    // A click is a press with no travel. Resizing on a 0px delta would rewrite
    // the width and relayout the frozen columns, which shifts the handle out
    // from under the pointer and makes the second click of a double-click miss.
    if (!drag.moved && Math.abs(dx) < 3) return;
    drag.moved = true;
    setWidth(drag.i, drag.startW + dx);
    layoutFrozen();
  });

  document.addEventListener('mouseup', function () {
    if (!drag) return;
    var moved = drag.moved;
    drag = null;
    document.body.classList.remove('resizing');
    Array.prototype.forEach.call(document.querySelectorAll('.rz.active'), function (h) {
      h.classList.remove('active');
    });
    if (moved) saveState();     // a bare click changed nothing worth storing
  });

  // ================= frozen columns =================
  // Sticky needs a real left offset per column, and those offsets change with
  // every resize — so they are recomputed from live widths rather than baked in.
  var freezeSel = document.getElementById('freeze');
  var maxFreeze = Math.min(SCALARS, 5);
  for (var f = 0; f <= maxFreeze; f++) {
    var opt = document.createElement('option');
    opt.value = String(f);
    opt.textContent = f === 0 ? 'None' : String(f);
    freezeSel.appendChild(opt);
  }

  function layoutFrozen() {
    var n = state.freeze;
    var offset = 0;
    for (var i = 0; i < TOTAL_COLS; i++) {
      var cells = table.querySelectorAll('[data-col="' + i + '"]');
      var frozen = i < n;
      var width = 0;
      if (frozen) {
        var probe = headerCells[i];
        width = (cols[i] && cols[i].offsetWidth) || (probe ? probe.offsetWidth : 0);
      }
      for (var k = 0; k < cells.length; k++) {
        var cell = cells[k];
        cell.classList.toggle('froz', frozen);
        cell.classList.toggle('froz-last', frozen && i === n - 1);
        cell.style.left = frozen ? offset + 'px' : '';
      }
      if (frozen) offset += width;
    }
    // The group header spans several columns, so it can't be pinned per column;
    // it stays scrollable while the leaf headers freeze.
  }

  freezeSel.addEventListener('change', function () {
    state.freeze = Number(freezeSel.value) || 0;
    layoutFrozen();
    saveState();
  });

  // Wrap toggles live in the Columns menu, next to each show/hide checkbox.
  Array.prototype.forEach.call(menu.querySelectorAll('label'), function (l, i) {
    var btn = document.createElement('button');
    btn.type = 'button';
    btn.textContent = '↵';
    btn.title = 'Toggle wrap / truncate for this column';
    btn.style.cssText = 'margin-left:auto;border:1px solid var(--line);background:var(--bg);'
      + 'border-radius:4px;font-size:10px;line-height:1;padding:2px 5px;cursor:pointer';
    btn.addEventListener('click', function (e) {
      e.preventDefault();
      e.stopPropagation();
      state.nowrap[i] = !state.nowrap[i];
      btn.style.background = state.nowrap[i] ? 'var(--grp)' : 'var(--bg)';
      applyWrap();
      saveState();
    });
    l.appendChild(btn);
    if (state.nowrap[i]) btn.style.background = 'var(--grp)';
  });

  document.getElementById('resetCols').addEventListener('click', function () {
    state.w = {};
    state.nowrap = {};
    state.freeze = 0;
    freezeSel.value = '0';
    applyWidths();
    applyWrap();
    layoutFrozen();
    Array.prototype.forEach.call(menu.querySelectorAll('label button'), function (b) {
      b.style.background = 'var(--bg)';
    });
    try { localStorage.removeItem(STORE_KEY); } catch (e) {}
  });

  // ================= copy cell =================
  tbody.addEventListener('click', function (e) {
    var td = e.target;
    while (td && td.tagName !== 'TD') td = td.parentNode;
    if (!td || !tbody.contains(td)) return;
    var text = (td.textContent || '').trim();
    if (!text) return;
    var mark = function () {
      td.classList.add('copied');
      setTimeout(function () { td.classList.remove('copied'); }, 600);
    };
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(mark, function () {});
    } else {
      // file:// without the async clipboard: fall back to the old selection API.
      var ta = document.createElement('textarea');
      ta.value = text;
      document.body.appendChild(ta);
      ta.select();
      try { document.execCommand('copy'); mark(); } catch (err) {}
      ta.remove();
    }
  });

  // ================= pivot =================
  // Deliberately bounded: one row field, one optional column field, one value,
  // one aggregation. Anything beyond that (multi-level, calculated fields,
  // drag-drop shelves) is a different feature and is not attempted here.

  var pvRow = document.getElementById('pvRow');
  var pvCol = document.getElementById('pvCol');
  var pvVal = document.getElementById('pvVal');
  var pvAgg = document.getElementById('pvAgg');
  var pvOut = document.getElementById('pvOut');
  var pvNote = document.getElementById('pvNote');

  function fillFieldSelect(sel, includeNone, noneLabel) {
    if (includeNone) {
      var none = document.createElement('option');
      none.value = '';
      none.textContent = noneLabel;
      sel.appendChild(none);
    }
    PIVOT_FIELDS.forEach(function (f) {
      var o = document.createElement('option');
      o.value = String(f.i);
      // Nested fields stay selectable but are marked, because pivoting on one
      // counts sub-rows rather than records — see the note rendered below.
      o.textContent = f.label + (f.scalar ? '' : '  (nested)');
      sel.appendChild(o);
    });
  }

  fillFieldSelect(pvRow, false);
  fillFieldSelect(pvCol, true, '(none)');
  fillFieldSelect(pvVal, true, '(count rows)');

  function num(text) {
    if (text == null) return NaN;
    // Tolerate thousands separators and currency symbols in the formatted text.
    var cleaned = String(text).replace(/[^0-9eE+\\-.]/g, '');
    if (!cleaned || cleaned === '-' || cleaned === '.') return NaN;
    return Number(cleaned);
  }

  function aggregate(values, how) {
    if (how === 'count') return values.length;
    var nums = [];
    for (var i = 0; i < values.length; i++) {
      var n = num(values[i]);
      if (!isNaN(n)) nums.push(n);
    }
    if (!nums.length) return null;
    if (how === 'sum') return nums.reduce(function (a, b) { return a + b; }, 0);
    if (how === 'avg') return nums.reduce(function (a, b) { return a + b; }, 0) / nums.length;
    if (how === 'min') return Math.min.apply(null, nums);
    if (how === 'max') return Math.max.apply(null, nums);
    return null;
  }

  function fmt(v, how) {
    if (v === null || v === undefined) return '';
    if (how === 'count') return String(v);
    var rounded = Math.round(v * 100) / 100;
    return rounded.toLocaleString(undefined, { maximumFractionDigits: 2 });
  }

  function isScalarField(i) {
    for (var k = 0; k < PIVOT_FIELDS.length; k++) {
      if (PIVOT_FIELDS[k].i === i) return PIVOT_FIELDS[k].scalar;
    }
    return true;
  }

  function labelFor(i) {
    for (var k = 0; k < PIVOT_FIELDS.length; k++) {
      if (PIVOT_FIELDS[k].i === i) return PIVOT_FIELDS[k].label;
    }
    return '';
  }

  function renderPivot() {
    var rowI = Number(pvRow.value);
    var colI = pvCol.value === '' ? null : Number(pvCol.value);
    var valI = pvVal.value === '' ? null : Number(pvVal.value);
    var how = pvAgg.value;
    if (valI === null) how = 'count';

    // State the sub-row caveat rather than silently producing a count the reader
    // would misread as "records".
    var nested = [rowI, colI, valI].filter(function (x) { return x !== null; })
      .some(function (x) { return !isScalarField(x); });
    pvNote.classList.toggle('on', nested);
    if (nested) {
      pvNote.textContent = 'A nested field is in use, so this pivot aggregates SUB-ROWS: '
        + 'counts reflect sub-rows rather than records, and a record-level value '
        + '(such as a salary) is repeated once per sub-row. Scalar-only pivots count '
        + 'each record exactly once.';
    }

    // Cell 0 of every source row is its record index; fields are offset by one.
    var F = function (rec, i) { return rec[i + 1]; };

    // When every field in play is a scalar, the record is the unit of meaning:
    // counting or summing per sub-row would multiply each record by its
    // dependant count. Collapsing to one entry per record is what makes
    // "Sum of GrossSalary by Department" agree with the table.
    var perRecord = !nested;

    var rowKeys = [], colKeys = [], cellMap = {}, seen = {};
    for (var r = 0; r < PIVOT_DATA.length; r++) {
      var rec = PIVOT_DATA[r];
      if (perRecord) {
        if (seen[rec[0]]) continue;      // already counted this record
        seen[rec[0]] = 1;
      }
      var rk = F(rec, rowI) || '(blank)';
      var ck = colI === null ? '' : (F(rec, colI) || '(blank)');
      if (rowKeys.indexOf(rk) === -1) rowKeys.push(rk);
      if (colI !== null && colKeys.indexOf(ck) === -1) colKeys.push(ck);
      var key = rk + '\\u0000' + ck;
      (cellMap[key] || (cellMap[key] = [])).push(valI === null ? 1 : F(rec, valI));
    }

    var collate = function (a, b) {
      return String(a).localeCompare(String(b), undefined, { numeric: true });
    };
    rowKeys.sort(collate);
    colKeys.sort(collate);

    var html = '<table><thead><tr><th>' + esc(labelFor(rowI)) + '</th>';
    if (colI !== null) {
      colKeys.forEach(function (ck) { html += '<th>' + esc(ck) + '</th>'; });
    }
    html += '<th class="tot">Total</th></tr></thead><tbody>';

    var grand = [];
    var colTotals = {};

    rowKeys.forEach(function (rk) {
      html += '<tr><th>' + esc(rk) + '</th>';
      var rowValues = [];
      if (colI !== null) {
        colKeys.forEach(function (ck) {
          var vals = cellMap[rk + '\\u0000' + ck] || [];
          rowValues = rowValues.concat(vals);
          (colTotals[ck] || (colTotals[ck] = [])).push.apply(colTotals[ck], vals);
          html += '<td class="num">' + esc(fmt(aggregate(vals, how), how)) + '</td>';
        });
      } else {
        rowValues = cellMap[rk + '\\u0000'] || [];
      }
      grand = grand.concat(rowValues);
      html += '<td class="num tot">' + esc(fmt(aggregate(rowValues, how), how)) + '</td></tr>';
    });

    html += '</tbody><tfoot><tr><th class="tot">Total</th>';
    if (colI !== null) {
      colKeys.forEach(function (ck) {
        html += '<td class="num tot">' + esc(fmt(aggregate(colTotals[ck] || [], how), how)) + '</td>';
      });
    }
    html += '<td class="num tot">' + esc(fmt(aggregate(grand, how), how)) + '</td></tr></tfoot></table>';

    pvOut.innerHTML = html;
  }

  function esc(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;')
      .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  }

  [pvRow, pvCol, pvVal, pvAgg].forEach(function (sel) {
    sel.addEventListener('change', renderPivot);
  });

  var pivotEl = document.getElementById('pivot');
  var tableViewEl = document.getElementById('tableView');
  var viewBtn = document.getElementById('viewBtn');
  var pivotOn = false;

  viewBtn.addEventListener('click', function () {
    pivotOn = !pivotOn;
    pivotEl.classList.toggle('on', pivotOn);
    tableViewEl.classList.toggle('hidden-view', pivotOn);
    viewBtn.textContent = pivotOn ? 'Table view' : 'Pivot view';
    if (pivotOn) renderPivot();
  });

  // ---- boot ----
  loadState();
  applyWidths();
  applyWrap();
  freezeSel.value = String(state.freeze);
  Array.prototype.forEach.call(menu.querySelectorAll('label button'), function (b, i) {
    if (state.nowrap[i]) b.style.background = 'var(--grp)';
  });
  apply();
  layoutFrozen();
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
