// ম্যাট্রিক্স · Matrix — /api/matrix (docs/MATRIX_PROMPT.md §3.10).
//
// Every rule is the server's: which sections and actions the user has, what is
// masked, what a refusal says. These functions post and fetch; nothing here
// decides who may do what.

import { http } from './http.js'

/** Upload one or many workbooks; each is profiled. Returns { uploads: [{ id, fileName, profile, … }] }. */
export function uploadMatrixFiles(files) {
  const form = new FormData()
  for (const f of files) form.append('files', f, f.name)
  return http.postForm('/matrix/uploads', form)
}

export function listMatrixUploads() {
  return http.get('/matrix/uploads')
}

export function getMatrixProfile(uploadId) {
  return http.get(`/matrix/uploads/${uploadId}/profile`)
}

/**
 * Imports an upload's tables. `tables` lists [{ table, kind?, reportDate?, mapping?, confirm? }];
 * omitted, every selected table with a matching template is imported ("no questions").
 * Returns { datasets, templates, message }.
 */
export function importMatrixUpload(uploadId, tables) {
  return http.post(`/matrix/uploads/${uploadId}/import`, tables ? { tables } : {})
}

export function listMatrixTemplates() {
  return http.get('/matrix/templates')
}

export function updateMatrixTemplate(id, patch) {
  return http.put(`/matrix/templates/${id}`, patch)
}

export function listMatrixDatasets() {
  return http.get('/matrix/datasets')
}

export function getMatrixDataset(id) {
  return http.get(`/matrix/datasets/${id}`)
}

/**
 * One page of a dataset's rows, masked by the server for what the caller may not see.
 * `filter` is [[column, op, value], …]; `highlight` a citation or quality token.
 * Returns { total, offset, columns, rows, highlight }.
 */
export function getMatrixRows(id, { offset = 0, limit = 200, filter, highlight, only } = {}) {
  const q = new URLSearchParams({ offset: String(offset), limit: String(limit) })
  if (filter?.length) q.set('filter', JSON.stringify(filter))
  if (highlight) q.set('highlight', highlight)
  if (only !== undefined) q.set('only', String(only))
  return http.get(`/matrix/datasets/${id}/rows?${q}`)
}

export function getMatrixQuality(datasetId) {
  return http.get(`/matrix/datasets/${datasetId}/quality`)
}

export function fixMatrixIssue(issueId, note) {
  return http.post(`/matrix/quality/${issueId}/fix`, { note })
}

/** The field catalog: { fields: [{ key, group, role, type, sensitivity, en, bn, derived }] }. */
export function getMatrixCatalog() {
  return http.get('/matrix/catalog')
}

// ---- Dashboards (§3.8) ----

/** The dashboards the caller may see: { dashboards: [{ id, name, nameBn, auto, canEdit, widgets, … }] }. */
export function listMatrixDashboards() {
  return http.get('/matrix/dashboards')
}

/** One dashboard with its layout: { id, name, nameBn, auto, shared, canEdit, layout: { widgets } }. */
export function getMatrixDashboard(id) {
  return http.get(`/matrix/dashboards/${id}`)
}

/**
 * Every widget's numbers. `filters` is { unit, section, line, empType, status, shift, from, to };
 * `compare` adds the previous snapshot's rows. Returns { widgets, filterOptions, datasets }.
 */
export function runMatrixDashboard(id, { filters, compare = false } = {}) {
  return http.post(`/matrix/dashboards/${id}/run`, { filters: filters ?? null, compare })
}

export function createMatrixDashboard(input) {
  return http.post('/matrix/dashboards', input)
}

export function saveMatrixDashboard(id, { name, layout, shared }) {
  return http.put(`/matrix/dashboards/${id}`, { name, layout, shared })
}

export function deleteMatrixDashboard(id) {
  return http.del(`/matrix/dashboards/${id}`)
}

// ---- KPIs (§3.7) ----

/** The KPI library: { kpis: [{ key, area, nameEn, nameBn, words, unit, shape, query, thresholds, kinds }] }. */
export function listMatrixKpis() {
  return http.get('/matrix/kpis')
}

/** Names, formula words or thresholds ({ direction, green, amber } or { cleared: true }). */
export function updateMatrixKpi(key, patch) {
  return http.put(`/matrix/kpis/${key}`, patch)
}

// ---- Export ----

/**
 * Downloads a dataset as .xlsx or .csv (kind) — the server leaves out what the caller
 * may not see — or a dashboard's layout (kind "dashboard").
 */
export async function downloadMatrixExport(kind, id) {
  const res = await http.raw(`/matrix/export/${kind}/${id}`)
  const name = /filename="?([^";]+)/.exec(res.headers.get('content-disposition') ?? '')?.[1] ?? `matrix-${kind}-${id}`
  saveBlob(await res.blob(), name)
}

/** Saves a Blob (or a data: URL) under a file name, through a temporary link. */
export function saveBlob(blobOrUrl, name) {
  const url = typeof blobOrUrl === 'string' ? blobOrUrl : URL.createObjectURL(blobOrUrl)
  const a = document.createElement('a')
  a.href = url
  a.download = name
  document.body.appendChild(a)
  a.click()
  a.remove()
  if (typeof blobOrUrl !== 'string') setTimeout(() => URL.revokeObjectURL(url), 10_000)
}
