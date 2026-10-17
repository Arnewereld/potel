import { useState } from 'react'
import { api } from '../lib/api'
import { useToast } from '../lib/toast'
import { Field, Modal } from './ui'

export function PasswordModal({ onClose }: { onClose: () => void }) {
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [repeat, setRepeat] = useState('')
  const toast = useToast()

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    if (next !== repeat) return toast('De nieuwe wachtwoorden zijn niet gelijk', 'error')
    try {
      await api.put('/auth/password', { current, new: next })
      toast('Wachtwoord gewijzigd')
      onClose()
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  return (
    <Modal title="Wachtwoord wijzigen" onClose={onClose} footer={<>
      <button className="btn" type="button" onClick={onClose}>Annuleren</button>
      <button className="btn btn-primary" form="password-form">Opslaan</button>
    </>}>
      <form id="password-form" className="form-grid" onSubmit={save}>
        <Field label="Huidig wachtwoord" full><input type="password" autoComplete="current-password" autoFocus required value={current} onChange={e => setCurrent(e.target.value)} /></Field>
        <Field label="Nieuw wachtwoord"><input type="password" autoComplete="new-password" minLength={8} required value={next} onChange={e => setNext(e.target.value)} /></Field>
        <Field label="Herhaal nieuw wachtwoord"><input type="password" autoComplete="new-password" minLength={8} required value={repeat} onChange={e => setRepeat(e.target.value)} /></Field>
      </form>
    </Modal>
  )
}
