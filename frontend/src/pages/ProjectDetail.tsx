import { useCallback, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { Pencil, Trash2, Clock, Receipt, Info, Plus, GitBranch, Lock, FileText, Euro, Gauge, CalendarClock } from 'lucide-react'
import { useApi } from '../lib/useApi'
import { api } from '../lib/api'
import { useTabs, useTabTitle } from '../lib/tabs'
import { useToast } from '../lib/toast'
import { useOnTimeChanged } from '../lib/timer'
import type { Invoice, Project, TimeEntry } from '../lib/types'
import { date, euro, hm, hours, invoiceTotals } from '../lib/format'
import { invoiceStatuses, projectStatuses } from '../lib/status'
import { Badge, Empty, ErrorBox, Loading, Modal, SubTabs } from '../components/ui'
import { ProjectForm } from '../components/ProjectForm'
import { TimeEntryForm } from '../components/TimeEntryForm'
import { MiniStat } from './Time'

type TabId = 'uren' | 'facturen' | 'info'

export function ProjectDetailPage() {
  const { id } = useParams()
  const { data: project, error, loading, reload } = useApi<Project>(`/projects/${id}`)
  const { data: entries, reload: reloadEntries } = useApi<TimeEntry[]>(`/time?projectId=${id}`)
  const { data: invoices } = useApi<Invoice[]>('/invoices')
  const [tab, setTab] = useState<TabId>('uren')
  const [editing, setEditing] = useState(false)
  const [entry, setEntry] = useState<{ entry?: TimeEntry } | null>(null)
  const [invoicing, setInvoicing] = useState(false)
  const navigate = useNavigate()
  const { close } = useTabs()
  const toast = useToast()
  useTabTitle(project?.name)

  useOnTimeChanged(useCallback(() => { reload(true); reloadEntries(true) }, [reload, reloadEntries]))

  if (error) return <ErrorBox message={error} />
  if (loading || !project) return <Loading />

  const invoiceIds = new Set((entries ?? []).map(e => e.invoiceId).filter(Boolean))
  const mine = (invoices ?? []).filter(i => invoiceIds.has(i.id))
  const st = projectStatuses.find(s => s.id === project.status)!
  const pct = project.budgetHours ? project.minutesTotal / 60 / project.budgetHours : null

  const remove = async () => {
    if (!confirm(`Project ${project.name} en alle uren erop verwijderen?`)) return
    try {
      await api.del(`/projects/${project.id}`)
      toast('Project verwijderd')
      close(`/projecten/${project.id}`)
      navigate('/projecten')
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  return (
    <>
      <div className="detail-hero card" style={{ borderTop: `4px solid ${project.color}` }}>
        <div className="avatar avatar-lg" style={{ background: project.color }}><FileText size={24} /></div>
        <div className="grow">
          <div className="row" style={{ gap: 10 }}>
            <h1>{project.name}</h1>
            <Badge tone={st.tone}>{st.label}</Badge>
          </div>
          <div className="row wrap muted" style={{ marginTop: 6, gap: 16 }}>
            <a className="row" onClick={() => navigate(`/klanten/${project.customerId}`)} style={{ cursor: 'pointer' }}>{project.customerName}</a>
            <span>{project.billing === 'uur' ? `${euro(project.hourlyRate)} per uur` : `Vaste prijs ${euro(project.fixedPrice)}`}</span>
            {project.deadline && <span className="row"><CalendarClock size={14} /> Deadline {date(project.deadline)}</span>}
            {project.repoUrl && <a className="row" href={project.repoUrl} target="_blank" rel="noreferrer"><GitBranch size={14} /> Repository</a>}
          </div>
        </div>
        <div className="row">
          {project.billing === 'uur' && (
            <button className="btn btn-primary" disabled={project.minutesUnbilled === 0} onClick={() => setInvoicing(true)}>
              <Receipt size={15} /> Factureer {hours(project.minutesUnbilled)}
            </button>
          )}
          <button className="btn" onClick={() => setEditing(true)}><Pencil size={15} /> Bewerken</button>
          <button className="btn btn-danger" onClick={remove}><Trash2 size={15} /></button>
        </div>
      </div>

      <div className="grid grid-stats" style={{ marginBottom: 18 }}>
        <MiniStat icon={<Clock size={18} />} accent={project.color} label="Gewerkt" value={hours(project.minutesTotal)}
          sub={project.budgetHours ? `van ${project.budgetHours} uur budget` : 'Geen budget ingesteld'} progress={pct ?? undefined} />
        {project.billing === 'uur'
          ? <MiniStat icon={<Euro size={18} />} accent="var(--green)" label="Open om te factureren" value={euro(project.unbilledValue)} sub={`${hours(project.minutesUnbilled)} nog niet gefactureerd`} />
          : <MiniStat icon={<Gauge size={18} />} accent="var(--green)" label="Effectief uurtarief" value={euro(project.minutesTotal ? project.fixedPrice / (project.minutesTotal / 60) : 0)} sub={`${euro(project.fixedPrice)} gedeeld door je uren`} />}
        <MiniStat icon={<Receipt size={18} />} accent="var(--blue)" label="Gefactureerd" value={euro(project.invoicedValue)} sub={`${mine.length} facturen, excl. btw`} />
      </div>

      <SubTabs<TabId>
        value={tab}
        onChange={setTab}
        tabs={[
          { id: 'uren', label: 'Uren', icon: <Clock size={14} />, count: entries?.length },
          { id: 'facturen', label: 'Facturen', icon: <Receipt size={14} />, count: mine.length },
          { id: 'info', label: 'Omschrijving', icon: <Info size={14} /> },
        ]}
      />

      {tab === 'uren' && (
        <div className="card">
          <div className="card-head"><h3>Boekingen</h3><button className="btn btn-sm btn-primary" onClick={() => setEntry({})}><Plus size={14} /> Uren boeken</button></div>
          {!entries?.length ? <Empty icon={<Clock size={22} />} title="Nog geen uren op dit project" /> : (
            <div className="table-wrap" style={{ marginTop: 10 }}>
              <table>
                <thead><tr><th>Datum</th><th>Omschrijving</th><th>Status</th><th className="num">Tijd</th></tr></thead>
                <tbody>
                  {entries.map(e => (
                    <tr key={e.id} className="clickable" onClick={() => e.invoiceId ? navigate(`/facturen/${e.invoiceId}`) : setEntry({ entry: e })}>
                      <td className="muted" style={{ whiteSpace: 'nowrap' }}>{date(e.date)}</td>
                      <td>{e.description || <span className="muted">—</span>}</td>
                      <td>
                        {e.invoiceId ? <Badge tone={e.invoiceNumber ? 'green' : 'gray'}><Lock size={10} /> {e.invoiceNumber ?? 'Concept'}</Badge>
                          : !e.billable ? <Badge tone="gray">Niet factureerbaar</Badge> : <Badge tone="yellow">Open</Badge>}
                      </td>
                      <td className="num"><strong>{hm(e.minutes)}</strong></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {tab === 'facturen' && (
        <div className="card">
          <div className="card-head"><h3>Facturen met uren van dit project</h3></div>
          {mine.length === 0 ? <Empty icon={<Receipt size={22} />} title="Nog niets gefactureerd" /> : (
            <div className="table-wrap" style={{ marginTop: 10 }}>
              <table>
                <thead><tr><th>Nummer</th><th>Datum</th><th>Status</th><th className="num">Bedrag</th></tr></thead>
                <tbody>
                  {mine.map(i => {
                    const s = invoiceStatuses.find(x => x.id === i.status)!
                    return (
                      <tr key={i.id} className="clickable" onClick={() => navigate(`/facturen/${i.id}`)}>
                        <td>{i.number ?? <span className="muted">Concept</span>}</td><td className="muted">{date(i.issueDate)}</td>
                        <td><Badge tone={s.tone}>{s.label}</Badge></td>
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

      {tab === 'info' && (
        <div className="card">
          <div className="card-body">
            {project.description ? <p style={{ margin: 0, whiteSpace: 'pre-wrap' }}>{project.description}</p> : <p className="muted" style={{ margin: 0 }}>Nog geen omschrijving. Klik op Bewerken om er een toe te voegen.</p>}
          </div>
        </div>
      )}

      {editing && <ProjectForm project={project} onClose={() => setEditing(false)} onSaved={() => { setEditing(false); reload(true) }} />}
      {entry && <TimeEntryForm entry={entry.entry} projectId={project.id} onClose={() => setEntry(null)} onSaved={() => setEntry(null)} />}
      {invoicing && <InvoiceHoursModal project={project} onClose={() => setInvoicing(false)} />}
    </>
  )
}

function InvoiceHoursModal({ project, onClose }: { project: Project; onClose: () => void }) {
  const [detailed, setDetailed] = useState(false)
  const [busy, setBusy] = useState(false)
  const navigate = useNavigate()
  const toast = useToast()

  const create = async () => {
    setBusy(true)
    try {
      const inv = await api.post<{ id: number }>(`/projects/${project.id}/invoice`, { detailed })
      toast('Conceptfactuur aangemaakt')
      onClose()
      navigate(`/facturen/${inv.id}`)
    } catch (e) {
      toast((e as Error).message, 'error')
      setBusy(false)
    }
  }

  return (
    <Modal title="Open uren factureren" onClose={onClose} footer={<>
      <button className="btn" onClick={onClose}>Annuleren</button>
      <button className="btn btn-primary" disabled={busy} onClick={create}><Receipt size={15} /> Conceptfactuur maken</button>
    </>}>
      <p style={{ marginTop: 0 }}>
        Je zet <strong>{hours(project.minutesUnbilled, 2)}</strong> à {euro(project.hourlyRate)} op een nieuwe factuur
        voor <strong>{project.customerName}</strong>: <strong>{euro(project.unbilledValue)}</strong> excl. btw.
      </p>
      <div className="segmented">
        <button type="button" className={!detailed ? 'active' : ''} onClick={() => setDetailed(false)}>Eén regel met het totaal</button>
        <button type="button" className={detailed ? 'active' : ''} onClick={() => setDetailed(true)}>Elke boeking een eigen regel</button>
      </div>
      <p className="muted" style={{ marginBottom: 0 }}>De uren worden aan de factuur gekoppeld. Verwijder je de factuur, dan komen ze weer vrij.</p>
    </Modal>
  )
}
