// De service worker van Potel. vite.config.ts vult bij elke build de versie en de lijst met bestanden van de app-schil in
// en zet hem als /sw.js in de gebouwde frontend.
//
// Hij bewaart alleen de app-schil: de gebouwde bestanden met een hash in de naam, de iconen, het manifest en de offlinepagina.
// Nooit iets onder /api, nooit een pagina (dus ook geen factuur, offerte of klantportaal met een token in de link) en nooit
// iets van een gebruiker. Pagina's komen altijd van de server; zonder verbinding toont hij de offlinepagina.
const VERSION = '__VERSION__'
const PREFIX = 'potel-shell-'
const CACHE = PREFIX + VERSION
const SHELL = __SHELL__
const OFFLINE = '/offline.html'

self.addEventListener('install', event => {
  event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(SHELL)))
})

// Een nieuwe versie ruimt de caches van oudere versies op.
self.addEventListener('activate', event => {
  event.waitUntil((async () => {
    for (const key of await caches.keys()) {
      if (key.startsWith(PREFIX) && key !== CACHE) await caches.delete(key)
    }
    await self.clients.claim()
  })())
})

// De pagina vraagt dit als je op Vernieuwen klikt bij een nieuwe versie; anders wacht de nieuwe versie tot je volgende bezoek.
self.addEventListener('message', event => {
  if (event.data === 'skip-waiting') self.skipWaiting()
})

const isShell = url => SHELL.includes(url.pathname) || url.pathname.startsWith('/assets/')

self.addEventListener('fetch', event => {
  const request = event.request
  if (request.method !== 'GET') return
  const url = new URL(request.url)
  if (url.origin !== self.location.origin) return
  if (url.pathname === '/api' || url.pathname.startsWith('/api/')) return

  if (request.mode === 'navigate') {
    event.respondWith(fetch(request).catch(async () => (await caches.match(OFFLINE)) ?? Response.error()))
    return
  }

  if (!isShell(url) || url.search) return
  // Bestanden met een hash in de naam veranderen nooit: eerst uit de cache. Een gebouwd bestand dat er nog niet in stond
  // (van een nieuwere versie) komt er na de eerste keer bij, maar alleen een echt bestand en nooit een pagina.
  event.respondWith((async () => {
    const hit = await caches.match(request)
    if (hit) return hit
    const response = await fetch(request)
    const type = response.headers.get('Content-Type') ?? ''
    if (response.ok && url.pathname.startsWith('/assets/') && !type.startsWith('text/html')) {
      const cache = await caches.open(CACHE)
      await cache.put(request, response.clone())
    }
    return response
  })())
})
