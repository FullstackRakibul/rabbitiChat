<script setup>
import { ref, computed, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { getPublicNote } from '@/services/index.js'
import { renderMarkdown } from '@/lib/markdown'
import { FileText, AlertCircle } from '@lucide/vue'

const route = useRoute()
const token = route.params.token
const note = ref(null)
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    note.value = await getPublicNote(token)
    // Update the document title for SEO
    if (note.value?.title) {
      document.title = `${note.value.title} - Mostisko`
    }
  } catch (e) {
    error.value = "This note isn't available"
  } finally {
    loading.value = false
  }
})

const htmlContent = computed(() => {
  if (!note.value) return ''
  return renderMarkdown(note.value.content)
})

function formatRelativeTime(dateStr) {
  if (!dateStr) return ''
  const d = new Date(dateStr)
  const diff = (Date.now() - d.getTime()) / 1000
  if (diff < 60) return 'Just now'
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`
  if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`
  return `${Math.floor(diff / 86400)}d ago`
}
</script>

<template>
  <div class="min-h-screen bg-background text-foreground flex flex-col items-center">
    <!-- Meta tag for robots added in the main app / index.html, but dynamically can be set -->
    
    <div v-if="loading" class="flex-1 flex items-center justify-center p-8">
      <div class="animate-pulse text-muted-foreground flex flex-col items-center gap-4">
        <FileText class="w-12 h-12 opacity-50" />
        <p>Loading note...</p>
      </div>
    </div>
    
    <div v-else-if="error" class="flex-1 flex items-center justify-center p-8 text-center max-w-md mx-auto">
      <div class="flex flex-col items-center gap-4 text-muted-foreground">
        <div class="w-16 h-16 rounded-full bg-muted flex items-center justify-center">
          <AlertCircle class="w-8 h-8 opacity-50" />
        </div>
        <h2 class="text-xl font-semibold text-foreground">Note unavailable</h2>
        <p>{{ error }}</p>
      </div>
    </div>
    
    <div v-else class="w-full max-w-3xl flex flex-col flex-1 shadow-sm bg-card sm:my-10 sm:rounded-2xl sm:border overflow-hidden relative">
      <div class="p-8 sm:p-12 sm:pb-8 flex-1">
        <div class="flex items-center gap-3 text-sm text-muted-foreground mb-12 pb-4 border-b">
          <FileText class="w-4 h-4" />
          <span>Last updated {{ formatRelativeTime(note.updatedAt) }}</span>
        </div>
        
        <div class="prose dark:prose-invert prose-p:leading-relaxed prose-pre:p-0 max-w-none" v-html="htmlContent"></div>
      </div>
      
      <div class="bg-muted/30 p-6 text-center border-t mt-auto text-sm text-muted-foreground">
        Shared with <span class="font-semibold text-foreground">Mostisko</span>
      </div>
    </div>
  </div>
</template>
