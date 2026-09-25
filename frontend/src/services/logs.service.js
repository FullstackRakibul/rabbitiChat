// logs.service.js — Observability API client for the frontend.
// All log content is treated as untrusted text; never use v-html on log values.

import { http } from "./http.js";

// ---- Activity logs ----

export async function listActivityLogs(params = {}) {
  const qs = buildQs(params);
  return await http.get(`/logs/activity${qs}`);
}

export async function getActivityLog(id) {
  return await http.get(`/logs/activity/${id}`);
}

// ---- Audit logs ----

export async function listAuditLogs(params = {}) {
  const qs = buildQs(params);
  return await http.get(`/logs/audit${qs}`);
}

// ---- Correlation ----

export async function getCorrelation(correlationId) {
  return await http.get(
    `/logs/correlation/${encodeURIComponent(correlationId)}`,
  );
}

// ---- System info ----

export async function getSystemInfo() {
  return await http.get("/logs/system-info");
}

// ---- Stats ----

export async function getLogStats(from, to) {
  const qs = buildQs({ from, to });
  return await http.get(`/logs/stats${qs}`);
}

// ---- Client events ----

const CLIENT_EVENT_BATCH = [];
let _batchTimer = null;
const BATCH_MAX = 20;
const BATCH_INTERVAL_MS = 5000;

export function logClientEvent(eventName, module, properties) {
  CLIENT_EVENT_BATCH.push({
    eventName,
    module,
    timestampUtc: new Date().toISOString(),
    properties: properties ?? null,
  });

  if (CLIENT_EVENT_BATCH.length >= BATCH_MAX) {
    flushClientEvents();
    return;
  }

  if (!_batchTimer) {
    _batchTimer = setTimeout(flushClientEvents, BATCH_INTERVAL_MS);
  }
}

export async function flushClientEvents() {
  clearTimeout(_batchTimer);
  _batchTimer = null;

  if (CLIENT_EVENT_BATCH.length === 0) return;

  // Splice to avoid holding references during the async call.
  const batch = CLIENT_EVENT_BATCH.splice(0, 50);

  try {
    await http.post("/logs/client", batch);
  } catch {
    // Best-effort: drop the batch if the API is unavailable.
  }
}

// ---- SSE live stream ----

/**
 * Opens an SSE connection to the live log stream.
 * Returns a close() function the caller must invoke on unmount.
 *
 * @param {object} filters  - { category, minLevel }
 * @param {function} onEntry - called with each ActivityLog object
 * @param {function} onError - called on connection error
 */
export function openLogStream(filters, onEntry, onError) {
  const token = localStorage.getItem("token"); // existing session mechanism
  const params = new URLSearchParams();
  if (filters.category) params.set("category", filters.category);
  if (filters.minLevel) params.set("minLevel", filters.minLevel);
  const qs = params.toString() ? `?${params.toString()}` : "";

  // Use the base URL from environment (same as http.js)
  const baseUrl = window.__MOSTISKO_ENV__?.apiBaseUrl ?? "";
  const url = `${baseUrl}/stream/logs${qs}`;

  let closed = false;
  let controller = new AbortController();

  async function connect() {
    try {
      const res = await fetch(url, {
        headers: { Authorization: `Bearer ${token}` },
        signal: controller.signal,
      });

      if (!res.ok) {
        onError(new Error(`SSE ${res.status}`));
        return;
      }

      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      let buffer = "";

      while (!closed) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });

        let idx;
        while ((idx = buffer.indexOf("\n\n")) !== -1) {
          const raw = buffer.slice(0, idx).trim();
          buffer = buffer.slice(idx + 2);
          if (raw.startsWith("data: ")) {
            try {
              const entry = JSON.parse(raw.slice(6));
              onEntry(entry);
            } catch {
              // Malformed SSE frame — skip
            }
          }
        }
      }
    } catch (e) {
      if (!closed) onError(e);
    }
  }

  connect();

  return function close() {
    closed = true;
    controller.abort();
  };
}

// ---- Helpers ----

function buildQs(params) {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v !== undefined && v !== null && v !== "") p.set(k, v);
  }
  const s = p.toString();
  return s ? `?${s}` : "";
}
