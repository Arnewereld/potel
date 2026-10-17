import { useState } from 'react'
import { Moon } from 'lucide-react'
import { Modal } from './ui'
import { hm, toDateTimeInput } from '../lib/format'
import type { RunningTimer } from '../lib/timer'

// Wat de gebruiker koos: tot nu boeken (de server neemt dan zijn eigen klok), of tot een eigen eindtijd.
export type EndChoice = { confirmed?: boolean; endedAt?: string }

// Gewerkte minuten tot een moment, afgerond zoals de server boekt (TimerEndpoints.Minutes).
function minutesUntil(t: RunningTimer, end: number) {
  const paused = t.pausedAt ? Date.parse(t.pausedAt) : null
  const until = paused !== null && paused < end ? paused : end
  return Math.max(1, Math.round(((until - Date.parse(t.startedAt)) / 1000 - t.pausedSeconds) / 60))
}

const longDate = (d: Date) => d.toLocaleDateString('nl-NL', { weekday: 'long', day: 'numeric', month: 'long' })
const clockTime = (d: Date) => d.toLocaleTimeString('nl-NL', { hour: '2-digit', minute: '2-digit' })

// Een timer die over middernacht of langer dan 12 uur liep: eerst vragen wanneer je echt stopte, in plaats van stilletjes alles te boeken.
export function TimerEndModal({ timer, nextProject, serverNow, busy, onCancel, onConfirm }: {
  timer: RunningTimer
  nextProject?: string
  serverNow: () => number
  busy: boolean
  onCancel: () => void
  onConfirm: (choice: EndChoice) => Promise<string | null>
}) {
  const [opened] = useState(serverNow)
  const now = toDateTimeInput(new Date(opened))
  const [value, setValue] = useState(now)
  const [error, setError] = useState<string | null>(null)
  const started = new Date(timer.startedAt)
  const chosen = value === now ? opened : new Date(value).getTime()
  const valid = Number.isFinite(chosen) && chosen > started.getTime() && chosen <= opened + 60_000
  const minutes = valid ? minutesUntil(timer, chosen) : 0
  const tooLong = minutes > 24 * 60

  const confirm = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!valid || tooLong) return
    setError(await onConfirm(value === now ? { confirmed: true } : { endedAt: new Date(chosen).toISOString() }))
  }

  return (
    <Modal title="Klopt de eindtijd?" onClose={onCancel} footer={<>
      <button type="button" className="btn" onClick={onCancel}>Annuleren</button>
      <button className="btn btn-primary" form="timer-end" disabled={busy || !valid || tooLong}>
        {busy ? 'Boeken…' : nextProject ? 'Boeken en wisselen' : 'Boeken'}
      </button>
    </>}>
      <form id="timer-end" className="timer-end" onSubmit={confirm}>
        <p className="timer-end-lead">
          <Moon size={18} />
          <span>
            Je timer op <strong>{timer.projectName}</strong> loopt sinds {longDate(started)} om {clockTime(started)}.
            Ben je vergeten hem te stoppen? Kies dan wanneer je echt stopte. De tijd komt op {longDate(started)}.
          </span>
        </p>
        <label className="field">
          <span>Gestopt op</span>
          <input type="datetime-local" value={value} min={toDateTimeInput(started)} max={now} required onChange={e => { setValue(e.target.value); setError(null) }} />
        </label>
        {valid && !tooLong && <p className="muted timer-end-sum">Je boekt <strong className="text-strong">{hm(minutes)}</strong> op {timer.projectName}{nextProject ? `, daarna loopt de timer verder op ${nextProject}` : ''}.</p>}
        {valid && tooLong && <p className="text-red timer-end-sum">Dat is meer dan 24 uur. Meer dan 24 uur op één dag kan niet, dus kies een eerdere eindtijd.</p>}
        {!valid && <p className="text-red timer-end-sum">Kies een eindtijd tussen het begin van de timer en nu.</p>}
        {error && <p className="text-red timer-end-sum">{error}</p>}
      </form>
    </Modal>
  )
}
