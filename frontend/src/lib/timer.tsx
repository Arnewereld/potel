import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { api, ApiError } from './api'
import { useAuth } from './auth'
import { focusAlert, mmss, prepareFocusAlerts, rememberFocusMinutes, unlockSound, useFocusMinutes } from './focus'
import { hm } from './format'
import { timerKey } from './storage'
import { useToast } from './toast'
import { TimerEndModal, type EndChoice } from '../components/TimerEndModal'

// Focusmodus: blokken werk met een pauze ertussen. De server houdt bij waar je bent; tijdens een pauze staat de timer stil.
export interface FocusState {
  minutes: number
  // focus: aan het werk; break: de pauze loopt; ready: de pauze is om en het volgende blok kan beginnen.
  phase: 'focus' | 'break' | 'ready'
  // Afgemaakte blokken met deze timer, en hoeveel daarvan sinds de laatste lange pauze.
  blocks: number
  round: number
  // Gewerkte seconden van de timer toen dit blok begon.
  blockStart: number
  breakStartedAt: string | null
  breakMinutes: number
  remainingSeconds: number
}

// Wat er nu in beeld staat, elke seconde bijgewerkt.
export interface FocusLive {
  phase: FocusState['phase']
  remaining: number
  total: number
}

// De lopende timer staat op de server, per gebruiker: op je laptop gestart, op je telefoon gestopt.
export interface RunningTimer {
  key: string
  projectId: number
  projectName: string
  projectColor: string
  customerName?: string | null
  description: string
  billable: boolean
  // Tijden in UTC, zoals de server ze geeft.
  startedAt: string
  pausedAt: string | null
  pausedSeconds: number
  elapsedSeconds: number
  // Loopt hij over middernacht of langer dan 12 uur, dan vraagt stoppen eerst om de eindtijd.
  needsConfirmation: boolean
  focus: FocusState | null
}

export interface TimerState {
  timer: RunningTimer | null
  serverNow: string
  entry?: { id: number; projectId: number; minutes: number } | null
  // De lengte van een focusblok die je het laatst koos, op elk apparaat dezelfde.
  focusMinutes?: number | null
}

export interface TimerStart { projectId: number; description: string; billable?: boolean }
export type TimerSwitch = TimerStart & { projectName: string }
type TimerPatch = { description?: string; billable?: boolean }

// Na de vraag of de eindtijd klopt: gewoon stoppen, of wisselen naar een ander project.
type Ending = { timer: RunningTimer; next?: TimerSwitch }

interface TimerContextValue {
  running: RunningTimer | null
  elapsed: number
  paused: boolean
  stopping: boolean
  start: (t: TimerStart) => Promise<boolean>
  update: (patch: TimerPatch) => void
  pause: () => Promise<void>
  resume: () => Promise<void>
  stop: () => Promise<void>
  switchTo: (t: TimerSwitch) => Promise<void>
  discard: () => Promise<void>
  refresh: () => Promise<void>
  // De tijd van de server in milliseconden, ook als de klok van dit apparaat afwijkt.
  serverNow: () => number
  focus: FocusLive | null
  focusMinutes: number
  startFocus: (minutes: number, start?: TimerStart) => Promise<boolean>
  stopFocus: () => Promise<void>
  nextBlock: () => Promise<void>
  focusOpen: boolean
  openFocus: () => void
  closeFocus: () => void
}

const TimerContext = createContext<TimerContextValue | null>(null)

// Pagina's met uren luisteren hiernaar om zichzelf te verversen.
export const TIME_CHANGED = 'potel:time-changed'
export const notifyTimeChanged = () => window.dispatchEvent(new Event(TIME_CHANGED))

export function useOnTimeChanged(fn: () => void) {
  useEffect(() => {
    window.addEventListener(TIME_CHANGED, fn)
    return () => window.removeEventListener(TIME_CHANGED, fn)
  }, [fn])
}

// Gewerkte seconden tot een moment, zonder pauzes. Dezelfde som als op de server (TimerEndpoints.WorkedSeconds).
function workedSeconds(t: RunningTimer, at: number) {
  const paused = t.pausedAt ? Date.parse(t.pausedAt) : null
  const end = paused !== null && paused < at ? paused : at
  return Math.max(0, Math.floor((end - Date.parse(t.startedAt)) / 1000 - t.pausedSeconds))
}

// Hoe vaak de timer stilletjes bij de server kijkt of er op een ander apparaat iets veranderde.
const REFRESH_MS = 30_000
const CHANNEL = 'potel-timer'

// Waar de focusmodus nu staat: de stand van de server, doorgeteld met de klok van dit apparaat.
function focusLive(t: RunningTimer, elapsed: number, at: number): FocusLive | null {
  const f = t.focus
  if (!f) return null
  if (f.phase === 'focus') {
    const total = f.minutes * 60
    return { phase: 'focus', remaining: Math.max(0, total - (elapsed - f.blockStart)), total }
  }
  const total = f.breakMinutes * 60
  const since = f.breakStartedAt ? Math.floor((at - Date.parse(f.breakStartedAt)) / 1000) : total
  const remaining = Math.max(0, Math.min(total, total - since))
  return { phase: remaining > 0 ? 'break' : 'ready', remaining, total }
}

const needsConfirmation = (e: unknown) =>
  e instanceof ApiError && e.status === 409 && (e.data as { needsConfirmation?: boolean } | undefined)?.needsConfirmation === true

// Een timer uit een oudere versie van Potel, die alleen in deze browser stond.
interface LegacyTimer { projectId: number; description?: string; billable?: boolean; startedAt: number; clientId?: string }

export function TimerProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth()
  const toast = useToast()
  const [running, setRunning] = useState<RunningTimer | null>(null)
  const [offset, setOffset] = useState(0)
  const [now, setNow] = useState(() => Date.now())
  const [stopping, setStopping] = useState(false)
  const [ending, setEnding] = useState<Ending | null>(null)
  const focusMinutes = useFocusMinutes()
  const [focusOpen, setFocusOpen] = useState(false)
  const runningRef = useRef<RunningTimer | null>(null)
  const offsetRef = useRef(0)
  const stoppingRef = useRef(false)
  // Elke wijziging telt op; een verversing die al onderweg was toen er iets veranderde, telt dan niet meer.
  const seq = useRef(0)
  // Een omschrijving die je aan het typen bent en die nog niet naar de server is.
  const pending = useRef<TimerPatch | null>(null)
  const flushTimer = useRef<number | undefined>(undefined)
  const channel = useRef<BroadcastChannel | null>(null)

  const serverNow = useCallback(() => Date.now() + offsetRef.current, [])

  const apply = useCallback((state: Pick<TimerState, 'timer' | 'serverNow'>) => {
    const server = Date.parse(state.serverNow)
    if (!Number.isNaN(server)) {
      offsetRef.current = server - Date.now()
      setOffset(offsetRef.current)
    }
    const local = pending.current
    const timer = state.timer && local && state.timer.key === runningRef.current?.key ? { ...state.timer, ...local } : state.timer
    if (!timer) pending.current = null
    // Gestopt of focusmodus uit, hier of op een ander apparaat: dan gaat de focusweergave ook dicht.
    if (!timer?.focus) setFocusOpen(false)
    runningRef.current = timer
    setRunning(timer)
    setNow(Date.now())
  }, [])

  // Andere tabbladen in deze browser horen het meteen; andere apparaten bij de volgende verversing.
  const announce = useCallback(() => { try { channel.current?.postMessage('changed') } catch { /* negeren */ } }, [])

  // Een wijziging naar de server. Weigert de server, dan staat de echte stand in het antwoord en nemen we die over.
  const mutate = useCallback(async (send: () => Promise<TimerState>) => {
    seq.current++
    try {
      const state = await send()
      seq.current++
      apply(state)
      announce()
      return state
    } catch (e) {
      const data = e instanceof ApiError ? e.data as Partial<TimerState> | undefined : undefined
      if (data && 'timer' in data && data.serverNow) { seq.current++; apply(data as TimerState) }
      throw e
    }
  }, [apply, announce])

  const refresh = useCallback(async () => {
    const mine = ++seq.current
    try {
      const state = await api.get<TimerState>('/timer')
      if (state.focusMinutes) rememberFocusMinutes(state.focusMinutes)
      if (mine !== seq.current) return
      // Op een ander apparaat gestopt of gewisseld: dan staan er nieuwe uren, en ververst de urenpagina zich.
      const before = runningRef.current?.key
      apply(state)
      if (before && before !== state.timer?.key) notifyTimeChanged()
    } catch { /* offline of uitgelogd: de volgende keer weer */ }
  }, [apply])

  const flush = useCallback(async () => {
    window.clearTimeout(flushTimer.current)
    const patch = pending.current
    const key = runningRef.current?.key
    if (!patch || !key) return
    try {
      await mutate(() => api.put<TimerState>('/timer', { key, ...patch }))
      // Pas weg als het is aangekomen; wat je intussen verder typte, blijft staan.
      if (pending.current === patch) pending.current = null
    } catch (e) {
      if (pending.current === patch) pending.current = null
      toast((e as Error).message, 'error')
    }
  }, [mutate, toast])

  // Bij het openen, als je terugkomt naar het venster en af en toe tussendoor: kijken hoe de timer er op de server voor staat.
  useEffect(() => {
    void refresh()
    const onFocus = () => void refresh()
    const onVisible = () => { if (document.visibilityState === 'visible') void refresh() }
    window.addEventListener('focus', onFocus)
    window.addEventListener('online', onFocus)
    document.addEventListener('visibilitychange', onVisible)
    const every = window.setInterval(onVisible, REFRESH_MS)
    try {
      channel.current = new BroadcastChannel(CHANNEL)
      channel.current.onmessage = () => void refresh()
    } catch { channel.current = null }
    return () => {
      window.removeEventListener('focus', onFocus)
      window.removeEventListener('online', onFocus)
      document.removeEventListener('visibilitychange', onVisible)
      window.clearInterval(every)
      channel.current?.close()
      channel.current = null
    }
  }, [refresh])

  // Eén keer overzetten: een timer die nog in deze browser stond, gaat naar de server. Daarna gebruiken we de browser er niet meer voor.
  const imported = useRef(false)
  useEffect(() => {
    if (imported.current) return
    imported.current = true
    const storageKey = timerKey(user)
    let legacy: LegacyTimer | null = null
    try { legacy = JSON.parse(localStorage.getItem(storageKey) ?? 'null') as LegacyTimer | null } catch { legacy = null }
    const forget = () => { try { localStorage.removeItem(storageKey) } catch { /* negeren */ } }
    if (!legacy) return
    if (typeof legacy.projectId !== 'number' || typeof legacy.startedAt !== 'number') { forget(); return }
    mutate(() => api.post<TimerState>('/timer/import', {
      key: legacy.clientId, projectId: legacy.projectId, description: legacy.description ?? '', billable: legacy.billable ?? true,
      startedAt: new Date(legacy.startedAt).toISOString(),
    })).then(forget, e => {
      // Alleen-lezen: bewaren tot het weer kan. Geen verbinding: de volgende keer opnieuw.
      if (!(e instanceof ApiError)) return
      if (e.status !== 402) forget()
      toast(e.message, 'error')
    })
  }, [user, mutate, toast])

  // Elke seconde verder zolang de timer loopt, en tijdens de pauze van de focusmodus voor het aftellen.
  const ticking = running !== null && (running.pausedAt === null || running.focus?.phase === 'break')
  useEffect(() => {
    if (!ticking) return
    const t = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(t)
  }, [ticking])

  const elapsed = running ? workedSeconds(running, now + offset) : 0
  const paused = running?.pausedAt != null
  const focus = running ? focusLive(running, elapsed, now + offset) : null

  useEffect(() => {
    const base = 'Potel · Freelance dev'
    if (!running) { document.title = base; return }
    const m = Math.floor(elapsed / 60)
    document.title = focus
      ? `${focus.phase === 'focus' ? (paused ? '⏸' : '◎') : '☕'} ${mmss(focus.remaining)} ${running.projectName} · ${base}`
      : `${paused ? '⏸' : '▶'} ${Math.floor(m / 60)}:${String(m % 60).padStart(2, '0')} ${running.projectName} · ${base}`
  }, [running, elapsed, paused, focus])

  // Het blok is hier om: de server zet de timer stil en begint de pauze. Even vragen hoe hij ervoor staat, tot het zover is.
  const blockDone = running?.focus?.phase === 'focus' && focus?.remaining === 0 && !paused
  useEffect(() => {
    if (!blockDone) return
    void refresh()
    const again = window.setInterval(() => void refresh(), 3000)
    return () => window.clearInterval(again)
  }, [blockDone, refresh])

  // Een geluid en een melding als een blok of een pauze om is, maar alleen als je dat hier zag gebeuren: wie Potel opent
  // na een blok dat allang klaar was, krijgt geen melding achteraf.
  const focusKey = running?.focus ? running.key : null
  const focusBlocks = running?.focus?.blocks ?? 0
  const focusPhase = focus?.phase ?? null
  const lastFocus = useRef<{ key: string; blocks: number; phase: string } | null>(null)
  useEffect(() => {
    const before = lastFocus.current
    const current = focusKey && focusPhase ? { key: focusKey, blocks: focusBlocks, phase: focusPhase } : null
    lastFocus.current = current
    const t = runningRef.current
    if (!before || !current || !t?.focus || before.key !== current.key) return
    const id = `${current.key}:${current.blocks}:${current.phase}`
    if (before.phase === 'focus' && current.phase !== 'focus' && current.blocks > before.blocks) {
      const long = t.focus.breakMinutes > 5
      // Zonder projectnaam: een melding staat soms op een vergrendeld scherm.
      focusAlert(id, 'Focusblok klaar', long
        ? `Vier blokken gedaan. Neem een lange pauze van ${t.focus.breakMinutes} minuten; de timer staat zolang stil.`
        : `Goed bezig. Neem ${t.focus.breakMinutes} minuten pauze; de timer staat zolang stil.`)
    } else if (before.phase === 'break' && current.phase === 'ready') {
      focusAlert(id, 'Pauze voorbij', 'Klaar voor het volgende focusblok? Open Potel en ga verder.')
    }
  }, [focusKey, focusBlocks, focusPhase])

  const booked = useCallback((state: TimerState, projectName: string, next?: string) => {
    if (state.entry) toast(`${hm(state.entry.minutes)} geboekt op ${projectName}${next ? `. De timer loopt nu op ${next}.` : ''}`)
    notifyTimeChanged()
  }, [toast])

  const start = useCallback(async (t: TimerStart) => {
    try {
      await mutate(() => api.post<TimerState>('/timer', t))
      return true
    } catch (e) {
      toast((e as Error).message, 'error')
      return false
    }
  }, [mutate, toast])

  // Omschrijving en factureerbaar meteen in beeld, en even later naar de server.
  const update = useCallback((patch: TimerPatch) => {
    if (!runningRef.current) return
    pending.current = { ...pending.current, ...patch }
    runningRef.current = { ...runningRef.current, ...patch }
    setRunning(runningRef.current)
    window.clearTimeout(flushTimer.current)
    flushTimer.current = window.setTimeout(() => void flush(), 700)
  }, [flush])

  const simple = useCallback(async (path: string) => {
    await flush()
    const key = runningRef.current?.key
    if (!key) return
    try {
      await mutate(() => api.post<TimerState>(path, { key }))
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }, [flush, mutate, toast])

  const pause = useCallback(() => simple('/timer/pause'), [simple])
  const resume = useCallback(() => simple('/timer/resume'), [simple])
  // Na de pauze van de focusmodus (of eerder, als je de pauze overslaat): verder met het volgende blok.
  const nextBlock = useCallback(() => { unlockSound(); return simple('/timer/resume') }, [simple])

  // Focusmodus aan, op de timer die loopt of met een nieuwe. Vanuit de klik zelf vragen we of meldingen mogen.
  const startFocus = useCallback(async (minutes: number, t?: TimerStart) => {
    prepareFocusAlerts()
    await flush()
    const key = runningRef.current?.key
    try {
      await mutate(() => api.post<TimerState>('/timer/focus', key ? { key, minutes } : { minutes, ...t }))
      rememberFocusMinutes(minutes)
      setFocusOpen(true)
      return true
    } catch (e) {
      toast((e as Error).message, 'error')
      return false
    }
  }, [flush, mutate, toast])

  // Focusmodus uit: de timer loopt als gewone timer verder.
  const stopFocus = useCallback(async () => {
    const key = runningRef.current?.key
    if (!key) return
    try {
      await mutate(() => api.del<TimerState>(`/timer/focus?key=${encodeURIComponent(key)}`))
      setFocusOpen(false)
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }, [mutate, toast])

  const openFocus = useCallback(() => setFocusOpen(true), [])
  const closeFocus = useCallback(() => setFocusOpen(false), [])

  // Eén keer stoppen is één boeking: een dubbelklik doet niets extra, en stopt een ander apparaat dezelfde timer tegelijk,
  // dan herkent de server hem aan zijn kenmerk en blijft het bij de eerste boeking.
  // Vanuit de vraag naar de eindtijd gaat het om de timer waar die vraag over ging, ook als er intussen op een ander apparaat
  // een nieuwe is gestart: de server herkent hem aan zijn kenmerk en stopt de nieuwe dan niet.
  const end = useCallback(async (next?: TimerSwitch, choice?: EndChoice, asked?: RunningTimer) => {
    if (stoppingRef.current) return null
    await flush()
    const timer = asked ?? runningRef.current
    if (!timer) return null
    stoppingRef.current = true
    setStopping(true)
    try {
      const state = await mutate(() => next
        ? api.post<TimerState>('/timer/switch', { key: timer.key, ...next, ...choice })
        : api.post<TimerState>('/timer/stop', { key: timer.key, ...choice }))
      setEnding(null)
      booked(state, timer.projectName, next?.projectName)
      return null
    } catch (e) {
      if (needsConfirmation(e)) {
        setEnding({ timer: runningRef.current ?? timer, next })
        return null
      }
      // In de vraag naar de eindtijd blijft de melding daar staan; anders als melding onderin.
      if (choice && e instanceof ApiError && e.status === 400) return e.message
      setEnding(null)
      toast((e as Error).message, 'error')
      return null
    } finally {
      stoppingRef.current = false
      setStopping(false)
    }
  }, [flush, mutate, booked, toast])

  const stop = useCallback(async () => { await end() }, [end])
  const switchTo = useCallback(async (next: TimerSwitch) => { await end(next) }, [end])

  const discard = useCallback(async () => {
    window.clearTimeout(flushTimer.current)
    pending.current = null
    const key = runningRef.current?.key
    if (!key) return
    try {
      await mutate(() => api.del<TimerState>(`/timer?key=${encodeURIComponent(key)}`))
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }, [mutate, toast])

  return (
    <TimerContext.Provider value={{
      running, elapsed, paused, stopping, start, update, pause, resume, stop, switchTo, discard, refresh, serverNow,
      focus, focusMinutes, startFocus, stopFocus, nextBlock, focusOpen, openFocus, closeFocus,
    }}>
      {children}
      {ending && (
        <TimerEndModal
          timer={ending.timer}
          nextProject={ending.next?.projectName}
          serverNow={serverNow}
          busy={stopping}
          onCancel={() => setEnding(null)}
          onConfirm={choice => end(ending.next, choice, ending.timer)}
        />
      )}
    </TimerContext.Provider>
  )
}

export function useTimer() {
  const ctx = useContext(TimerContext)
  if (!ctx) throw new Error('useTimer buiten TimerProvider')
  return ctx
}

export const clock = (seconds: number) =>
  `${Math.floor(seconds / 3600)}:${String(Math.floor((seconds % 3600) / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`
