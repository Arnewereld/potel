import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import react from '@vitejs/plugin-react'
import { defineConfig, type Plugin } from 'vite'

// Wat de service worker naast de gebouwde bestanden bewaart. Dit staat in public/ en komt zo in de build.
const publicShell = ['/offline.html', '/manifest.webmanifest', '/favicon.svg', '/icons/icon-192.png', '/icons/icon-512.png', '/icons/apple-touch-icon.png']

// Zet src/sw.js als /sw.js in de build, met de lijst met bestanden van de app-schil en een versie die bij elke andere
// build verandert. Zo pakt de browser een nieuwe versie op en ruimt hij de cache van de vorige op.
function serviceWorker(): Plugin {
  return {
    name: 'potel-service-worker',
    apply: 'build',
    generateBundle(_, bundle) {
      const built = Object.keys(bundle).filter(f => f.startsWith('assets/')).map(f => `/${f}`)
      const shell = [...publicShell, ...built].sort()
      const hash = createHash('sha256')
      for (const file of shell) hash.update(file)
      for (const file of publicShell) hash.update(readFileSync(new URL(`./public${file}`, import.meta.url)))
      const template = readFileSync(new URL('./src/sw.js', import.meta.url), 'utf8')
      hash.update(template)
      const source = template.replace('__VERSION__', hash.digest('hex').slice(0, 16)).replace('__SHELL__', JSON.stringify(shell))
      this.emitFile({ type: 'asset', fileName: 'sw.js', source })
    },
  }
}

export default defineConfig({
  plugins: [react(), serviceWorker()],
  server: {
    proxy: { '/api': 'http://localhost:5080' },
  },
  build: {
    // De C# backend serveert de gebouwde frontend vanuit wwwroot.
    outDir: '../backend/Potel.Api/wwwroot',
    emptyOutDir: true,
  },
})
