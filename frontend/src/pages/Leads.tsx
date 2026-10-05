import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Target, Plus, KanbanSquare, List, Building2, UserCheck, Trash2 } from 'lucide-react'
import { useApi } from '../lib/useApi'
import { api } from '../lib/api'
import { useToast } from '../lib/toast'
import type { Customer, Lead, LeadStatus } from '../lib/types'
import { euro, initials } from '../lib/format'
import { colorFor, leadStatuses } from '../lib/status'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHeader, SubTabs } from '../components/ui'

type View = 'bord' | 'lijst'

export function LeadsPage() {
  const { data, setData, error, loading, reload } = useApi<Lead[]>('/leads')
  const [view, setView] = useState<View>('bord')
  const [editing, setEditing] = useState<Partial<Lead> | null>(null)
  const [dragId, setDragId] = useState<number | null>(null)
  const [overCol, setOverCol] = useState<LeadStatus | null>(null)
  const toast = useToast()

  const move = async (lead: Lead, status: LeadStatus) => {
    if (lead.status === status) return
    setData(prev => prev?.map(l => (l.id === lead.id ? { ...l, status } : l)) ?? null)
    try {
      await api.put(`/leads/${lead.id}`, { ...lead, status })
    } catch (e) {
      toast((e as Error).message, 'error')
      reload()
    }
  }

  const leads = data ?? []

  return (
    <>
      <PageHeader
        icon={<Target size={20} />}
        title="Leads"
        subtitle="Sleep leads door je verkoopfases."
        actions={<button className="btn btn-primary" onClick={() => setEditing({ status: 'nieuw', value: 0 })}><Plus size={16} /> Nieuwe lead</button>}
      />

      <SubTabs<View> value={view} onChange={setView} tabs={[
        { id: 'bord', label: 'Bord', icon: <KanbanSquare size={14} /> },
        { id: 'lijst', label: 'Lijst', icon: <List size={14} />, count: leads.length },
      ]} />

      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && <Loading />}

      {data && view === 'bord' && (
        <div className="kanban">
          {leadStatuses.map(col => {
            const items = leads.filter(l => l.status === col.id)
            return (
              <div
                key={col.id}
                className={`kanban-col ${overCol === col.id ? 'over' : ''}`}
                onDragOver={e => { e.preventDefault(); setOverCol(col.id) }}
                onDragLeave={() => setOverCol(c => (c === col.id ? null : c))}
                onDrop={() => {
                  const lead = leads.find(l => l.id === dragId)
                  if (lead) move(lead, col.id)
                  setDragId(null); setOverCol(null)
                }}
              >
                <div className="kanban-head">
                  <span className="legend-dot" style={{ background: col.color }} />
                  <strong>{col.label}</strong>
                  <span className="muted">{items.length}</span>
                  <span className="kanban-sum">{euro(items.reduce((a, l) => a + l.value, 0))}</span>
                </div>
                <div className="kanban-items">
                  {items.map(l => (
                    <div
                      key={l.id}
                      className={`lead-card ${dragId === l.id ? 'dragging' : ''}`}
                      draggable
                      onDragStart={() => setDragId(l.id)}
                      onDragEnd={() => { setDragId(null); setOverCol(null) }}
                      onClick={() => setEditing(l)}
                      style={{ borderLeftColor: col.color }}
                    >
                      <div className="row between">
                        <strong className="truncate">{l.name}</strong>
                        <div className="avatar" style={{ width: 24, height: 24, fontSize: 10, background: colorFor(l.name) }}>{initials(l.name)}</div>
                      </div>
                      {l.company && <div className="cell-sub row"><Building2 size={12} /> {l.company}</div>}
                      <div className="row between" style={{ marginTop: 8 }}>
                        <span className="lead-value">{euro(l.value)}</span>
                        {l.source && <span className="cell-sub">{l.source}</span>}
                      </div>
                    </div>
                  ))}
                  {items.length === 0 && <div className="kanban-empty">Sleep een lead hierheen</div>}
                </div>
              </div>
            )
          })}
        </div>
      )}

      {data && view === 'lijst' && (
        <div className="card">
          {leads.length === 0 ? <Empty icon={<Target size={22} />} title="Nog geen leads" /> : (
            <div className="table-wrap">
              <table>
                <thead><tr><th>Naam</th><th>Bron</th><th>Status</th><th className="num">Waarde</th></tr></thead>
                <tbody>
                  {leads.map(l => {
                    const st = leadStatuses.find(s => s.id === l.status)!
                    return (
                      <tr key={l.id} className="clickable" onClick={() => setEditing(l)}>
                        <td><div>{l.name}</div><div className="cell-sub">{l.company}</div></td>
                        <td className="muted">{l.source}</td>
                        <td><Badge tone={st.tone}>{st.label}</Badge></td>
                        <td className="num">{euro(l.value)}</td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {editing && <LeadForm lead={editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload() }} />}
    </>
  )
}

function LeadForm({ lead, onClose, onSaved }: { lead: Partial<Lead>; onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState<Partial<Lead>>({ name: '', company: '', email: '', phone: '', source: '', notes: '', ...lead })
  const toast = useToast()
  const navigate = useNavigate()
  const set = (k: keyof Lead) => (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement>) =>
    setForm(f => ({ ...f, [k]: k === 'value' ? Number(e.target.value) : e.target.value }))

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    try {
      if (lead.id) await api.put(`/leads/${lead.id}`, form)
      else await api.post('/leads', form)
      toast(lead.id ? 'Lead opgeslagen' : 'Lead toegevoegd')
      onSaved()
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  const convert = async () => {
    try {
      const c = await api.post<Customer>(`/leads/${lead.id}/convert`)
      toast(`${c.name} is nu een klant`)
      onSaved()
      navigate(`/klanten/${c.id}`)
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  const remove = async () => {
    if (!confirm('Deze lead verwijderen?')) return
    await api.del(`/leads/${lead.id}`)
    toast('Lead verwijderd')
    onSaved()
  }

  return (
    <Modal
      title={lead.id ? 'Lead bewerken' : 'Nieuwe lead'}
      onClose={onClose}
      footer={<>
        {lead.id && <button className="btn btn-danger" type="button" onClick={remove}><Trash2 size={15} /></button>}
        {lead.id && (
          <button className="btn" type="button" onClick={convert}>
            <UserCheck size={15} /> {lead.customerId ? 'Open klant' : 'Maak klant'}
          </button>
        )}
        <span className="spacer" />
        <button className="btn" type="button" onClick={onClose}>Annuleren</button>
        <button className="btn btn-primary" form="lead-form">Opslaan</button>
      </>}
    >
      <form id="lead-form" className="form-grid" onSubmit={save}>
        <Field label="Naam *"><input required autoFocus value={form.name} onChange={set('name')} /></Field>
        <Field label="Bedrijf"><input value={form.company ?? ''} onChange={set('company')} /></Field>
        <Field label="E-mail"><input type="email" value={form.email ?? ''} onChange={set('email')} /></Field>
        <Field label="Telefoon"><input value={form.phone ?? ''} onChange={set('phone')} /></Field>
        <Field label="Waarde (€)"><input type="number" min="0" step="50" value={form.value ?? 0} onChange={set('value')} /></Field>
        <Field label="Status">
          <select value={form.status} onChange={set('status')}>
            {leadStatuses.map(s => <option key={s.id} value={s.id}>{s.label}</option>)}
          </select>
        </Field>
        <Field label="Bron" full><input placeholder="Website, beurs, doorverwijzing…" value={form.source ?? ''} onChange={set('source')} /></Field>
        <Field label="Notities" full><textarea value={form.notes ?? ''} onChange={set('notes')} /></Field>
      </form>
    </Modal>
  )
}
