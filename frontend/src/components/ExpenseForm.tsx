import { useRef, useState } from 'react'
import { Download, Paperclip, Trash2, Upload } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useAuth } from '../lib/auth'
import { useToast } from '../lib/toast'
import type { Customer, Expense, ExpenseCategory, Project } from '../lib/types'
import { euro, toDateInput } from '../lib/format'
import { expenseCategories, expenseVat, expenseVatRates, fileSize, MaxReceiptBytes, receiptTypes } from '../lib/expenses'
import { Field, Modal } from './ui'

// Leest een bestand als data-URL; de server haalt de base64 eruit en kijkt zelf wat het is.
const readFile = (file: File) => new Promise<string>((resolve, reject) => {
  const reader = new FileReader()
  reader.onload = () => resolve(String(reader.result))
  reader.onerror = () => reject(new Error('Het bestand kon niet worden gelezen'))
  reader.readAsDataURL(file)
})

export function ExpenseForm({ expense, date, onClose, onSaved }: {
  expense?: Expense | null
  date?: string
  onClose: () => void
  onSaved: () => void
}) {
  const { user } = useAuth()
  const admin = user.role === 'beheerder'
  const { data: projects } = useApi<Project[]>('/projects')
  const { data: customers } = useApi<Customer[]>('/customers')
  const [form, setForm] = useState(() => ({
    date: expense?.date.slice(0, 10) ?? date ?? toDateInput(new Date()),
    supplier: expense?.supplier ?? '',
    description: expense?.description ?? '',
    category: expense?.category ?? 'software' as ExpenseCategory,
    amount: expense ? String(expense.amount) : '',
    vatRate: expense ? expense.vatRate : 21 as number | null,
    reverseCharge: expense?.reverseCharge ?? false,
    supplierRegion: expense?.supplierRegion ?? 'eu',
    projectId: expense?.projectId ?? 0,
    customerId: expense?.customerId ?? 0,
  }))
  // Een zelf ingevuld btw-bedrag, voor een bon die net anders is afgerond. Leeg is: laat de server het uitrekenen.
  const [vatOverride, setVatOverride] = useState<string | null>(() => {
    if (!expense) return null
    return expense.vatAmount !== expenseVat(expense.amount, expense.vatRate, expense.reverseCharge) ? String(expense.vatAmount) : null
  })
  const [file, setFile] = useState<File | null>(null)
  const [receipt, setReceipt] = useState(expense?.receipt ?? null)
  const [busy, setBusy] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  const toast = useToast()

  const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) => setForm(f => ({ ...f, [key]: value }))
  const amount = Number(form.amount)
  const canReverse = form.vatRate === 21 || form.vatRate === 9
  const reverse = form.reverseCharge && canReverse
  const computed = expenseVat(amount, form.vatRate, reverse)
  const vat = vatOverride !== null && form.vatRate ? Number(vatOverride) : computed
  const project = projects?.find(p => p.id === form.projectId)
  const selectable = (projects ?? []).filter(p => p.status !== 'afgerond' || p.id === form.projectId)
  // Bestaat er al een bonnetje, dan mag alleen een beheerder het vervangen.
  const canPickFile = !receipt || admin

  const pickProject = (id: number) => {
    const p = projects?.find(x => x.id === id)
    setForm(f => ({ ...f, projectId: id, customerId: p ? p.customerId : f.customerId }))
  }

  const pickFile = (f?: File) => {
    if (input.current) input.current.value = ''
    if (!f) return
    if (!receiptTypes.includes(f.type)) { toast('Kies een PNG-, JPG- of WebP-afbeelding of een PDF', 'error'); return }
    if (f.size > MaxReceiptBytes) { toast('Een bonnetje mag maximaal 5 MB zijn', 'error'); return }
    setFile(f)
  }

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!amount) { toast('Vul het bedrag in', 'error'); return }
    setBusy(true)
    const body = {
      date: form.date, supplier: form.supplier, description: form.description || null, category: form.category,
      amount, vatRate: form.vatRate, vatAmount: vatOverride !== null && form.vatRate ? Number(vatOverride) : null,
      reverseCharge: reverse, supplierRegion: reverse ? form.supplierRegion : null,
      projectId: form.projectId || null, customerId: form.customerId || null,
    }
    try {
      const saved = expense ? await api.put<Expense>(`/expenses/${expense.id}`, body) : await api.post<Expense>('/expenses', body)
      if (file) {
        try {
          await api.put(`/expenses/${saved.id}/receipt`, { fileName: file.name, data: await readFile(file) })
        } catch (err) {
          // De uitgave staat er al; laat de gebruiker het bonnetje los opnieuw proberen.
          toast(`De uitgave is opgeslagen, maar het bonnetje niet: ${(err as Error).message}`, 'error')
          onSaved()
          return
        }
      }
      toast(expense ? 'Uitgave aangepast' : 'Uitgave toegevoegd')
      onSaved()
    } catch (err) {
      toast((err as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    if (!expense || !confirm(`De uitgave bij ${expense.supplier} verwijderen?${receipt ? ' Het bonnetje gaat ook weg.' : ''}`)) return
    try {
      await api.del(`/expenses/${expense.id}`)
      toast('Uitgave verwijderd')
      onSaved()
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  const removeReceipt = async () => {
    if (!expense || !confirm('Dit bonnetje verwijderen? Je moet bonnetjes zeven jaar bewaren voor je administratie.')) return
    try {
      await api.del(`/expenses/${expense.id}/receipt`)
      setReceipt(null)
      toast('Bonnetje verwijderd')
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  return (
    <Modal
      wide
      title={expense ? 'Uitgave bewerken' : 'Uitgave toevoegen'}
      onClose={onClose}
      footer={<>
        {expense && admin && <button className="btn btn-danger" type="button" onClick={remove}>Verwijderen</button>}
        <span className="spacer" />
        <button className="btn" type="button" onClick={onClose}>Annuleren</button>
        <button className="btn btn-primary" form="expense-form" disabled={busy}>Opslaan</button>
      </>}
    >
      <form id="expense-form" className="form-grid" onSubmit={save}>
        <Field label="Datum op de bon"><input type="date" required value={form.date} onChange={e => set('date', e.target.value)} /></Field>
        <Field label="Leverancier *">
          <input required maxLength={200} autoFocus={!expense} value={form.supplier} onChange={e => set('supplier', e.target.value)} placeholder="Bijv. Coolblue, JetBrains" />
        </Field>
        <Field label="Omschrijving" full>
          <input maxLength={1000} value={form.description} onChange={e => set('description', e.target.value)} placeholder="Bijv. nieuwe laptop, licentie IntelliJ" />
        </Field>
        <Field label="Categorie">
          <select value={form.category} onChange={e => set('category', e.target.value as ExpenseCategory)}>
            {expenseCategories.map(c => <option key={c.id} value={c.id}>{c.label}</option>)}
          </select>
        </Field>
        <Field label={reverse ? 'Bedrag zonder btw *' : 'Bedrag inclusief btw *'}>
          <input type="number" required step="0.01" inputMode="decimal" value={form.amount} onChange={e => set('amount', e.target.value)} placeholder="0,00" />
        </Field>
        <Field label="Btw-tarief">
          <div className="segmented">
            {expenseVatRates.map(r => (
              <button type="button" key={String(r.value)} className={form.vatRate === r.value ? 'active' : ''}
                onClick={() => { set('vatRate', r.value); setVatOverride(null) }}>{r.label}</button>
            ))}
          </div>
        </Field>
        <Field label="Btw-bedrag">
          <div className="input-with-chips">
            <input type="number" step="0.01" inputMode="decimal" disabled={!form.vatRate}
              value={vatOverride ?? String(computed)} onChange={e => setVatOverride(e.target.value)} />
            {vatOverride !== null && form.vatRate ? (
              <button type="button" className="chip" title="Btw opnieuw uitrekenen" onClick={() => setVatOverride(null)}>Berekend: {euro(computed)}</button>
            ) : null}
          </div>
        </Field>

        <label className={`check-row field-full ${canReverse ? '' : 'muted'}`}>
          <input type="checkbox" disabled={!canReverse} checked={reverse} onChange={e => { set('reverseCharge', e.target.checked); setVatOverride(null) }} />
          <span>Btw verlegd naar mij</span>
          <span className="muted">· een buitenlandse leverancier rekende geen btw</span>
        </label>
        {reverse && (<>
          <Field label="Waar zit de leverancier?">
            <div className="segmented">
              <button type="button" className={form.supplierRegion === 'eu' ? 'active' : ''} onClick={() => set('supplierRegion', 'eu')}>Binnen de EU</button>
              <button type="button" className={form.supplierRegion === 'buiten-eu' ? 'active' : ''} onClick={() => set('supplierRegion', 'buiten-eu')}>Buiten de EU</button>
            </div>
          </Field>
          <div className="notice field-full" style={{ marginBottom: 0 }}>
            Je betaalde {amount ? euro(amount) : 'het bedrag'} zonder btw. Je geeft de {euro(vat)} btw zelf aan in je btw-aangifte
            en trekt hem in dezelfde aangifte weer af. Met de KOR of vrijgestelde omzet mag dat aftrekken niet.
          </div>
        </>)}

        <Field label="Project (optioneel)">
          <select value={form.projectId} onChange={e => pickProject(Number(e.target.value))}>
            <option value={0}>Geen project</option>
            {selectable.map(p => <option key={p.id} value={p.id}>{p.name} · {p.customerName}</option>)}
          </select>
        </Field>
        <Field label="Klant (optioneel)">
          <select value={form.customerId} disabled={!!project} title={project ? 'Hoort bij het gekozen project' : undefined}
            onChange={e => set('customerId', Number(e.target.value))}>
            <option value={0}>Geen klant</option>
            {(customers ?? []).map(c => <option key={c.id} value={c.id}>{c.company || c.name}</option>)}
          </select>
        </Field>

        <div className="field-full">
          <div className="field-label">Bonnetje (optioneel)</div>
          <div className="logo-drop" onClick={() => canPickFile && input.current?.click()} style={canPickFile ? undefined : { cursor: 'default' }}>
            <Paperclip size={20} className="muted" />
            <div>
              {file ? <><strong>{file.name}</strong><div className="muted">{fileSize(file.size)} · wordt opgeslagen met de uitgave</div></>
                : receipt ? <><strong>{receipt.fileName}</strong><div className="muted">{fileSize(receipt.size)}{admin ? ' · klik om te vervangen' : ''}</div></>
                : <><strong>Kies een bestand</strong><div className="muted">PNG, JPG, WebP of PDF, maximaal 5 MB</div></>}
            </div>
            {receipt && expense && !file && (
              <a className="icon-btn" title="Bonnetje downloaden" href={`/api/expenses/${expense.id}/receipt`} onClick={e => e.stopPropagation()}><Download size={16} /></a>
            )}
            {file && <button type="button" className="icon-btn" title="Niet toevoegen" onClick={e => { e.stopPropagation(); setFile(null) }}><Trash2 size={16} /></button>}
            {receipt && admin && !file && <button type="button" className="icon-btn danger" title="Bonnetje verwijderen" onClick={e => { e.stopPropagation(); removeReceipt() }}><Trash2 size={16} /></button>}
            {canPickFile && !receipt && !file && <Upload size={16} className="muted" />}
          </div>
          <input ref={input} type="file" accept={receiptTypes.join(',')} hidden onChange={e => pickFile(e.target.files?.[0])} />
        </div>
      </form>
    </Modal>
  )
}
