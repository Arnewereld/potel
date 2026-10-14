import { useState } from 'react'
import { Target } from 'lucide-react'
import { Modal } from './ui'
import { FOCUS_CHOICES } from '../lib/focus'

// Focusmodus starten: kies hoe lang een blok duurt. Pas als je op Start klikt, vraagt de browser of Potel meldingen mag geven.
export function FocusStartModal({ minutes: initial, projectName, onStart, onClose }: {
  minutes: number
  projectName?: string
  onStart: (minutes: number) => Promise<boolean>
  onClose: () => void
}) {
  const [minutes, setMinutes] = useState(FOCUS_CHOICES.includes(initial) ? initial : 25)
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (busy) return
    setBusy(true)
    const started = await onStart(minutes)
    setBusy(false)
    if (started) onClose()
  }

  return (
    <Modal title="Focusmodus" onClose={onClose} footer={<>
      <button type="button" className="btn" onClick={onClose}>Annuleren</button>
      <button className="btn btn-primary" form="focus-start" disabled={busy}><Target size={15} /> {busy ? 'Starten…' : 'Start focus'}</button>
    </>}>
      <form id="focus-start" className="focus-start" onSubmit={submit}>
        <p className="muted" style={{ marginTop: 0 }}>
          Je werkt in blokken{projectName ? <> aan <strong>{projectName}</strong></> : null}, met na elk blok 5 minuten pauze en na
          vier blokken een lange pauze van 15 minuten. De timer loopt gewoon door en boekt je gewerkte tijd als je stopt. De
          pauzes tellen niet mee.
        </p>
        <label className="field">
          <span>Een blok duurt</span>
          <select value={minutes} onChange={e => setMinutes(Number(e.target.value))}>
            {FOCUS_CHOICES.map(m => <option key={m} value={m}>{m} minuten</option>)}
          </select>
        </label>
        <p className="muted" style={{ marginBottom: 0, fontSize: 13 }}>
          Is een blok of een pauze om, dan hoor je een zacht geluid en krijg je een melding. Potel vraagt daar zo om
          toestemming. Je keuze voor de lengte onthouden we voor de volgende keer.
        </p>
      </form>
    </Modal>
  )
}
