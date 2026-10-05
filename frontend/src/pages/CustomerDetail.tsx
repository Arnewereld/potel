import { useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { Pencil, Trash2, Mail, Phone, MapPin, Receipt, CalendarDays, StickyNote, User, Plus } from 'lucide-react'
import { useApi } from '../lib/useApi'
import { api } from '../lib/api'
import { useTabs, useTabTitle } from '../lib/tabs'
import { useToast } from '../lib/toast'
import type { Appointment, Customer, Invoice } from '../lib/types'
import { date, dateTime, euro, initials, invoiceTotals } from '../lib/format'
import { colorFor, invoiceStatuses, kindColor } from '../lib/status'
import { Badge, Empty, ErrorBox, Loading, SubTabs } from '../components/ui'
import { CustomerForm } from '../components/CustomerForm'

type TabId = 'overzicht' | 'facturen' | 'planning' | 'notities'

export function CustomerDetailPage() {
  const { id } = useParams()
  const { data: customer, setData, error, loading } = useApi<Customer>(`/customers/${id}`)
  const { data: invoices } = useApi<Invoice[]>('/invoices')
  const { data: appointments } = useApi<Appointment[]>('/appointments')
  const [tab, setTab] = useState<TabId>('overzicht')
  const [editing, setEditing] = useState(false)
  const [notes, setNotes] = useState<string | null>(null)
  const navigate = useNavigate()
  const { close } = useTabs()
  const toast = useToast()
  useTabTitle(customer?.name)

  if (error) return <ErrorBox message={error} />
  if (loading || !customer) return <Loading />

  const mine = (invoices ?? []).filter(i => i.customerId === customer.id)
  const myAppointments = (appointments ?? []).filter(a => a.customerId === customer.id)
  const revenue = mine.filter(i => i.status === 'betaald').reduce((a, i) => a + invoiceTotals(i.lines).total, 0)
  const open = mine.filter(i => i.status === 'verzonden' || i.status === 'verlopen').reduce((a, i) => a + invoiceTotals(i.lines).total, 0)

  const remove = async () => {
    if (!confirm(`${customer.name} verwijderen?`)) return
    try {
      await api.del(`/customers/${customer.id}`)
      toast('Klant verwijderd')
      close(`/klanten/${customer.id}`)
      navigate('/klanten')
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  const saveNotes = async () => {
    try {
      const saved = await api.put<Customer>(`/customers/${customer.id}`, { ...customer, notes })
      setData(saved)
      setNotes(null)
      toast('Notities opgeslagen')
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  return (
    <>
      <div className="detail-hero card">
        <div className="avatar avatar-lg" style={{ background: colorFor(customer.name) }}>{initials(customer.name)}</div>
        <div className="grow">
          <h1>{customer.name}</h1>
          <div className="row wrap muted" style={{ marginTop: 6, gap: 16 }}>
            {customer.company && <span>{customer.company}</span>}
            {customer.email && <a href={`mailto:${customer.email}`} className="row"><Mail size={14} /> {customer.email}</a>}
            {customer.phone && <span className="row"><Phone size={14} /> {customer.phone}</span>}
            {customer.city && <span className="row"><MapPin size={14} /> {customer.city}</span>}
          </div>
        </div>
        <div className="row">
          <button className="btn" onClick={() => setEditing(true)}><Pencil size={15} /> Bewerken</button>
          <button className="btn btn-danger" onClick={remove}><Trash2 size={15} /></button>
        </div>
      </div>

      <SubTabs<TabId>
        value={tab}
        onChange={setTab}
        tabs={[
          { id: 'overzicht', label: 'Overzicht', icon: <User size={14} /> },
          { id: 'facturen', label: 'Facturen', icon: <Receipt size={14} />, count: mine.length },
          { id: 'planning', label: 'Planning', icon: <CalendarDays size={14} />, count: myAppointments.length },
          { id: 'notities', label: 'Notities', icon: <StickyNote size={14} /> },
        ]}
      />

      {tab === 'overzicht' && (
        <div className="grid grid-1-1">
          <div className="card">
            <div className="card-head"><h3>Gegevens</h3></div>
            <div className="card-body">
              <dl className="dl">
                <dt>Naam</dt><dd>{customer.name}</dd>
                <dt>Bedrijf</dt><dd>{customer.company || '—'}</dd>
                <dt>E-mail</dt><dd>{customer.email || '—'}</dd>
                <dt>Telefoon</dt><dd>{customer.phone || '—'}</dd>
                <dt>Adres</dt><dd>{[customer.address, customer.city].filter(Boolean).join(', ') || '—'}</dd>
                <dt>Klant sinds</dt><dd>{date(customer.createdAt)}</dd>
              </dl>
            </div>
          </div>
          <div className="grid">
            <div className="card stat" style={{ '--accent': 'var(--green)' } as React.CSSProperties}>
              <div><div className="stat-label">Totaal betaald</div><div className="stat-value">{euro(revenue)}</div></div>
            </div>
            <div className="card stat" style={{ '--accent': 'var(--yellow)' } as React.CSSProperties}>
              <div><div className="stat-label">Openstaand</div><div className="stat-value">{euro(open)}</div></div>
            </div>
          </div>
        </div>
      )}

      {tab === 'facturen' && (
        <div className="card">
          <div className="card-head"><h3>Facturen</h3><button className="btn btn-sm btn-primary" onClick={() => navigate(`/facturen/nieuw?klant=${customer.id}`)}><Plus size={14} /> Factuur</button></div>
          {mine.length === 0 ? <Empty icon={<Receipt size={22} />} title="Nog geen facturen" /> : (
            <div className="table-wrap" style={{ marginTop: 10 }}>
              <table>
                <thead><tr><th>Nummer</th><th>Datum</th><th>Status</th><th className="num">Bedrag</th></tr></thead>
                <tbody>
                  {mine.map(i => {
                    const st = invoiceStatuses.find(s => s.id === i.status)!
                    return (
                      <tr key={i.id} className="clickable" onClick={() => navigate(`/facturen/${i.id}`)}>
                        <td>{i.number}</td><td className="muted">{date(i.issueDate)}</td>
                        <td><Badge tone={st.tone}>{st.label}</Badge></td>
                        <td className="num">{euro(invoiceTotals(i.lines).total)}</td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {tab === 'planning' && (
        <div className="card">
          <div className="card-head"><h3>Afspraken en taken</h3></div>
          <div className="card-body">
            {myAppointments.length === 0 ? <Empty icon={<CalendarDays size={22} />} title="Niets gepland voor deze klant" /> : (
              <div className="list">
                {myAppointments.map(a => (
                  <div key={a.id} className="list-item">
                    <span className="kind-bar" style={{ background: kindColor(a.kind) }} />
                    <div className="grow"><div>{a.title}</div><div className="cell-sub">{dateTime(a.start)}</div></div>
                    {a.done && <Badge tone="green">Klaar</Badge>}
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
      )}

      {tab === 'notities' && (
        <div className="card">
          <div className="card-head"><h3>Notities</h3></div>
          <div className="card-body">
            <textarea style={{ minHeight: 200 }} placeholder="Schrijf hier je notities over deze klant…"
              value={notes ?? customer.notes ?? ''} onChange={e => setNotes(e.target.value)} />
            <div className="row mt" style={{ justifyContent: 'flex-end' }}>
              <button className="btn btn-primary" disabled={notes === null} onClick={saveNotes}>Notities opslaan</button>
            </div>
          </div>
        </div>
      )}

      {editing && <CustomerForm customer={customer} onClose={() => setEditing(false)} onSaved={c => { setData(c); setEditing(false) }} />}
    </>
  )
}
