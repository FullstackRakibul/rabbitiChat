// All backend calls go through here. In dev, /api is proxied to the .NET app by Vite.
const BASE = '/api';

export async function getModels() {
  const res = await fetch(`${BASE}/models`);
  if (!res.ok) throw new Error('Could not load models');
  const data = await res.json();
  return (data.models || []).map((m) => m.name);
}

export async function listConversations() {
  const res = await fetch(`${BASE}/conversations`);
  if (!res.ok) throw new Error('Could not load conversations');
  return res.json();
}

export async function getConversation(id) {
  const res = await fetch(`${BASE}/conversations/${id}`);
  if (!res.ok) throw new Error('Could not load conversation');
  return res.json();
}

export async function deleteConversation(id) {
  await fetch(`${BASE}/conversations/${id}`, { method: 'DELETE' });
}

/**
 * Send a message and stream the reply.
 * @param {object} args - { conversationId, model, message }
 * @param {(chunk: string) => void} onToken - called for each streamed token
 * @param {AbortSignal} signal
 * @returns {Promise<number>} the conversation id (new or existing)
 */
export async function streamChat({ conversationId, model, message }, onToken, signal) {
  const res = await fetch(`${BASE}/chat`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ conversationId, model, message }),
    signal,
  });

  if (!res.ok) {
    const text = await res.text().catch(() => '');
    throw new Error(text || `Request failed (${res.status})`);
  }

  const convoId = Number(res.headers.get('X-Conversation-Id')) || conversationId;

  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    onToken(decoder.decode(value, { stream: true }));
  }
  return convoId;
}
