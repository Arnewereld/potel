import { useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  Euro, Clock, Target, Activity, CalendarDays, TrendingUp, Plus, LayoutDashboard, AlertTriangle, Timer, Hourglass, Landmark,
  Award, FolderKanban,
} from 'lucide-react'
import { useApi } from '../lib/useApi'
import { useOnTimeChanged } from '../lib/timer'
import type { Dashboard } from '../lib/types'
import { euro, date, dateTime, hm, hours, relative, toDateInput } from '../lib/format'
import { kindColor, leadStatuses } from '../lib/status'
import { ErrorBox, Loading, PageHeader } from '../components/ui'

const monthName = (m: string) => new Date(`${m}-01T00:00:00`).toLocaleDateString('nl-NL', { month: 'short' })

export function DashboardPage() {
  const { data, error, loading, reload } = useApi<Dashboard>('/dashboard')
  const navigate = useNavigate()
  useOnTimeChanged(useCallback(() => reload(true), [reload]))

  if (error) return <ErrorBox message={error} onRetry={() => reload()} />
  if (loading || !data) return <Loading />

  const max = Math.max(1, ...data.revenueByMonth.map(m => m.total))
  const leadTotal = Math.max(1, data.leadsByStatus.reduce((a, s) => a + s.count, 0))
  const hour = new Date().getHours()
  const greeting = hour < 12 ? 'Goedemorgen' : hour < 18 ? 'Goedemiddag' : 'Goedenavond'

  const h = data.hours
  const dayMax = Math.max(h.weekTarget * 60 / 5, ...h.byDay.map(d => d.minutes), 1)
  const today = toDateInput(new Date())
  const yearPct = h.yearTarget ? Math.min(1, h.year / 60 / h.yearTarget) : 0
  const yearLeft = Math.max(0, h.yearTarget - h.year / 60)

  return (
    <>
      <PageHeader
        icon={<LayoutDashboard size={20} />}
        title={greeting}
        subtitle="Zo staat je freelancepraktijk er vandaag voor."
        actions={<>
          <button className="btn" onClick={() => navigate('/uren')}><Timer size={16} /> Uren boeken</button>
          <button className="btn btn-primary" onClick={() => navigate('/facturen/nieuw')}><Plus size={16} /> Nieuwe factuur</button>
        </>}
      />

      <div className="grid grid-4">
        <Stat icon={<Euro size={20} />} accent="var(--green)" label="Omzet dit jaar" value={euro(data.revenueYear)} sub="Betaalde facturen, incl. btw" />
        <Stat icon={<Hourglass size={20} />} accent="var(--primary)" label="Klaar om te factureren" value={euro(data.unbilled.value)}
          sub={`${hours(data.unbilled.minutes)} open uren`} onClick={() => navigate('/projecten')} />
        <Stat icon={<Clock size={20} />} accent="var(--yellow)" label="Openstaand" value={euro(data.outstanding)}
          sub={data.overdue > 0 ? <span style={{ color: 'var(--red)' }}><AlertTriangle size={11} /> {data.overdue} verlopen</span> : 'Niets verlopen'}
          onClick={() => navigate('/facturen')} />
        <Stat icon={<Landmark size={20} />} accent="var(--purple)" label={`Btw ${data.vat.label}`} value={euro(data.vat.amount)}
          sub={data.vat.kor || !data.vat.dueDate ? 'KOR: je doet geen btw-aangifte' : `Aangifte vóór ${date(data.vat.dueDate)}`} />
      </div>

      <div className="grid grid-2-1 mt">
        <div className="card">
          <div className="card-head">
            <h3><Timer size={16} /> Uren deze week</h3>
            <span className="muted">{hm(h.week)} van {h.weekTarget} uur · {hm(h.weekBillable)} factureerbaar</span>
          </div>
          <div className="card-body">
            <div className="bars bars-sm">
              {h.byDay.map((d, i) => (
                <div key={d.date} className="bar-col" title={hm(d.minutes)}>
                  <div className="bar-value">{d.minutes > 0 ? hm(d.minutes) : ''}</div>
                  <div className="bar-track"><div className={`bar ${d.date === today ? 'bar-today' : ''}`} style={{ height: `${(d.minutes / dayMax) * 100}%` }} /></div>
                  <div className={`bar-label ${d.date === today ? 'text-primary' : ''}`}>{['ma', 'di', 'wo', 'do', 'vr', 'za', 'zo'][i]}</div>
                </div>
              ))}
            </div>
            <div className="progress mt"><div style={{ width: `${Math.min(100, (h.week / 60 / Math.max(1, h.weekTarget)) * 100)}%` }} /></div>
          </div>
        </div>

        <div className="card">
          <div className="card-head"><h3><Award size={16} /> Urencriterium {new Date().getFullYear()}</h3></div>
          <div className="card-body ring-wrap">
            <div className="ring" style={{ '--pct': yearPct } as React.CSSProperties}>
              <div><strong>{Math.round(h.year / 60).toLocaleString('nl-NL')}</strong><span>van {h.yearTarget.toLocaleString('nl-NL')} uur</span></div>
            </div>
            <p className="muted" style={{ margin: 0, textAlign: 'center' }}>
              {yearLeft === 0
                ? 'Gehaald! Je hebt recht op de zelfstandigenaftrek.'
                : <>Nog <strong className="text-strong">{Math.ceil(yearLeft)} uur</strong>, ongeveer {Math.ceil(yearLeft / h.weeksLeft)} uur per week.</>}
            </p>
          </div>
        </div>
      </div>

      <div className="grid grid-2-1 mt">
        <div className="card">
          <div className="card-head"><h3><FolderKanban size={16} /> Lopende projecten</h3><button className="btn btn-sm btn-ghost" onClick={() => navigate('/projecten')}>Alle projecten</button></div>
          <div className="card-body">
            {data.projects.length === 0 && <p className="muted">Geen actieve projecten.</p>}
            <div className="list">
              {data.projects.map(p => {
                const pct = p.budgetHours ? p.minutesTotal / 60 / p.budgetHours : null
                return (
                  <button key={p.id} className="list-item list-button" onClick={() => navigate(`/projecten/${p.id}`)}>
                    <span className="kind-bar" style={{ background: p.color }} />
                    <div className="grow">
                      <div className="truncate">{p.name}</div>
                      <div className="cell-sub">{p.customerName} · {hours(p.minutesTotal, 0)}{p.budgetHours ? ` van ${p.budgetHours} u` : ''}</div>
                      {pct !== null && <div className={`progress progress-thin ${pct > 1 ? 'over' : ''}`}><div style={{ width: `${Math.min(100, pct * 100)}%`, background: p.color }} /></div>}
                    </div>
                    <div style={{ textAlign: 'right' }}>
                      {p.billing === 'uur'
                        ? <><strong className={p.unbilledValue > 0 ? 'text-green' : 'muted'}>{euro(p.unbilledValue)}</strong><div className="cell-sub">open</div></>
                        : <><strong>{euro(p.fixedPrice)}</strong><div className="cell-sub">vaste prijs</div></>}
                    </div>
                  </button>
                )
              })}
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

      <div className="grid grid-3 mt">
        <div className="card">
          <div className="card-head"><h3><TrendingUp size={16} /> Omzet per maand</h3><span className="muted">Betaald</span></div>
          <div className="card-body">
            <div className="bars">
              {data.revenueByMonth.map(m => (
                <div key={m.month} className="bar-col" title={euro(m.total)}>
                  <div className="bar-value">{m.total > 0 ? `${Math.round(m.total / 1000)}k` : ''}</div>
                  <div className="bar-track"><div className="bar" style={{ height: `${(m.total / max) * 100}%` }} /></div>
                  <div className="bar-label">{monthName(m.month)}</div>
                </div>
              ))}
            </div>
          </div>
        </div>

        <div className="card">
          <div className="card-head"><h3><CalendarDays size={16} /> Binnenkort</h3><button className="btn btn-sm btn-ghost" onClick={() => navigate('/planning')}>Planning</button></div>
          <div className="card-body">
            {data.upcoming.length === 0 && <p className="muted">Niets gepland. Tijd om te bouwen.</p>}
            <div className="list">
              {data.upcoming.map(a => (
                <div key={a.id} className="list-item">
                  <span className="kind-bar" style={{ background: kindColor(a.kind) }} />
                  <div className="grow">
                    <div className="truncate">{a.title}</div>
                    <div className="cell-sub">{dateTime(a.start)}{a.location ? ` · ${a.location}` : ''}</div>
                  </div>
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

function Stat({ icon, label, value, sub, accent, onClick }: { icon: React.ReactNode; label: string; value: string; sub: React.ReactNode; accent: string; onClick?: () => void }) {
  return (
    <div className={`card stat ${onClick ? 'clickable' : ''}`} style={{ '--accent': accent } as React.CSSProperties} onClick={onClick}>
      <div className="stat-icon">{icon}</div>
      <div>
        <div className="stat-label">{label}</div>
        <div className="stat-value">{value}</div>
        <div className="stat-sub">{sub}</div>
      </div>
    </div>
  )
}
