import path from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import tailwindcss from "@tailwindcss/vite";

const here = path.dirname(fileURLToPath(import.meta.url));

// During `npm run dev`, calls to /api are forwarded to the .NET backend,
// so the browser stays same-origin and we never fight CORS in development.
export default defineConfig({
  plugins: [vue(), tailwindcss()],
  resolve: {
    // shadcn-vue generates components that import from "@/lib/utils".
    alias: {
      "@": path.resolve(here, "./src"),
    },
  },
  server: {
    port: 5173,
    proxy: {
      // '/api': 'http://localhost:8080'
      "/api": "http://localhost:5274",
      // '/api': 'http://192.168.61.41:5274'
    },
  },
});
