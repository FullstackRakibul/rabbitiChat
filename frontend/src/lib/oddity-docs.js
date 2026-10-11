// Oddity — what the docs say about the API, written once.
//
// The Docs board and the public /oddity/docs page render these lists. The eval
// holds them to the code (DOC.2–DOC.5): every route here exists, every route that
// exists is here, every status the gateway answers is listed, and every field
// matches the server's own OpenAPI file. Change the API and the eval names the
// line to change here.
//
// Describes the gateway as it works today — each later phase adds its part.
// Plain ES module with no imports, so Node can load it as well.

/** The gateway's body limit: Kestrel's default, about 28 MB. */
export const BODY_LIMIT_BYTES = 30_000_000

// ─────────────── The gateway (callers, with a key) ───────────────

/** Under the gateway base URL. Same paths and JSON as Ollama's API. */
export const GATEWAY_ENDPOINTS = [
  { method: 'POST', path: '/api/chat', recorded: true, summary: 'Chat with a model. Ollama\'s request; Ollama\'s NDJSON stream, or one JSON object with "stream": false.' },
  { method: 'POST', path: '/api/generate', recorded: true, summary: 'Complete a prompt, as Ollama does.' },
  { method: 'POST', path: '/api/embed', recorded: true, summary: 'Embed text. Forwarded byte for byte.' },
  { method: 'POST', path: '/api/embeddings', recorded: true, summary: 'The older embeddings endpoint. Forwarded byte for byte.' },
  { method: 'GET', path: '/api/tags', recorded: false, summary: 'The installed models, limited to the ones this gateway allows.' },
  { method: 'POST', path: '/api/show', recorded: false, summary: 'A model\'s details and capabilities. Forwarded.' },
  { method: 'GET', path: '/api/version', recorded: false, summary: 'The version of the Ollama behind the gateway.' },
  { method: 'GET', path: '/api/ps', recorded: false, summary: 'The models loaded in memory right now.' },
]

/** Model management stays with whoever runs Ollama: these answer 403 for every method. */
export const CLOSED_ENDPOINTS = ['/api/pull', '/api/push', '/api/create', '/api/copy', '/api/delete', '/api/blobs/*']

/** What Oddity does to a chat or generate request on its way to Ollama. */
export const REQUEST_CHANGES = [
  'options.num_ctx is set to the gateway\'s context window, whatever the call asked for.',
  'keep_alive is set to the gateway\'s keep-alive, whatever the call asked for.',
  'A call that names no model gets the gateway\'s default model, when one is set.',
  'The top-level "oddity" field is removed. It is reserved for Oddity\'s own options and changes nothing yet.',
  'Everything else — messages, tools, format, images, other options, fields Oddity does not know — reaches Ollama as sent.',
  'The body is read as JSON whatever Content-Type it arrives with, so curl -d works as it does with Ollama.',
  'Embedding calls (/api/embed, /api/embeddings) are forwarded byte for byte.',
  'Only Content-Type and Accept are passed on. Your key, cookies and other headers never reach Ollama.',
]

export const RESPONSE_NOTES = [
  'Replies are Ollama\'s own bytes: the same status, the same Content-Type and the same NDJSON frames, passed on as they arrive.',
  'Every response carries an X-Correlation-Id header. Quote it when you report a problem; Ground Control can find the call by it.',
  'The gateway sets no timeout of its own. Closing the connection stops the call at Ollama too.',
]

/**
 * Every status the gateway itself answers. Each body is {"error": "oddity: …"},
 * the shape Ollama's clients already turn into an error.
 */
export const GATEWAY_ERRORS = [
  { status: 400, when: 'The body is not a JSON object, or "options" is not an object.' },
  { status: 401, when: 'The key is missing, unknown, disabled, expired or revoked. The message is the same for all five.' },
  { status: 403, when: 'The model is not on the allowed list; the key\'s owner no longer has the Oddity tab; or a model-management path.' },
  { status: 404, when: 'There is no such endpoint on the gateway.' },
  { status: 413, when: 'The body is over 30,000,000 bytes (about 28 MB).' },
  { status: 429, when: 'The key has used its calls for this minute. Wait for the next minute.' },
  { status: 502, when: 'Oddity cannot reach Ollama.' },
  { status: 503, when: 'Ground Control has turned the gateway off.' },
]

export const PASSTHROUGH_ERRORS = [
  'Any other error status comes from Ollama, with Ollama\'s own body, unchanged — for example 404 for a model that is not installed.',
  'An error after a stream has started arrives as a last NDJSON line {"error": "…"} with status 200, exactly as with Ollama.',
]

/** How talking to Oddity differs from talking to Ollama directly. */
export const DIFFERENCES = [
  'A key is required on every call: Authorization: Bearer odd_…',
  'num_ctx and keep_alive are set by the operator; a caller\'s own values are replaced.',
  'Model management is closed: pull, push, create, copy, delete and blobs answer 403.',
  'The extra top-level "oddity" field is reserved. Today it is removed and changes nothing.',
]

// ─────────────── Ground Control's API (session token, the Oddity tab) ───────────────

export const ADMIN_ENDPOINTS = [
  { method: 'GET', path: '/api/oddity/settings', summary: 'The install-wide settings.', response: 'OdditySettingsDto' },
  {
    method: 'PUT', path: '/api/oddity/settings', summary: 'Replace the whole settings row. Applies to the next gateway call, with no restart.',
    body: 'OdditySettingsInput', response: 'OdditySettingsDto', notes: '400 names every invalid field, in "error" and keyed by field in "errors".',
  },
  { method: 'GET', path: '/api/oddity/keys', summary: 'Your keys, newest first. Never the key itself.', response: 'OddityKeyDto[]' },
  {
    method: 'POST', path: '/api/oddity/keys', summary: 'Create a key. This response is the only time the key is shown.',
    body: 'OddityKeyInput', response: 'OddityKeyCreatedDto', notes: '400 for an invalid field.',
  },
  {
    method: 'PUT', path: '/api/oddity/keys/{id}', summary: 'Change a key\'s name, note, rate, enabled state or expiry.',
    body: 'OddityKeyInput', response: 'OddityKeyDto', notes: '404 when the key is not yours or does not exist; 400 for an invalid field.',
  },
  {
    method: 'DELETE', path: '/api/oddity/keys/{id}', summary: 'Revoke a key. Its request history stays.',
    notes: '204 on success; 404 when the key is not yours or does not exist.',
  },
  {
    method: 'GET', path: '/api/oddity/requests', summary: 'Your gateway calls, newest first, a page at a time.',
    query: 'OddityRequestFilter', response: 'OddityRequestPageDto',
  },
  {
    method: 'GET', path: '/api/oddity/requests/{id}', summary: 'One call in full, with its previews.',
    response: 'OddityRequestDetailDto', notes: '404 when the call is not yours or does not exist.',
  },
]

/** Schemas reached only through another one: a page's items, a detail's request. */
export const NESTED_SCHEMAS = ['OddityRequestDto']

const SETTINGS_FIELDS = [
  { name: 'enabled', type: 'boolean', about: 'On or off. Off answers every gateway call with 503.' },
  { name: 'defaultModel', type: 'string', about: 'Model for calls that name none. Empty leaves them as sent.' },
  { name: 'allowedModels', type: 'string[]', about: 'Models callers may use; ":latest" and case are ignored. Empty allows any.' },
  { name: 'numCtx', type: 'integer', about: 'The context window every chat and generate call runs at: 2048–262144.' },
  { name: 'replyReserve', type: 'integer', about: 'Tokens kept free for the reply, at most half of numCtx. Not used yet.' },
  { name: 'keepAlive', type: 'string', about: 'How long Ollama keeps the model loaded: seconds (-1 is forever) or a duration such as 30m.' },
  { name: 'defaultRatePerMinute', type: 'integer', about: 'Calls a minute for keys without their own rate. 0 is no limit.' },
  { name: 'maxInlineFileMb', type: 'integer', about: 'Size limit for a file attached to a call, 1–20 MB. Not used yet.' },
  { name: 'logBodies', type: 'string', about: 'off, preview or full: how much of each question and reply the request log keeps.' },
  { name: 'retentionDays', type: 'integer', about: 'Days the request log keeps a row, 1–3650. Not applied yet.' },
  { name: 'publicBaseUrl', type: 'string', about: 'The gateway\'s address for callers outside this network, e.g. https://ai.example.com/oddity. Empty: only the address the page was opened on is shown.' },
]

export const SCHEMAS = {
  OdditySettingsDto: [
    ...SETTINGS_FIELDS,
    { name: 'updatedAtUtc', type: 'date-time', about: 'When the settings were last saved.' },
    { name: 'updatedByUserId', type: 'integer | null', about: 'Who saved them.' },
  ],
  OdditySettingsInput: SETTINGS_FIELDS,

  OddityKeyDto: [
    { name: 'id', type: 'integer', about: 'The key\'s id, for PUT and DELETE.' },
    { name: 'name', type: 'string', about: 'What the key is for.' },
    { name: 'note', type: 'string | null', about: 'Free text: who uses it, for what.' },
    { name: 'prefix', type: 'string', about: 'The 8 hex characters after "odd_" — how a key is recognised without being shown.' },
    { name: 'defaultProfileId', type: 'integer | null', about: 'The profile a call without one uses. Not used yet.' },
    { name: 'ratePerMinute', type: 'integer | null', about: 'Calls a minute. null uses the settings default; 0 is no limit.' },
    { name: 'enabled', type: 'boolean', about: 'A disabled key gets 401.' },
    { name: 'expiresAtUtc', type: 'date-time | null', about: 'After this, the key gets 401. null never expires.' },
    { name: 'createdAtUtc', type: 'date-time', about: 'When the key was made.' },
    { name: 'lastUsedUtc', type: 'date-time | null', about: 'The key\'s latest recorded call.' },
    { name: 'requests24h', type: 'integer', about: 'Recorded calls in the last 24 hours.' },
  ],
  OddityKeyCreatedDto: [
    { name: 'id', type: 'integer', about: 'The key\'s id.' },
    { name: 'name', type: 'string', about: 'What the key is for.' },
    { name: 'note', type: 'string | null', about: 'Free text.' },
    { name: 'prefix', type: 'string', about: 'The 8 hex characters after "odd_".' },
    { name: 'ratePerMinute', type: 'integer | null', about: 'Calls a minute; null is the settings default.' },
    { name: 'enabled', type: 'boolean', about: 'Always true for a new key unless the request said otherwise.' },
    { name: 'expiresAtUtc', type: 'date-time | null', about: 'When it stops working; null never.' },
    { name: 'createdAtUtc', type: 'date-time', about: 'When the key was made.' },
    { name: 'key', type: 'string', about: 'The key itself: odd_ + 8 hex + _ + 43 characters. Shown in this response only; it cannot be read back.' },
  ],
  OddityKeyInput: [
    { name: 'name', type: 'string', about: 'Required, at most 80 characters.' },
    { name: 'note', type: 'string | null', about: 'At most 500 characters.' },
    { name: 'ratePerMinute', type: 'integer | null', about: 'null uses the settings default; 0 is no limit; at most 100000.' },
    { name: 'enabled', type: 'boolean | null', about: 'On create, null means true. On change, null keeps the current value.' },
    { name: 'expiresAtUtc', type: 'date-time | null', about: 'null never expires. A past time disables the key at once.' },
  ],

  OddityRequestFilter: [
    { name: 'before', type: 'integer', about: 'Rows older than this id: pass the previous page\'s nextBefore.' },
    { name: 'limit', type: 'integer', about: 'Rows a page, 1–200. Default 50.' },
    { name: 'keyId', type: 'integer', about: 'Calls made with one key.' },
    { name: 'profile', type: 'string', about: 'Calls that used a profile. Profiles arrive in a later phase.' },
    { name: 'model', type: 'string', about: 'Calls sent to one model.' },
    { name: 'status', type: 'string', about: 'An HTTP status code, or an outcome: ok, rejected, upstream_error, client_abort.' },
    { name: 'ip', type: 'string', about: 'Calls from one client address.' },
    { name: 'from', type: 'date-time', about: 'Calls at or after this time, UTC.' },
    { name: 'to', type: 'date-time', about: 'Calls at or before this time, UTC.' },
    { name: 'q', type: 'string', about: 'Text in the correlation id, key name, model or error.' },
  ],
  OddityRequestPageDto: [
    { name: 'items', type: 'OddityRequestDto[]', about: 'The page, newest first.' },
    { name: 'nextBefore', type: 'integer | null', about: 'Pass as "before" for the next page; null on the last page.' },
  ],
  OddityRequestDetailDto: [
    { name: 'request', type: 'OddityRequestDto', about: 'The row, as in the list.' },
    { name: 'receiptJson', type: 'string | null', about: 'What context was added and why. Empty until profiles arrive.' },
    { name: 'requestPreview', type: 'string | null', about: 'The question, kept as logBodies says.' },
    { name: 'responsePreview', type: 'string | null', about: 'The reply, kept as logBodies says.' },
  ],
  OddityRequestDto: [
    { name: 'id', type: 'integer', about: 'The row\'s id; pages count down from the newest.' },
    { name: 'timestampUtc', type: 'date-time', about: 'When the call arrived.' },
    { name: 'correlationId', type: 'string | null', about: 'The X-Correlation-Id the caller received.' },
    { name: 'keyId', type: 'integer', about: 'The key used. The row stays after the key is revoked.' },
    { name: 'keyName', type: 'string', about: 'The key\'s name at the time of the call.' },
    { name: 'clientIp', type: 'string | null', about: 'The caller\'s address. Behind a reverse proxy this is the proxy\'s.' },
    { name: 'userAgent', type: 'string | null', about: 'The caller\'s User-Agent, cut to 512 characters.' },
    { name: 'endpoint', type: 'string', about: 'chat, generate, embed or embeddings.' },
    { name: 'requestedModel', type: 'string | null', about: 'The model the caller named.' },
    { name: 'model', type: 'string | null', about: 'The model sent to Ollama.' },
    { name: 'profile', type: 'string | null', about: 'The profile used. Profiles arrive in a later phase.' },
    { name: 'stream', type: 'boolean', about: 'Whether the reply was streamed.' },
    { name: 'statusCode', type: 'integer', about: 'The status the caller received.' },
    { name: 'outcome', type: 'string', about: 'ok, rejected, upstream_error or client_abort.' },
    { name: 'error', type: 'string | null', about: 'Why it failed, cut to 500 characters.' },
    { name: 'doneReason', type: 'string | null', about: 'Ollama\'s done_reason; "length" means the reply was cut off.' },
    { name: 'promptTokens', type: 'integer | null', about: 'Ollama\'s prompt_eval_count.' },
    { name: 'cachedPromptTokens', type: 'integer | null', about: 'Prompt tokens Ollama reused from its cache.' },
    { name: 'outputTokens', type: 'integer | null', about: 'Ollama\'s eval_count.' },
    { name: 'loadMs', type: 'integer | null', about: 'Time spent loading the model.' },
    { name: 'promptMs', type: 'integer | null', about: 'Time spent reading the prompt.' },
    { name: 'generationMs', type: 'integer | null', about: 'Time spent writing the reply.' },
    { name: 'firstByteMs', type: 'integer | null', about: 'From arrival to the first byte of the reply.' },
    { name: 'totalMs', type: 'integer', about: 'From arrival to the end of the reply.' },
    { name: 'estimatedPromptTokens', type: 'integer | null', about: 'Oddity\'s own estimate. Not filled yet.' },
    { name: 'addedTokens', type: 'integer | null', about: 'Context Oddity added. Not filled yet.' },
    { name: 'requestBytes', type: 'integer', about: 'Size of the caller\'s body.' },
    { name: 'responseBytes', type: 'integer', about: 'Size of the reply passed back.' },
  ],
}
