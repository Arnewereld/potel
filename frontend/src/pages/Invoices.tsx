import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Receipt, Plus, Search } from 'lucide-react'
import { useApi } from '../lib/useApi'
import type { Invoice, InvoiceStatus } from '../lib/types'
import { date, euro, invoiceTotals } from '../lib/format'
import { invoiceStatuses } from '../lib/status'
import { Badge, Empty, ErrorBox, Loading, PageHeader, SubTabs } from '../components/ui'

type Filter = 'alle' | InvoiceStatus

export function InvoicesPage() {
  const { data, error, loading, reload } = useApi<Invoice[]>('/invoices')
  const [filter, setFilter] = useState<Filter>('alle')
  const [q, setQ] = useState('')
  const navigate = useNavigate()

  const invoices = data ?? []
  const filtered = useMemo(() => invoices.filter(i =>
    (filter === 'alle' || i.status === filter) &&
    (i.number.toLowerCase().includes(q.toLowerCase()) || (i.customer?.name ?? '').toLowerCase().includes(q.toLowerCase()))
  ), [invoices, filter, q])
  const sum = filtered.reduce((a, i) => a + invoiceTotals(i.lines).total, 0)

  return (
    <>
      <PageHeader
        icon={<Receipt size={20} />}
        title="Facturen"
        subtitle={`${filtered.length} facturen · ${euro(sum)}`}
        actions={<>
          <div className="search-input"><Search size={16} /><input placeholder="Zoek nummer of klant…" value={q} onChange={e => setQ(e.target.value)} /></div>
          <button className="btn btn-primary" onClick={() => navigate('/facturen/nieuw')}><Plus size={16} /> Nieuwe factuur</button>
        </>}
      />

      <SubTabs<Filter> value={filter} onChange={setFilter} tabs={[
        { id: 'alle', label: 'Alle', count: invoices.length },
        ...invoiceStatuses.map(s => ({ id: s.id, label: s.label, count: invoices.filter(i => i.status === s.id).length })),
      ]} />

      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && <Loading />}

      {data && (
        <div className="card">
          {filtered.length === 0 ? (
            <Empty icon={<Receipt size={22} />} title="Geen facturen" text="Hier staan nog geen facturen."
              action={<button className="btn btn-primary" onClick={() => navigate('/facturen/nieuw')}><Plus size={16} /> Factuur maken</button>} />
          ) : (
            <div className="table-wrap">
              <table>
                <thead><tr><th>Nummer</th><th>Klant</th><th>Datum</th><th>Vervalt</th><th>Status</th><th className="num">Bedrag</th></tr></thead>
                <tbody>
                  {filtered.map(i => {
                    const st = invoiceStatuses.find(s => s.id === i.status)!
                    return (
                      <tr key={i.id} className="clickable" onClick={() => navigate(`/facturen/${i.id}`)}>
                        <td><strong>{i.number}</strong></td>
                        <td><div>{i.customer?.name}</div><div className="cell-sub">{i.customer?.company}</div></td>
                        <td className="muted">{date(i.issueDate)}</td>
                        <td className="muted">{date(i.dueDate)}</td>
                        <td><Badge tone={st.tone}>{st.label}</Badge></td>
                        <td className="num"><strong>{euro(invoiceTotals(i.lines).total)}</strong></td>
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
