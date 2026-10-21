// Shared by the evals that drive the real shell in a browser (rbac-eval, matrix-eval):
// Chrome or Edge in headless mode, driven over the DevTools protocol with Node's
// own WebSocket — no packages. Moved here from scripts/rbac-eval.mjs unchanged
// apart from taking the API's base URL and a profile folder as arguments.

import fs from 'node:fs'
import net from 'node:net'
import path from 'node:path'
import { spawn } from 'node:child_process'

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

export function findBrowser() {
  const candidates = [
    process.env.RBAC_EVAL_BROWSER,
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    '/usr/bin/google-chrome', '/usr/bin/chromium', '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  ].filter(Boolean)
  const found = candidates.find((c) => fs.existsSync(c))
  if (!found) throw new Error('no Chrome or Edge found; set RBAC_EVAL_BROWSER')
  return found
}

async function freePort() {
  const s = net.createServer()
  await new Promise((r) => s.listen(0, '127.0.0.1', r))
  const port = s.address().port
  await new Promise((r) => s.close(r))
  return port
}

export async function launchBrowser({ base, profileRoot }) {
  const port = await freePort()
  fs.mkdirSync(profileRoot, { recursive: true })
  const profile = fs.mkdtempSync(path.join(profileRoot, 'chrome-'))
  const proc = spawn(findBrowser(), [
    '--headless=new', `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
    '--no-first-run', '--no-default-browser-check', '--disable-gpu', '--hide-scrollbars', 'about:blank',
  ], { stdio: 'ignore' })
  let version
  for (let i = 0; i < 100 && !version; i++) {
    try { version = await (await fetch(`http://127.0.0.1:${port}/json/version`)).json() } catch { await sleep(150) }
  }
  if (!version) { proc.kill(); throw new Error('browser did not start') }

  const ws = new WebSocket(version.webSocketDebuggerUrl)
  await new Promise((r, j) => { ws.onopen = r; ws.onerror = j })
  let seq = 0
  const pending = new Map()
  const listeners = []
  ws.onmessage = (m) => {
    const msg = JSON.parse(m.data)
    if (msg.id && pending.has(msg.id)) {
      const { resolve, reject } = pending.get(msg.id)
      pending.delete(msg.id)
      msg.error ? reject(new Error(msg.error.message)) : resolve(msg.result)
    } else for (const l of listeners) l(msg)
  }
  const send = (method, params = {}, sessionId) => new Promise((resolve, reject) => {
    const id = ++seq
    const timer = setTimeout(() => { pending.delete(id); reject(new Error(`${method}: no answer in 30 s`)) }, 30_000)
    pending.set(id, { resolve: (v) => { clearTimeout(timer); resolve(v) }, reject: (e) => { clearTimeout(timer); reject(e) } })
    ws.send(JSON.stringify({ id, method, params, sessionId }))
  })

  return {
    async page() {
      // Its own browser context: two signed-in users must not share one origin's localStorage.
      const { browserContextId } = await send('Target.createBrowserContext')
      const { targetId } = await send('Target.createTarget', { url: 'about:blank', browserContextId })
      const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true })
      const s = (m, p) => send(m, p, sessionId)
      await s('Page.enable'); await s('Runtime.enable')
      const evaluate = async (expression) => {
        const r = await s('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })
        if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text)
        return r.result.value
      }
      const loaded = () => new Promise((resolve, reject) => {
        const timer = setTimeout(() => { listeners.splice(listeners.indexOf(l), 1); reject(new Error('page did not load in 20 s')) }, 20_000)
        const l = (msg) => {
          if (msg.sessionId === sessionId && msg.method === 'Page.loadEventFired') {
            clearTimeout(timer); listeners.splice(listeners.indexOf(l), 1); resolve()
          }
        }
        listeners.push(l)
      })
      const p = {
        eval: evaluate,
        async viewport(width, height) {
          await s('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 600 })
          await sleep(400)
        },
        async session(user, theme, width, height, route) {
          await p.viewport(width, height)
          let l = loaded(); await s('Page.navigate', { url: `${base}/settings` }); await l
          await evaluate(`localStorage.setItem('rabbitic.token', ${JSON.stringify(user.token)});
            localStorage.setItem('rabbitic.username', ${JSON.stringify(user.username)});
            localStorage.setItem('rabbitic-theme', ${JSON.stringify(theme)}); 0`)
          l = loaded(); await s('Page.navigate', { url: `${base}${route}` }); await l
          await sleep(1200)
        },
        async reload() { const l = loaded(); await s('Page.reload'); await l; await sleep(1200) },
        async setTheme(theme) {
          await evaluate(`localStorage.setItem('rabbitic-theme', '${theme}'); document.documentElement.classList.toggle('dark', ${theme === 'dark'}); 0`)
          await sleep(300)
        },
        async waitFor(selector, ms = 8000) {
          const end = Date.now() + ms
          while (Date.now() < end) {
            if (await evaluate(`!!document.querySelector(${JSON.stringify(selector)})`)) return
            await sleep(150)
          }
          throw new Error(`timed out waiting for ${selector}`)
        },
        async click(selector) {
          await p.waitFor(selector)
          await evaluate(`document.querySelector(${JSON.stringify(selector)}).click(); 0`)
          await sleep(300)
        },
        async type(selector, text) {
          await p.waitFor(selector)
          await evaluate(`(() => { const el = document.querySelector(${JSON.stringify(selector)});
            el.value = ${JSON.stringify(text)}; el.dispatchEvent(new Event('input', { bubbles: true })); })()`)
        },
        /** Opens the module launcher and reads its tiles. */
        async launcherIds(shotFile) {
          await evaluate(`document.querySelector('[data-testid="module-chip"]')?.click(); 0`)
          await sleep(500)
          if (shotFile) await p.shot(shotFile)
          const ids = await evaluate(`[...document.querySelectorAll('[data-launcher-tile]')].map((e) => e.getAttribute('data-tab-key'))`)
          await evaluate(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); 0`)
          await sleep(300)
          return ids ?? []
        },
        async shot(file, { full = false } = {}) {
          const { data } = await s('Page.captureScreenshot', { format: 'png', captureBeyondViewport: full })
          fs.writeFileSync(file, Buffer.from(data, 'base64'))
        },
        /** A real mouse click at page coordinates (canvas charts take no element clicks). */
        async mouseClick(x, y) {
          for (const type of ['mouseMoved', 'mousePressed', 'mouseReleased'])
            await s('Input.dispatchMouseEvent', { type, x, y, button: type === 'mouseMoved' ? 'none' : 'left', clickCount: type === 'mouseMoved' ? 0 : 1 })
          await sleep(300)
        },
        /** The page through the browser's own print-to-PDF — what "Print / PDF" produces. */
        async pdf() {
          const { data } = await s('Page.printToPDF', { printBackground: true })
          return Buffer.from(data, 'base64')
        },
        /** Blurs every element matching the selector (screenshots of real data). */
        async blur(selector) {
          await evaluate(`document.querySelectorAll(${JSON.stringify(selector)}).forEach((e) => { e.style.filter = 'blur(6px)' }); 0`)
        },
      }
      return p
    },
    async close() {
      try { await send('Browser.close') } catch { /* already gone */ }
      ws.close()
      proc.kill()
    },
  }
}

