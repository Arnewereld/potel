import { useMemo, useState } from 'react'
import { Crown, Building2, Euro, Hourglass, Search, Users, Trash2, UserPlus } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useAuth } from '../lib/auth'
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
  kvk?: string | null
  termsVersion?: string | null
  termsAcceptedAt?: string | null
  // Online betalen via Mollie.
  paidUntil?: string | null
  subscriptionActive?: boolean
  subscriptionCanceledAt?: string | null
  readOnly?: boolean
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
      const res = await api.put<Pick<Row, 'plan' | 'trialEndsAt' | 'paidUntil' | 'readOnly'>>(`/platform/workspaces/${row.id}/plan`, { plan })
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
                      <td>
                        <strong>{r.name}</strong>
                        <div className="cell-sub">{r.owner} · nr. {r.id}{r.kvk && <> · KvK {r.kvk}</>}</div>
                        <div className="cell-sub">{r.termsVersion ? `Voorwaarden ${r.termsVersion} geaccepteerd op ${date(r.termsAcceptedAt)}` : 'Nog geen voorwaarden geaccepteerd'}</div>
                      </td>
                      <td>
                        <div className="row" style={{ gap: 8 }}>
                          <select value={r.plan} onChange={e => setPlan(r, e.target.value as Row['plan'])} style={{ width: 'auto' }}>
                            {plans.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
                          </select>
                          {left !== null && <Badge tone={left <= 0 ? 'red' : left <= 5 ? 'yellow' : 'gray'}>{left <= 0 ? 'verlopen' : `nog ${left} d`}</Badge>}
                          {r.plan !== 'proef' && r.readOnly && <Badge tone="red">alleen-lezen</Badge>}
                        </div>
                        {r.subscriptionActive && <div className="cell-sub">Betaalt via Mollie, betaald tot {date(r.paidUntil)}</div>}
                        {!r.subscriptionActive && r.subscriptionCanceledAt && r.paidUntil && <div className="cell-sub">Opgezegd, betaald tot {date(r.paidUntil)}</div>}
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

      <PlatformAdmins />
    </>
  )
}

interface PlatformAdmin { id: number; name: string; email: string; workspace?: string | null }

// Wie deze pagina nog meer mag zien. Een platformbeheerder wijst anderen aan; een e-mailadres alleen geeft geen toegang.
function PlatformAdmins() {
  const { data, error, setData } = useApi<PlatformAdmin[]>('/platform/admins')
  const { user } = useAuth()
  const [email, setEmail] = useState('')
  const toast = useToast()

  const add = async (e: React.FormEvent) => {
    e.preventDefault()
    try {
      setData(await api.post<PlatformAdmin[]>('/platform/admins', { email }))
      toast(`${email} is nu platformbeheerder`)
      setEmail('')
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  const remove = async (a: PlatformAdmin) => {
    if (!confirm(`${a.email} geen platformbeheerder meer laten zijn?`)) return
    try {
      await api.del(`/platform/admins/${a.id}`)
      setData(prev => prev?.filter(x => x.id !== a.id) ?? null)
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  return (
    <div className="card" style={{ marginTop: 18 }}>
      <div className="card-head"><h3><Crown size={16} /> Platformbeheerders</h3></div>
      <div className="card-body">
        {error && <ErrorBox message={error} />}
        {data?.map(a => (
          <div key={a.id} className="row between" style={{ padding: '6px 0' }}>
            <div><strong>{a.name}</strong><div className="cell-sub">{a.email}{a.workspace ? ` · ${a.workspace}` : ''}</div></div>
            {a.id !== user.id && <button type="button" className="icon-btn danger" title="Weghalen" onClick={() => remove(a)}><Trash2 size={16} /></button>}
          </div>
        ))}
        <form className="row" style={{ gap: 8, marginTop: 10 }} onSubmit={add}>
          <input type="email" required placeholder="E-mailadres van een bestaand account" value={email} onChange={e => setEmail(e.target.value)} />
          <button className="btn"><UserPlus size={15} /> Toevoegen</button>
        </form>
      </div>
    </div>
  )
}
