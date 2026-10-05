import { useEffect, useState } from 'react'
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import {
  Receipt, Plus, Trash2, Printer, Save, Building, ChevronUp, ChevronDown, Send, CheckCircle2, Copy, Undo2, BellRing, Clock, Mail,
} from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useTabs, useTabTitle } from '../lib/tabs'
import { useToast } from '../lib/toast'
import type { Customer, Invoice, InvoiceLine, Project, Settings } from '../lib/types'
import { date, euro, hours, invoiceTotals, toDateInput } from '../lib/format'
import { invoiceStatuses } from '../lib/status'
import { daysUntil, isCredit, reminderMail, sendMail, units } from '../lib/invoice'
import { Badge, ErrorBox, Field, Loading, Modal, PageHeader } from '../components/ui'
import { InvoicePaper } from '../components/InvoicePaper'

type Draft = Omit<Invoice, 'id'> & { id?: number }

const newLine = (unit = 'uur', unitPrice = 0): InvoiceLine => ({ description: '', quantity: 1, unit, unitPrice, vatRate: 21 })
const emptySettings: Settings = { companyName: '…', defaultHourlyRate: 0, paymentTermDays: 14, weeklyHoursTarget: 0, yearlyHoursTarget: 0 }
const addDays = (d: string, n: number) => { const x = new Date(`${d}T00:00:00`); x.setDate(x.getDate() + n); return toDateInput(x) }

export function InvoiceEditorPage() {
  const { id } = useParams()
  const isNew = id === 'nieuw'
  const [params] = useSearchParams()
  const { pathname, search } = useLocation()
  const { data: customers } = useApi<Customer[]>('/customers')
  const { data: settings } = useApi<Settings>('/settings')
  const [draft, setDraft] = useState<Draft | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [dirty, setDirty] = useState(false)
  const [dialog, setDialog] = useState<'send' | 'paid' | null>(null)
  const { data: projects, reload: reloadProjects } = useApi<Project[]>(draft?.customerId ? `/projects?customerId=${draft.customerId}` : null)
  const { retarget, close } = useTabs()
  const navigate = useNavigate()
  const toast = useToast()
  const credit = draft ? isCredit(draft) : false
  useTabTitle(isNew ? 'Nieuwe factuur' : draft ? `${credit ? 'Creditnota' : 'Factuur'} ${draft.number}` : null)

  useEffect(() => {
    if (isNew) {
      const today = toDateInput(new Date())
      Promise.all([api.get<{ number: string }>('/invoices/next-number'), api.get<Settings>('/settings')]).then(([r, s]) => setDraft({
        number: r.number, customerId: Number(params.get('klant')) || 0, issueDate: today, dueDate: addDays(today, s.paymentTermDays),
        status: 'concept', reference: '', reverseCharge: false, notes: 'Bedankt voor de fijne samenwerking!', lines: [newLine('uur', s.defaultHourlyRate)],
      })).catch(e => setError(e.message))
    } else {
      api.get<Invoice>(`/invoices/${id}`).then(inv => setDraft(fromServer(inv))).catch(e => setError(e.message))
    }
  }, [id, isNew, params])

  if (error) return <ErrorBox message={error} />
  if (!draft) return <Loading />

  const company = settings ?? emptySettings
  const customer = customers?.find(c => c.id === draft.customerId)
  const totals = invoiceTotals(draft.lines)
  const locked = draft.status !== 'concept'
  const openProjects = (projects ?? []).filter(p => p.billing === 'uur' && p.minutesUnbilled > 0)
  const due = daysUntil(draft.dueDate)

  const update = (patch: Partial<Draft>) => { setDraft({ ...draft, ...patch }); setDirty(true) }
  const updateLine = (idx: number, patch: Partial<InvoiceLine>) =>
    update({ lines: draft.lines.map((l, i) => (i === idx ? { ...l, ...patch } : l)) })
  const moveLine = (idx: number, dir: -1 | 1) => {
    const lines = [...draft.lines]
    ;[lines[idx], lines[idx + dir]] = [lines[idx + dir]!, lines[idx]!]
    update({ lines })
  }

  // Slaat op en geeft het id terug; een nieuwe factuur krijgt daarbij een eigen tabblad.
  // Met stay blijft een nieuwe factuur nog even op dit tabblad, zodat er eerst iets aan toegevoegd kan worden.
  const save = async (patch: Partial<Draft> = {}, quiet = false, stay = false): Promise<number | null> => {
    const body = { ...draft, ...patch }
    try {
      if (isNew) {
        const saved = await api.post<Invoice>('/invoices', body)
        if (!quiet) toast(`Factuur ${saved.number} aangemaakt`)
        setDirty(false)
        if (!stay) retarget(pathname + search, `/facturen/${saved.id}`)
        return saved.id
      }
      const saved = await api.put<Invoice>(`/invoices/${id}`, body)
      setDraft(fromServer(saved))
      if (!quiet) toast('Factuur opgeslagen')
      setDirty(false)
      return saved.id
    } catch (e) {
      toast((e as Error).message, 'error')
      return null
    }
  }

  const setStatus = async (status: Draft['status'], paidAt?: string) => {
    if (dirty && !(await save({}, true))) return
    try {
      const saved = await api.post<Invoice>(`/invoices/${id}/status`, { status, paidAt })
      setDraft(fromServer(saved))
      toast(`Factuur ${saved.number} is ${invoiceStatuses.find(s => s.id === saved.status)?.label.toLowerCase()}`)
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  const copy = async (kind: 'duplicate' | 'credit') => {
    try {
      const inv = await api.post<Invoice>(`/invoices/${id}/${kind}`)
      toast(kind === 'credit' ? `Creditnota ${inv.number} aangemaakt` : `Kopie ${inv.number} aangemaakt`)
      navigate(`/facturen/${inv.id}`)
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  const addHours = async (p: Project) => {
    // Lege regels mogen weg; blijft er niets over, dan houdt een tijdelijke regel de factuur geldig tot de uren erop staan.
    const filled = draft.lines.filter(l => l.description.trim())
    const lines = filled.length ? filled : [{ ...newLine(), description: '-', unitPrice: 0 }]
    const invoiceId = isNew || dirty ? await save({ lines }, true, true) : Number(id)
    if (!invoiceId) return
    try {
      const saved = await api.post<Invoice>(`/invoices/${invoiceId}/hours`, { projectId: p.id, detailed: false })
      toast(`${hours(p.minutesUnbilled)} van ${p.name} toegevoegd`)
      setDraft(fromServer(saved))
      reloadProjects(true)
    } catch (e) {
      toast((e as Error).message, 'error')
    }
    if (isNew) retarget(pathname + search, `/facturen/${invoiceId}`)
  }

  const remove = async () => {
    if (!confirm(`Factuur ${draft.number} verwijderen?${locked ? ' Hij is al verstuurd; maak liever een creditnota.' : ''}`)) return
    try {
      await api.del(`/invoices/${id}`)
      toast('Factuur verwijderd')
      close(pathname)
      navigate('/facturen')
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  const statusInfo = invoiceStatuses.find(s => s.id === draft.status)!
  const subtitle = dirty ? 'Niet opgeslagen wijzigingen'
    : draft.status === 'betaald' ? `Betaald${draft.paidAt ? ` op ${date(draft.paidAt)}` : ''}`
    : draft.status === 'verlopen' ? `${-due} dagen te laat`
    : draft.status === 'verzonden' ? (due >= 0 ? `Vervalt over ${due} dagen` : 'Vervallen')
    : 'Concept, nog niet verstuurd'

  return (
    <>
      <div className="no-print">
        <PageHeader
          icon={<Receipt size={20} />}
          title={isNew ? 'Nieuwe factuur' : `${credit ? 'Creditnota' : 'Factuur'} ${draft.number}`}
          subtitle={subtitle}
          actions={<>
            {!isNew && <Badge tone={statusInfo.tone}>{statusInfo.label}</Badge>}
            {!isNew && <button className="btn btn-danger" title="Verwijderen" onClick={remove}><Trash2 size={15} /></button>}
            {!isNew && <button className="btn" title="Dupliceren" onClick={() => copy('duplicate')}><Copy size={15} /></button>}
            {!isNew && locked && !credit && <button className="btn" title="Creditnota maken" onClick={() => copy('credit')}><Undo2 size={15} /> Credit</button>}
            <button className="btn" onClick={() => window.print()}><Printer size={15} /> PDF</button>
            <button className="btn" onClick={() => save()} disabled={!dirty && !isNew}><Save size={15} /> Opslaan</button>
            {draft.status === 'concept' && !isNew && <button className="btn btn-primary" onClick={() => setDialog('send')}><Send size={15} /> Versturen</button>}
            {draft.status === 'verlopen' && <a className="btn" href={reminderMail({ ...draft, id: Number(id) }, customer, company)}><BellRing size={15} /> Herinnering</a>}
            {(draft.status === 'verzonden' || draft.status === 'verlopen') && <button className="btn btn-success" onClick={() => setDialog('paid')}><CheckCircle2 size={15} /> Betaald</button>}
          </>}
        />
      </div>

      <div className="invoice-layout">
        <div className="no-print">
          {locked && (
            <div className="notice">
              Deze factuur is {statusInfo.label.toLowerCase()}. Pas hem liever niet meer aan; maak bij een fout een creditnota.
            </div>
          )}

          <div className="card invoice-form">
            <div className="card-body">
              <div className="form-grid">
                <Field label="Klant *">
                  <select value={draft.customerId} onChange={e => update({ customerId: Number(e.target.value) })}>
                    <option value={0}>Kies een klant…</option>
                    {customers?.map(c => <option key={c.id} value={c.id}>{c.company || c.name}{c.company ? ` (${c.name})` : ''}</option>)}
                  </select>
                </Field>
                <Field label="Factuurnummer"><input value={draft.number} onChange={e => update({ number: e.target.value })} /></Field>
                <Field label="Factuurdatum"><input type="date" value={draft.issueDate} onChange={e => update({ issueDate: e.target.value })} /></Field>
                <Field label="Vervaldatum">
                  <div className="input-with-chips">
                    <input type="date" value={draft.dueDate} onChange={e => update({ dueDate: e.target.value })} />
                    {[14, 30].map(n => (
                      <button key={n} type="button" className={`chip ${draft.dueDate === addDays(draft.issueDate, n) ? 'active' : ''}`} onClick={() => update({ dueDate: addDays(draft.issueDate, n) })}>{n} d</button>
                    ))}
                  </div>
                </Field>
                <Field label="Status">
                  <select value={draft.status} onChange={e => update({ status: e.target.value as Draft['status'] })}>
                    {invoiceStatuses.map(s => <option key={s.id} value={s.id}>{s.label}</option>)}
                  </select>
                </Field>
                <Field label="Referentie van de klant"><input value={draft.reference ?? ''} placeholder="Inkoopnummer of PO" onChange={e => update({ reference: e.target.value })} /></Field>
                <Field label="Btw" full>
                  <label className="check-row toggle-row">
                    <input type="checkbox" checked={draft.reverseCharge}
                      onChange={e => update({ reverseCharge: e.target.checked, lines: draft.lines.map(l => ({ ...l, vatRate: e.target.checked ? 0 : 21 })) })} />
                    <span>Btw verlegd</span>
                    {draft.reverseCharge && !customer?.vatNumber && <span className="text-red" style={{ fontSize: 12 }}>klant heeft geen btw-nummer</span>}
                  </label>
                </Field>
              </div>

              <h4 className="section-title">Regels</h4>
              <div className="lines">
                <div className="line line-head"><span>Omschrijving</span><span>Aantal</span><span>Eenheid</span><span>Tarief</span><span>Btw</span><span className="num">Bedrag</span><span /></div>
                {draft.lines.map((l, i) => (
                  <div key={i} className="line">
                    <input placeholder="Bijv. Ontwikkeling API-koppeling" value={l.description} onChange={e => updateLine(i, { description: e.target.value })} />
                    <input type="number" step="0.25" value={l.quantity} onChange={e => updateLine(i, { quantity: Number(e.target.value) })} />
                    <select value={l.unit} onChange={e => updateLine(i, { unit: e.target.value })}>
                      {(units.includes(l.unit) ? units : [l.unit, ...units]).map(u => <option key={u}>{u}</option>)}
                    </select>
                    <input type="number" step="0.01" value={l.unitPrice} onChange={e => updateLine(i, { unitPrice: Number(e.target.value) })} />
                    <select value={l.vatRate} disabled={draft.reverseCharge} onChange={e => updateLine(i, { vatRate: Number(e.target.value) })}>
                      <option value={21}>21%</option><option value={9}>9%</option><option value={0}>0%</option>
                    </select>
                    <span className="num">{euro(l.quantity * l.unitPrice)}</span>
                    <span className="line-actions">
                      <button className="icon-btn tiny" disabled={i === 0} onClick={() => moveLine(i, -1)} aria-label="Omhoog"><ChevronUp size={14} /></button>
                      <button className="icon-btn tiny" disabled={i === draft.lines.length - 1} onClick={() => moveLine(i, 1)} aria-label="Omlaag"><ChevronDown size={14} /></button>
                      <button className="icon-btn tiny danger" disabled={draft.lines.length === 1} onClick={() => update({ lines: draft.lines.filter((_, x) => x !== i) })} aria-label="Regel verwijderen"><Trash2 size={14} /></button>
                    </span>
                  </div>
                ))}
              </div>
              <div className="row mt between wrap">
                <button className="btn btn-sm" onClick={() => update({ lines: [...draft.lines, newLine(draft.lines.at(-1)?.unit, draft.lines.at(-1)?.unitPrice)] })}><Plus size={14} /> Regel toevoegen</button>
                <div className="line-totals">
                  <span>Subtotaal <strong>{euro(totals.subtotal)}</strong></span>
                  <span>Btw <strong>{euro(totals.vat)}</strong></span>
                  <span>Totaal <strong>{euro(totals.total)}</strong></span>
                </div>
              </div>

              {draft.status === 'concept' && openProjects.length > 0 && (
                <div className="open-hours">
                  <div className="open-hours-head"><Clock size={15} /> Open uren van {customer?.company || customer?.name}</div>
                  {openProjects.map(p => (
                    <div key={p.id} className="open-hours-row">
                      <span className="legend-dot" style={{ background: p.color }} />
                      <span className="grow truncate">{p.name}</span>
                      <span className="muted">{hours(p.minutesUnbilled)} · {euro(p.unbilledValue)}</span>
                      <button className="btn btn-sm" onClick={() => addHours(p)}><Plus size={13} /> Toevoegen</button>
                    </div>
                  ))}
                </div>
              )}

              <Field label="Opmerking op factuur" full><textarea value={draft.notes ?? ''} onChange={e => update({ notes: e.target.value })} /></Field>
              {!settings?.iban && (
                <p className="muted" style={{ fontSize: 13, marginBottom: 0 }}>
                  Tip: vul je IBAN, KvK en btw-nummer in bij <a onClick={() => navigate('/instellingen')} style={{ cursor: 'pointer' }}><Building size={12} /> Instellingen</a>, dan staan ze op elke factuur.
                </p>
              )}
            </div>
          </div>
        </div>

        <InvoicePaper invoice={draft} customer={customer} company={company} />
      </div>

      {dialog === 'send' && (
        <SendDialog
          mailHref={sendMail({ ...draft, id: Number(id) }, customer, company)}
          email={customer?.email}
          onClose={() => setDialog(null)}
          onSent={() => { setDialog(null); setStatus('verzonden') }}
        />
      )}
      {dialog === 'paid' && (
        <PaidDialog total={totals.total} onClose={() => setDialog(null)} onConfirm={paidAt => { setDialog(null); setStatus('betaald', paidAt) }} />
      )}
    </>
  )
}

const fromServer = (inv: Invoice): Draft => ({ ...inv, issueDate: inv.issueDate.slice(0, 10), dueDate: inv.dueDate.slice(0, 10), reference: inv.reference ?? '' })

function SendDialog({ mailHref, email, onClose, onSent }: { mailHref: string; email?: string | null; onClose: () => void; onSent: () => void }) {
  return (
    <Modal title="Factuur versturen" onClose={onClose} footer={<>
      <button className="btn" onClick={onClose}>Annuleren</button>
      <button className="btn btn-primary" onClick={onSent}><CheckCircle2 size={15} /> Markeer als verzonden</button>
    </>}>
      <ol className="steps">
        <li>
          <div><strong>Sla de factuur op als PDF</strong><div className="muted">Kies in het afdrukvenster voor "Opslaan als PDF".</div></div>
          <button className="btn btn-sm" onClick={() => window.print()}><Printer size={14} /> PDF</button>
        </li>
        <li>
          <div><strong>Mail hem naar je klant</strong><div className="muted">{email ? `Er staat een mail klaar voor ${email}. Voeg de PDF als bijlage toe.` : 'Deze klant heeft nog geen e-mailadres.'}</div></div>
          <a className="btn btn-sm" href={mailHref}><Mail size={14} /> Mail openen</a>
        </li>
        <li>
          <div><strong>Markeer als verzonden</strong><div className="muted">Dan houdt het portaal de vervaldatum voor je in de gaten.</div></div>
        </li>
      </ol>
    </Modal>
  )
}

function PaidDialog({ total, onClose, onConfirm }: { total: number; onClose: () => void; onConfirm: (paidAt: string) => void }) {
  const [paidAt, setPaidAt] = useState(toDateInput(new Date()))
  return (
    <Modal title="Betaling ontvangen" onClose={onClose} footer={<>
      <button className="btn" onClick={onClose}>Annuleren</button>
      <button className="btn btn-success" onClick={() => onConfirm(paidAt)}><CheckCircle2 size={15} /> {euro(total)} ontvangen</button>
    </>}>
      <Field label="Betaald op"><input type="date" value={paidAt} onChange={e => setPaidAt(e.target.value)} /></Field>
    </Modal>
  )
}
