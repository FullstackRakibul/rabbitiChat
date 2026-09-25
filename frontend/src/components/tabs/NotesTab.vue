<script setup>
import { ref, computed, onMounted, watch } from 'vue'
import {
  listNotes, createNote, getNote, updateNote, softDeleteNote,
  restoreNote, permanentDeleteNote, shareNote, unshareNote
} from '@/services/index.js'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  Plus, Search, Trash2, Pin, Share, ChevronLeft, RefreshCcw, FileText
} from '@lucide/vue'
import { renderMarkdown } from '@/lib/markdown'
import { cn } from '@/lib/utils'
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter, DialogDescription } from '@/components/ui/dialog'
import { Switch } from '@/components/ui/switch'
import { Label } from '@/components/ui/label'

const notes = ref([])
const activeNoteId = ref(null)
const search = ref('')
const folder = ref('')
const showDeleted = ref(false)

const editContent = ref('')
const previewMode = ref(false)
const saveState = ref('saved') // saved, saving, conflict
const isMobile = ref(false)

const shareDialog = ref(false)
const shareUrl = ref('')
const isShared = ref(false)

onMounted(() => {
  isMobile.value = window.innerWidth < 768
  window.addEventListener('resize', () => {
    isMobile.value = window.innerWidth < 768
  })
  loadNotes()
})

async function loadNotes() {
  notes.value = await listNotes(search.value, folder.value, showDeleted.value)
}

watch([search, folder, showDeleted], () => {
  loadNotes()
})

const pinnedNotes = computed(() => notes.value.filter(n => n.isPinned))
const unpinnedNotes = computed(() => notes.value.filter(n => !n.isPinned))
const activeNote = computed(() => notes.value.find(n => n.id === activeNoteId.value))

const wordsCount = computed(() => {
  return editContent.value.trim().split(/\s+/).filter(Boolean).length
})
const charsCount = computed(() => {
  return editContent.value.length
})

let debounceTimer = null

async function selectNote(id) {
  activeNoteId.value = id
  showDeleted.value = false
  const n = await getNote(id)
  editContent.value = n.content
  isShared.value = n.isShared
  shareUrl.value = n.shareToken ? `${window.location.origin}/n/${n.shareToken}` : ''
  previewMode.value = false
  saveState.value = 'saved'
}

async function newNote() {
  const n = await createNote('New Note\n')
  await loadNotes()
  await selectNote(n.id)
  previewMode.value = false
}

function onInput() {
  if (saveState.value === 'conflict') return
  saveState.value = 'saving'
  clearTimeout(debounceTimer)
  debounceTimer = setTimeout(saveNote, 800)
}

async function saveNote() {
  if (!activeNoteId.value) return
  const current = activeNote.value
  if (!current) return

  try {
    const updated = await updateNote(
      current.id,
      editContent.value,
      current.isPinned,
      current.folderName,
      current.updatedAt
    )
    current.updatedAt = updated.updatedAt
    current.title = updated.title
    saveState.value = 'saved'
  } catch (e) {
    if (e.response?.status === 409 || e.message === 'Conflict') {
      saveState.value = 'conflict'
    } else {
      saveState.value = 'saved'
    }
  }
}

async function forceSave() {
  if (!activeNoteId.value) return
  const current = activeNote.value
  try {
    const n = await getNote(current.id)
    current.updatedAt = n.updatedAt
    await saveNote()
    saveState.value = 'saved'
  } catch {}
}

async function loadLatest() {
  if (!activeNoteId.value) return
  const n = await getNote(activeNoteId.value)
  editContent.value = n.content
  activeNote.value.updatedAt = n.updatedAt
  activeNote.value.title = n.title
  saveState.value = 'saved'
}

async function togglePin() {
  if (!activeNote.value) return
  activeNote.value.isPinned = !activeNote.value.isPinned
  await saveNote()
  await loadNotes()
}

async function deleteNote() {
  if (!activeNote.value) return
  await softDeleteNote(activeNote.value.id)
  activeNoteId.value = null
  await loadNotes()
}

async function doRestore(id) {
  await restoreNote(id)
  await loadNotes()
}

async function doPermanentDelete(id) {
  await permanentDeleteNote(id)
  await loadNotes()
}

function closeEditor() {
  activeNoteId.value = null
}

const previewHtml = computed(() => {
  return renderMarkdown(editContent.value)
})

const previewRef = ref(null)

onMounted(() => {
  setInterval(() => {
    if (!previewMode.value || !previewRef.value) return
    const checkboxes = previewRef.value.querySelectorAll('input[type="checkbox"]')
    checkboxes.forEach((cb, i) => {
      if (cb.dataset.bound) return
      cb.dataset.bound = "true"
      cb.removeAttribute('disabled')
      cb.addEventListener('change', () => {
        let count = 0
        let newContent = editContent.value.replace(/-\s+\[([ x])\]/gi, (match, checked) => {
          if (count === i) {
            count++
            return cb.checked ? '- [x]' : '- [ ]'
          }
          count++
          return match
        })
        editContent.value = newContent
        saveNote()
      })
    })
  }, 500)
})

async function onShareToggle(val) {
  if (!activeNote.value) return
  if (val) {
    const res = await shareNote(activeNote.value.id)
    shareUrl.value = `${window.location.origin}/n/${res.shareUrl.split('/').pop()}`
    isShared.value = true
  } else {
    await unshareNote(activeNote.value.id)
    shareUrl.value = ''
    isShared.value = false
  }
}

async function copyShareUrl() {
  await navigator.clipboard.writeText(shareUrl.value)
}

function formatRelativeTime(dateStr) {
  if (!dateStr) return ''
  const d = new Date(dateStr)
  const diff = (Date.now() - d.getTime()) / 1000
  if (diff < 60) return 'Just now'
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`
  if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`
  return `${Math.floor(diff / 86400)}d ago`
}

defineExpose({
  newNote, // Expose for command palette
})
</script>

<template>
  <div class="flex h-full w-full overflow-hidden bg-background">
    <!-- List Pane -->
    <div v-show="!isMobile || !activeNoteId" class="w-full md:w-80 border-r flex flex-col bg-muted/10 h-full">
      <div class="p-4 pb-2 border-b">
        <div class="flex items-center gap-2">
          <Input v-model="search" placeholder="Search notes..." class="flex-1 h-9" />
          <Button variant="outline" size="icon" class="h-9 w-9 shrink-0" @click="newNote" title="New Note (Cmd/Ctrl+N)">
            <Plus class="w-4 h-4" />
          </Button>
        </div>
      </div>
      
      <div class="flex-1 overflow-auto p-4 space-y-6">
        <div v-if="showDeleted" class="space-y-2">
          <h3 class="text-xs font-semibold uppercase text-muted-foreground px-2">Recently Deleted</h3>
          <div v-for="n in notes" :key="n.id" class="p-3 rounded-xl border bg-card hover:border-primary/50 transition-colors">
            <div class="font-medium truncate">{{ n.title }}</div>
            <div class="text-xs text-muted-foreground mt-1 flex justify-between">
              <span>Deleted {{ formatRelativeTime(n.deletedAt) }}</span>
            </div>
            <div class="flex gap-2 mt-3">
              <Button size="sm" variant="secondary" @click="doRestore(n.id)">Restore</Button>
              <Button size="sm" variant="destructive" @click="doPermanentDelete(n.id)">Delete permanently</Button>
            </div>
          </div>
        </div>
        <template v-else>
          <div v-if="pinnedNotes.length" class="space-y-2">
            <h3 class="text-xs font-semibold uppercase text-muted-foreground px-2">Pinned</h3>
            <div v-for="n in pinnedNotes" :key="n.id" @click="selectNote(n.id)" 
                :class="cn('p-3 rounded-xl border bg-card cursor-pointer hover:border-primary/50 transition-colors', activeNoteId === n.id && 'border-primary bg-primary/5')">
              <div class="font-medium truncate">{{ n.title }}</div>
              <div class="text-xs text-muted-foreground mt-1 flex justify-between">
                <span>{{ formatRelativeTime(n.updatedAt) }}</span>
                <Pin v-if="n.isPinned" class="w-3 h-3 text-primary" />
              </div>
            </div>
          </div>
          
          <div class="space-y-2">
            <h3 v-if="pinnedNotes.length" class="text-xs font-semibold uppercase text-muted-foreground px-2">Notes</h3>
            <div v-for="n in unpinnedNotes" :key="n.id" @click="selectNote(n.id)"
                :class="cn('p-3 rounded-xl border bg-card cursor-pointer hover:border-primary/50 transition-colors', activeNoteId === n.id && 'border-primary bg-primary/5')">
              <div class="font-medium truncate">{{ n.title }}</div>
              <div class="text-xs text-muted-foreground mt-1 flex justify-between">
                <span>{{ formatRelativeTime(n.updatedAt) }}</span>
              </div>
            </div>
            <div v-if="!notes.length && !search" class="text-center text-muted-foreground text-sm p-4">
              No notes yet. Create one!
            </div>
          </div>
        </template>
      </div>
      
      <div class="p-3 border-t bg-muted/20">
        <Button variant="ghost" class="w-full justify-start text-muted-foreground" @click="showDeleted = !showDeleted">
          <Trash2 class="w-4 h-4 mr-2" /> {{ showDeleted ? 'Back to Notes' : 'Recently Deleted' }}
        </Button>
      </div>
    </div>
    
    <!-- Editor Pane -->
    <div v-show="!isMobile || activeNoteId" class="flex-1 flex flex-col h-full bg-background relative max-w-full overflow-hidden">
      <template v-if="activeNoteId && !showDeleted">
        <div class="h-14 border-b flex items-center px-4 justify-between shrink-0 bg-background/95 backdrop-blur z-10">
          <div class="flex items-center gap-1">
             <Button v-if="isMobile" variant="ghost" size="icon" @click="closeEditor"><ChevronLeft class="w-5 h-5" /></Button>
             <Button variant="ghost" size="icon" @click="togglePin" :title="activeNote?.isPinned ? 'Unpin' : 'Pin'"><Pin :class="cn('w-4 h-4', activeNote?.isPinned && 'fill-current')" /></Button>
             <Button variant="ghost" size="icon" @click="shareDialog = true" title="Share"><Share class="w-4 h-4" /></Button>
             <Button variant="ghost" size="icon" @click="deleteNote" title="Delete" class="text-destructive"><Trash2 class="w-4 h-4" /></Button>
          </div>
          <div class="flex items-center gap-3">
             <span class="text-xs text-muted-foreground hidden sm:inline">{{ wordsCount }} words, {{ charsCount }} chars</span>
             
             <div v-if="saveState === 'conflict'" class="flex items-center gap-2">
               <span class="text-xs text-destructive font-medium">Changed elsewhere</span>
               <Button size="sm" variant="outline" class="h-7 text-xs px-2" @click="forceSave">Keep mine</Button>
               <Button size="sm" variant="default" class="h-7 text-xs px-2" @click="loadLatest">Load latest</Button>
             </div>
             <span v-else-if="saveState === 'saving'" class="text-xs text-muted-foreground flex items-center gap-1"><RefreshCcw class="w-3 h-3 animate-spin" /> Saving…</span>
             <span v-else class="text-xs text-muted-foreground">Saved</span>
             
             <div class="bg-muted p-1 rounded-lg flex ml-2">
               <button @click="previewMode = false" :class="cn('text-xs px-3 py-1 rounded-md transition-colors', !previewMode && 'bg-background shadow-sm font-medium')">Edit</button>
               <button @click="previewMode = true" :class="cn('text-xs px-3 py-1 rounded-md transition-colors', previewMode && 'bg-background shadow-sm font-medium')">Preview</button>
             </div>
          </div>
        </div>
        
        <div class="flex-1 overflow-y-auto p-6 md:p-12 relative w-full">
          <div class="max-w-3xl mx-auto h-full w-full">
            <div v-if="previewMode" ref="previewRef" class="prose dark:prose-invert prose-p:leading-relaxed prose-pre:p-0 max-w-none w-full pb-20" v-html="previewHtml"></div>
            <textarea v-else v-model="editContent" @input="onInput" 
              class="w-full h-[calc(100vh-10rem)] resize-none outline-none text-lg leading-relaxed font-serif bg-transparent placeholder:text-muted-foreground pb-20" 
              placeholder="Title..."></textarea>
          </div>
        </div>
      </template>
      <div v-else-if="!showDeleted" class="flex-1 flex flex-col items-center justify-center text-muted-foreground">
          <FileText class="w-12 h-12 mb-4 opacity-20" />
          <p>Select a note or create a new one</p>
      </div>
    </div>
    
    <Dialog v-model:open="shareDialog">
      <DialogContent class="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Share Note</DialogTitle>
          <DialogDescription>
            Create a public link for this note.
          </DialogDescription>
        </DialogHeader>
        <div class="flex items-center space-x-2 py-4">
          <Switch id="share-toggle" :checked="isShared" @update:checked="onShareToggle" />
          <Label htmlFor="share-toggle">Share via public link</Label>
        </div>
        <div v-if="isShared" class="space-y-3">
          <div class="text-sm text-muted-foreground">Anyone with this link can read this note.</div>
          <div class="flex items-center gap-2">
            <Input :modelValue="shareUrl" readonly class="flex-1" />
            <Button @click="copyShareUrl">Copy</Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  </div>
</template>
