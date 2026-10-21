// Matrix eval — a minimal .xlsx writer and reader, Node only (docs/MATRIX_PROMPT.md §8).
//
// The writer makes exactly what the fixtures need and nothing more: shared
// strings, numbers, booleans, Excel date serials with a date format, formulas
// with cached values, and merged ranges. The reader returns each sheet's cells
// as text, which is all the eval needs to read the real samples without
// printing them (it compares, it never shows).
//
// A zip is written with the deflate Node already has; no package is involved.

import zlib from 'node:zlib'
import fs from 'node:fs'

// ─────────────────────────────── zip ───────────────────────────────

const CRC_TABLE = (() => {
  const t = new Uint32Array(256)
  for (let n = 0; n < 256; n++) {
    let c = n
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1
    t[n] = c >>> 0
  }
  return t
})()

function crc32(buf) {
  let c = 0xffffffff
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8)
  return (c ^ 0xffffffff) >>> 0
}

function zip(entries) {
  const locals = []
  const central = []
  let offset = 0
  for (const { name, data } of entries) {
    const raw = Buffer.isBuffer(data) ? data : Buffer.from(data, 'utf8')
    const packed = zlib.deflateRawSync(raw)
    const nameBuf = Buffer.from(name, 'utf8')
    const crc = crc32(raw)
    const local = Buffer.alloc(30)
    local.writeUInt32LE(0x04034b50, 0); local.writeUInt16LE(20, 4); local.writeUInt16LE(0, 6)
    local.writeUInt16LE(8, 8); local.writeUInt32LE(0, 10); local.writeUInt32LE(crc, 14)
    local.writeUInt32LE(packed.length, 18); local.writeUInt32LE(raw.length, 22)
    local.writeUInt16LE(nameBuf.length, 26); local.writeUInt16LE(0, 28)
    locals.push(local, nameBuf, packed)
    const cen = Buffer.alloc(46)
    cen.writeUInt32LE(0x02014b50, 0); cen.writeUInt16LE(20, 4); cen.writeUInt16LE(20, 6)
    cen.writeUInt16LE(0, 8); cen.writeUInt16LE(8, 10); cen.writeUInt32LE(0, 12)
    cen.writeUInt32LE(crc, 16); cen.writeUInt32LE(packed.length, 20); cen.writeUInt32LE(raw.length, 24)
    cen.writeUInt16LE(nameBuf.length, 28); cen.writeUInt32LE(offset, 42)
    central.push(cen, nameBuf)
    offset += 30 + nameBuf.length + packed.length
  }
  const cenBuf = Buffer.concat(central)
  const end = Buffer.alloc(22)
  end.writeUInt32LE(0x06054b50, 0); end.writeUInt16LE(entries.length, 8); end.writeUInt16LE(entries.length, 10)
  end.writeUInt32LE(cenBuf.length, 12); end.writeUInt32LE(offset, 16)
  return Buffer.concat([...locals, cenBuf, end])
}

function unzip(buf) {
  let eocd = buf.length - 22
  while (eocd >= 0 && buf.readUInt32LE(eocd) !== 0x06054b50) eocd--
  if (eocd < 0) throw new Error('not a zip file')
  const count = buf.readUInt16LE(eocd + 10)
  let p = buf.readUInt32LE(eocd + 16)
  const files = new Map()
  for (let i = 0; i < count; i++) {
    const method = buf.readUInt16LE(p + 10)
    const size = buf.readUInt32LE(p + 20)
    const nameLen = buf.readUInt16LE(p + 28), extraLen = buf.readUInt16LE(p + 30), commentLen = buf.readUInt16LE(p + 32)
    const localAt = buf.readUInt32LE(p + 42)
    const name = buf.toString('utf8', p + 46, p + 46 + nameLen)
    const lNameLen = buf.readUInt16LE(localAt + 26), lExtraLen = buf.readUInt16LE(localAt + 28)
    const start = localAt + 30 + lNameLen + lExtraLen
    const data = buf.subarray(start, start + size)
    files.set(name, method === 8 ? zlib.inflateRawSync(data) : Buffer.from(data))
    p += 46 + nameLen + extraLen + commentLen
  }
  return files
}

// ─────────────────────────────── writer ───────────────────────────────

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')

export function colLetter(n) {
  let s = ''
  for (n++; n > 0; n = Math.floor((n - 1) / 26)) s = String.fromCharCode(65 + ((n - 1) % 26)) + s
  return s
}

/** Excel's serial for a JS date (UTC midnight), 1900 date system. */
export function serial(date) {
  return Math.round((Date.UTC(date.getUTCFullYear(), date.getUTCMonth(), date.getUTCDate()) - Date.UTC(1899, 11, 30)) / 86400000)
}

/**
 * Cell values:
 *   string                       → shared string
 *   number                       → number
 *   boolean                      → boolean
 *   null / undefined             → empty
 *   { date: Date }               → serial with the date format
 *   { number: n, asDate: true }  → a number formatted as a date (the "number formatted as date" quirk)
 *   { f: 'formula', v: cached }  → formula with its cached value
 */
export function writeWorkbook(file, sheets) {
  const sst = [], sstIndex = new Map()
  const str = (s) => {
    if (!sstIndex.has(s)) { sstIndex.set(s, sst.length); sst.push(s) }
    return sstIndex.get(s)
  }
  const sheetXml = sheets.map(({ rows, merges = [] }) => {
    const out = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>',
      '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>']
    rows.forEach((row, r) => {
      if (!row || !row.length) return
      const cells = []
      row.forEach((v, c) => {
        if (v === null || v === undefined || v === '') return
        const ref = `${colLetter(c)}${r + 1}`
        if (typeof v === 'string') cells.push(`<c r="${ref}" t="s"><v>${str(v)}</v></c>`)
        else if (typeof v === 'number') cells.push(`<c r="${ref}"><v>${v}</v></c>`)
        else if (typeof v === 'boolean') cells.push(`<c r="${ref}" t="b"><v>${v ? 1 : 0}</v></c>`)
        else if (v.date) cells.push(`<c r="${ref}" s="1"><v>${serial(v.date)}</v></c>`)
        else if (v.asDate) cells.push(`<c r="${ref}" s="1"><v>${v.number}</v></c>`)
        else if (v.f !== undefined) {
          const cached = typeof v.v === 'string' ? `t="str"` : ''
          cells.push(`<c r="${ref}" ${cached}><f>${esc(v.f)}</f><v>${esc(v.v)}</v></c>`)
        }
      })
      out.push(`<row r="${r + 1}">${cells.join('')}</row>`)
    })
    out.push('</sheetData>')
    if (merges.length) out.push(`<mergeCells count="${merges.length}">${merges.map((m) => `<mergeCell ref="${m}"/>`).join('')}</mergeCells>`)
    out.push('</worksheet>')
    return out.join('')
  })

  const entries = [
    { name: '[Content_Types].xml', data: '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
      + '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
      + '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
      + '<Default Extension="xml" ContentType="application/xml"/>'
      + '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
      + '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>'
      + '<Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>'
      + sheets.map((_, i) => `<Override PartName="/xl/worksheets/sheet${i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>`).join('')
      + '</Types>' },
    { name: '_rels/.rels', data: '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
      + '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
      + '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>'
      + '</Relationships>' },
    { name: 'xl/workbook.xml', data: '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
      + '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>'
      + sheets.map((s, i) => `<sheet name="${esc(s.name)}" sheetId="${i + 1}" r:id="rId${i + 1}"/>`).join('')
      + '</sheets></workbook>' },
    { name: 'xl/_rels/workbook.xml.rels', data: '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
      + '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
      + sheets.map((_, i) => `<Relationship Id="rId${i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet${i + 1}.xml"/>`).join('')
      + `<Relationship Id="rId${sheets.length + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>`
      + `<Relationship Id="rId${sheets.length + 2}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>`
      + '</Relationships>' },
    { name: 'xl/styles.xml', data: '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
      + '<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
      + '<numFmts count="1"><numFmt numFmtId="164" formatCode="dd\\-mmm\\-yyyy"/></numFmts>'
      + '<fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>'
      + '<fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>'
      + '<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>'
      + '<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>'
      + '<cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>'
      + '<xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs>'
      + '</styleSheet>' },
    ...sheetXml.map((data, i) => ({ name: `xl/worksheets/sheet${i + 1}.xml`, data })),
  ]
  // Shared strings last: every sheet has registered its strings by now.
  entries.push({ name: 'xl/sharedStrings.xml', data: '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
    + `<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="${sst.length}" uniqueCount="${sst.length}">`
    + sst.map((s) => `<si><t xml:space="preserve">${esc(s)}</t></si>`).join('') + '</sst>' })
  fs.writeFileSync(file, zip(entries))
}

// ─────────────────────────────── reader ───────────────────────────────

const unesc = (s) => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&')

/** Every sheet as { name, cells: Map<"A1", text> }. Values as text; formulas give their cached value. */
export function readWorkbook(file) {
  const files = unzip(fs.readFileSync(file))
  const sstXml = files.get('xl/sharedStrings.xml')?.toString('utf8') ?? ''
  // Element names may carry a namespace prefix (<x:c>, <x:v>: ClosedXML writes them so).
  const T = /<(?:\w+:)?t(?:\s[^>]*)?>([\s\S]*?)<\/(?:\w+:)?t>/g
  const sst = [...sstXml.matchAll(/<(?:\w+:)?si>([\s\S]*?)<\/(?:\w+:)?si>/g)].map((m) =>
    unesc([...m[1].matchAll(T)].map((t) => t[1]).join('')))
  const wb = files.get('xl/workbook.xml').toString('utf8')
  const rels = files.get('xl/_rels/workbook.xml.rels').toString('utf8')
  // Attributes in any order (ClosedXML writes Target before Id).
  const target = new Map([...rels.matchAll(/<(?:\w+:)?Relationship\s([^>]*)>/g)].map((m) => [/\bId="([^"]+)"/.exec(m[1])?.[1], /\bTarget="([^"]+)"/.exec(m[1])?.[1]]))
  const sheets = []
  for (const m of wb.matchAll(/<(?:\w+:)?sheet [^>]*name="([^"]+)"[^>]*r:id="([^"]+)"/g)) {
    const path = 'xl/' + target.get(m[2]).replace(/^\/?xl\//, '')
    const xml = files.get(path)?.toString('utf8') ?? ''
    const cells = new Map()
    for (const c of xml.matchAll(/<(?:\w+:)?c r="([A-Z]+\d+)"([^>]*?)(?:\/>|>([\s\S]*?)<\/(?:\w+:)?c>)/g)) {
      const attrs = c[2], body = c[3] ?? ''
      const v = body.match(/<(?:\w+:)?v>([\s\S]*?)<\/(?:\w+:)?v>/)?.[1]
      const t = attrs.match(/t="([^"]+)"/)?.[1]
      let text = null
      if (t === 's' && v !== undefined) text = sst[Number(v)]
      else if (t === 'inlineStr') text = unesc([...body.matchAll(T)].map((x) => x[1]).join(''))
      else if (v !== undefined) text = unesc(v)
      if (text !== null) cells.set(c[1], text)
    }
    sheets.push({ name: unesc(m[1]), cells })
  }
  return sheets
}
