// Oddity — the Ground Control API (/api/oddity/*), through the same http.js
// plumbing as every other module.

import { http } from './http.js'
import { environment } from '../environments/environment.js'

/**
 * The gateway's base URL as a client should use it: the API base resolved against
 * this page's origin, with its final "/api" replaced by "/oddity".
 */
export function gatewayBaseUrl() {
  const api = new URL(environment.apiBaseUrl, window.location.origin).href.replace(/\/+$/, '')
  return api.replace(/\/api$/, '/oddity')
}

/**
 * The addresses to show a caller, most useful first: the public URL from the
 * settings when one is set, then the one this page was opened on — once, when
 * they are the same. Pass null (the public docs page has no settings) for just
 * the page's own address.
 */
export function gatewayUrls(settings) {
  const here = gatewayBaseUrl()
  const pub = settings?.publicBaseUrl?.trim()
  if (!pub) return [{ label: 'Base URL', url: here }]
  if (pub.toLowerCase() === here.toLowerCase()) return [{ label: 'Public', url: pub }]
  return [{ label: 'Public', url: pub }, { label: 'This address', url: here }]
}

// ---- Settings (install-wide) ----

/** { enabled, defaultModel, allowedModels: [], numCtx, replyReserve, keepAlive, … } */
export function getOdditySettings() {
  return http.get('/oddity/settings')
}

/** Replaces the whole row. A 400 carries every field message in its error text. */
export function saveOdditySettings(settings) {
  return http.put('/oddity/settings', settings)
}

// ---- Keys ----

/** Your keys: [{ id, name, note, prefix, ratePerMinute, enabled, expiresAtUtc, lastUsedUtc, requests24h, … }] */
export function listOddityKeys() {
  return http.get('/oddity/keys')
}

/** Creates a key. The response's `key` is the only time it is ever shown. */
export function createOddityKey({ name, note, ratePerMinute, expiresAtUtc } = {}) {
  return http.post('/oddity/keys', { name, note, ratePerMinute, expiresAtUtc })
}

export function updateOddityKey(id, { name, note, ratePerMinute, enabled, expiresAtUtc } = {}) {
  return http.put(`/oddity/keys/${id}`, { name, note, ratePerMinute, enabled, expiresAtUtc })
}

/** Revokes a key. Its request history stays. */
export function deleteOddityKey(id) {
  return http.del(`/oddity/keys/${id}`)
}

// ---- Requests ----

/**
 * One page of your gateway calls, newest first: { items, nextBefore }.
 * Pass `nextBefore` back as `before` for the next page.
 */
export function listOddityRequests({ before, limit = 50, keyId, profile, model, status, ip, from, to, q } = {}) {
  const params = new URLSearchParams()
  const add = (k, v) => { if (v !== undefined && v !== null && v !== '') params.set(k, String(v)) }
  add('before', before); add('limit', limit); add('keyId', keyId); add('profile', profile)
  add('model', model); add('status', status); add('ip', ip); add('from', from); add('to', to); add('q', q)
  return http.get(`/oddity/requests?${params}`)
}

/** One call in full: { request, receiptJson, requestPreview, responsePreview }. */
export function getOddityRequest(id) {
  return http.get(`/oddity/requests/${id}`)
}
