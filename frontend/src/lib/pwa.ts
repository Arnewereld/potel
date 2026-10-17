import { useSyncExternalStore } from 'react'

// Potel als app op je telefoon: de service worker (snelle start en een offlinepagina), een nieuwe versie oppakken,
// en de knop om te installeren als de browser dat aanbiedt (Chrome en Edge; Safari doet het via de deelknop).

interface InstallPromptEvent extends Event {
  prompt: () => Promise<void>
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>
}

interface PwaState {
  // Een nieuwe versie die klaarstaat en wacht tot je op Vernieuwen klikt (of tot je volgende bezoek).
  update: ServiceWorker | null
  install: InstallPromptEvent | null
  installed: boolean
}

// Geopend vanaf het beginscherm, als app zonder adresbalk.
export function standalone() {
  try {
    return window.matchMedia('(display-mode: standalone)').matches || (navigator as Navigator & { standalone?: boolean }).standalone === true
  } catch { return false }
}

let state: PwaState = { update: null, install: null, installed: standalone() }
const listeners = new Set<() => void>()
const set = (patch: Partial<PwaState>) => {
  state = { ...state, ...patch }
  listeners.forEach(l => l())
}
const subscribe = (l: () => void) => { listeners.add(l); return () => { listeners.delete(l) } }

let reloading = false
// Een tabblad dat lang openstaat, kijkt af en toe of er een nieuwe versie is.
const CHECK_EVERY_MS = 30 * 60_000

export function setupPwa() {
  window.addEventListener('beforeinstallprompt', e => {
    e.preventDefault()
    set({ install: e as InstallPromptEvent })
  })
  window.addEventListener('appinstalled', () => set({ install: null, installed: true }))
  // Alleen in de gebouwde versie: tijdens het ontwikkelen zit een service worker alleen in de weg.
  if (!import.meta.env.PROD || !('serviceWorker' in navigator)) return

  navigator.serviceWorker.addEventListener('controllerchange', () => { if (reloading) window.location.reload() })
  const register = async () => {
    try {
      const registration = await navigator.serviceWorker.register('/sw.js')
      const watch = (worker: ServiceWorker | null) => {
        if (!worker) return
        // Alleen een update: bij de allereerste keer is er nog geen service worker die de pagina bedient.
        const check = () => { if (worker.state === 'installed' && navigator.serviceWorker.controller) set({ update: worker }) }
        check()
        worker.addEventListener('statechange', check)
      }
      watch(registration.waiting)
      registration.addEventListener('updatefound', () => watch(registration.installing))
      let checked = Date.now()
      document.addEventListener('visibilitychange', () => {
        if (document.visibilityState !== 'visible' || Date.now() - checked < CHECK_EVERY_MS) return
        checked = Date.now()
        registration.update().catch(() => { /* offline: later weer */ })
      })
    } catch { /* geen service worker: Potel werkt gewoon, alleen zonder offlinepagina */ }
  }
  if (document.readyState === 'complete') void register()
  else window.addEventListener('load', () => void register(), { once: true })
}

// Nu naar de nieuwe versie: de wachtende service worker neemt het over en de pagina laadt opnieuw.
export function applyUpdate() {
  if (!state.update) return
  reloading = true
  state.update.postMessage('skip-waiting')
}

export async function installApp() {
  const prompt = state.install
  if (!prompt) return
  await prompt.prompt()
  const { outcome } = await prompt.userChoice
  set({ install: null, installed: outcome === 'accepted' || state.installed })
}

export const usePwa = () => useSyncExternalStore(subscribe, () => state)
