import { useMemo, useState } from 'react'
import { Crown, Building2, Euro, Hourglass, Search, Users } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import { plans } from '../lib/plans'
import { date, euro, relative } from '../lib/format'
import { daysUntil } from '../lib/invoice'
import { Badge, Empty, ErrorBox, Loading, PageHeader } from '../components/ui'
import { MiniStat } from './Time'

interface Row {
  id: number
  name: string
  plan: 'proef' | 'zzp' | 'team'
  trialEndsAt?: string | null
  createdAt: string
  onboarded: boolean
  owner?: string | null
  users: number
  lastActive?: string | null
  invoices: number
  timeEntries: number
}

// Alleen voor de eigenaar van het platform: wie gebruikt Potel, op welk abonnement.
export function PlatformPage() {
  const { data, error, loading, setData } = useApi<Row[]>('/platform/workspaces')
  const [q, setQ] = useState('')
  const toast = useToast()

  const rows = useMemo(() => (data ?? []).filter(r => [r.name, r.owner].some(v => v?.toLowerCase().includes(q.toLowerCase()))), [data, q])
  if (error) return <ErrorBox message={error} />
  if (loading || !data) return <Loading />

  const paying = data.filter(r => r.plan !== 'proef')
  const mrr = paying.reduce((a, r) => a + (plans.find(p => p.id === r.plan)?.monthly ?? 0), 0)
  const trials = data.filter(r => r.plan === 'proef')

  const setPlan = async (row: Row, plan: Row['plan']) => {
    try {
      const res = await api.put<{ plan: Row['plan']; trialEndsAt?: string | null }>(`/platform/workspaces/${row.id}/plan`, { plan })
      setData(data.map(r => (r.id === row.id ? { ...r, ...res } : r)))
      toast(`${row.name} staat nu op ${plans.find(p => p.id === plan)?.name}`)
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  return (
    <>
      <PageHeader icon={<Crown size={20} />} title="Platform" subtitle="Alle werkruimtes en abonnementen. Alleen zichtbaar voor jou als eigenaar."
        actions={<div className="search-input"><Search size={16} /><input placeholder="Zoek bedrijf of e-mail…" value={q} onChange={e => setQ(e.target.value)} /></div>} />

      <div className="grid grid-stats" style={{ marginBottom: 18 }}>
        <MiniStat icon={<Building2 size={18} />} accent="var(--blue)" label="Werkruimtes" value={String(data.length)} sub={`${data.filter(r => r.onboarded).length} hebben de wizard afgerond`} />
        <MiniStat icon={<Euro size={18} />} accent="var(--green)" label="Omzet per maand" value={euro(mrr)} sub={`${paying.length} betalende klanten, excl. btw`} />
        <MiniStat icon={<Hourglass size={18} />} accent="var(--yellow)" label="In proefperiode" value={String(trials.length)}
          sub={`${trials.filter(r => r.trialEndsAt && daysUntil(r.trialEndsAt) <= 5).length} lopen binnen 5 dagen af`} />
        <MiniStat icon={<Users size={18} />} accent="var(--purple)" label="Gebruikers" value={String(data.reduce((a, r) => a + r.users, 0))} sub="Over alle werkruimtes" />
      </div>

      <div className="card">
        {rows.length === 0 ? <Empty icon={<Building2 size={22} />} title="Geen werkruimtes gevonden" /> : (
          <div className="table-wrap">
            <table>
              <thead><tr><th>Werkruimte</th><th>Abonnement</th><th>Gebruik</th><th>Laatst actief</th><th>Sinds</th></tr></thead>
              <tbody>
                {rows.map(r => {
                  const left = r.plan === 'proef' && r.trialEndsAt ? daysUntil(r.trialEndsAt) : null
                  return (
                    <tr key={r.id}>
                      <td><strong>{r.name}</strong><div className="cell-sub">{r.owner} · nr. {r.id}</div></td>
                      <td>
                        <div className="row" style={{ gap: 8 }}>
                          <select value={r.plan} onChange={e => setPlan(r, e.target.value as Row['plan'])} style={{ width: 'auto' }}>
                            {plans.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
                          </select>
                          {left !== null && <Badge tone={left <= 0 ? 'red' : left <= 5 ? 'yellow' : 'gray'}>{left <= 0 ? 'verlopen' : `nog ${left} d`}</Badge>}
                        </div>
                      </td>
                      <td className="muted">{r.users} gebruikers · {r.timeEntries} boekingen · {r.invoices} facturen</td>
                      <td className="muted">{r.lastActive ? relative(r.lastActive) : 'nooit'}</td>
                      <td className="muted">{date(r.createdAt)}</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </>
  )
}
