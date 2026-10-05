import { useCallback, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Timer, Plus, ChevronLeft, ChevronRight, Lock, Euro, CalendarCheck, Target } from 'lucide-react'
import { useApi } from '../lib/useApi'
import type { Project, Settings, TimeEntry } from '../lib/types'
import { euro, hm, hours, parseDate, toDateInput, weekNumber, weekStart } from '../lib/format'
import { useOnTimeChanged } from '../lib/timer'
import { Empty, ErrorBox, Loading, PageHeader } from '../components/ui'
import { TimerCard } from '../components/TimerCard'
import { TimeEntryForm } from '../components/TimeEntryForm'

const dayNames = ['ma', 'di', 'wo', 'do', 'vr', 'za', 'zo']

export function TimePage() {
  const [monday, setMonday] = useState(() => weekStart(new Date()))
  const days = useMemo(() => Array.from({ length: 7 }, (_, i) => { const d = new Date(monday); d.setDate(d.getDate() + i); return toDateInput(d) }), [monday])
  const { data: entries, error, loading, reload } = useApi<TimeEntry[]>(`/time?from=${days[0]}&to=${days[6]}`)
  const { data: projects, reload: reloadProjects } = useApi<Project[]>('/projects')
  const { data: settings } = useApi<Settings>('/settings')
  const [editing, setEditing] = useState<{ entry?: TimeEntry; date?: string; projectId?: number } | null>(null)
  const navigate = useNavigate()

  useOnTimeChanged(useCallback(() => { reload(true); reloadProjects(true) }, [reload, reloadProjects]))

  const shift = (weeks: number) => setMonday(m => { const d = new Date(m); d.setDate(d.getDate() + weeks * 7); return d })
  const today = toDateInput(new Date())
  const isThisWeek = days.includes(today)

  const list = entries ?? []
  const total = list.reduce((a, e) => a + e.minutes, 0)
  const billable = list.filter(e => e.billable).reduce((a, e) => a + e.minutes, 0)
  const target = (settings?.weeklyHoursTarget ?? 0) * 60
  const open = (projects ?? []).reduce((a, p) => a + p.unbilledValue, 0)
  const openMinutes = (projects ?? []).reduce((a, p) => a + p.minutesUnbilled, 0)

  // Rijen per project, kolommen per dag.
  const rows = useMemo(() => {
    const map = new Map<number, { id: number; name: string; color: string; customer?: string | null; perDay: number[] }>()
    for (const e of entries ?? []) {
      const row = map.get(e.projectId) ?? { id: e.projectId, name: e.projectName, color: e.projectColor, customer: e.customerName, perDay: Array(7).fill(0) }
      row.perDay[days.indexOf(e.date.slice(0, 10))] += e.minutes
      map.set(e.projectId, row)
    }
    return [...map.values()].sort((a, b) => a.name.localeCompare(b.name))
  }, [entries, days])
  const dayTotals = days.map(d => list.filter(e => e.date.startsWith(d)).reduce((a, e) => a + e.minutes, 0))

  const end = parseDate(days[6]!)
  const month = (d: Date) => d.toLocaleDateString('nl-NL', { month: 'short' })
  const range = monday.getMonth() === end.getMonth()
    ? `${monday.getDate()} t/m ${end.getDate()} ${month(end)}`
    : `${monday.getDate()} ${month(monday)} t/m ${end.getDate()} ${month(end)}`

  return (
    <>
      <PageHeader
        icon={<Timer size={20} />}
        title="Uren"
        subtitle={`Week ${weekNumber(monday)} · ${range}`}
        actions={<>
          <div className="btn-group">
            <button className="btn" onClick={() => shift(-1)} aria-label="Vorige week"><ChevronLeft size={16} /></button>
            <button className="btn" disabled={isThisWeek} onClick={() => setMonday(weekStart(new Date()))}>Deze week</button>
            <button className="btn" onClick={() => shift(1)} aria-label="Volgende week"><ChevronRight size={16} /></button>
          </div>
          <button className="btn btn-primary" onClick={() => setEditing({ date: isThisWeek ? today : days[0] })}><Plus size={16} /> Uren boeken</button>
        </>}
      />

      <TimerCard />

      <div className="grid grid-stats mt">
        <MiniStat icon={<Target size={18} />} accent="var(--primary)" label="Deze week" value={hm(total)}
          sub={target ? `${Math.round((total / target) * 100)}% van ${settings?.weeklyHoursTarget} uur` : undefined} progress={target ? total / target : undefined} />
        <MiniStat icon={<CalendarCheck size={18} />} accent="var(--blue)" label="Factureerbaar" value={hm(billable)}
          sub={total ? `${Math.round((billable / total) * 100)}% van je tijd` : 'Nog niets geboekt'} />
        <MiniStat icon={<Euro size={18} />} accent="var(--green)" label="Klaar om te factureren" value={euro(open)}
          sub={`${hours(openMinutes)} over alle projecten`} onClick={() => navigate('/projecten')} />
      </div>

      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !entries && <Loading />}

      {entries && (<>
        <div className="card mt">
          <div className="table-wrap">
            <table className="week-table">
              <thead>
                <tr>
                  <th>Project</th>
                  {days.map((d, i) => <th key={d} className={`num ${d === today ? 'today' : ''}`}>{dayNames[i]} {parseDate(d).getDate()}</th>)}
                  <th className="num">Totaal</th>
                </tr>
              </thead>
              <tbody>
                {rows.length === 0 && (
                  <tr><td colSpan={9}><Empty icon={<Timer size={22} />} title="Nog geen uren deze week" text="Start de timer of boek je uren met de knop rechtsboven." /></td></tr>
                )}
                {rows.map(r => (
                  <tr key={r.id}>
                    <td>
                      <div className="cell-main">
                        <span className="legend-dot" style={{ background: r.color }} />
                        <div><div className="truncate">{r.name}</div><div className="cell-sub">{r.customer}</div></div>
                      </div>
                    </td>
                    {r.perDay.map((m, i) => (
                      <td key={i} className={`num week-cell ${days[i] === today ? 'today' : ''}`} onClick={() => setEditing({ date: days[i], projectId: r.id })} title="Uren toevoegen">
                        {m ? hm(m) : <span className="muted">·</span>}
                      </td>
                    ))}
                    <td className="num"><strong>{hm(r.perDay.reduce((a, b) => a + b, 0))}</strong></td>
                  </tr>
                ))}
              </tbody>
              {rows.length > 0 && (
                <tfoot>
                  <tr>
                    <td>Totaal</td>
                    {dayTotals.map((m, i) => <td key={i} className={`num ${days[i] === today ? 'today' : ''}`}>{m ? hm(m) : ''}</td>)}
                    <td className="num"><strong>{hm(total)}</strong></td>
                  </tr>
                </tfoot>
              )}
            </table>
          </div>
        </div>

        {list.length > 0 && (
          <div className="card mt">
            <div className="card-head"><h3>Alle boekingen</h3><span className="muted">Klik om te bewerken</span></div>
            <div className="card-body">
              {days.slice().reverse().filter(d => list.some(e => e.date.startsWith(d))).map(d => (
                <div key={d} className="day-group">
                  <div className="day-head">
                    <span>{parseDate(d).toLocaleDateString('nl-NL', { weekday: 'long', day: 'numeric', month: 'long' })}</span>
                    <span>{hm(list.filter(e => e.date.startsWith(d)).reduce((a, e) => a + e.minutes, 0))}</span>
                  </div>
                  {list.filter(e => e.date.startsWith(d)).map(e => (
                    <button key={e.id} className="entry-row" onClick={() => e.invoiceId ? navigate(`/facturen/${e.invoiceId}`) : setEditing({ entry: e })}>
                      <span className="kind-bar" style={{ background: e.projectColor }} />
                      <span className="grow">
                        <span className="truncate">{e.description || <span className="muted">Geen omschrijving</span>}</span>
                        <span className="cell-sub">{e.projectName} · {e.customerName}</span>
                      </span>
                      {!e.billable && <span className="badge badge-gray">niet factureerbaar</span>}
                      {e.invoiceNumber && <span className="badge badge-green"><Lock size={10} /> {e.invoiceNumber}</span>}
                      <strong className="entry-time">{hm(e.minutes)}</strong>
                    </button>
                  ))}
                </div>
              ))}
            </div>
          </div>
        )}
      </>)}

      {editing && (
        <TimeEntryForm entry={editing.entry} date={editing.date} projectId={editing.projectId}
          onClose={() => setEditing(null)} onSaved={() => setEditing(null)} />
      )}
    </>
  )
}

export function MiniStat({ icon, label, value, sub, accent, progress, onClick }: {
  icon: React.ReactNode; label: string; value: string; sub?: React.ReactNode; accent: string; progress?: number; onClick?: () => void
}) {
  return (
    <div className={`card stat ${onClick ? 'clickable' : ''}`} style={{ '--accent': accent } as React.CSSProperties} onClick={onClick}>
      <div className="stat-icon">{icon}</div>
      <div className="grow">
        <div className="stat-label">{label}</div>
        <div className="stat-value">{value}</div>
        {progress !== undefined && <div className="progress"><div style={{ width: `${Math.min(100, progress * 100)}%` }} /></div>}
        {sub && <div className="stat-sub">{sub}</div>}
      </div>
    </div>
  )
}
