import { useState } from 'react'
import { Play, Square, Trash2 } from 'lucide-react'
import { useApi } from '../lib/useApi'
import type { Project } from '../lib/types'
import { clock, useTimer } from '../lib/timer'

// Start/stop-timer bovenaan de urenpagina.
export function TimerCard() {
  const { running, elapsed, start, stop, update, discard } = useTimer()
  const { data: projects } = useApi<Project[]>('/projects')
  const active = (projects ?? []).filter(p => p.status === 'actief')
  const [projectId, setProjectId] = useState(0)
  const [description, setDescription] = useState('')
  const chosen = projectId || active[0]?.id || 0

  const begin = (e: React.FormEvent) => {
    e.preventDefault()
    const p = active.find(x => x.id === chosen)
    if (!p) return
    start({ projectId: p.id, projectName: p.name, projectColor: p.color, description, billable: p.billing === 'uur' })
    setDescription('')
  }

  if (running) {
    return (
      <div className="card timer-card running" style={{ '--accent': running.projectColor } as React.CSSProperties}>
        <span className="timer-pulse" />
        <div className="grow">
          <div className="timer-project">{running.projectName}</div>
          <input className="timer-desc" value={running.description} placeholder="Waar werk je aan?" onChange={e => update({ description: e.target.value })} />
        </div>
        <div className="timer-clock">{clock(elapsed)}</div>
        <button className="icon-btn" title="Timer weggooien" onClick={() => confirm('Timer stoppen zonder de tijd te boeken?') && discard()}><Trash2 size={16} /></button>
        <button className="btn btn-stop" onClick={stop}><Square size={14} fill="currentColor" /> Stop</button>
      </div>
    )
  }

  return (
    <form className="card timer-card" onSubmit={begin}>
      <input className="timer-desc" value={description} placeholder="Waar ga je aan werken?" onChange={e => setDescription(e.target.value)} />
      <select value={chosen} onChange={e => setProjectId(Number(e.target.value))}>
        {active.length === 0 && <option value={0}>Maak eerst een project aan</option>}
        {active.map(p => <option key={p.id} value={p.id}>{p.name} · {p.customerName}</option>)}
      </select>
      <div className="timer-clock muted">0:00:00</div>
      <button className="btn btn-primary" disabled={!chosen}><Play size={14} fill="currentColor" /> Start</button>
    </form>
  )
}
