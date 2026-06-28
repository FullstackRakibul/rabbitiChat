import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// During `npm run dev`, calls to /api are forwarded to the .NET backend,
// so the browser stays same-origin and we never fight CORS in development.
export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5274'
    }
  }
})
