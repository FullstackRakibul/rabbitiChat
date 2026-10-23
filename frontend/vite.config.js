import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import tailwindcss from "@tailwindcss/vite";

const here = path.dirname(fileURLToPath(import.meta.url));

/**
 * সত্য মন্ত্রণালয় · Ministry of Truth (docs/TRUTH_PROMPT.md §4). truth.html is a
 * page of its own: the public shell, with no login. Two jobs here:
 *
 *  - Its static text (title, description, <noscript>) comes from the built-in
 *    site JSON at build time, so no text is written into the HTML. The first
 *    paint, before any script runs, is the eye mark and skeleton bars: no text,
 *    so it waits for no font file.
 *  - It is built on its own (`vite build --mode truth`, after the admin app's
 *    build), so its Vue is tree-shaken for it alone and no chunk is shared.
 *  - After a build, every module and file the truth entry can load is written to
 *    node_modules/.truth-build/modules.json; scripts/truth-eval.mjs (G1.5) reads
 *    it to prove no admin, auth or session code is in the public bundle.
 */
function truthPage() {
  const site = () => JSON.parse(fs.readFileSync(path.join(here, "src/truth/content/site.default.json"), "utf8"));
  const esc = (s) => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
  return {
    name: "truth-page",
    transformIndexHtml: {
      order: "pre",
      handler(html, ctx) {
        if (!ctx.filename.endsWith("truth.html")) return html;
        const { bn, en } = site();
        const noscript = ["bn", "en"].map((l) => {
          const t = l === "bn" ? bn : en;
          return `<p lang="${l}">${esc(t.noscript.text)} <a href="/truth/s/issue/current?lang=${l}">${esc(t.noscript.link)}</a></p>`;
        }).join("");
        const eye = '<svg width="28" height="28" viewBox="0 0 32 32" aria-hidden="true" class="flex-none"><circle cx="16" cy="16" r="15" class="eye-ring"/>'
          + '<path d="M5 16q11-10 22 0q-11 10-22 0z" class="eye-white"/><circle cx="16" cy="16" r="4.6" class="eye-iris"/><circle cx="17.6" cy="14.4" r="1.5" class="eye-glint"/></svg>';
        const shell = `<header class="border-b border-line bg-surface"><div class="shell-header mx-auto flex max-w-3xl items-center gap-2 px-4">`
          + `${eye}<span class="shell-bar"></span></div></header>`
          + `<main class="min-h-screen" aria-busy="true"><div class="mx-auto max-w-3xl px-4 pt-5"><div class="shell-block"></div></div></main>`;
        return html
          .replace("%TRUTH_SHELL%", shell)
          .replaceAll("%TRUTH_TITLE%", esc(`${bn.site.name} · ${bn.site.series}`))
          .replaceAll("%TRUTH_DESCRIPTION%", esc(bn.site.description))
          .replace("%TRUTH_NOSCRIPT%", noscript);
      },
    },
    generateBundle(_, bundle) {
      const chunks = Object.values(bundle).filter((c) => c.type === "chunk");
      const entry = chunks.find((c) => c.isEntry && c.facadeModuleId && c.facadeModuleId.replace(/\\/g, "/").endsWith("/truth.html"));
      if (!entry) return;
      const seen = new Set();
      const walk = (file) => {
        if (seen.has(file)) return;
        seen.add(file);
        const c = bundle[file];
        if (c?.type === "chunk") for (const f of [...c.imports, ...c.dynamicImports]) walk(f);
      };
      walk(entry.fileName);
      const files = [...seen];
      const modules = new Set();
      const sizes = {};
      const css = new Set();
      for (const f of files) {
        const c = bundle[f];
        for (const [id, info] of Object.entries(c.modules)) {
          const name = id.startsWith("\0") ? id : path.relative(here, id.split("?")[0]).replace(/\\/g, "/");
          modules.add(name);
          sizes[name] = (sizes[name] ?? 0) + (info.renderedLength ?? 0);
        }
        for (const s of c.viteMetadata?.importedCss ?? []) css.add(s);
      }
      const out = path.join(here, "node_modules/.truth-build");
      fs.mkdirSync(out, { recursive: true });
      fs.writeFileSync(path.join(out, "modules.json"), JSON.stringify({ entry: entry.fileName, files: [...files, ...css], modules: [...modules].sort(), sizes }, null, 2));
    },
  };
}

// During `npm run dev`, calls to /api are forwarded to the .NET backend,
// so the browser stays same-origin and we never fight CORS in development.
export default defineConfig(({ mode }) => ({
  plugins: [
    vue(),
    tailwindcss(),
    truthPage(),
    {
      // The dev server's version of the Vercel rewrite: /truth and below are the truth shell.
      name: "truth-dev-routes",
      configureServer(server) {
        server.middlewares.use((req, _res, next) => {
          if (/^\/truth(\/(?!s\/)|\?|$)/.test(req.url ?? "")) req.url = "/truth.html" + (req.url.includes("?") ? req.url.slice(req.url.indexOf("?")) : "");
          next();
        });
      },
    },
  ],
  resolve: {
    // shadcn-vue generates components that import from "@/lib/utils".
    alias: {
      "@": path.resolve(here, "./src"),
    },
  },
  build: {
    // `vite build` is the admin app; `vite build --mode truth` adds the public shell to the same dist/.
    emptyOutDir: mode !== "truth",
    // No data: URIs in the public shell: its CSP allows fonts and images from 'self' only.
    assetsInlineLimit: mode === "truth" ? 0 : undefined,
    rollupOptions: {
      input: mode === "truth" ? { truth: path.resolve(here, "truth.html") } : { main: path.resolve(here, "index.html") },
    },
  },
  server: {
    port: 5173,
    proxy: {
      // '/api': 'http://localhost:8080'
      "/api": "http://localhost:5274",
      // The Oddity gateway, so an Ollama client can point at the dev server too.
      "/oddity/api": "http://localhost:5274",
      // The Ministry of Truth's public API, as Vercel rewrites it (the backend answers /truth-api itself).
      "/truth-api": "http://localhost:5274",
      "/truth/s": "http://localhost:5274",
      // '/api': 'http://192.168.61.41:5274'
    },
  },
}));
