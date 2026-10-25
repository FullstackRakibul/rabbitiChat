<script setup>
// "Do it now" (§3.6): each item's version-check command with a copy button, and the
// keys to rotate as chips, on a telescreen. The commands passed the safety check
// when the issue was published.
import { computed } from 'vue'
import { useI18n } from '../lib/i18n.js'
import CodeBlock from './CodeBlock.vue'
import Msg from './Msg.js'
import SectionTitle from './SectionTitle.vue'

const props = defineProps({ items: { type: Array, required: true } })
const { t } = useI18n()
const rows = computed(() => props.items.filter((i) => i.doNowCommand || i.rotateKeys?.length))
</script>

<template>
  <section v-if="rows.length" id="donow" aria-labelledby="donow-title" class="scroll-mt-20" data-testid="do-now">
    <SectionTitle id="donow-title" plain="donow.title" nick="nick.donow" />
    <div class="space-y-4">
      <div v-for="i in rows" :key="i.id" class="rounded-2xl border border-line bg-surface p-4">
        <template v-if="i.doNowCommand">
          <Msg k="donow.check" tag="p" class="m-0 font-semibold" :values="{ tech: i.tech.name }" :keep="['tech']" />
          <CodeBlock :code="i.doNowCommand" :language="i.doNowLanguage" testid="do-now-code" />
        </template>
        <template v-if="i.rotateKeys?.length">
          <p class="m-0 mt-3 font-semibold">{{ t('donow.rotate') }}</p>
          <ul class="m-0 mt-2 flex list-none flex-wrap gap-2 p-0">
            <li v-for="k in i.rotateKeys" :key="k.label">
              <a v-if="k.url" :href="k.url" target="_blank" rel="noopener noreferrer" class="inline-flex min-h-11 items-center rounded-full border border-line px-4 font-mono text-sm" translate="no">{{ k.label }}</a>
              <span v-else class="inline-flex min-h-11 items-center rounded-full border border-line px-4 font-mono text-sm" translate="no">{{ k.label }}</span>
            </li>
          </ul>
        </template>
      </div>
    </div>
  </section>
</template>
