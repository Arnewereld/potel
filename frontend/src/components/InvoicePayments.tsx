import { useState } from 'react'
import { Banknote, CheckCircle2, Plus, Trash2 } from 'lucide-react'
import { api } from '../lib/api'
import { useToast } from '../lib/toast'
import type { Invoice, InvoicePayment, PaymentMethod } from '../lib/types'
import { date, euro, toDateInput } from '../lib/format'
import { paymentMethodLabel, paymentMethods } from '../lib/invoice'
import { Field, Modal } from './ui'

// De betalingen op een verstuurde factuur: wat er binnen is, wat er nog openstaat en een deelbetaling toevoegen.
// Weghalen mag alleen een beheerder; de factuur gaat dan terug naar verzonden of verlopen.
export function PaymentsPanel({ invoice, total, admin, view = false, onChange }: {
  invoice: Invoice
  total: number
  admin: boolean
  // Alleen bekijken, zoals een boekhouder: geen betaling toevoegen of weghalen.
  view?: boolean
  onChange: (inv: Invoice) => void
}) {
  const [adding, setAdding] = useState(false)
  const toast = useToast()
  const payments = invoice.payments ?? []
  const paid = invoice.paidTotal ?? payments.reduce((a, p) => a + p.amount, 0)
  const open = invoice.openAmount ?? 0
  const canAdd = !view && (invoice.status === 'verzonden' || invoice.status === 'verlopen') && !invoice.fullyCredited && open > 0

  const remove = async (p: InvoicePayment) => {
    const back = invoice.status === 'betaald' ? ' De factuur staat daarna weer open.' : ''
    if (!confirm(`De betaling van ${euro(p.amount)} op ${date(p.date)} weghalen?${back}`)) return
    try {
      const saved = await api.del<Invoice>(`/invoices/${invoice.id}/payments/${p.id}`)
      toast('Betaling weggehaald')
      onChange(saved)
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  return (
    <div className="card" style={{ marginBottom: 16 }}>
      <div className="card-head row between wrap" style={{ gap: 8 }}>
        <h3><Banknote size={16} /> Betalingen</h3>
        {canAdd && <button className="btn btn-sm" onClick={() => setAdding(true)}><Plus size={14} /> Betaling toevoegen</button>}
      </div>
      <div className="card-body">
        <p className="muted" style={{ marginTop: 0, fontSize: 13 }}>
          {payments.length === 0
            ? view ? `Er is nog niets van de ${euro(total)} betaald.` : `Er is nog niets betaald. Kreeg je een deel van de ${euro(total)}? Voeg dan een deelbetaling toe.`
            : invoice.status === 'betaald'
              ? `Helemaal betaald: ${euro(paid)} in ${payments.length === 1 ? '1 betaling' : `${payments.length} betalingen`}.`
              : `${euro(paid)} betaald, er staat nog ${euro(open)} open.`}
        </p>
        {payments.length > 0 && (
          <ul className="mail-log">
            {payments.map(p => (
              <li key={p.id}>
                <div className="row between wrap" style={{ gap: 8 }}>
                  <strong>{euro(p.amount)}</strong>
                  <span className="row" style={{ gap: 6 }}>
                    <span className="muted">{date(p.date)}</span>
                    {admin && !view && <button className="icon-btn tiny danger" title="Betaling weghalen" aria-label="Betaling weghalen" onClick={() => remove(p)}><Trash2 size={14} /></button>}
                  </span>
                </div>
                <div className="muted">
                  {paymentMethodLabel(p.method)[0]!.toUpperCase()}{paymentMethodLabel(p.method).slice(1)}
                  {p.createdBy ? `, vastgelegd door ${p.createdBy}` : ''}
                </div>
                {p.note && <div className="muted">{p.note}</div>}
              </li>
            ))}
          </ul>
        )}
      </div>
      {adding && (
        <PaymentDialog invoice={invoice} open={open} onClose={() => setAdding(false)} onSaved={inv => { setAdding(false); onChange(inv) }} />
      )}
    </div>
  )
}

function PaymentDialog({ invoice, open, onClose, onSaved }: {
  invoice: Invoice
  open: number
  onClose: () => void
  onSaved: (inv: Invoice) => void
}) {
  const [amount, setAmount] = useState(String(open.toFixed(2)).replace('.', ','))
  const [day, setDay] = useState(toDateInput(new Date()))
  const [method, setMethod] = useState<PaymentMethod>('bank')
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const toast = useToast()
  // Komma of punt als decimaalteken; duizendtallen met een punt mogen ook (1.250,00).
  const value = Number(amount.trim().replace(/\.(?=\d{3}(\D|$))/g, '').replace(',', '.'))
  const valid = Number.isFinite(value) && value > 0

  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      const saved = await api.post<Invoice>(`/invoices/${invoice.id}/payments`, { amount: value, date: day, method, note: note.trim() || null })
      toast(saved.status === 'betaald' ? 'Betaling vastgelegd. De factuur is nu helemaal betaald.' : `Betaling van ${euro(value)} vastgelegd`)
      onSaved(saved)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title="Betaling toevoegen" onClose={onClose} footer={<>
      <button className="btn" onClick={onClose}>Annuleren</button>
      <button className="btn btn-success" disabled={!valid || busy} onClick={save}><CheckCircle2 size={15} /> {valid ? `${euro(value)} vastleggen` : 'Vastleggen'}</button>
    </>}>
      <p className="muted" style={{ marginTop: 0, fontSize: 13 }}>
        Er staat nog {euro(open)} open. Betaalde de klant maar een deel? Vul dan dat bedrag in. Is alles binnen, dan wordt de factuur betaald.
      </p>
      <div className="form-grid">
        <Field label="Bedrag"><input inputMode="decimal" value={amount} onChange={e => setAmount(e.target.value)} autoFocus /></Field>
        <Field label="Ontvangen op"><input type="date" value={day} onChange={e => setDay(e.target.value)} /></Field>
        <Field label="Hoe">
          <select value={method} onChange={e => setMethod(e.target.value as PaymentMethod)}>
            {paymentMethods.map(m => <option key={m.id} value={m.id}>{m.label}</option>)}
          </select>
        </Field>
        <Field label="Notitie (mag leeg)"><input value={note} maxLength={500} onChange={e => setNote(e.target.value)} placeholder="Bijv. eerste termijn" /></Field>
      </div>
      {error && <p className="text-red" style={{ marginBottom: 0 }}>{error}</p>}
    </Modal>
  )
}
