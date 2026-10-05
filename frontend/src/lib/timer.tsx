import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { api } from './api'
import { toDateInput } from './format'
import { useToast } from './toast'

// Een lopende timer. Hij staat in de browser, zodat hij doorloopt als je de pagina ververst.
export interface RunningTimer {
  projectId: number
  projectName: string
  projectColor: string
  description: string
  billable: boolean
  startedAt: number
}

interface TimerContextValue {
  running: RunningTimer | null
  elapsed: number
  start: (t: Omit<RunningTimer, 'startedAt'>) => void
  update: (patch: Partial<RunningTimer>) => void
  stop: () => Promise<void>
  discard: () => void
}

const KEY = 'potel.timer'
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

function load(): RunningTimer | null {
  try {
    const raw = localStorage.getItem(KEY)
    return raw ? JSON.parse(raw) : null
  } catch { return null }
}

export function TimerProvider({ children }: { children: ReactNode }) {
  const [running, setRunning] = useState<RunningTimer | null>(load)
  const [now, setNow] = useState(Date.now())
  const toast = useToast()

  useEffect(() => {
    try {
      if (running) localStorage.setItem(KEY, JSON.stringify(running))
      else localStorage.removeItem(KEY)
    } catch { /* negeren */ }
  }, [running])

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

  const start = useCallback((t: Omit<RunningTimer, 'startedAt'>) => {
    setNow(Date.now())
    setRunning({ ...t, startedAt: Date.now() })
  }, [])

  const update = useCallback((patch: Partial<RunningTimer>) => setRunning(r => (r ? { ...r, ...patch } : r)), [])

  const stop = useCallback(async () => {
    if (!running) return
    const minutes = Math.max(1, Math.round((Date.now() - running.startedAt) / 60000))
    try {
      await api.post('/time', {
        projectId: running.projectId, date: toDateInput(new Date(running.startedAt)), minutes,
        description: running.description, billable: running.billable,
      })
      setRunning(null)
      toast(`${Math.floor(minutes / 60)}:${String(minutes % 60).padStart(2, '0')} geboekt op ${running.projectName}`)
      notifyTimeChanged()
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }, [running, toast])

  const discard = useCallback(() => setRunning(null), [])

  const elapsed = running ? Math.max(0, Math.floor((now - running.startedAt) / 1000)) : 0
  return <TimerContext.Provider value={{ running, elapsed, start, update, stop, discard }}>{children}</TimerContext.Provider>
}

export function useTimer() {
  const ctx = useContext(TimerContext)
  if (!ctx) throw new Error('useTimer buiten TimerProvider')
  return ctx
}

export const clock = (seconds: number) =>
  `${Math.floor(seconds / 3600)}:${String(Math.floor((seconds % 3600) / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`
