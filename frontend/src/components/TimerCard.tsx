import { useState } from 'react'
import { Pause, Play, Square, Trash2 } from 'lucide-react'
import { useApi } from '../lib/useApi'
import type { Project } from '../lib/types'
import { clock, useTimer } from '../lib/timer'

// Start/stop-timer bovenaan de urenpagina. Hij staat op de server, dus op elk apparaat dezelfde.
export function TimerCard() {
  const { running, elapsed, paused, stopping, start, stop, update, discard, pause, resume, switchTo } = useTimer()
  const { data: projects } = useApi<Project[]>('/projects')
  const active = (projects ?? []).filter(p => p.status === 'actief')
  const [projectId, setProjectId] = useState(0)
  const [description, setDescription] = useState('')
  const [starting, setStarting] = useState(false)
  const chosen = projectId || active[0]?.id || 0

  const begin = async (e: React.FormEvent) => {
    e.preventDefault()
    const p = active.find(x => x.id === chosen)
    if (!p || starting) return
    setStarting(true)
    if (await start({ projectId: p.id, description, billable: p.billing === 'uur' })) setDescription('')
    setStarting(false)
  }

  if (running) {
    // Een ander project kiezen boekt de tijd tot nu op het huidige project en laat de timer verder lopen op het nieuwe.
    const choices = active.some(p => p.id === running.projectId) ? active : [{ id: running.projectId, name: running.projectName, customerName: running.customerName }, ...active]
    const change = (id: number) => {
      const p = active.find(x => x.id === id)
      if (p && p.id !== running.projectId) void switchTo({ projectId: p.id, projectName: p.name, description: '', billable: p.billing === 'uur' })
    }
    return (
      <div className={`card timer-card running ${paused ? 'paused' : ''}`} style={{ '--accent': running.projectColor } as React.CSSProperties}>
        <span className="timer-pulse" />
        <div className="grow timer-main">
          <select className="timer-project" value={running.projectId} disabled={stopping} onChange={e => change(Number(e.target.value))}
            title="Ander project kiezen: de tijd tot nu wordt geboekt en de timer loopt verder op het nieuwe project" aria-label="Project van de timer">
            {choices.map(p => <option key={p.id} value={p.id}>{p.name}{p.customerName ? ` · ${p.customerName}` : ''}</option>)}
          </select>
          <input className="timer-desc" value={running.description} placeholder="Waar werk je aan?" onChange={e => update({ description: e.target.value })} />
        </div>
        <div className="timer-clock">{clock(elapsed)}{paused && <small>gepauzeerd</small>}</div>
        <div className="timer-actions">
          <button className="icon-btn timer-icon" title="Timer weggooien" aria-label="Timer weggooien"
            onClick={() => confirm('Timer stoppen zonder de tijd te boeken?') && void discard()}><Trash2 size={16} /></button>
          {paused
            ? <button className="btn" onClick={() => void resume()}><Play size={14} fill="currentColor" /> Verder</button>
            : <button className="btn" onClick={() => void pause()}><Pause size={14} fill="currentColor" /> Pauze</button>}
          <button className="btn btn-stop" disabled={stopping} onClick={() => void stop()}><Square size={14} fill="currentColor" /> {stopping ? 'Boeken…' : 'Stop'}</button>
        </div>
      </div>
    )
  }

  return (
    <form className="card timer-card" onSubmit={begin}>
      <input className="timer-desc" value={description} placeholder="Waar ga je aan werken?" onChange={e => setDescription(e.target.value)} />
      <select value={chosen} onChange={e => setProjectId(Number(e.target.value))} aria-label="Project">
        {active.length === 0 && <option value={0}>Maak eerst een project aan</option>}
        {active.map(p => <option key={p.id} value={p.id}>{p.name} · {p.customerName}</option>)}
      </select>
      <div className="timer-clock muted">0:00:00</div>
      <div className="timer-actions">
        <button className="btn btn-primary" disabled={!chosen || starting}><Play size={14} fill="currentColor" /> Start</button>
      </div>
    </form>
  )
}
