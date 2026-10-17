import { useSyncExternalStore } from 'react'

// Focusmodus: een zacht geluid en een melding als een focusblok of een pauze om is. Toestemming voor meldingen vragen we
// pas als je de focusmodus zelf start, nooit zomaar bij het openen van Potel.

export const FOCUS_MIN = 15
export const FOCUS_MAX = 90
export const FOCUS_CHOICES = Array.from({ length: (FOCUS_MAX - FOCUS_MIN) / 5 + 1 }, (_, i) => FOCUS_MIN + i * 5)

// De lengte van een blok die je het laatst koos. De server onthoudt hem per gebruiker; dit is wat dit apparaat ervan weet.
let preferred = 25
const listeners = new Set<() => void>()
const subscribe = (l: () => void) => { listeners.add(l); return () => { listeners.delete(l) } }
export function rememberFocusMinutes(minutes: number) {
  if (minutes === preferred) return
  preferred = minutes
  listeners.forEach(l => l())
}
export const useFocusMinutes = () => useSyncExternalStore(subscribe, () => preferred)

let audio: AudioContext | null = null

function audioContext() {
  try {
    const Ctx = window.AudioContext ?? (window as Window & { webkitAudioContext?: typeof AudioContext }).webkitAudioContext
    if (Ctx) audio ??= new Ctx()
    void audio?.resume()
  } catch { audio = null }
  return audio
}

// Vanuit een klik aanroepen: dan mag de browser later geluid afspelen, ook als het tabblad op de achtergrond staat.
export function unlockSound() { audioContext() }

// Bij het starten van de focusmodus, vanuit de klik zelf: geluid klaarzetten en één keer vragen of meldingen mogen.
export function prepareFocusAlerts() {
  unlockSound()
  try {
    if ('Notification' in window && Notification.permission === 'default') void Notification.requestPermission()
  } catch { /* deze browser kent geen meldingen */ }
}

export const notificationsBlocked = () => {
  try { return 'Notification' in window && Notification.permission === 'denied' } catch { return false }
}

// Twee zachte tonen kort na elkaar, die rustig uitklinken.
function chime() {
  const ctx = audioContext()
  if (!ctx) return
  try {
    const start = ctx.currentTime + 0.05
    for (const [i, frequency] of [659.25, 880].entries()) {
      const at = start + i * 0.3
      const osc = ctx.createOscillator()
      const gain = ctx.createGain()
      osc.type = 'sine'
      osc.frequency.value = frequency
      gain.gain.setValueAtTime(0, at)
      gain.gain.linearRampToValueAtTime(0.12, at + 0.03)
      gain.gain.exponentialRampToValueAtTime(0.0001, at + 1.2)
      osc.connect(gain).connect(ctx.destination)
      osc.start(at)
      osc.stop(at + 1.3)
    }
  } catch { /* dan zonder geluid */ }
}

async function notify(title: string, body: string, tag: string) {
  try {
    if (!('Notification' in window) || Notification.permission !== 'granted') return
    // Stil: het geluid komt van Potel zelf. Op Android kan een melding alleen via de service worker.
    const options: NotificationOptions = { body, tag, icon: '/icons/icon-192.png', silent: true }
    const registration = 'serviceWorker' in navigator ? await navigator.serviceWorker.getRegistration() : undefined
    if (registration) await registration.showNotification(title, options)
    else new Notification(title, options)
  } catch { /* geen melding mogelijk */ }
}

const ALERTED = 'potel.focus.gemeld'

// Eén keer per blok of pauze, ook als Potel in meer tabbladen openstaat.
function firstToAlert(id: string) {
  try {
    if (localStorage.getItem(ALERTED) === id) return false
    localStorage.setItem(ALERTED, id)
  } catch { /* zonder opslag dan maar in elk tabblad */ }
  return true
}

export function focusAlert(id: string, title: string, body: string) {
  if (!firstToAlert(id)) return
  chime()
  void notify(title, body, `potel-focus-${id}`)
}

export const mmss = (seconds: number) => `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
