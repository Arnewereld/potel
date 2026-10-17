import { useState } from 'react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import type { Project, TimeEntry } from '../lib/types'
import { hm, parseDuration, toDateInput } from '../lib/format'
import { notifyTimeChanged } from '../lib/timer'
import { useToast } from '../lib/toast'
import { Field, Modal } from './ui'

export function TimeEntryForm({ entry, date, projectId, onClose, onSaved }: {
  entry?: TimeEntry | null
  date?: string
  projectId?: number
  onClose: () => void
  onSaved: () => void
}) {
  const { data: projects } = useApi<Project[]>('/projects')
  const [form, setForm] = useState({
    projectId: entry?.projectId ?? projectId ?? 0,
    date: entry?.date.slice(0, 10) ?? date ?? toDateInput(new Date()),
    duration: entry ? hm(entry.minutes) : '',
    description: entry?.description ?? '',
    billable: entry?.billable ?? true,
  })
  const [busy, setBusy] = useState(false)
  const toast = useToast()
  const minutes = parseDuration(form.duration)
  const selectable = (projects ?? []).filter(p => p.status !== 'afgerond' || p.id === form.projectId)

  const pickProject = (id: number) => {
    const p = projects?.find(x => x.id === id)
    // Uren op een project met vaste prijs zijn standaard niet apart te factureren.
    setForm(f => ({ ...f, projectId: id, billable: entry ? f.billable : p?.billing !== 'vast' }))
  }

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!minutes) { toast('Vul de tijd in, bijvoorbeeld 1:30 of 1,5', 'error'); return }
    setBusy(true)
    const body = { projectId: form.projectId, date: form.date, minutes, description: form.description, billable: form.billable }
    try {
      if (entry) await api.put(`/time/${entry.id}`, body)
      else await api.post('/time', body)
      toast(entry ? 'Uren aangepast' : 'Uren geboekt')
      notifyTimeChanged()
      onSaved()
    } catch (err) {
      toast((err as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    if (!entry || !confirm('Deze uren verwijderen?')) return
    try {
      await api.del(`/time/${entry.id}`)
      toast('Uren verwijderd')
      notifyTimeChanged()
      onSaved()
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  return (
    <Modal
      title={entry ? 'Uren bewerken' : 'Uren boeken'}
      onClose={onClose}
      footer={<>
        {entry && <button className="btn btn-danger" type="button" onClick={remove}>Verwijderen</button>}
        <span className="spacer" />
        <button className="btn" type="button" onClick={onClose}>Annuleren</button>
        <button className="btn btn-primary" form="time-form" disabled={busy}>Opslaan</button>
      </>}
    >
      <form id="time-form" className="form-grid" onSubmit={save}>
        <Field label="Project *" full>
          <select required value={form.projectId} onChange={e => pickProject(Number(e.target.value))}>
            <option value={0} disabled>Kies een project…</option>
            {selectable.map(p => <option key={p.id} value={p.id}>{p.name} · {p.customerName}</option>)}
          </select>
        </Field>
        <Field label="Datum"><input type="date" required value={form.date} onChange={e => setForm(f => ({ ...f, date: e.target.value }))} /></Field>
        <Field label="Tijd *">
          <input required autoFocus={!!projectId} placeholder="1:30 of 1,5" value={form.duration} onChange={e => setForm(f => ({ ...f, duration: e.target.value }))} />
        </Field>
        <Field label="Wat heb je gedaan?" full>
          <input value={form.description} onChange={e => setForm(f => ({ ...f, description: e.target.value }))} placeholder="Bijv. bugfix checkout, code review" />
        </Field>
        <label className="check-row field-full">
          <input type="checkbox" checked={form.billable} onChange={e => setForm(f => ({ ...f, billable: e.target.checked }))} />
          <span>Factureerbaar</span>
          {minutes ? <span className="muted" style={{ marginLeft: 'auto' }}>{hm(minutes)} uur</span> : null}
        </label>
      </form>
    </Modal>
  )
}
