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
