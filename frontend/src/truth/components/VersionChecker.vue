<script setup>
// The version checker (§3.5): type a version, get safe / update to X / end of life /
// "the patch isn't out yet" with the temporary guide. In the browser only (G2.5).
import { computed, ref } from 'vue'
import { useI18n } from '../lib/i18n.js'
import Link from './Link.vue'
import SectionTitle from './SectionTitle.vue'
import { lang } from '../lib/lang.js'
import { checkVersion } from '../lib/version.js'
import { serverNow } from '../lib/clock.js'
import { formatWithUtc } from '../lib/time.js'
import { guidesFor } from '../lib/guides.js'

const props = defineProps({ items: { type: Array, required: true } })
const { t } = useI18n()

/** The technologies the issue has advisories for. */
const techs = computed(() => {
  const by = new Map()
  for (const i of props.items) if (i.advisories.length) by.set(i.tech.slug, i.tech)
  return [...by.values()]
})
const chooseTech = computed(() => techs.value.length > 1)
const techSlug = ref(null)
const tech = computed(() => techs.value.find((x) => x.slug === techSlug.value) ?? techs.value[0])
const input = ref('')
const result = ref(null)

function check() {
  const items = props.items.filter((i) => i.tech.slug === tech.value.slug)
  result.value = checkVersion(input.value, tech.value, items, serverNow())
}

const message = computed(() => {
  const r = result.value
  if (!r) return null
  const values = { version: r.version, fix: r.fix, line: r.line, time: r.item?.patchScheduledAt ? formatWithUtc(t, lang.value, r.item.patchScheduledAt) : '' }
  return t(`checker.${r.kind}`, values)
})
const guide = computed(() => (result.value?.kind === 'notYet' ? guidesFor(result.value.item.id).find((g) => g.phase === 'before_patch') : null))
const tone = computed(() => ({ safe: 'tone-green', update: 'tone-amber', notYet: 'tone-amber', noFix: 'tone-red', eol: 'tone-red', invalid: 'tone-grey' })[result.value?.kind])
</script>

<template>
  <section v-if="techs.length" id="checker" aria-labelledby="checker-title" class="scroll-mt-20" data-testid="version-checker">
    <SectionTitle id="checker-title" plain="checker.title" nick="nick.checker" />
    <form class="flex flex-wrap items-end gap-3 rounded-2xl border border-line bg-surface p-4" @submit.prevent="check">
      <label v-if="chooseTech" class="flex flex-col text-sm text-muted">{{ t('checker.tech') }}
        <select v-model="techSlug" class="mt-1 min-h-11 rounded-lg border border-line bg-surface px-2 text-base text-fg">
          <option v-for="x in techs" :key="x.slug" :value="x.slug" translate="no">{{ x.name }}</option>
        </select>
      </label>
      <label class="flex min-w-0 flex-1 basis-40 flex-col text-sm text-muted">
        <span>{{ t('checker.version') }} · <span translate="no">{{ tech.name }}</span></span>
        <input v-model="input" data-testid="checker-input" inputmode="decimal" autocomplete="off" :placeholder="t('checker.placeholder')"
          class="mt-1 min-h-11 rounded-lg border border-line bg-surface px-3 font-mono text-base text-fg">
      </label>
      <button type="submit" data-testid="checker-submit" class="min-h-11 rounded-full bg-fg px-5 font-semibold text-bg">{{ t('checker.check') }}</button>
      <p class="m-0 w-full text-xs text-muted">{{ t('checker.note') }}</p>
    </form>
    <div v-if="message" class="mt-3 rounded-xl p-3" :class="tone" data-testid="checker-result" :data-kind="result.kind" role="status">
      <p class="m-0 font-semibold">{{ message }}</p>
      <Link v-if="guide" :to="`/truth/guide/${guide.slug}`" class="mt-1 inline-flex min-h-11 items-center font-semibold">{{ t('checker.guide') }}</Link>
    </div>
  </section>
</template>
