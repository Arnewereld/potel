import { useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { Plus, Search, Trash2, Settings2, Check } from 'lucide-react'
import { useApi } from '../lib/useApi'
import { api } from '../lib/api'
import { useModules, parseFields } from '../lib/modules'
import { useTabTitle } from '../lib/tabs'
import { useToast } from '../lib/toast'
import type { CustomRecord, ModuleField } from '../lib/types'
import { date } from '../lib/format'
import { Empty, ErrorBox, Field, Loading, Modal, PageHeader } from '../components/ui'
import { ModuleIcon } from '../components/Icon'

type Data = Record<string, string | number | boolean>

const parse = (r: CustomRecord): Data => { try { return JSON.parse(r.dataJson) } catch { return {} } }

function show(f: ModuleField, v: Data[string] | undefined) {
  if (v === undefined || v === '') return <span className="muted">—</span>
  if (f.type === 'checkbox') return v ? <Check size={16} color="var(--green)" /> : <span className="muted">—</span>
  if (f.type === 'date') return date(String(v))
  if (f.type === 'number') return Number(v).toLocaleString('nl-NL')
  return String(v)
}

export function ModuleRecordsPage() {
  const { id } = useParams()
  const { modules } = useModules()
  const module = modules.find(m => m.id === Number(id))
  const { data, error, loading, reload } = useApi<CustomRecord[]>(`/modules/${id}/records`)
  const [editing, setEditing] = useState<{ id?: number; data: Data } | null>(null)
  const [q, setQ] = useState('')
  const navigate = useNavigate()
  useTabTitle(module?.name)

  const fields = module ? parseFields(module) : []
  const rows = useMemo(() => (data ?? []).map(r => ({ id: r.id, data: parse(r) }))
    .filter(r => !q || Object.values(r.data).some(v => String(v).toLowerCase().includes(q.toLowerCase()))), [data, q])

  if (error) return <ErrorBox message={error} onRetry={() => reload()} />
  if (!module || (loading && !data)) return <Loading />

  return (
    <>
      <PageHeader
        icon={<ModuleIcon name={module.icon} size={20} />}
        title={module.name}
        subtitle={`${data?.length ?? 0} items`}
        actions={<>
          <div className="search-input"><Search size={16} /><input placeholder="Zoeken…" value={q} onChange={e => setQ(e.target.value)} /></div>
          <button className="btn" onClick={() => navigate('/modules')}><Settings2 size={15} /> Velden</button>
          <button className="btn btn-primary" onClick={() => setEditing({ data: {} })}><Plus size={16} /> Toevoegen</button>
        </>}
      />
      <div className="card">
        {rows.length === 0 ? (
          <Empty icon={<ModuleIcon name={module.icon} size={22} />} title="Nog niets toegevoegd" action={<button className="btn btn-primary" onClick={() => setEditing({ data: {} })}><Plus size={16} /> Toevoegen</button>} />
        ) : (
          <div className="table-wrap">
            <table>
              <thead><tr>{fields.map(f => <th key={f.key} className={f.type === 'number' ? 'num' : ''}>{f.label}</th>)}</tr></thead>
              <tbody>
                {rows.map(r => (
                  <tr key={r.id} className="clickable" onClick={() => setEditing(r)}>
                    {fields.map(f => <td key={f.key} className={f.type === 'number' ? 'num' : ''}>{show(f, r.data[f.key])}</td>)}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      {editing && <RecordForm moduleId={module.id} fields={fields} record={editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload() }} />}
    </>
  )
}

function RecordForm({ moduleId, fields, record, onClose, onSaved }: {
  moduleId: number; fields: ModuleField[]; record: { id?: number; data: Data }; onClose: () => void; onSaved: () => void
}) {
  const [form, setForm] = useState<Data>(record.data)
  const toast = useToast()

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    const body = { dataJson: JSON.stringify(form) }
    try {
      if (record.id) await api.put(`/modules/${moduleId}/records/${record.id}`, body)
      else await api.post(`/modules/${moduleId}/records`, body)
      toast('Opgeslagen')
      onSaved()
    } catch (err) { toast((err as Error).message, 'error') }
  }

  const remove = async () => {
    await api.del(`/modules/${moduleId}/records/${record.id}`)
    toast('Verwijderd')
    onSaved()
  }

  return (
    <Modal title={record.id ? 'Bewerken' : 'Toevoegen'} onClose={onClose} footer={<>
      {record.id && <button className="btn btn-danger" type="button" onClick={remove}><Trash2 size={15} /></button>}
      <span className="spacer" />
      <button className="btn" type="button" onClick={onClose}>Annuleren</button>
      <button className="btn btn-primary" form="record-form">Opslaan</button>
    </>}>
      <form id="record-form" className="form-grid" onSubmit={save}>
        {fields.map((f, i) => (
          <Field key={f.key} label={f.label} full={f.type === 'textarea' || f.type === 'checkbox'}>
            {f.type === 'textarea' ? (
              <textarea autoFocus={i === 0} value={String(form[f.key] ?? '')} onChange={e => setForm({ ...form, [f.key]: e.target.value })} />
            ) : f.type === 'checkbox' ? (
              <input type="checkbox" checked={!!form[f.key]} onChange={e => setForm({ ...form, [f.key]: e.target.checked })} />
            ) : (
              <input autoFocus={i === 0} type={f.type} value={String(form[f.key] ?? '')}
                onChange={e => setForm({ ...form, [f.key]: f.type === 'number' ? (e.target.value === '' ? '' : Number(e.target.value)) : e.target.value })} />
            )}
          </Field>
        ))}
      </form>
    </Modal>
  )
}
