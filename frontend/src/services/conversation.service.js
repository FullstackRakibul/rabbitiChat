import { http } from './http.js'

/** The current user's conversations, newest first: [{ id, title, createdAt }] */
export function listConversations() {
  return http.get('/conversations')
}

/** One conversation with its messages: { id, title, createdAt, messages } */
export function getConversation(id) {
  return http.get(`/conversations/${id}`)
}

export function deleteConversation(id) {
  return http.del(`/conversations/${id}`)
}
