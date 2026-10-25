<script setup>
// The fix guides (docs/TRUTH_PROMPT.md §6): every guide, and a new draft.
import { onMounted, ref } from 'vue'
import { Plus } from '@lucide/vue'
import { listTruthGuides } from '@/services/index.js'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { formatDhaka, STATUS_BADGE } from '@/lib/truth-admin.js'

defineProps({ canEdit: { type: Boolean, default: false } })
const emit = defineEmits(['open'])
const guides = ref([])
const error = ref('')
onMounted(async () => { try { guides.value = (await listTruthGuides()).guides } catch (e) { error.value = e.message } })
</script>

<template>
  <section class="flex flex-col gap-3" data-truth-guides>
    <div class="flex items-center gap-2">
      <h3 class="flex-1 font-semibold">সমাধান গাইড <span class="text-sm font-normal text-muted-foreground">Fix guides</span></h3>
      <Button v-if="canEdit" size="sm" variant="outline" data-truth-new-guide @click="emit('open', 'new')"><Plus class="size-4" /> নতুন <span class="text-xs opacity-70">New</span></Button>
    </div>
    <p v-if="error" class="rounded-md border border-destructive/40 bg-destructive/10 p-3 text-sm text-destructive">{{ error }}</p>
    <p v-if="!guides.length" class="rounded-lg border bg-card p-4 text-sm text-muted-foreground">এখনো কোনো গাইড নেই। <span class="opacity-70">No guides yet.</span></p>
    <ul class="grid gap-2">
      <li v-for="g in guides" :key="g.id">
        <button type="button" class="flex w-full flex-wrap items-center gap-x-3 gap-y-1 rounded-lg border bg-card p-3 text-left hover:bg-accent" :data-truth-guide="g.slug" @click="emit('open', g.id)">
          <span class="font-mono text-xs text-muted-foreground">{{ g.slug }}</span>
          <span class="min-w-0 flex-1 truncate font-medium">{{ g.titleBn || '—' }}</span>
          <Badge variant="outline">{{ g.phase }}</Badge>
          <Badge :variant="STATUS_BADGE[g.status]">{{ g.status }}</Badge>
          <Badge v-if="g.unpublishedChanges" variant="outline">unpublished changes</Badge>
          <span class="w-full text-xs text-muted-foreground">{{ g.tech }} · {{ g.platforms.join(', ') }} · edited {{ formatDhaka(g.editedAt) }}<template v-if="g.publishedAt"> · published {{ formatDhaka(g.publishedAt) }}</template></span>
        </button>
      </li>
    </ul>
  </section>
</template>
