import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: { '/api': 'http://localhost:5080' },
  },
  build: {
    // De C# backend serveert de gebouwde frontend vanuit wwwroot.
    outDir: '../backend/Potel.Api/wwwroot',
    emptyOutDir: true,
  },
})
