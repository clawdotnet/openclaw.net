import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'
import tailwindcss from '@tailwindcss/vite'

// Dev: Vite serves the SPA on :5173 and proxies API calls to the ASP.NET host on :5174.
// Build: emits static assets into ../server/wwwroot so `dotnet run` serves the whole app.
export default defineConfig({
  plugins: [svelte(), tailwindcss()],
  build: {
    outDir: '../server/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5174',
        changeOrigin: true,
      },
    },
  },
})
