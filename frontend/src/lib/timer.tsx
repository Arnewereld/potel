import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { api } from './api'
import { useAuth } from './auth'
import { toDateInput } from './format'
import { timerKey } from './storage'
import { useToast } from './toast'

// Een lopende timer. Hij staat in de browser, per gebruiker en werkruimte, zodat hij doorloopt als je de pagina ververst.
export interface RunningTimer {
  projectId: number
  projectName: string
  projectColor: string
  description: string
  billable: boolean
  startedAt: number
  // Gaat mee naar de server, zodat dezelfde timer nooit twee keer geboekt wordt.
  clientId: string
}

interface TimerContextValue {
  running: RunningTimer | null
  elapsed: number
  stopping: boolean
  start: (t: Omit<RunningTimer, 'startedAt' | 'clientId'>) => void
  update: (patch: Partial<RunningTimer>) => void
  stop: () => Promise<void>
  discard: () => void
}

const TimerContext = createContext<TimerContextValue | null>(null)

const newClientId = () =>
  typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`

// Pagina's met uren luisteren hiernaar om zichzelf te verversen.
export const TIME_CHANGED = 'potel:time-changed'
export const notifyTimeChanged = () => window.dispatchEvent(new Event(TIME_CHANGED))

export function useOnTimeChanged(fn: () => void) {
  useEffect(() => {
    window.addEventListener(TIME_CHANGED, fn)
    return () => window.removeEventListener(TIME_CHANGED, fn)
  }, [fn])
}

function parse(raw: string | null): RunningTimer | null {
  try {
    const t = raw ? JSON.parse(raw) as RunningTimer : null
    return t ? { ...t, clientId: t.clientId || newClientId() } : null
  } catch { return null }
}

function load(key: string): RunningTimer | null {
  try { return parse(localStorage.getItem(key)) } catch { return null }
}

export function TimerProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth()
  const key = timerKey(user)
  const [running, setRunning] = useState<RunningTimer | null>(() => load(key))
  const [now, setNow] = useState(Date.now())
  const [stopping, setStopping] = useState(false)
  const stoppingRef = useRef(false)
  const toast = useToast()

  useEffect(() => {
    try {
      if (running) localStorage.setItem(key, JSON.stringify(running))
      else localStorage.removeItem(key)
    } catch { /* negeren */ }
  }, [running, key])

  // Andere tabbladen houden dezelfde timer bij: starten, stoppen en de omschrijving lopen gelijk.
  useEffect(() => {
    const onStorage = (e: StorageEvent) => { if (e.key === key) setRunning(parse(e.newValue)) }
    window.addEventListener('storage', onStorage)
    return () => window.removeEventListener('storage', onStorage)
  }, [key])

  useEffect(() => {
    if (!running) return
    const t = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(t)
  }, [running])

  useEffect(() => {
    const base = 'Potel · Freelance dev'
    if (!running) { document.title = base; return }
    const m = Math.floor((now - running.startedAt) / 60000)
    document.title = `▶ ${Math.floor(m / 60)}:${String(m % 60).padStart(2, '0')} ${running.projectName} · ${base}`
  }, [running, now])

  const start = useCallback((t: Omit<RunningTimer, 'startedAt' | 'clientId'>) => {
    setNow(Date.now())
    setRunning({ ...t, startedAt: Date.now(), clientId: newClientId() })
  }, [])

  const update = useCallback((patch: Partial<RunningTimer>) => setRunning(r => (r ? { ...r, ...patch } : r)), [])

  // Eén keer stoppen is één boeking: een dubbelklik doet niets extra, en stopt een ander tabblad dezelfde
  // timer tegelijk, dan herkent de server hem aan clientId en blijft het bij de eerste boeking.
  const stop = useCallback(async () => {
    if (!running || stoppingRef.current) return
    stoppingRef.current = true
    setStopping(true)
    const minutes = Math.max(1, Math.round((Date.now() - running.startedAt) / 60000))
    try {
      await api.post('/time', {
        projectId: running.projectId, date: toDateInput(new Date(running.startedAt)), minutes,
        description: running.description, billable: running.billable, clientId: running.clientId,
      })
      setRunning(null)
      toast(`${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, '0')} geboekt op ${running.projectName}`)
      notifyTimeChanged()
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      stoppingRef.current = false
      setStopping(false)
    }
  }, [running, toast])

  const discard = useCallback(() => setRunning(null), [])

  const elapsed = running ? Math.max(0, Math.floor((now - running.startedAt) / 1000)) : 0
  return <TimerContext.Provider value={{ running, elapsed, stopping, start, update, stop, discard }}>{children}</TimerContext.Provider>
}

export function useTimer() {
  const ctx = useContext(TimerContext)
  if (!ctx) throw new Error('useTimer buiten TimerProvider')
  return ctx
}

export const clock = (seconds: number) =>
  `${Math.floor(seconds / 3600)}:${String(Math.floor((seconds % 3600) / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`
