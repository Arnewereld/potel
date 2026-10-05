import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Users, Plus, Search, Mail, Phone } from 'lucide-react'
import { useApi } from '../lib/useApi'
import type { Customer } from '../lib/types'
import { initials } from '../lib/format'
import { colorFor } from '../lib/status'
import { Empty, ErrorBox, Loading, PageHeader } from '../components/ui'
import { CustomerForm } from '../components/CustomerForm'

export function CustomersPage() {
  const { data, error, loading, reload } = useApi<Customer[]>('/customers')
  const [q, setQ] = useState('')
  const [creating, setCreating] = useState(false)
  const navigate = useNavigate()

  const filtered = useMemo(() => {
    const s = q.toLowerCase()
    return (data ?? []).filter(c => [c.name, c.company, c.email, c.city].some(v => v?.toLowerCase().includes(s)))
  }, [data, q])

  return (
    <>
      <PageHeader
        icon={<Users size={20} />}
        title="Klanten"
        subtitle={data ? `${data.length} klanten in je portaal` : undefined}
        actions={<>
          <div className="search-input"><Search size={16} /><input placeholder="Zoek klant…" value={q} onChange={e => setQ(e.target.value)} /></div>
          <button className="btn btn-primary" onClick={() => setCreating(true)}><Plus size={16} /> Nieuwe klant</button>
        </>}
      />

      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && <Loading />}

      {data && (
        <div className="card">
          {filtered.length === 0 ? (
            <Empty icon={<Users size={24} />} title={q ? 'Geen klanten gevonden' : 'Nog geen klanten'}
              text={q ? 'Probeer een andere zoekterm.' : 'Voeg je eerste klant toe om te beginnen.'}
              action={!q && <button className="btn btn-primary" onClick={() => setCreating(true)}><Plus size={16} /> Klant toevoegen</button>} />
          ) : (
            <div className="table-wrap">
              <table>
                <thead><tr><th>Naam</th><th>Contact</th><th>Plaats</th></tr></thead>
                <tbody>
                  {filtered.map(c => (
                    <tr key={c.id} className="clickable" onClick={() => navigate(`/klanten/${c.id}`)}>
                      <td>
                        <div className="cell-main">
                          <div className="avatar" style={{ background: colorFor(c.name) }}>{initials(c.name)}</div>
                          <div><div>{c.name}</div><div className="cell-sub">{c.company}</div></div>
                        </div>
                      </td>
                      <td>
                        {c.email && <div className="row muted"><Mail size={13} /> {c.email}</div>}
                        {c.phone && <div className="row muted"><Phone size={13} /> {c.phone}</div>}
                      </td>
                      <td className="muted">{c.city}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {creating && <CustomerForm onClose={() => setCreating(false)} onSaved={c => { setCreating(false); reload(); navigate(`/klanten/${c.id}`) }} />}
    </>
  )
}
