import { useNavigate } from 'react-router-dom'
import { Euro, Clock, Target, Users, Activity, CalendarDays, TrendingUp, Plus, LayoutDashboard, AlertTriangle } from 'lucide-react'
import { useApi } from '../lib/useApi'
import type { Dashboard } from '../lib/types'
import { euro, dateTime, relative } from '../lib/format'
import { kindColor, leadStatuses } from '../lib/status'
import { ErrorBox, Loading, PageHeader } from '../components/ui'

const monthName = (m: string) => new Date(`${m}-01T00:00:00`).toLocaleDateString('nl-NL', { month: 'short' })

export function DashboardPage() {
  const { data, error, loading, reload } = useApi<Dashboard>('/dashboard')
  const navigate = useNavigate()

  if (error) return <ErrorBox message={error} onRetry={() => reload()} />
  if (loading || !data) return <Loading />

  const max = Math.max(1, ...data.revenueByMonth.map(m => m.total))
  const leadTotal = Math.max(1, data.leadsByStatus.reduce((a, s) => a + s.count, 0))
  const hour = new Date().getHours()
  const greeting = hour < 12 ? 'Goedemorgen' : hour < 18 ? 'Goedemiddag' : 'Goedenavond'

  return (
    <>
      <PageHeader
        icon={<LayoutDashboard size={20} />}
        title={greeting}
        subtitle="Zo staat je bedrijf er vandaag voor."
        actions={<>
          <button className="btn" onClick={() => navigate('/planning')}><CalendarDays size={16} /> Planning</button>
          <button className="btn btn-primary" onClick={() => navigate('/facturen/nieuw')}><Plus size={16} /> Nieuwe factuur</button>
        </>}
      />

      <div className="grid grid-4">
        <Stat icon={<Euro size={20} />} accent="var(--green)" label="Omzet dit jaar" value={euro(data.revenueYear)} sub="Betaalde facturen, incl. btw" />
        <Stat icon={<Clock size={20} />} accent="var(--yellow)" label="Openstaand" value={euro(data.outstanding)}
          sub={data.overdue > 0 ? <span style={{ color: 'var(--red)' }}><AlertTriangle size={11} /> {data.overdue} verlopen</span> : 'Niets verlopen'} />
        <Stat icon={<Target size={20} />} accent="var(--primary)" label="Pipeline" value={euro(data.pipelineValue)} sub={`${data.openLeads} open leads`} />
        <Stat icon={<Users size={20} />} accent="var(--blue)" label="Klanten" value={String(data.customers)} sub="Totaal in je portaal" />
      </div>

      <div className="grid grid-2-1 mt">
        <div className="card">
          <div className="card-head"><h3><TrendingUp size={16} /> Omzet per maand</h3><span className="muted">Laatste 6 maanden</span></div>
          <div className="card-body">
            <div className="bars">
              {data.revenueByMonth.map(m => (
                <div key={m.month} className="bar-col" title={euro(m.total)}>
                  <div className="bar-value">{m.total > 0 ? euro(m.total).replace(/,\d+$/, '') : ''}</div>
                  <div className="bar-track"><div className="bar" style={{ height: `${(m.total / max) * 100}%` }} /></div>
                  <div className="bar-label">{monthName(m.month)}</div>
                </div>
              ))}
            </div>
          </div>
        </div>

        <div className="card">
          <div className="card-head"><h3><Target size={16} /> Leads per fase</h3><button className="btn btn-sm btn-ghost" onClick={() => navigate('/leads')}>Bekijk</button></div>
          <div className="card-body">
            <div className="funnel-bar">
              {data.leadsByStatus.map(s => {
                const info = leadStatuses.find(x => x.id === s.status)!
                return s.count > 0 && <div key={s.status} style={{ flex: s.count / leadTotal, background: info.color }} title={`${info.label}: ${s.count}`} />
              })}
            </div>
            <div className="list">
              {data.leadsByStatus.map(s => {
                const info = leadStatuses.find(x => x.id === s.status)!
                return (
                  <div key={s.status} className="list-item">
                    <span className="legend-dot" style={{ background: info.color }} />
                    <span className="grow">{info.label}</span>
                    <span className="muted">{s.count}</span>
                    <strong style={{ width: 100, textAlign: 'right' }}>{euro(s.value)}</strong>
                  </div>
                )
              })}
            </div>
          </div>
        </div>
      </div>

      <div className="grid grid-1-1 mt">
        <div className="card">
          <div className="card-head"><h3><CalendarDays size={16} /> Binnenkort</h3><button className="btn btn-sm btn-ghost" onClick={() => navigate('/planning')}>Planning</button></div>
          <div className="card-body">
            {data.upcoming.length === 0 && <p className="muted">Niets gepland. Tijd voor koffie.</p>}
            <div className="list">
              {data.upcoming.map(a => (
                <div key={a.id} className="list-item">
                  <span className="kind-bar" style={{ background: kindColor(a.kind) }} />
                  <div className="grow">
                    <div className="truncate">{a.title}</div>
                    <div className="cell-sub">{dateTime(a.start)}{a.location ? ` · ${a.location}` : ''}</div>
                  </div>
                  <span className="badge badge-gray">{a.kind}</span>
                </div>
              ))}
            </div>
          </div>
        </div>

        <div className="card">
          <div className="card-head"><h3><Activity size={16} /> Activiteit</h3></div>
          <div className="card-body">
            <div className="timeline">
              {data.activity.map(a => (
                <div key={a.id} className="timeline-item">
                  <span className="timeline-dot" />
                  <div className="grow">
                    <div>{a.text}</div>
                    <div className="cell-sub">{a.kind} · {relative(a.at)}</div>
                  </div>
                </div>
              ))}
            </div>
          </div>
        </div>
      </div>
    </>
  )
}

function Stat({ icon, label, value, sub, accent }: { icon: React.ReactNode; label: string; value: string; sub: React.ReactNode; accent: string }) {
  return (
    <div className="card stat" style={{ '--accent': accent } as React.CSSProperties}>
      <div className="stat-icon">{icon}</div>
      <div>
        <div className="stat-label">{label}</div>
        <div className="stat-value">{value}</div>
        <div className="stat-sub">{sub}</div>
      </div>
    </div>
  )
}
