import { useState } from 'react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import type { Customer, Project, Settings } from '../lib/types'
import { useToast } from '../lib/toast'
import { Field, Modal } from './ui'

const colors = ['#ff6d5a', '#4ea5ff', '#3ecf8e', '#9b7bff', '#f5b83d', '#ff5ca8', '#2fc6c6']

type Form = Pick<Project, 'name' | 'customerId' | 'status' | 'billing' | 'hourlyRate' | 'fixedPrice' | 'budgetHours' | 'color' | 'repoUrl' | 'description' | 'deadline'>

export function ProjectForm({ project, customerId, onClose, onSaved }: {
  project?: Project | null
  customerId?: number
  onClose: () => void
  onSaved: (p: { id: number }) => void
}) {
  const { data: customers } = useApi<Customer[]>('/customers')
  const { data: settings } = useApi<Settings>('/settings')
  const [form, setForm] = useState<Form>(() => project ? { ...project, deadline: project.deadline?.slice(0, 10) } : {
    name: '', customerId: customerId ?? 0, status: 'actief', billing: 'uur', hourlyRate: 0, fixedPrice: 0,
    budgetHours: null, color: colors[Math.floor(Math.random() * colors.length)]!, repoUrl: '', description: '', deadline: null,
  })
  const [busy, setBusy] = useState(false)
  const toast = useToast()
  const set = <K extends keyof Form>(k: K, v: Form[K]) => setForm(f => ({ ...f, [k]: v }))
  // Een nieuw project krijgt je standaardtarief zodra de instellingen binnen zijn.
  const rate = form.hourlyRate || (project ? 0 : settings?.defaultHourlyRate ?? 0)

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    const body = { ...form, hourlyRate: rate, deadline: form.deadline || null }
    try {
      const saved = project
        ? await api.put<Project>(`/projects/${project.id}`, body)
        : await api.post<Project>('/projects', body)
      toast(project ? 'Project opgeslagen' : 'Project aangemaakt')
      onSaved(saved)
    } catch (err) {
      toast((err as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      title={project ? 'Project bewerken' : 'Nieuw project'}
      onClose={onClose}
      footer={<>
        <button className="btn" type="button" onClick={onClose}>Annuleren</button>
        <button className="btn btn-primary" form="project-form" disabled={busy}>Opslaan</button>
      </>}
    >
      <form id="project-form" className="form-grid" onSubmit={save}>
        <Field label="Projectnaam *" full><input required autoFocus value={form.name} onChange={e => set('name', e.target.value)} placeholder="Bijv. API-koppeling met Exact" /></Field>
        <Field label="Klant *">
          <select required value={form.customerId} onChange={e => set('customerId', Number(e.target.value))}>
            <option value={0} disabled>Kies een klant…</option>
            {customers?.map(c => <option key={c.id} value={c.id}>{c.company || c.name}</option>)}
          </select>
        </Field>
        <Field label="Status">
          <select value={form.status} onChange={e => set('status', e.target.value as Form['status'])}>
            <option value="actief">Actief</option><option value="gepauzeerd">Gepauzeerd</option><option value="afgerond">Afgerond</option>
          </select>
        </Field>
        <Field label="Afrekenen" full>
          <div className="segmented">
            <button type="button" className={form.billing === 'uur' ? 'active' : ''} onClick={() => set('billing', 'uur')}>Per uur</button>
            <button type="button" className={form.billing === 'vast' ? 'active' : ''} onClick={() => set('billing', 'vast')}>Vaste prijs</button>
          </div>
        </Field>
        {form.billing === 'uur'
          ? <Field label="Uurtarief (excl. btw)"><input type="number" min={0} step="0.01" value={rate} onChange={e => set('hourlyRate', Number(e.target.value))} /></Field>
          : <Field label="Vaste prijs (excl. btw)"><input type="number" min={0} step="0.01" value={form.fixedPrice} onChange={e => set('fixedPrice', Number(e.target.value))} /></Field>}
        <Field label="Budget in uren">
          <input type="number" min={0} step="1" placeholder="Geen budget" value={form.budgetHours ?? ''} onChange={e => set('budgetHours', e.target.value === '' ? null : Number(e.target.value))} />
        </Field>
        <Field label="Deadline"><input type="date" value={form.deadline ?? ''} onChange={e => set('deadline', e.target.value)} /></Field>
        <Field label="Repository"><input value={form.repoUrl ?? ''} onChange={e => set('repoUrl', e.target.value)} placeholder="https://github.com/…" /></Field>
        <Field label="Kleur" full>
          <div className="color-row">
            {colors.map(c => <button type="button" key={c} className={`color-swatch ${form.color === c ? 'selected' : ''}`} style={{ background: c }} onClick={() => set('color', c)} aria-label={c} />)}
          </div>
        </Field>
        <Field label="Omschrijving" full><textarea value={form.description ?? ''} onChange={e => set('description', e.target.value)} placeholder="Wat ga je bouwen?" /></Field>
      </form>
    </Modal>
  )
}
