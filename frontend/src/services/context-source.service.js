// Connected sources and buyer profiles. One backend resource, split by `kind`:
// the Sources tab passes "source", the Workspace tab passes "buyer". Both get
// identical behaviour because they call the same endpoints.
import { http } from './http.js'

/** @typedef {{id:number,name:string,kind:string,content:string,isActive:boolean,sortOrder:number}} ContextSource */

/**
 * List the current user's context sources.
 * @param {string} [kind] - "source" | "buyer". Omit for every kind.
 * @returns {Promise<ContextSource[]>} ordered by sortOrder
 */
export function listContextSources(kind) {
  return http.get(kind ? `/context-sources?kind=${encodeURIComponent(kind)}` : '/context-sources')
}

/**
 * @param {{name:string, kind?:string, content?:string, isActive?:boolean}} input
 * @returns {Promise<ContextSource>}
 */
export function createContextSource(input) {
  return http.post('/context-sources', input)
}

/**
 * Partial update — omitted fields are left as they are.
 * @param {number} id
 * @param {{name?:string, content?:string, isActive?:boolean, sortOrder?:number}} input
 * @returns {Promise<ContextSource>}
 */
export function updateContextSource(id, input) {
  return http.put(`/context-sources/${id}`, input)
}

/** @param {number} id */
export function deleteContextSource(id) {
  return http.del(`/context-sources/${id}`)
}

/**
 * Persist a new ordering.
 * @param {number[]} ids - every id, in the order they should appear
 * @returns {Promise<ContextSource[]>} the full list, re-ordered
 */
export function reorderContextSources(ids) {
  return http.put('/context-sources/reorder', { ids })
}
