import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Blocks, Plus, Trash2, Pencil, ArrowRight } from 'lucide-react'
import { api } from '../lib/api'
import { useModules, parseFields } from '../lib/modules'
import { useToast } from '../lib/toast'
import type { CustomModule, FieldType, ModuleField } from '../lib/types'
import { Empty, Field, Modal, PageHeader } from '../components/ui'
import { ModuleIcon, moduleIcons } from '../components/Icon'

const fieldTypes: { id: FieldType; label: string }[] = [
  { id: 'text', label: 'Tekst' },
  { id: 'textarea', label: 'Lange tekst' },
  { id: 'number', label: 'Getal' },
  { id: 'date', label: 'Datum' },
  { id: 'checkbox', label: 'Ja/nee' },
]
const colors = ['#ff6d5a', '#4ea5ff', '#3ecf8e', '#9b7bff', '#f5b83d', '#ff5ca8', '#2fc6c6']

export function ModulesPage() {
  const { modules, reload } = useModules()
  const [editing, setEditing] = useState<Partial<CustomModule> | null>(null)
  const navigate = useNavigate()

  return (
    <>
      <PageHeader
        icon={<Blocks size={20} />}
        title="Eigen modules"
        subtitle="Maak zelf lijsten met je eigen velden, voor alles wat je wilt bijhouden."
        actions={<button className="btn btn-primary" onClick={() => setEditing({ name: '', icon: 'box', color: colors[0], fieldsJson: '[]' })}><Plus size={16} /> Nieuwe module</button>}
      />
      {modules.length === 0 && (
        <div className="card"><Empty icon={<Blocks size={24} />} title="Nog geen eigen modules" text="Bijvoorbeeld voertuigen, voorraad, contracten of projecten." /></div>
      )}
      <div className="grid grid-3">
        {modules.map(m => {
          const fields = parseFields(m)
          return (
            <div key={m.id} className="card module-card" style={{ '--c': m.color } as React.CSSProperties}>
              <div className="card-body">
                <div className="row between">
                  <div className="row">
                    <span className="wf-mini" style={{ background: m.color, width: 38, height: 38, borderRadius: 10 }}><ModuleIcon name={m.icon} size={18} /></span>
                    <div><strong>{m.name}</strong><div className="cell-sub">{fields.length} velden</div></div>
                  </div>
                  <button className="icon-btn" onClick={() => setEditing(m)} title="Bewerken"><Pencil size={15} /></button>
                </div>
                <div className="chips">{fields.map(f => <span key={f.key} className="chip">{f.label}</span>)}</div>
                <button className="btn btn-sm" style={{ width: '100%', justifyContent: 'center' }} onClick={() => navigate(`/modules/${m.id}`)}>Openen <ArrowRight size={14} /></button>
              </div>
            </div>
          )
        })}
      </div>
      {editing && <ModuleForm module={editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload() }} />}
    </>
  )
}

const slug = (s: string) => s.toLowerCase().normalize('NFD').replace(/[^a-z0-9]+/g, '_').replace(/^_|_$/g, '') || 'veld'

function ModuleForm({ module, onClose, onSaved }: { module: Partial<CustomModule>; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(module.name ?? '')
  const [icon, setIcon] = useState(module.icon ?? 'box')
  const [color, setColor] = useState(module.color ?? colors[0]!)
  const [fields, setFields] = useState<ModuleField[]>(() => (module.fieldsJson ? JSON.parse(module.fieldsJson) : []))
  const toast = useToast()

  const updateField = (i: number, patch: Partial<ModuleField>) => setFields(fs => fs.map((f, x) => (x === i ? { ...f, ...patch } : f)))

  const save = async () => {
    if (!name.trim()) return toast('Geef de module een naam', 'error')
    if (fields.length === 0) return toast('Voeg minstens één veld toe', 'error')
    // Sleutels moeten uniek zijn; nieuwe velden krijgen er een op basis van hun naam.
    const used = new Set<string>()
    const clean = fields.map(f => {
      let key = f.key || slug(f.label)
      while (used.has(key)) key += '_2'
      used.add(key)
      return { ...f, key, label: f.label || key }
    })
    const body = { ...module, name, icon, color, fieldsJson: JSON.stringify(clean) }
    try {
      if (module.id) await api.put(`/modules/${module.id}`, body)
      else await api.post('/modules', body)
      toast('Module opgeslagen')
      onSaved()
    } catch (e) { toast((e as Error).message, 'error') }
  }

  const remove = async () => {
    if (!confirm(`Module "${module.name}" en alle gegevens erin verwijderen?`)) return
    await api.del(`/modules/${module.id}`)
    toast('Module verwijderd')
    onSaved()
  }

  return (
    <Modal wide title={module.id ? 'Module bewerken' : 'Nieuwe module'} onClose={onClose} footer={<>
      {module.id && <button className="btn btn-danger" onClick={remove}><Trash2 size={15} /> Verwijderen</button>}
      <span className="spacer" />
      <button className="btn" onClick={onClose}>Annuleren</button>
      <button className="btn btn-primary" onClick={save}>Opslaan</button>
    </>}>
      <div className="form-grid">
        <Field label="Naam" full><input autoFocus placeholder="Bijvoorbeeld Voorraad" value={name} onChange={e => setName(e.target.value)} /></Field>
        <Field label="Pictogram">
          <div className="icon-grid">
            {Object.keys(moduleIcons).map(k => (
              <button key={k} type="button" className={icon === k ? 'selected' : ''} onClick={() => setIcon(k)}><ModuleIcon name={k} size={16} /></button>
            ))}
          </div>
        </Field>
        <Field label="Kleur">
          <div className="icon-grid">
            {colors.map(c => <button key={c} type="button" className={color === c ? 'selected' : ''} style={{ background: c }} onClick={() => setColor(c)} aria-label={c} />)}
          </div>
        </Field>
      </div>
      <h4 className="section-title">Velden</h4>
      <div className="lines">
        {fields.map((f, i) => (
          <div key={i} className="field-row">
            <input placeholder="Naam van het veld" value={f.label} onChange={e => updateField(i, { label: e.target.value })} />
            <select value={f.type} onChange={e => updateField(i, { type: e.target.value as FieldType })}>
              {fieldTypes.map(t => <option key={t.id} value={t.id}>{t.label}</option>)}
            </select>
            <button className="icon-btn danger" onClick={() => setFields(fs => fs.filter((_, x) => x !== i))}><Trash2 size={15} /></button>
          </div>
        ))}
      </div>
      <button className="btn btn-sm mt" onClick={() => setFields(fs => [...fs, { key: '', label: '', type: 'text' }])}><Plus size={14} /> Veld toevoegen</button>
    </Modal>
  )
}
