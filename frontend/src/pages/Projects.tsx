import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { FolderKanban, Plus, Clock, CalendarClock } from 'lucide-react'
import { useApi } from '../lib/useApi'
import type { Project, ProjectStatus } from '../lib/types'
import { date, euro, hours } from '../lib/format'
import { projectStatuses } from '../lib/status'
import { Badge, Empty, ErrorBox, Loading, PageHeader, SubTabs } from '../components/ui'
import { ProjectForm } from '../components/ProjectForm'

export function ProjectsPage() {
  const { data, error, loading, reload } = useApi<Project[]>('/projects')
  const [filter, setFilter] = useState<ProjectStatus>('actief')
  const [creating, setCreating] = useState(false)
  const navigate = useNavigate()

  const shown = useMemo(() => (data ?? []).filter(p => p.status === filter), [data, filter])
  const count = (s: ProjectStatus) => (data ?? []).filter(p => p.status === s).length

  return (
    <>
      <PageHeader
        icon={<FolderKanban size={20} />}
        title="Projecten"
        subtitle={data ? `${count('actief')} actief · ${euro(data.reduce((a, p) => a + p.unbilledValue, 0))} klaar om te factureren` : undefined}
        actions={<button className="btn btn-primary" onClick={() => setCreating(true)}><Plus size={16} /> Nieuw project</button>}
      />

      <SubTabs<ProjectStatus> value={filter} onChange={setFilter}
        tabs={projectStatuses.map(s => ({ id: s.id, label: s.label, count: count(s.id) }))} />

      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && <Loading />}

      {data && shown.length === 0 && (
        <div className="card">
          <Empty icon={<FolderKanban size={24} />} title="Geen projecten hier"
            text={filter === 'actief' ? 'Maak een project aan om uren op te boeken.' : undefined}
            action={filter === 'actief' && <button className="btn btn-primary" onClick={() => setCreating(true)}><Plus size={16} /> Project aanmaken</button>} />
        </div>
      )}

      <div className="project-grid">
        {shown.map(p => <ProjectCard key={p.id} project={p} onClick={() => navigate(`/projecten/${p.id}`)} />)}
      </div>

      {creating && <ProjectForm onClose={() => setCreating(false)} onSaved={p => { setCreating(false); reload(); navigate(`/projecten/${p.id}`) }} />}
    </>
  )
}

export function ProjectCard({ project: p, onClick }: { project: Project; onClick: () => void }) {
  const used = p.minutesTotal / 60
  const pct = p.budgetHours ? used / p.budgetHours : null
  const st = projectStatuses.find(s => s.id === p.status)!
  return (
    <button className="card project-card" style={{ '--accent': p.color } as React.CSSProperties} onClick={onClick}>
      <div className="row between">
        <div className="project-name truncate">{p.name}</div>
        {p.status !== 'actief' && <Badge tone={st.tone}>{st.label}</Badge>}
      </div>
      <div className="cell-sub">{p.customerName}</div>
      <div className="project-meta">
        <span>{p.billing === 'uur' ? `${euro(p.hourlyRate)} / uur` : `Vaste prijs ${euro(p.fixedPrice)}`}</span>
        {p.deadline && <span className="row" style={{ gap: 4 }}><CalendarClock size={12} /> {date(p.deadline)}</span>}
      </div>
      <div className="row between project-hours">
        <span className="row" style={{ gap: 6 }}><Clock size={13} /> {hours(p.minutesTotal, 0)}{p.budgetHours ? ` van ${p.budgetHours} u` : ''}</span>
        {pct !== null && <span className={pct > 1 ? 'text-red' : pct > 0.85 ? 'text-yellow' : 'muted'}>{Math.round(pct * 100)}%</span>}
      </div>
      {pct !== null && <div className={`progress ${pct > 1 ? 'over' : ''}`}><div style={{ width: `${Math.min(100, pct * 100)}%` }} /></div>}
      <div className="project-foot">
        {p.billing === 'uur'
          ? p.unbilledValue > 0
            ? <><span className="muted">Open om te factureren</span><strong className="text-green">{euro(p.unbilledValue)}</strong></>
            : <span className="muted">Alles gefactureerd</span>
          : <span className="muted">Effectief {euro(used ? p.fixedPrice / used : 0)} / uur</span>}
      </div>
    </button>
  )
}
