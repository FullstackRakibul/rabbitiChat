<script setup>
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { renderMarkdown } from '@/lib/markdown'

/**
 * Sanitized assistant markdown, with a header bolted onto every code block.
 *
 * The header is built in the DOM rather than baked into the markdown string so
 * the copy/download handlers are real listeners — injecting onclick attributes
 * would be both unsanitizable and untestable.
 */
const props = defineProps({
  content: { type: String, default: '' },
})

const root = ref(null)

/** Live listeners, torn down before each re-render so nothing leaks while streaming. */
let cleanups = []

function teardown() {
  cleanups.forEach((fn) => fn())
  cleanups = []
}

function on(el, event, handler) {
  el.addEventListener(event, handler)
  cleanups.push(() => el.removeEventListener(event, handler))
}

function decorate() {
  teardown()
  const host = root.value
  if (!host) return

  for (const pre of host.querySelectorAll('pre')) {
    if (pre.parentElement?.dataset.codeBlock) continue // already wrapped

    const code = pre.querySelector('code')
    const lang = pre.dataset.lang || 'text'
    const text = code?.textContent ?? ''

    const wrapper = document.createElement('div')
    wrapper.dataset.codeBlock = 'true'
    wrapper.className = 'my-3 overflow-hidden rounded-lg border'

    const header = document.createElement('div')
    header.className =
      'flex items-center justify-between gap-2 border-b bg-muted/50 px-3 py-1.5 text-[11px] text-muted-foreground'

    const label = document.createElement('span')
    label.className = 'font-medium uppercase tracking-wide'
    label.textContent = lang
    header.appendChild(label)

    const actions = document.createElement('div')
    actions.className = 'flex items-center gap-1'

    const copy = document.createElement('button')
    copy.type = 'button'
    copy.className = 'rounded px-1.5 py-0.5 hover:bg-accent hover:text-accent-foreground'
    copy.textContent = 'Copy'
    on(copy, 'click', async () => {
      try {
        await navigator.clipboard.writeText(text)
        copy.textContent = 'Copied'
        setTimeout(() => { copy.textContent = 'Copy' }, 1200)
      } catch {
        copy.textContent = 'Failed'
        setTimeout(() => { copy.textContent = 'Copy' }, 1200)
      }
    })
    actions.appendChild(copy)

    const download = document.createElement('button')
    download.type = 'button'
    download.className = 'rounded px-1.5 py-0.5 hover:bg-accent hover:text-accent-foreground'
    download.textContent = 'Download'
    on(download, 'click', () => {
      const blob = new Blob([text], { type: 'text/plain;charset=utf-8' })
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = `snippet.${EXTENSIONS[lang] || 'txt'}`
      a.click()
      URL.revokeObjectURL(url)
    })
    actions.appendChild(download)

    header.appendChild(actions)

    pre.replaceWith(wrapper)
    wrapper.appendChild(header)
    wrapper.appendChild(pre)
    pre.classList.add('!my-0', '!rounded-none', '!border-0')
  }
}

/** Best-effort file extension for the download. Unknown languages fall back to .txt. */
const EXTENSIONS = {
  javascript: 'js', js: 'js', typescript: 'ts', ts: 'ts', jsx: 'jsx', tsx: 'tsx',
  python: 'py', py: 'py', csharp: 'cs', cs: 'cs', java: 'java', go: 'go',
  rust: 'rs', rb: 'rb', ruby: 'rb', php: 'php', sql: 'sql', json: 'json',
  yaml: 'yml', yml: 'yml', html: 'html', css: 'css', vue: 'vue',
  bash: 'sh', sh: 'sh', shell: 'sh', powershell: 'ps1', ps1: 'ps1', xml: 'xml',
}

// Re-decorate on every content change: while streaming, a fenced block is
// incomplete until its closing fence arrives, so blocks appear mid-flight.
watch(() => props.content, () => nextTick(decorate), { immediate: true })

onBeforeUnmount(teardown)
</script>

<template>
  <div ref="root" class="md" v-html="renderMarkdown(content)" />
</template>
