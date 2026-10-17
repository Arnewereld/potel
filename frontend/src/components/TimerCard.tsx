import { useState } from 'react'
import { Coffee, Pause, Play, Square, Target, Trash2 } from 'lucide-react'
import { useApi } from '../lib/useApi'
import type { Project } from '../lib/types'
import { clock, useTimer } from '../lib/timer'
import { mmss } from '../lib/focus'
import { FocusStartModal } from './FocusStart'

// Start/stop-timer bovenaan de urenpagina. Hij staat op de server, dus op elk apparaat dezelfde.
export function TimerCard() {
  const { running, elapsed, paused, stopping, start, stop, update, discard, pause, resume, switchTo, focus, focusMinutes, startFocus, openFocus, nextBlock } = useTimer()
  const { data: projects } = useApi<Project[]>('/projects')
  const active = (projects ?? []).filter(p => p.status === 'actief')
  const [projectId, setProjectId] = useState(0)
  const [description, setDescription] = useState('')
  const [starting, setStarting] = useState(false)
  const [choosingFocus, setChoosingFocus] = useState(false)
  const chosen = projectId || active[0]?.id || 0
  const chosenProject = active.find(x => x.id === chosen)

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
    return (<>
      <div className={`card timer-card running ${paused ? 'paused' : ''}`} style={{ '--accent': running.projectColor } as React.CSSProperties}>
        <span className="timer-pulse" />
        <div className="grow timer-main">
          <select className="timer-project" value={running.projectId} disabled={stopping} onChange={e => change(Number(e.target.value))}
            title="Ander project kiezen: de tijd tot nu wordt geboekt en de timer loopt verder op het nieuwe project" aria-label="Project van de timer">
            {choices.map(p => <option key={p.id} value={p.id}>{p.name}{p.customerName ? ` · ${p.customerName}` : ''}</option>)}
          </select>
          <input className="timer-desc" value={running.description} placeholder="Waar werk je aan?" onChange={e => update({ description: e.target.value })} />
        </div>
        <div className="timer-clock">{clock(elapsed)}{paused && <small>{focus && focus.phase !== 'focus' ? 'pauze, niet geboekt' : 'gepauzeerd'}</small>}</div>
        <div className="timer-actions">
          <button className="icon-btn timer-icon" title="Timer weggooien" aria-label="Timer weggooien"
            onClick={() => confirm('Timer stoppen zonder de tijd te boeken?') && void discard()}><Trash2 size={16} /></button>
          {focus
            ? <button type="button" className="btn timer-focus-on" onClick={openFocus} title="Focusweergave openen">
                {focus.phase === 'focus' ? <Target size={14} /> : <Coffee size={14} />} {focus.phase === 'ready' ? 'Pauze voorbij' : mmss(focus.remaining)}
              </button>
            : <button type="button" className="btn" onClick={() => setChoosingFocus(true)} title="Werken in focusblokken met pauzes ertussen"><Target size={14} /> Focus</button>}
          {paused
            ? focus && focus.phase !== 'focus'
              ? <button className="btn" onClick={() => void nextBlock()}><Play size={14} fill="currentColor" /> Volgend blok</button>
              : <button className="btn" onClick={() => void resume()}><Play size={14} fill="currentColor" /> Verder</button>
            : <button className="btn" onClick={() => void pause()}><Pause size={14} fill="currentColor" /> Pauze</button>}
          <button className="btn btn-stop" disabled={stopping} onClick={() => void stop()}><Square size={14} fill="currentColor" /> {stopping ? 'Boeken…' : 'Stop'}</button>
        </div>
      </div>
      {choosingFocus && <FocusStartModal minutes={focusMinutes} projectName={running.projectName} onStart={m => startFocus(m)} onClose={() => setChoosingFocus(false)} />}
    </>)
  }

  return (<>
    <form className="card timer-card" onSubmit={begin}>
      <input className="timer-desc" value={description} placeholder="Waar ga je aan werken?" onChange={e => setDescription(e.target.value)} />
      <select value={chosen} onChange={e => setProjectId(Number(e.target.value))} aria-label="Project">
        {active.length === 0 && <option value={0}>Maak eerst een project aan</option>}
        {active.map(p => <option key={p.id} value={p.id}>{p.name} · {p.customerName}</option>)}
      </select>
      <div className="timer-clock muted">0:00:00</div>
      <div className="timer-actions">
        <button type="button" className="btn" disabled={!chosenProject} onClick={() => setChoosingFocus(true)} title="Starten in focusblokken met pauzes ertussen"><Target size={14} /> Focus</button>
        <button className="btn btn-primary" disabled={!chosen || starting}><Play size={14} fill="currentColor" /> Start</button>
      </div>
    </form>
    {choosingFocus && chosenProject && (
      <FocusStartModal minutes={focusMinutes} projectName={chosenProject.name} onClose={() => setChoosingFocus(false)}
        onStart={async m => {
          const ok = await startFocus(m, { projectId: chosenProject.id, description, billable: chosenProject.billing === 'uur' })
          if (ok) setDescription('')
          return ok
        }} />
    )}
  </>)
}
