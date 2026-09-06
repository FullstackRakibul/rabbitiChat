// todorabbit — a creation of Mostisko, the AI.
//
// Kanban board API. Everything goes through http.js so the bearer token and
// AuthError handling stay in one place.

import { http } from './http.js'

// ---- Boards ----

/** Board summaries, newest first: [{ id, name, createdAt, columnCount, cardCount }] */
export function listBoards() {
  return http.get('/todorabbit/boards')
}

/** Creates a board seeded with three default columns; returns the full tree. */
export function createBoard(name) {
  return http.post('/todorabbit/boards', { name })
}

/** One board with columns and cards nested, each already in sort order. */
export function getBoard(id) {
  return http.get(`/todorabbit/boards/${id}`)
}

export function renameBoard(id, name) {
  return http.put(`/todorabbit/boards/${id}`, { name })
}

export function deleteBoard(id) {
  return http.del(`/todorabbit/boards/${id}`)
}

// ---- Columns ----

export function addColumn(boardId, { title, colorKey } = {}) {
  return http.post(`/todorabbit/boards/${boardId}/columns`, { title, colorKey })
}

/** Rename and/or recolor. Omitted fields are left as they are. */
export function updateColumn(id, { title, colorKey } = {}) {
  return http.put(`/todorabbit/columns/${id}`, { title, colorKey })
}

export function deleteColumn(id) {
  return http.del(`/todorabbit/columns/${id}`)
}

/** Persists a new left-to-right order. Pass every column id on the board. */
export function reorderColumns(columnIds) {
  return http.put('/todorabbit/columns/reorder', { columnIds })
}

// ---- Cards ----

export function addCard(columnId, { title, content, priority } = {}) {
  return http.post(`/todorabbit/columns/${columnId}/cards`, { title, content, priority })
}

/**
 * Updates a card. Priority is tri-state: omit to leave it, pass '' to clear the
 * badge, pass 'low' | 'medium' | 'high' to set one.
 */
export function updateCard(id, { title, content, priority } = {}) {
  return http.put(`/todorabbit/cards/${id}`, { title, content, priority })
}

export function deleteCard(id) {
  return http.del(`/todorabbit/cards/${id}`)
}

/** Moves a card to toIndex (0-based) within toColumnId. */
export function moveCard(id, toColumnId, toIndex) {
  return http.put(`/todorabbit/cards/${id}/move`, { toColumnId, toIndex })
}
