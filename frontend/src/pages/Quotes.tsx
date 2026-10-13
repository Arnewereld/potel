import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { FileSignature, Plus, Search, Send, FileClock, ThumbsUp, Hourglass, Copy } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import type { Quote, QuoteState } from '../lib/types'
import { date, euro, invoiceTotals } from '../lib/format'
import { quoteStatuses } from '../lib/status'
import { daysUntil } from '../lib/invoice'
import { quoteState } from '../lib/quote'
import { Badge, Empty, ErrorBox, Loading, PageHeader, SubTabs } from '../components/ui'
import { MiniStat } from './Time'

type Filter = 'alle' | QuoteState

const who = (q: Quote) => q.buyer?.name ?? q.customer?.company ?? q.customer?.name ?? q.lead?.company ?? q.lead?.name ?? 'Onbekend'

export function QuotesPage() {
  const { data, error, loading, reload } = useApi<Quote[]>('/quotes')
  const [filter, setFilter] = useState<Filter>('alle')
  const [q, setQ] = useState('')
  const navigate = useNavigate()
  const toast = useToast()

  const quotes = useMemo(() => (data ?? []).map(x => ({ ...x, state: quoteState(x) })), [data])
  const filtered = useMemo(() => {
    const s = q.toLowerCase()
    return quotes.filter(x => (filter === 'alle' || x.state === filter)
      && [x.number, x.subject, who(x), x.customer?.name, x.lead?.name].some(v => v?.toLowerCase().includes(s)))
  }, [quotes, filter, q])

  const by = (s: QuoteState) => quotes.filter(x => x.state === s)
  const subtotal = (list: Quote[]) => list.reduce((a, x) => a + invoiceTotals(x.lines).subtotal, 0)
  const open = by('verstuurd')
  const expiring = open.filter(x => daysUntil(x.validUntil) <= 7)
  // Van de offertes waarover besloten is (of die verlopen zijn): hoeveel er geaccepteerd zijn.
  const decided = by('geaccepteerd').length + by('afgewezen').length + by('verlopen').length
  const rate = decided ? Math.round(100 * by('geaccepteerd').length / decided) : null

  const duplicate = async (quote: Quote) => {
    try {
      const copy = await api.post<Quote>(`/quotes/${quote.id}/duplicate`)
      toast(copy.version > 1 ? `Versie ${copy.version} aangemaakt als concept` : 'Kopie aangemaakt als concept')
      navigate(`/offertes/${copy.id}`)
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  return (
    <>
      <PageHeader
        icon={<FileSignature size={20} />}
        title="Offertes"
        subtitle={`${filtered.length} offertes · ${euro(subtotal(filtered))} zonder btw`}
        actions={<>
          <div className="search-input"><Search size={16} /><input placeholder="Zoek nummer, onderwerp of klant…" value={q} onChange={e => setQ(e.target.value)} /></div>
          <button className="btn btn-primary" onClick={() => navigate('/offertes/nieuw')}><Plus size={16} /> Nieuwe offerte</button>
        </>}
      />

      {data && (
        <div className="grid grid-stats" style={{ marginBottom: 18 }}>
          <MiniStat icon={<Send size={18} />} accent="var(--blue)" label="Openstaand" value={euro(subtotal(open))}
            sub={`${open.length} offertes wachten op je klant`} onClick={() => setFilter('verstuurd')} />
          <MiniStat icon={<Hourglass size={18} />} accent="var(--yellow)" label="Verloopt binnen 7 dagen" value={String(expiring.length)}
            sub={expiring.length ? 'Even nabellen?' : 'Niets loopt binnenkort af'} onClick={() => setFilter('verstuurd')} />
          <MiniStat icon={<ThumbsUp size={18} />} accent="var(--green)" label="Geaccepteerd" value={rate === null ? '–' : `${rate}%`}
            sub={decided ? `${by('geaccepteerd').length} van de ${decided} offertes waarover besloten is` : 'Nog geen besluiten'} onClick={() => setFilter('geaccepteerd')} />
          <MiniStat icon={<FileClock size={18} />} accent="var(--purple)" label="Concepten" value={String(by('concept').length)}
            sub={by('concept').length ? `${euro(subtotal(by('concept')))} nog te versturen` : 'Alles is verstuurd'} onClick={() => setFilter('concept')} />
        </div>
      )}

      <SubTabs<Filter> value={filter} onChange={setFilter} tabs={[
        { id: 'alle', label: 'Alle', count: quotes.length },
        ...quoteStatuses.map(s => ({ id: s.id, label: s.label, count: by(s.id).length })),
      ]} />

      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && <Loading />}

      {data && (
        <div className="card">
          {filtered.length === 0 ? (
            <Empty icon={<FileSignature size={22} />} title="Geen offertes" text={q ? 'Probeer een andere zoekterm.' : 'Maak een offerte voor een klant of lead. Je klant accepteert hem online, met één klik.'}
              action={!q && <button className="btn btn-primary" onClick={() => navigate('/offertes/nieuw')}><Plus size={16} /> Offerte maken</button>} />
          ) : (
            <div className="table-wrap">
              <table>
                <thead><tr><th>Nummer</th><th>Voor</th><th>Datum</th><th>Geldig tot</th><th>Status</th><th className="num">Bedrag</th><th /></tr></thead>
                <tbody>
                  {filtered.map(x => {
                    const st = quoteStatuses.find(s => s.id === x.state)!
                    const days = daysUntil(x.validUntil)
                    return (
                      <tr key={x.id} className="clickable" onClick={() => navigate(`/offertes/${x.id}`)}>
                        <td>
                          {x.number ? <strong>{x.number}</strong> : <span className="muted">Concept</span>}
                          {x.version > 1 && <span className="badge badge-purple" style={{ marginLeft: 8 }}>versie {x.version}</span>}
                          {x.demo && <span className="badge badge-gray" style={{ marginLeft: 8 }}>voorbeeld</span>}
                          {x.subject && <div className="cell-sub">{x.subject}</div>}
                        </td>
                        <td>
                          <div>{who(x)}</div>
                          <div className="cell-sub">{x.customerId ? 'klant' : 'lead'}</div>
                        </td>
                        <td className="muted">{date(x.issueDate)}</td>
                        <td>
                          <div className="muted">{date(x.validUntil)}</div>
                          {x.state === 'verstuurd' && <div className={`cell-sub ${days <= 3 ? 'text-yellow' : ''}`}>{days === 0 ? 'laatste dag' : `nog ${days} dagen`}</div>}
                          {x.state === 'geaccepteerd' && x.decidedName && <div className="cell-sub text-green">door {x.decidedName}</div>}
                        </td>
                        <td><Badge tone={st.tone}>{st.label}</Badge></td>
                        <td className="num"><strong>{euro(invoiceTotals(x.lines).total)}</strong></td>
                        <td className="row-actions" onClick={e => e.stopPropagation()}>
                          {!x.newerVersionId && (
                            <button className="icon-btn" title={x.state === 'concept' || x.demo ? 'Kopie maken als nieuw concept' : 'Nieuwe versie maken'} onClick={() => duplicate(x)}><Copy size={16} /></button>
                          )}
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
