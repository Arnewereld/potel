import { useEffect, useState } from 'react'
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { Receipt, Plus, Trash2, Printer, Save, Building, GripVertical } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useTabs, useTabTitle } from '../lib/tabs'
import { useToast } from '../lib/toast'
import type { Customer, Invoice, InvoiceLine, Settings } from '../lib/types'
import { date, euro, invoiceTotals, toDateInput } from '../lib/format'
import { invoiceStatuses } from '../lib/status'
import { ErrorBox, Field, Loading, PageHeader } from '../components/ui'

type Draft = Omit<Invoice, 'id'> & { id?: number }

const newLine = (): InvoiceLine => ({ description: '', quantity: 1, unitPrice: 0, vatRate: 21 })

export function InvoiceEditorPage() {
  const { id } = useParams()
  const isNew = id === 'nieuw'
  const [params] = useSearchParams()
  const { pathname, search } = useLocation()
  const { data: customers } = useApi<Customer[]>('/customers')
  const [draft, setDraft] = useState<Draft | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [dirty, setDirty] = useState(false)
  const { data: settings } = useApi<Settings>('/settings')
  const { retarget, close } = useTabs()
  const navigate = useNavigate()
  const toast = useToast()
  useTabTitle(isNew ? 'Nieuwe factuur' : draft ? `Factuur ${draft.number}` : null)

  useEffect(() => {
    if (isNew) {
      const today = new Date()
      Promise.all([api.get<{ number: string }>('/invoices/next-number'), api.get<Settings>('/settings')]).then(([r, s]) => {
        const due = new Date(); due.setDate(due.getDate() + s.paymentTermDays)
        setDraft({
          number: r.number, customerId: Number(params.get('klant')) || 0, issueDate: toDateInput(today), dueDate: toDateInput(due),
          status: 'concept', notes: paymentNote(s), lines: [newLine()],
        })
      }).catch(e => setError(e.message))
    } else {
      api.get<Invoice>(`/invoices/${id}`).then(inv => setDraft({ ...inv, issueDate: inv.issueDate.slice(0, 10), dueDate: inv.dueDate.slice(0, 10) }))
        .catch(e => setError(e.message))
    }
  }, [id, isNew, params])

  if (error) return <ErrorBox message={error} />
  if (!draft) return <Loading />

  const update = (patch: Partial<Draft>) => { setDraft({ ...draft, ...patch }); setDirty(true) }
  const updateLine = (idx: number, patch: Partial<InvoiceLine>) =>
    update({ lines: draft.lines.map((l, i) => (i === idx ? { ...l, ...patch } : l)) })
  const totals = invoiceTotals(draft.lines)
  const customer = customers?.find(c => c.id === draft.customerId)
  const company: Settings = settings ?? { companyName: '…', defaultHourlyRate: 0, paymentTermDays: 14, weeklyHoursTarget: 0, yearlyHoursTarget: 0 }

  const save = async () => {
    try {
      if (isNew) {
        const saved = await api.post<Invoice>('/invoices', draft)
        toast(`Factuur ${saved.number} aangemaakt`)
        setDirty(false)
        retarget(pathname + search, `/facturen/${saved.id}`)
      } else {
        await api.put<Invoice>(`/invoices/${id}`, draft)
        toast('Factuur opgeslagen')
        setDirty(false)
      }
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  const remove = async () => {
    if (!confirm(`Factuur ${draft.number} verwijderen?`)) return
    await api.del(`/invoices/${id}`)
    toast('Factuur verwijderd')
    close(pathname)
    navigate('/facturen')
  }

  // Btw per tarief voor de specificatie onderaan.
  const vatGroups = draft.lines.reduce<Record<string, number>>((acc, l) => {
    const net = (Number(l.quantity) || 0) * (Number(l.unitPrice) || 0)
    acc[l.vatRate] = (acc[l.vatRate] ?? 0) + Math.round(net * l.vatRate) / 100
    return acc
  }, {})

  return (
    <>
      <div className="no-print">
        <PageHeader
          icon={<Receipt size={20} />}
          title={isNew ? 'Nieuwe factuur' : `Factuur ${draft.number}`}
          subtitle={dirty ? 'Niet opgeslagen wijzigingen' : 'Alles opgeslagen'}
          actions={<>
            {!isNew && <button className="btn btn-danger" onClick={remove}><Trash2 size={15} /></button>}
            <button className="btn" onClick={() => navigate('/instellingen')}><Building size={15} /> Mijn gegevens</button>
            <button className="btn" onClick={() => window.print()}><Printer size={15} /> Afdrukken / PDF</button>
            <button className="btn btn-primary" onClick={save}><Save size={15} /> Opslaan</button>
          </>}
        />
      </div>

      <div className="invoice-layout">
        <div className="card invoice-form no-print">
          <div className="card-body">
            <div className="form-grid">
              <Field label="Klant *">
                <select value={draft.customerId} onChange={e => update({ customerId: Number(e.target.value) })}>
                  <option value={0}>Kies een klant…</option>
                  {customers?.map(c => <option key={c.id} value={c.id}>{c.name}{c.company ? ` (${c.company})` : ''}</option>)}
                </select>
              </Field>
              <Field label="Factuurnummer"><input value={draft.number} onChange={e => update({ number: e.target.value })} /></Field>
              <Field label="Factuurdatum"><input type="date" value={draft.issueDate} onChange={e => update({ issueDate: e.target.value })} /></Field>
              <Field label="Vervaldatum"><input type="date" value={draft.dueDate} onChange={e => update({ dueDate: e.target.value })} /></Field>
              <Field label="Status" full>
                <div className="status-picker">
                  {invoiceStatuses.map(s => (
                    <button key={s.id} type="button" className={`badge badge-${s.tone} ${draft.status === s.id ? 'selected' : ''}`} onClick={() => update({ status: s.id })}>{s.label}</button>
                  ))}
                </div>
              </Field>
            </div>

            <h4 className="section-title">Regels</h4>
            <div className="lines">
              <div className="line line-head"><span /><span>Omschrijving</span><span>Aantal</span><span>Prijs</span><span>Btw</span><span className="num">Totaal</span><span /></div>
              {draft.lines.map((l, i) => (
                <div key={i} className="line">
                  <GripVertical size={14} className="muted" />
                  <input placeholder="Omschrijving" value={l.description} onChange={e => updateLine(i, { description: e.target.value })} />
                  <input type="number" step="0.25" value={l.quantity} onChange={e => updateLine(i, { quantity: Number(e.target.value) })} />
                  <input type="number" step="0.01" value={l.unitPrice} onChange={e => updateLine(i, { unitPrice: Number(e.target.value) })} />
                  <select value={l.vatRate} onChange={e => updateLine(i, { vatRate: Number(e.target.value) })}>
                    <option value={21}>21%</option><option value={9}>9%</option><option value={0}>0%</option>
                  </select>
                  <span className="num">{euro(l.quantity * l.unitPrice)}</span>
                  <button className="icon-btn danger" disabled={draft.lines.length === 1} onClick={() => update({ lines: draft.lines.filter((_, x) => x !== i) })}><Trash2 size={15} /></button>
                </div>
              ))}
            </div>
            <button className="btn btn-sm mt" onClick={() => update({ lines: [...draft.lines, newLine()] })}><Plus size={14} /> Regel toevoegen</button>

            <Field label="Opmerking op factuur" full><textarea value={draft.notes ?? ''} onChange={e => update({ notes: e.target.value })} /></Field>
          </div>
        </div>

        {/* Voorbeeld zoals de factuur er op papier uitziet */}
        <div className="invoice-paper">
          <div className="paper-head">
            <div>
              <div className="paper-logo">{company.companyName.slice(0, 1)}</div>
              <strong>{company.companyName}</strong>
              {company.ownerName && <div>{company.ownerName}</div>}
              <div>{company.address}</div>
              <div>{company.city}</div>
              <div>{[company.email, company.website].filter(Boolean).join(' · ')}</div>
            </div>
            <div className="paper-title">
              <h2>FACTUUR</h2>
              <div>Nummer <strong>{draft.number}</strong></div>
              <div>Datum {date(draft.issueDate)}</div>
              <div>Vervalt {date(draft.dueDate)}</div>
            </div>
          </div>
          <div className="paper-to">
            <div className="paper-label">Factuur aan</div>
            {customer ? (<>
              <strong>{customer.company || customer.name}</strong>
              {customer.company && <div>t.a.v. {customer.name}</div>}
              <div>{customer.address}</div>
              <div>{customer.city}</div>
            </>) : <span className="paper-muted">Nog geen klant gekozen</span>}
          </div>
          <table className="paper-table">
            <thead><tr><th>Omschrijving</th><th className="num">Aantal</th><th className="num">Prijs</th><th className="num">Btw</th><th className="num">Totaal</th></tr></thead>
            <tbody>
              {draft.lines.map((l, i) => (
                <tr key={i}>
                  <td>{l.description || <span className="paper-muted">…</span>}</td>
                  <td className="num">{l.quantity}</td>
                  <td className="num">{euro(l.unitPrice)}</td>
                  <td className="num">{l.vatRate}%</td>
                  <td className="num">{euro(l.quantity * l.unitPrice)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="paper-totals">
            <div><span>Subtotaal</span><span>{euro(totals.subtotal)}</span></div>
            {Object.entries(vatGroups).map(([rate, v]) => <div key={rate}><span>Btw {rate}%</span><span>{euro(v)}</span></div>)}
            <div className="paper-total"><span>Totaal</span><span>{euro(totals.total)}</span></div>
          </div>
          {draft.notes && <p className="paper-notes">{draft.notes}</p>}
          <div className="paper-foot">
            {[company.kvk && `KvK ${company.kvk}`, company.btw && `Btw ${company.btw}`, company.iban && `IBAN ${company.iban}`].filter(Boolean).join(' · ')}
          </div>
        </div>
      </div>

    </>
  )
}

// Zelfde tekst als de server zet bij facturen die van uren gemaakt worden.
const paymentNote = (s: Settings) =>
  `Graag binnen ${s.paymentTermDays} dagen overmaken${s.iban ? ` op ${s.iban} t.n.v. ${s.companyName}` : ''} onder vermelding van het factuurnummer.`
