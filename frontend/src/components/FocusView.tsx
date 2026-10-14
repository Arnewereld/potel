import { useEffect, useRef, useState } from 'react'
import { Maximize2, Minimize2, Pause, Play, Square, X } from 'lucide-react'
import { clock, useTimer } from '../lib/timer'
import { mmss, notificationsBlocked } from '../lib/focus'

const fullscreenEnabled = () => { try { return document.fullscreenEnabled === true } catch { return false } }
const exitFullscreen = () => { if (document.fullscreenElement) void document.exitFullscreen().catch(() => { /* al weg */ }) }

// De focusweergave: rustig en schermvullend, met alleen de tijd die nog over is, het project en waar je aan werkt.
// Sluiten brengt je terug naar Potel; de focusmodus loopt dan gewoon door.
export function FocusView() {
  const { focusOpen, running } = useTimer()
  return focusOpen && running?.focus ? <FocusScreen /> : null
}

function FocusScreen() {
  const { running, focus, paused, elapsed, stopping, pause, resume, stop, nextBlock, stopFocus, closeFocus } = useTimer()
  const ref = useRef<HTMLDivElement>(null)
  const [full, setFull] = useState(() => document.fullscreenElement != null)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !document.fullscreenElement) closeFocus() }
    const onFull = () => setFull(document.fullscreenElement != null)
    window.addEventListener('keydown', onKey)
    document.addEventListener('fullscreenchange', onFull)
    ref.current?.focus()
    return () => {
      window.removeEventListener('keydown', onKey)
      document.removeEventListener('fullscreenchange', onFull)
      exitFullscreen()
    }
  }, [closeFocus])

  if (!running?.focus || !focus) return null
  const f = running.focus
  const long = f.breakMinutes > 5
  const label = focus.phase === 'focus' ? (paused ? 'Focusblok gepauzeerd' : 'Focus') : focus.phase === 'break' ? (long ? 'Lange pauze' : 'Pauze') : 'Pauze voorbij'
  const hint = focus.phase === 'focus'
    ? paused ? 'Het blok staat stil tot je verder gaat.' : 'Werk door tot het blok om is. Je hoort dan een zacht geluid.'
    : focus.phase === 'break'
      ? 'Sta even op en neem wat te drinken. De timer staat stil, dus deze pauze wordt niet geboekt.'
      : 'Ga verder wanneer je er klaar voor bent. Tot die tijd staat de timer stil.'
  // Na het vierde blok staan alle vier de stippen vol, tot de lange pauze voorbij is.
  const filled = focus.phase !== 'focus' && long ? 4 : f.round
  const progress = focus.total ? 1 - focus.remaining / focus.total : 0
  const r = 46
  const circle = 2 * Math.PI * r

  const toggleFull = () => {
    if (document.fullscreenElement) exitFullscreen()
    else void ref.current?.requestFullscreen().catch(() => { /* niet toegestaan */ })
  }

  return (
    <div ref={ref} tabIndex={-1} className={`focus-view focus-${focus.phase} ${paused && focus.phase === 'focus' ? 'focus-paused' : ''}`}
      role="dialog" aria-modal="true" aria-label="Focusmodus" style={{ '--accent': running.projectColor } as React.CSSProperties}>
      <div className="focus-bar">
        <span className="focus-round" title={`${filled} van de 4 blokken tot de lange pauze`} aria-label={`${filled} van de 4 blokken tot de lange pauze`}>
          {Array.from({ length: 4 }, (_, i) => <span key={i} className={i < filled ? 'done' : ''} />)}
        </span>
        <div className="focus-bar-actions">
          {fullscreenEnabled() && (
            <button type="button" className="icon-btn" onClick={toggleFull} title={full ? 'Volledig scherm uit' : 'Volledig scherm'} aria-label={full ? 'Volledig scherm uit' : 'Volledig scherm'}>
              {full ? <Minimize2 size={18} /> : <Maximize2 size={18} />}
            </button>
          )}
          <button type="button" className="icon-btn" onClick={closeFocus} title="Terug naar Potel; de focusmodus loopt door" aria-label="Focusweergave sluiten"><X size={20} /></button>
        </div>
      </div>

      <div className="focus-center">
        <div className="focus-phase">{label}</div>
        <div className="focus-ring">
          <svg viewBox="0 0 100 100" aria-hidden="true">
            <circle className="focus-ring-track" cx="50" cy="50" r={r} />
            <circle className="focus-ring-fill" cx="50" cy="50" r={r} strokeDasharray={circle} strokeDashoffset={circle * (1 - progress)} />
          </svg>
          <div className="focus-time" role="timer">{mmss(focus.remaining)}</div>
        </div>
        <div className="focus-project">
          <span className="legend-dot" style={{ background: running.projectColor }} />
          <span className="truncate">{running.projectName}{running.customerName && <span className="muted"> · {running.customerName}</span>}</span>
        </div>
        {running.description && <div className="focus-desc">{running.description}</div>}
        <p className="focus-hint">{hint}</p>
        <div className="focus-actions">
          {focus.phase === 'focus'
            ? paused
              ? <button type="button" className="btn btn-primary btn-lg" onClick={() => void resume()}><Play size={16} fill="currentColor" /> Verder</button>
              : <button type="button" className="btn btn-lg" onClick={() => void pause()}><Pause size={16} fill="currentColor" /> Pauzeren</button>
            : <button type="button" className="btn btn-primary btn-lg" onClick={() => void nextBlock()}>
                <Play size={16} fill="currentColor" /> {focus.phase === 'ready' ? 'Volgend blok' : 'Pauze overslaan'}
              </button>}
          <button type="button" className="btn btn-stop btn-lg" disabled={stopping} onClick={() => void stop()}>
            <Square size={15} fill="currentColor" /> {stopping ? 'Boeken…' : 'Stop en boek'}
          </button>
        </div>
        <div className="focus-foot">
          <span>{f.blocks === 1 ? '1 blok af' : `${f.blocks} blokken af`} · {clock(elapsed)} gewerkt</span>
          <button type="button" className="btn btn-ghost btn-sm" onClick={() => void stopFocus()}>Focusmodus uit</button>
        </div>
        {notificationsBlocked() && (
          <p className="focus-note">Meldingen staan uit voor Potel in je browser. Zolang Potel openstaat, hoor je wel een geluid.</p>
        )}
      </div>
    </div>
  )
}
