// এইচআর ওয়ার্কস্পেস · HRIS Workspace — /api/hris (docs/HRIS_WORKSPACE_PROMPT.md §3.5).
//
// Every rule is the server's: who may arrange the shared board, what a pin may show
// (Matrix's salary and personal checks), what a refusal says. These functions post
// and fetch; nothing here decides who may do what.

import { http } from './http.js'
import { saveBlob } from './matrix.service.js'

export function listHrisPins(board = 'shared') {
  return http.get(`/hris/pins?board=${encodeURIComponent(board)}`)
}

/** { board, kind, titleEn, titleBn, payload, size } → the pin. */
export function createHrisPin(pin) {
  return http.post('/hris/pins', pin)
}

export function updateHrisPin(id, patch) {
  return http.put(`/hris/pins/${id}`, patch)
}

export function deleteHrisPin(id) {
  return http.del(`/hris/pins/${id}`)
}

export function orderHrisPins(board, ids) {
  return http.put('/hris/pins/order', { board, ids })
}

/** Every pin of a board run by Matrix with the filter bar: { pins, filterOptions }. */
export function renderHrisPins(board, filters, ids) {
  return http.post('/hris/pins/render', { board, filters, ids })
}

export function getHrisStatus() {
  return http.get('/hris/status')
}

/** A pin (`kind` "pin", with `pin`) or the whole board ("board") as .xlsx. */
export async function downloadHrisExport(kind, { board, pin, filters } = {}) {
  const q = new URLSearchParams()
  if (board) q.set('board', board)
  if (pin) q.set('pin', String(pin))
  if (filters) q.set('filters', JSON.stringify(filters))
  const res = await http.raw(`/hris/export/${kind}?${q}`)
  const name = /filename="?([^";]+)/.exec(res.headers.get('content-disposition') ?? '')?.[1] ?? `hris-${kind}.xlsx`
  saveBlob(await res.blob(), name)
}
