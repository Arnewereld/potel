import { useCallback, useEffect, useRef, useState } from 'react'
import { useLocation, useNavigate, useParams } from 'react-router-dom'
import { Save, Play, Trash2, Plus, Minus, Maximize, Check, Search } from 'lucide-react'
import { api } from '../lib/api'
import { useTabs, useTabTitle } from '../lib/tabs'
import { useToast } from '../lib/toast'
import type { Workflow, WorkflowEdge, WorkflowNode } from '../lib/types'
import { nodeType, nodeTypes } from '../lib/nodes'
import { ErrorBox, Loading } from '../components/ui'

const NODE = 92
const HANDLE_Y = NODE / 2

interface View { x: number; y: number; k: number }
type Drag =
  | { kind: 'pan'; startX: number; startY: number; view: View }
  | { kind: 'node'; id: string; offX: number; offY: number }
  | { kind: 'link'; from: string }

function edgePath(x1: number, y1: number, x2: number, y2: number) {
  const dx = Math.max(60, Math.abs(x2 - x1) / 2)
  return `M ${x1} ${y1} C ${x1 + dx} ${y1}, ${x2 - dx} ${y2}, ${x2} ${y2}`
}

export function WorkflowEditorPage() {
  const { id } = useParams()
  const { pathname } = useLocation()
  const [wf, setWf] = useState<Workflow | null>(null)
  const [nodes, setNodes] = useState<WorkflowNode[]>([])
  const [edges, setEdges] = useState<WorkflowEdge[]>([])
  const [view, setView] = useState<View>({ x: 0, y: 0, k: 1 })
  const [selected, setSelected] = useState<string | null>(null)
  const [selectedEdge, setSelectedEdge] = useState<number | null>(null)
  const [cursor, setCursor] = useState<{ x: number; y: number; from: string } | null>(null)
  const [running, setRunning] = useState<Record<string, 'run' | 'ok'>>({})
  const [paletteQuery, setPaletteQuery] = useState('')
  const [dirty, setDirty] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const drag = useRef<Drag | null>(null)
  const canvas = useRef<HTMLDivElement>(null)
  const toast = useToast()
  const navigate = useNavigate()
  const { close } = useTabs()
  useTabTitle(wf?.name)

  useEffect(() => {
    api.get<Workflow>(`/workflows/${id}`).then(w => {
      setWf(w)
      try {
        const g = JSON.parse(w.graphJson)
        setNodes(g.nodes ?? []); setEdges(g.edges ?? [])
      } catch { /* lege grafiek */ }
    }).catch(e => setError(e.message))
  }, [id])

  const toCanvas = useCallback((clientX: number, clientY: number) => {
    const r = canvas.current!.getBoundingClientRect()
    return { x: (clientX - r.left - view.x) / view.k, y: (clientY - r.top - view.y) / view.k }
  }, [view])

  // Slepen van nodes, verbindingen en het canvas zelf.
  useEffect(() => {
    const move = (e: PointerEvent) => {
      const d = drag.current
      if (!d) return
      if (d.kind === 'pan') setView({ ...d.view, x: d.view.x + e.clientX - d.startX, y: d.view.y + e.clientY - d.startY })
      if (d.kind === 'node') {
        const p = toCanvas(e.clientX, e.clientY)
        const x = Math.round((p.x - d.offX) / 10) * 10, y = Math.round((p.y - d.offY) / 10) * 10
        setNodes(ns => ns.map(n => (n.id === d.id ? { ...n, x, y } : n)))
        setDirty(true)
      }
      if (d.kind === 'link') setCursor({ ...toCanvas(e.clientX, e.clientY), from: d.from })
    }
    const up = () => { drag.current = null; setCursor(null) }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
    return () => { window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up) }
  }, [toCanvas])

  const removeSelected = useCallback(() => {
    if (selected) {
      setNodes(ns => ns.filter(n => n.id !== selected))
      setEdges(es => es.filter(e => e.from !== selected && e.to !== selected))
      setSelected(null); setDirty(true)
    } else if (selectedEdge !== null) {
      setEdges(es => es.filter((_, i) => i !== selectedEdge))
      setSelectedEdge(null); setDirty(true)
    }
  }, [selected, selectedEdge])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement).tagName
      if (tag === 'INPUT' || tag === 'TEXTAREA') return
      if (!canvas.current?.offsetParent) return // tabblad niet zichtbaar
      if (e.key === 'Delete' || e.key === 'Backspace') removeSelected()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [removeSelected])

  if (error) return <ErrorBox message={error} />
  if (!wf) return <Loading />

  const addNode = (type: string) => {
    const t = nodeType(type)
    const r = canvas.current!.getBoundingClientRect()
    const c = toCanvas(r.left + r.width / 2, r.top + r.height / 2)
    const last = nodes.find(n => n.id === selected)
    const node: WorkflowNode = {
      id: `n${Date.now().toString(36)}`, type, label: t.label,
      x: last ? last.x + 200 : Math.round(c.x - NODE / 2), y: last ? last.y : Math.round(c.y - NODE / 2),
    }
    setNodes(ns => [...ns, node])
    if (last) setEdges(es => [...es, { from: last.id, to: node.id }])
    setSelected(node.id); setDirty(true)
  }

  const connect = (to: string) => {
    const d = drag.current
    if (d?.kind !== 'link' || d.from === to) return
    if (!edges.some(e => e.from === d.from && e.to === to)) setEdges(es => [...es, { from: d.from, to }])
    setDirty(true)
  }

  const save = async (patch: Partial<Workflow> = {}) => {
    const body = { ...wf, ...patch, graphJson: JSON.stringify({ nodes, edges }) }
    try {
      const saved = await api.put<Workflow>(`/workflows/${wf.id}`, body)
      setWf(saved); setDirty(false)
      toast('Werkstroom opgeslagen')
    } catch (e) { toast((e as Error).message, 'error') }
  }

  const remove = async () => {
    if (!confirm(`Werkstroom "${wf.name}" verwijderen?`)) return
    await api.del(`/workflows/${wf.id}`)
    close(pathname)
    navigate('/werkstromen')
  }

  // Een proefrun: elk blok licht na elkaar op langs de verbindingen.
  const test = async () => {
    const incoming = new Set(edges.map(e => e.to))
    let frontier = nodes.filter(n => !incoming.has(n.id)).map(n => n.id)
    const seen = new Set<string>()
    setRunning({})
    while (frontier.length) {
      frontier = frontier.filter(n => !seen.has(n))
      frontier.forEach(n => seen.add(n))
      setRunning(r => ({ ...r, ...Object.fromEntries(frontier.map(n => [n, 'run' as const])) }))
      await new Promise(res => setTimeout(res, 650))
      setRunning(r => ({ ...r, ...Object.fromEntries(frontier.map(n => [n, 'ok' as const])) }))
      frontier = edges.filter(e => frontier.includes(e.from)).map(e => e.to)
    }
    toast(`Testrun klaar: ${seen.size} blokken uitgevoerd`)
    setTimeout(() => setRunning({}), 2500)
  }

  const zoom = (factor: number) => setView(v => {
    const k = Math.min(2, Math.max(0.4, v.k * factor))
    const r = canvas.current!.getBoundingClientRect()
    const cx = r.width / 2, cy = r.height / 2
    return { k, x: cx - ((cx - v.x) / v.k) * k, y: cy - ((cy - v.y) / v.k) * k }
  })

  const fit = () => {
    if (nodes.length === 0) return setView({ x: 0, y: 0, k: 1 })
    const r = canvas.current!.getBoundingClientRect()
    const minX = Math.min(...nodes.map(n => n.x)), maxX = Math.max(...nodes.map(n => n.x)) + NODE
    const minY = Math.min(...nodes.map(n => n.y)), maxY = Math.max(...nodes.map(n => n.y)) + NODE + 30
    const k = Math.min(1.2, (r.width - 120) / (maxX - minX), (r.height - 120) / (maxY - minY))
    setView({ k, x: (r.width - (maxX - minX) * k) / 2 - minX * k, y: (r.height - (maxY - minY) * k) / 2 - minY * k })
  }

  const byId = Object.fromEntries(nodes.map(n => [n.id, n]))
  const sel = selected ? byId[selected] : null
  const linkFrom = cursor ? byId[cursor.from] : null
  const groups = ['Triggers', 'Acties', 'Logica'] as const

  return (
    <div className="wf-editor">
      <div className="wf-toolbar">
        <input className="wf-name" value={wf.name} onChange={e => { setWf({ ...wf, name: e.target.value }); setDirty(true) }} />
        {dirty && <span className="muted">Niet opgeslagen</span>}
        <span className="spacer" />
        <label className="row muted" style={{ gap: 8 }}>
          {wf.active ? 'Actief' : 'Uit'}
          <button className={`switch ${wf.active ? 'on' : ''}`} onClick={() => save({ active: !wf.active })}><span /></button>
        </label>
        <button className="btn btn-danger btn-sm" onClick={remove}><Trash2 size={14} /></button>
        <button className="btn btn-sm" onClick={test}><Play size={14} /> Testen</button>
        <button className="btn btn-primary btn-sm" onClick={() => save()}><Save size={14} /> Opslaan</button>
      </div>

      <div className="wf-body">
        <div
          ref={canvas}
          className="wf-canvas"
          style={{ backgroundPosition: `${view.x}px ${view.y}px`, backgroundSize: `${22 * view.k}px ${22 * view.k}px` }}
          onPointerDown={e => {
            if (e.target !== e.currentTarget && !(e.target as HTMLElement).classList.contains('wf-layer')) return
            drag.current = { kind: 'pan', startX: e.clientX, startY: e.clientY, view }
            setSelected(null); setSelectedEdge(null)
          }}
          onWheel={e => zoom(e.deltaY < 0 ? 1.1 : 0.9)}
        >
          <div className="wf-layer" style={{ transform: `translate(${view.x}px, ${view.y}px) scale(${view.k})` }}>
            <svg className="wf-edges">
              {edges.map((e, i) => {
                const a = byId[e.from], b = byId[e.to]
                if (!a || !b) return null
                const d = edgePath(a.x + NODE, a.y + HANDLE_Y, b.x, b.y + HANDLE_Y)
                const active = running[e.from] === 'ok'
                return (
                  <g key={i} onPointerDown={ev => { ev.stopPropagation(); setSelectedEdge(i); setSelected(null) }}>
                    <path d={d} className="wf-edge-hit" />
                    <path d={d} className={`wf-edge ${selectedEdge === i ? 'selected' : ''} ${active ? 'active' : ''}`} />
                  </g>
                )
              })}
              {linkFrom && cursor && <path d={edgePath(linkFrom.x + NODE, linkFrom.y + HANDLE_Y, cursor.x, cursor.y)} className="wf-edge drafting" />}
            </svg>

            {nodes.map(n => {
              const t = nodeType(n.type)
              const isTrigger = n.type.startsWith('trigger')
              return (
                <div
                  key={n.id}
                  className={`wf-node ${isTrigger ? 'trigger' : ''} ${selected === n.id ? 'selected' : ''} ${running[n.id] ?? ''}`}
                  style={{ left: n.x, top: n.y, '--c': t.color } as React.CSSProperties}
                  onPointerDown={e => {
                    e.stopPropagation()
                    const p = toCanvas(e.clientX, e.clientY)
                    drag.current = { kind: 'node', id: n.id, offX: p.x - n.x, offY: p.y - n.y }
                    setSelected(n.id); setSelectedEdge(null)
                  }}
                  onPointerUp={() => connect(n.id)}
                >
                  <div className="wf-node-box">
                    <t.icon size={34} />
                    {running[n.id] === 'ok' && <span className="wf-ok"><Check size={12} /></span>}
                  </div>
                  {!isTrigger && <span className="wf-handle in" />}
                  <span
                    className="wf-handle out"
                    onPointerDown={e => {
                      e.stopPropagation()
                      drag.current = { kind: 'link', from: n.id }
                      setCursor({ ...toCanvas(e.clientX, e.clientY), from: n.id })
                    }}
                  />
                  <div className="wf-node-label">{n.label}</div>
                </div>
              )
            })}
          </div>

          <div className="wf-zoom">
            <button className="icon-btn" onClick={() => zoom(1.2)} title="Inzoomen"><Plus size={16} /></button>
            <button className="icon-btn" onClick={() => zoom(0.8)} title="Uitzoomen"><Minus size={16} /></button>
            <button className="icon-btn" onClick={fit} title="Passend maken"><Maximize size={16} /></button>
          </div>
          {nodes.length === 0 && <div className="wf-hint">Kies rechts een trigger om te beginnen</div>}
        </div>

        <aside className="wf-panel">
          {sel ? (
            <>
              <div className="wf-panel-head" style={{ '--c': nodeType(sel.type).color } as React.CSSProperties}>
                <span className="wf-mini" style={{ background: nodeType(sel.type).color }}>{(() => { const I = nodeType(sel.type).icon; return <I size={14} /> })()}</span>
                <strong>{nodeType(sel.type).label}</strong>
              </div>
              <p className="muted" style={{ marginTop: 0 }}>{nodeType(sel.type).description}</p>
              <label className="field">
                <span>Naam van dit blok</span>
                <input value={sel.label} onChange={e => { setNodes(ns => ns.map(n => (n.id === sel.id ? { ...n, label: e.target.value } : n))); setDirty(true) }} />
              </label>
              <button className="btn btn-danger btn-sm mt" onClick={removeSelected}><Trash2 size={14} /> Blok verwijderen</button>
              <hr className="wf-sep" />
              <p className="muted" style={{ fontSize: 12 }}>Tip: klik hieronder op een blok om het direct achter dit blok te koppelen.</p>
            </>
          ) : (
            <p className="muted" style={{ marginTop: 0, fontSize: 12 }}>Sleep vanaf het bolletje rechts van een blok naar een ander blok om ze te verbinden. Klik op een lijn en druk op Delete om hem weg te halen.</p>
          )}
          <div className="search-input" style={{ marginBottom: 10 }}>
            <Search size={14} />
            <input style={{ width: '100%' }} placeholder="Zoek blok…" value={paletteQuery} onChange={e => setPaletteQuery(e.target.value)} />
          </div>
          {groups.map(g => {
            const items = nodeTypes.filter(t => t.group === g && t.label.toLowerCase().includes(paletteQuery.toLowerCase()))
            if (items.length === 0) return null
            return (
              <div key={g}>
                <div className="nav-label" style={{ padding: '10px 0 6px' }}>{g}</div>
                {items.map(t => (
                  <button key={t.type} className="wf-palette-item" onClick={() => addNode(t.type)}>
                    <span className="wf-mini" style={{ background: t.color }}><t.icon size={14} /></span>
                    <span><strong>{t.label}</strong><span className="cell-sub">{t.description}</span></span>
                  </button>
                ))}
              </div>
            )
          })}
        </aside>
      </div>
    </div>
  )
}
