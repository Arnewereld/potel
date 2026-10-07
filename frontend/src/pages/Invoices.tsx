import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Receipt, Plus, Search, CheckCircle2, Copy, BellRing, AlertTriangle, FileClock, Send, Euro } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import type { Invoice, InvoiceStatus, Settings } from '../lib/types'
import { date, euro, invoiceTotals, toDateInput } from '../lib/format'
import { invoiceStatuses } from '../lib/status'
import { daysUntil, invoiceTitle, isCredit, openAmount, reminderMail } from '../lib/invoice'
import { Badge, Empty, ErrorBox, Loading, PageHeader, SubTabs } from '../components/ui'
import { MiniStat } from './Time'

type Filter = 'alle' | InvoiceStatus

export function InvoicesPage() {
  const { data, error, loading, reload } = useApi<Invoice[]>('/invoices')
  const { data: settings } = useApi<Settings>('/settings')
  const [filter, setFilter] = useState<Filter>('alle')
  const [q, setQ] = useState('')
  const navigate = useNavigate()
  const toast = useToast()

  const invoices = data ?? []
  const filtered = useMemo(() => {
    const s = q.toLowerCase()
    return invoices.filter(i =>
      (filter === 'alle' || i.status === filter) &&
      [i.number, i.customer?.name, i.customer?.company, i.reference].some(v => v?.toLowerCase().includes(s)))
  }, [invoices, filter, q])

  const total = (list: Invoice[]) => list.reduce((a, i) => a + invoiceTotals(i.lines).total, 0)
  // Wat klanten nog moeten betalen: verstuurde creditnota's zijn al van hun factuur afgetrokken.
  const open = (list: Invoice[]) => list.reduce((a, i) => a + openAmount(i), 0)
  const by = (s: InvoiceStatus) => invoices.filter(i => i.status === s)
  const late = by('verlopen').filter(i => openAmount(i) > 0)
  const year = new Date().getFullYear()
  // Een verrekende creditnota telt alleen mee als zijn factuur betaald is; een helemaal gecrediteerde factuur telde ook niet mee.
  const paidIds = new Set(by('betaald').map(i => i.id))
  const paidThisYear = by('betaald').filter(i => (i.creditForInvoiceId == null || paidIds.has(i.creditForInvoiceId))
    && new Date(i.paidAt ?? i.issueDate).getFullYear() === year)

  const markPaid = async (inv: Invoice) => {
    try {
      await api.post(`/invoices/${inv.id}/status`, { status: 'betaald', paidAt: toDateInput(new Date()) })
      toast(`${invoiceTitle(inv)} is ${isCredit(inv) ? 'verrekend' : 'betaald'}`)
      reload(true)
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  const duplicate = async (inv: Invoice) => {
    try {
      const copy = await api.post<Invoice>(`/invoices/${inv.id}/duplicate`)
      toast('Kopie aangemaakt als concept')
      navigate(`/facturen/${copy.id}`)
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  return (
    <>
      <PageHeader
        icon={<Receipt size={20} />}
        title="Facturen"
        subtitle={`${filtered.length} facturen · ${euro(total(filtered))}`}
        actions={<>
          <div className="search-input"><Search size={16} /><input placeholder="Zoek nummer, klant of referentie…" value={q} onChange={e => setQ(e.target.value)} /></div>
          <button className="btn btn-primary" onClick={() => navigate('/facturen/nieuw')}><Plus size={16} /> Nieuwe factuur</button>
        </>}
      />

      {data && (
        <div className="grid grid-stats" style={{ marginBottom: 18 }}>
          <MiniStat icon={<Send size={18} />} accent="var(--blue)" label="Openstaand" value={euro(open(by('verzonden')))}
            sub={`${by('verzonden').length} verzonden, nog niet vervallen`} onClick={() => setFilter('verzonden')} />
          <MiniStat icon={<AlertTriangle size={18} />} accent="var(--red)" label="Te laat" value={euro(open(late))}
            sub={late.length ? `${late.length} facturen, stuur een herinnering` : 'Niemand is te laat'} onClick={() => setFilter('verlopen')} />
          <MiniStat icon={<FileClock size={18} />} accent="var(--yellow)" label="Concepten" value={String(by('concept').length)}
            sub={by('concept').length ? `${euro(total(by('concept')))} nog te versturen` : 'Alles is verstuurd'} onClick={() => setFilter('concept')} />
          <MiniStat icon={<Euro size={18} />} accent="var(--green)" label={`Ontvangen in ${year}`} value={euro(total(paidThisYear))}
            sub={`${paidThisYear.length} betaalde facturen`} onClick={() => setFilter('betaald')} />
        </div>
      )}

      <SubTabs<Filter> value={filter} onChange={setFilter} tabs={[
        { id: 'alle', label: 'Alle', count: invoices.length },
        ...invoiceStatuses.map(s => ({ id: s.id, label: s.label, count: by(s.id).length })),
      ]} />

      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && <Loading />}

      {data && (
        <div className="card">
          {filtered.length === 0 ? (
            <Empty icon={<Receipt size={22} />} title="Geen facturen" text={q ? 'Probeer een andere zoekterm.' : 'Hier staan nog geen facturen.'}
              action={!q && <button className="btn btn-primary" onClick={() => navigate('/facturen/nieuw')}><Plus size={16} /> Factuur maken</button>} />
          ) : (
            <div className="table-wrap">
              <table>
                <thead><tr><th>Nummer</th><th>Klant</th><th>Datum</th><th>Vervalt</th><th>Status</th><th className="num">Bedrag</th><th /></tr></thead>
                <tbody>
                  {filtered.map(i => {
                    const st = invoiceStatuses.find(s => s.id === i.status)!
                    const days = daysUntil(i.dueDate)
                    const unpaid = (i.status === 'verzonden' || i.status === 'verlopen') && !i.fullyCredited
                    const credited = !isCredit(i) && (i.creditNotes?.length ?? 0) > 0
                    return (
                      <tr key={i.id} className="clickable" onClick={() => navigate(`/facturen/${i.id}`)}>
                        <td>
                          {i.number ? <strong>{i.number}</strong> : <span className="muted">Concept</span>}
                          {isCredit(i) && <span className="badge badge-purple" style={{ marginLeft: 8 }}>credit</span>}
                          {credited && <span className="badge badge-purple" style={{ marginLeft: 8 }}>{i.fullyCredited ? 'gecrediteerd' : 'deels gecrediteerd'}</span>}
                          {i.demo && <span className="badge badge-gray" style={{ marginLeft: 8 }}>voorbeeld</span>}
                          {i.reference && <div className="cell-sub">{i.reference}</div>}
                        </td>
                        <td><div>{i.customer?.company || i.customer?.name}</div>{i.customer?.company && <div className="cell-sub">{i.customer.name}</div>}</td>
                        <td className="muted">{date(i.issueDate)}</td>
                        <td>
                          <div className="muted">{date(i.dueDate)}</div>
                          {unpaid && <div className={`cell-sub ${days < 0 ? 'text-red' : days <= 3 ? 'text-yellow' : ''}`}>
                            {days < 0 ? `${-days} dagen te laat` : days === 0 ? 'vandaag' : `nog ${days} dagen`}
                          </div>}
                          {i.status === 'betaald' && i.paidAt && <div className="cell-sub text-green">betaald {date(i.paidAt)}</div>}
                        </td>
                        <td><Badge tone={st.tone}>{st.label}</Badge></td>
                        <td className="num">
                          <strong>{euro(invoiceTotals(i.lines).total)}</strong>
                          {credited && unpaid && <div className="cell-sub">nog {euro(openAmount(i))} open</div>}
                        </td>
                        <td className="row-actions" onClick={e => e.stopPropagation()}>
                          {unpaid && <button className="icon-btn" title={isCredit(i) ? 'Markeer als verrekend' : 'Markeer als betaald'} onClick={() => markPaid(i)}><CheckCircle2 size={16} /></button>}
                          {i.status === 'verlopen' && unpaid && settings && <a className="icon-btn" title="Herinnering mailen" href={reminderMail(i, i.customer, settings)}><BellRing size={16} /></a>}
                          {!isCredit(i) && <button className="icon-btn" title="Kopie maken als nieuw concept" onClick={() => duplicate(i)}><Copy size={16} /></button>}
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
    </>
  )
}
