import { http } from "./http.js";

export async function listNotes(search = "", folder = "", deleted = false) {
  const params = new URLSearchParams();
  if (search) params.append("search", search);
  if (folder) params.append("folder", folder);
  if (deleted) params.append("deleted", "true");
  const qs = params.toString() ? `?${params.toString()}` : "";
  return await http.get(`/notes${qs}`);
}

export async function createNote(content, isPinned = false, folderName = null) {
  return await http.post("/notes", { content, isPinned, folderName });
}

export async function getNote(id) {
  return await http.get(`/notes/${id}`);
}

export async function updateNote(
  id,
  content,
  isPinned,
  folderName,
  expectedUpdatedAt,
) {
  return await http.put(`/notes/${id}`, {
    content,
    isPinned,
    folderName,
    expectedUpdatedAt,
  });
}

export async function softDeleteNote(id) {
  return await http.del(`/notes/${id}`);
}

export async function restoreNote(id) {
  return await http.post(`/notes/${id}/restore`);
}

export async function permanentDeleteNote(id) {
  return await http.del(`/notes/${id}/permanent`);
}

export async function shareNote(id) {
  return await http.post(`/notes/${id}/share`);
}

export async function unshareNote(id) {
  return await http.del(`/notes/${id}/share`);
}

export async function getPublicNote(token) {
  const res = await fetch(`/public/notes/${token}`);
  if (!res.ok) {
    throw new Error("Not found");
  }
  return await res.json();
}
