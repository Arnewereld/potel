import { useCallback, useEffect, useRef, useState } from 'react'
import { useLocation, useNavigate, useParams } from 'react-router-dom'
import { Save, Play, Trash2, Plus, Minus, Maximize, Check, Search, X, AlertTriangle, Clock, History, Blocks, ChevronDown, ChevronRight } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useAuth } from '../lib/auth'
import { useTabs, useTabTitle } from '../lib/tabs'
import { useToast } from '../lib/toast'
import type { Invoice, Lead, Workflow, WorkflowEdge, WorkflowNode, WorkflowRun, WorkflowRunLog } from '../lib/types'
import { adminOnlyTypes, defaultConfig, needsAdmin, nodeType, nodeTypes, variables, type NodeField } from '../lib/nodes'
import { dateTime, euro, invoiceTotals } from '../lib/format'
import { ErrorBox, Field, Loading, Modal, SubTabs } from '../components/ui'

const NODE = 92
const HANDLE_Y = NODE / 2
// Een Als/dan-blok heeft twee uitgangen: Ja boven, Nee onder.
const BRANCH_Y = { ja: 28, nee: 64 } as const

type Branch = 'ja' | 'nee'
type NodeState = 'run' | WorkflowRunLog['status']
interface View { x: number; y: number; k: number }
type Drag =
  | { kind: 'pan'; startX: number; startY: number; view: View }
  | { kind: 'node'; id: string; offX: number; offY: number }
  | { kind: 'link'; from: string; branch?: Branch }

function edgePath(x1: number, y1: number, x2: number, y2: number) {
  const dx = Math.max(60, Math.abs(x2 - x1) / 2)
  return `M ${x1} ${y1} C ${x1 + dx} ${y1}, ${x2 - dx} ${y2}, ${x2} ${y2}`
}

const outY = (n: WorkflowNode, branch?: Branch) => n.y + (n.type === 'logic.if' && branch ? BRANCH_Y[branch] : HANDLE_Y)
const parseLog = (run: WorkflowRun): WorkflowRunLog[] => { try { return JSON.parse(run.logJson) } catch { return [] } }
const runTone: Record<WorkflowRun['status'], string> = { bezig: 'yellow', wachtend: 'yellow', klaar: 'green', fout: 'red' }

export function WorkflowEditorPage() {
  const { id } = useParams()
  const { pathname } = useLocation()
  const [wf, setWf] = useState<Workflow | null>(null)
  const [nodes, setNodes] = useState<WorkflowNode[]>([])
  const [edges, setEdges] = useState<WorkflowEdge[]>([])
  const [view, setView] = useState<View>({ x: 0, y: 0, k: 1 })
  const [selected, setSelected] = useState<string | null>(null)
  const [selectedEdge, setSelectedEdge] = useState<number | null>(null)
  const [cursor, setCursor] = useState<{ x: number; y: number; from: string; branch?: Branch } | null>(null)
  const [states, setStates] = useState<Record<string, NodeState>>({})
  const [panel, setPanel] = useState<'blokken' | 'uitvoeringen'>('blokken')
  const [paletteQuery, setPaletteQuery] = useState('')
  const [runDialog, setRunDialog] = useState(false)
  const [dirty, setDirty] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const runs = useApi<WorkflowRun[]>(`/workflows/${id}/runs`)
  const drag = useRef<Drag | null>(null)
  const canvas = useRef<HTMLDivElement>(null)
  const toast = useToast()
  const navigate = useNavigate()
  const { close } = useTabs()
  const { user } = useAuth()
  const admin = user.role === 'beheerder'
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
      if (d.kind === 'link') setCursor({ ...toCanvas(e.clientX, e.clientY), from: d.from, branch: d.branch })
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
      if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return
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
      id: `n${Date.now().toString(36)}`, type, label: t.label, config: defaultConfig(type),
      x: last ? last.x + 220 : Math.round(c.x - NODE / 2), y: last ? last.y : Math.round(c.y - NODE / 2),
    }
    setNodes(ns => [...ns, node])
    if (last && !type.startsWith('trigger.')) setEdges(es => [...es, { from: last.id, to: node.id, ...(last.type === 'logic.if' ? { branch: 'ja' as const } : {}) }])
    setSelected(node.id); setDirty(true)
  }

  const connect = (to: string) => {
    const d = drag.current
    if (d?.kind !== 'link' || d.from === to) return
    if (!edges.some(e => e.from === d.from && e.to === to && e.branch === d.branch))
      setEdges(es => [...es, { from: d.from, to, ...(d.branch ? { branch: d.branch } : {}) }])
    setDirty(true)
  }

  const updateNode = (nodeId: string, patch: Partial<WorkflowNode>) => {
    setNodes(ns => ns.map(n => (n.id === nodeId ? { ...n, ...patch } : n)))
    setDirty(true)
  }

  const save = async (patch: Partial<Workflow> = {}, quiet = false) => {
    const body = { ...wf, ...patch, graphJson: JSON.stringify({ nodes, edges }) }
    try {
      const saved = await api.put<Workflow>(`/workflows/${wf.id}`, body)
      setWf(saved); setDirty(false)
      if (!quiet) toast(patch.active === undefined ? 'Werkstroom opgeslagen' : patch.active ? 'Werkstroom staat aan' : 'Werkstroom staat uit')
      return true
    } catch (e) {
      toast((e as Error).message, 'error')
      return false
    }
  }

  const remove = async () => {
    if (!confirm(`Werkstroom "${wf.name}" verwijderen?`)) return
    await api.del(`/workflows/${wf.id}`)
    close(pathname)
    navigate('/werkstromen')
  }

  // Laat de blokken één voor één oplichten in de volgorde waarin de server ze uitvoerde.
  const showRun = async (run: WorkflowRun) => {
    const log = parseLog(run)
    setStates({})
    for (const entry of log) {
      if (!entry.nodeId) continue
      setStates(s => ({ ...s, [entry.nodeId]: 'run' }))
      await new Promise(res => setTimeout(res, 220))
      setStates(s => ({ ...s, [entry.nodeId]: entry.status }))
    }
  }

  const execute = async (body: { leadId?: number; invoiceId?: number }) => {
    setRunDialog(false)
    if (dirty && !locked && !(await save({}, true))) return
    try {
      const run = await api.post<WorkflowRun>(`/workflows/${wf.id}/run`, body)
      runs.reload(true)
      setPanel('uitvoeringen')
      await showRun(run)
      const label = { klaar: 'Werkstroom uitgevoerd', wachtend: 'Werkstroom loopt en wacht nu op een Wachten-blok', fout: 'Werkstroom is vastgelopen, zie Uitvoeringen', bezig: 'Werkstroom loopt' }[run.status]
      toast(label, run.status === 'fout' ? 'error' : 'ok')
    } catch (e) {
      toast((e as Error).message, 'error')
    }
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
  const hasTrigger = nodes.some(n => n.type.startsWith('trigger.'))
  // E-mail en webhooks sturen gegevens naar buiten: zo'n werkstroom maakt of wijzigt alleen een beheerder.
  const locked = !admin && (needsAdmin(wf.graphJson) || needsAdmin(nodes))

  return (
    <div className="wf-editor">
      <div className="wf-toolbar">
        <input className="wf-name" value={wf.name} disabled={locked} onChange={e => { setWf({ ...wf, name: e.target.value }); setDirty(true) }} />
        {locked
          ? <span className="row muted" style={{ gap: 6 }} title="Deze werkstroom stuurt e-mail of roept een webhook aan"><AlertTriangle size={14} /> Alleen een beheerder kan deze werkstroom wijzigen</span>
          : dirty && <span className="muted">Niet opgeslagen</span>}
        <span className="spacer" />
        <label className="row muted" style={{ gap: 8 }} title="Als hij aan staat, start hij vanzelf bij zijn trigger">
          {wf.active ? 'Actief' : 'Uit'}
          <button className={`switch ${wf.active ? 'on' : ''}`} disabled={locked} onClick={() => save({ active: !wf.active })}><span /></button>
        </label>
        <button className="btn btn-danger btn-sm" onClick={remove}><Trash2 size={14} /></button>
        <button className="btn btn-sm" disabled={!hasTrigger} title={hasTrigger ? 'Nu één keer uitvoeren' : 'Voeg eerst een trigger toe'} onClick={() => setRunDialog(true)}><Play size={14} /> Uitvoeren</button>
        <button className="btn btn-primary btn-sm" disabled={locked} onClick={() => save()}><Save size={14} /> Opslaan</button>
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
                const d = edgePath(a.x + NODE, outY(a, e.branch), b.x, b.y + HANDLE_Y)
                const done = states[e.from] === 'ok' && states[e.to] !== undefined
                return (
                  <g key={i} onPointerDown={ev => { ev.stopPropagation(); setSelectedEdge(i); setSelected(null) }}>
                    <path d={d} className="wf-edge-hit" />
                    <path d={d} className={`wf-edge ${e.branch ? `branch-${e.branch}` : ''} ${selectedEdge === i ? 'selected' : ''} ${done ? 'active' : ''}`} />
                  </g>
                )
              })}
              {linkFrom && cursor && <path d={edgePath(linkFrom.x + NODE, outY(linkFrom, cursor.branch), cursor.x, cursor.y)} className="wf-edge drafting" />}
            </svg>

            {nodes.map(n => {
              const t = nodeType(n.type)
              const isTrigger = n.type.startsWith('trigger')
              const state = states[n.id]
              const startLink = (branch?: Branch) => (e: React.PointerEvent) => {
                e.stopPropagation()
                drag.current = { kind: 'link', from: n.id, branch }
                setCursor({ ...toCanvas(e.clientX, e.clientY), from: n.id, branch })
              }
              return (
                <div
                  key={n.id}
                  className={`wf-node ${isTrigger ? 'trigger' : ''} ${selected === n.id ? 'selected' : ''} ${state ? `state-${state.replace(' ', '-')}` : ''}`}
                  style={{ left: n.x, top: n.y, '--c': t.color } as React.CSSProperties}
                  onPointerDown={e => {
                    e.stopPropagation()
                    const p = toCanvas(e.clientX, e.clientY)
                    drag.current = { kind: 'node', id: n.id, offX: p.x - n.x, offY: p.y - n.y }
                    setSelected(n.id); setSelectedEdge(null); setPanel('blokken')
                  }}
                  onPointerUp={() => connect(n.id)}
                >
                  <div className="wf-node-box">
                    <t.icon size={34} />
                    {state && state !== 'run' && (
                      <span className={`wf-badge wf-badge-${state.replace(' ', '-')}`}>
                        {state === 'ok' ? <Check size={12} /> : state === 'fout' ? <X size={12} /> : state === 'wacht' ? <Clock size={11} /> : <AlertTriangle size={11} />}
                      </span>
                    )}
                  </div>
                  {!isTrigger && <span className="wf-handle in" />}
                  {n.type === 'logic.if' ? (
                    <>
                      <span className="wf-handle out branch-ja" style={{ top: BRANCH_Y.ja - 7 }} onPointerDown={startLink('ja')} title="Ja" />
                      <span className="wf-branch-label" style={{ top: BRANCH_Y.ja - 20 }}>ja</span>
                      <span className="wf-handle out branch-nee" style={{ top: BRANCH_Y.nee - 7 }} onPointerDown={startLink('nee')} title="Nee" />
                      <span className="wf-branch-label" style={{ top: BRANCH_Y.nee + 6 }}>nee</span>
                    </>
                  ) : (
                    <span className="wf-handle out" onPointerDown={startLink()} />
                  )}
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
          <SubTabs value={panel} onChange={setPanel} tabs={[
            { id: 'blokken', label: 'Blokken', icon: <Blocks size={14} /> },
            { id: 'uitvoeringen', label: 'Uitvoeringen', icon: <History size={14} />, count: runs.data?.length },
          ]} />

          {panel === 'uitvoeringen' && <RunList key={runs.data?.[0]?.id ?? 0} runs={runs.data ?? []} onShow={showRun} />}

          {panel === 'blokken' && sel && (
            <NodeSettings key={sel.id} node={sel} onChange={patch => updateNode(sel.id, patch)} onRemove={removeSelected} />
          )}

          {panel === 'blokken' && !sel && (
            <>
              <p className="muted" style={{ marginTop: 0, fontSize: 12 }}>
                Klik op een blok om het in te stellen. Sleep vanaf het bolletje rechts van een blok naar een ander blok om ze te verbinden.
                Klik op een lijn en druk op Delete om hem weg te halen.
              </p>
              <div className="search-input" style={{ marginBottom: 10 }}>
                <Search size={14} />
                <input style={{ width: '100%' }} placeholder="Zoek blok…" value={paletteQuery} onChange={e => setPaletteQuery(e.target.value)} />
              </div>
              {groups.map(g => {
                const items = nodeTypes.filter(t => t.group === g && (admin || !adminOnlyTypes.includes(t.type)) && t.label.toLowerCase().includes(paletteQuery.toLowerCase()))
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
            </>
          )}
        </aside>
      </div>

      {runDialog && <RunDialog nodes={nodes} onClose={() => setRunDialog(false)} onRun={execute} />}
    </div>
  )
}

function NodeSettings({ node, onChange, onRemove }: { node: WorkflowNode; onChange: (p: Partial<WorkflowNode>) => void; onRemove: () => void }) {
  const t = nodeType(node.type)
  const config = { ...defaultConfig(node.type), ...node.config }
  const setField = (key: string, value: string) => onChange({ config: { ...config, [key]: value } })
  const usesTemplates = (t.fields ?? []).some(f => f.type === 'text' || f.type === 'textarea')

  return (
    <>
      <div className="wf-panel-head">
        <span className="wf-mini" style={{ background: t.color }}><t.icon size={14} /></span>
        <strong>{t.label}</strong>
      </div>
      <p className="muted" style={{ marginTop: 0 }}>{t.description}</p>
      <div className="wf-fields">
        <Field label="Naam van dit blok"><input value={node.label} onChange={e => onChange({ label: e.target.value })} /></Field>
        {(t.fields ?? []).map(f => <ConfigField key={f.key} field={f} value={config[f.key] ?? ''} onChange={v => setField(f.key, v)} />)}
      </div>
      {usesTemplates && node.type !== 'logic.if' && (
        <details className="wf-vars">
          <summary>Gegevens invoegen</summary>
          <p className="muted">Zet een van deze tussen dubbele accolades, bijvoorbeeld <code>{'{{lead.name}}'}</code>.</p>
          <div className="chips">{variables.map(v => <code key={v} className="chip">{`{{${v}}}`}</code>)}</div>
        </details>
      )}
      {node.type === 'action.email' && <p className="muted" style={{ fontSize: 12 }}>Mail gaat pas echt de deur uit als er een mailserver is ingesteld in appsettings.json (zie README). Je bedrijfsnaam staat erin als afzender en antwoorden gaan naar het e-mailadres onder Instellingen. Er geldt een daglimiet per werkruimte.</p>}
      <button className="btn btn-danger btn-sm mt" onClick={onRemove}><Trash2 size={14} /> Blok verwijderen</button>
    </>
  )
}

function ConfigField({ field, value, onChange }: { field: NodeField; value: string; onChange: (v: string) => void }) {
  return (
    <Field label={field.label}>
      {field.type === 'select' ? (
        <select value={value} onChange={e => onChange(e.target.value)}>
          {field.options!.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
        </select>
      ) : field.type === 'textarea' ? (
        <textarea value={value} placeholder={field.placeholder} onChange={e => onChange(e.target.value)} />
      ) : (
        <input type={field.type === 'number' ? 'number' : 'text'} value={value} placeholder={field.placeholder} onChange={e => onChange(e.target.value)} />
      )}
      {field.help && <small className="muted">{field.help}</small>}
    </Field>
  )
}

function RunList({ runs, onShow }: { runs: WorkflowRun[]; onShow: (r: WorkflowRun) => void }) {
  const [open, setOpen] = useState<number | null>(runs[0]?.id ?? null)
  if (runs.length === 0) return <p className="muted" style={{ fontSize: 12 }}>Nog geen uitvoeringen. Klik op Uitvoeren, of zet de werkstroom aan zodat hij vanzelf start.</p>
  return (
    <div className="wf-runs">
      {runs.map(r => {
        const log = parseLog(r)
        const isOpen = open === r.id
        return (
          <div key={r.id} className="wf-run">
            <button className="wf-run-head" onClick={() => { setOpen(isOpen ? null : r.id); if (!isOpen) onShow(r) }}>
              {isOpen ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
              <span className="grow">
                <span className="truncate" style={{ display: 'block' }}>{r.trigger}</span>
                <span className="cell-sub">{dateTime(r.startedAt.endsWith('Z') ? r.startedAt : `${r.startedAt}Z`)}</span>
              </span>
              <span className={`badge badge-${runTone[r.status]}`}>{r.status}</span>
            </button>
            {isOpen && (
              <ol className="wf-run-log">
                {log.map((l, i) => (
                  <li key={i} className={`log-${l.status.replace(' ', '-')}`}>
                    <strong>{l.label || 'Werkstroom'}</strong>
                    <span>{l.message}</span>
                  </li>
                ))}
                {r.status === 'wachtend' && r.resumeAt && (
                  <li className="log-wacht"><strong>Wacht</strong><span>Gaat verder rond {dateTime(r.resumeAt.endsWith('Z') ? r.resumeAt : `${r.resumeAt}Z`)}</span></li>
                )}
              </ol>
            )}
          </div>
        )
      })}
    </div>
  )
}

function RunDialog({ nodes, onClose, onRun }: { nodes: WorkflowNode[]; onClose: () => void; onRun: (b: { leadId?: number; invoiceId?: number }) => void }) {
  const { data: leads } = useApi<Lead[]>('/leads')
  const { data: invoices } = useApi<Invoice[]>('/invoices')
  const [leadId, setLeadId] = useState(0)
  const [invoiceId, setInvoiceId] = useState(0)
  const wantsInvoice = nodes.some(n => n.type === 'trigger.paid' || (n.type === 'action.status' && n.config?.target === 'invoice'))

  return (
    <Modal title="Werkstroom uitvoeren" onClose={onClose} footer={<>
      <button className="btn" onClick={onClose}>Annuleren</button>
      <button className="btn btn-primary" onClick={() => onRun({ leadId: leadId || undefined, invoiceId: invoiceId || undefined })}><Play size={14} /> Nu uitvoeren</button>
    </>}>
      <p className="muted" style={{ marginTop: 0 }}>
        De werkstroom draait één keer echt: taken, statussen en facturen worden echt aangemaakt of gewijzigd.
        Kies met welke gegevens hij moet werken.
      </p>
      <div className="form-grid">
        <Field label="Met lead" full>
          <select value={leadId} onChange={e => setLeadId(Number(e.target.value))}>
            <option value={0}>Geen lead</option>
            {leads?.map(l => <option key={l.id} value={l.id}>{l.name}{l.company ? ` (${l.company})` : ''} · {euro(l.value)}</option>)}
          </select>
        </Field>
        {(wantsInvoice || invoiceId > 0) && (
          <Field label="Met factuur" full>
            <select value={invoiceId} onChange={e => setInvoiceId(Number(e.target.value))}>
              <option value={0}>Geen factuur</option>
              {invoices?.map(i => <option key={i.id} value={i.id}>{i.number ?? 'Concept'} · {i.customer?.name} · {euro(invoiceTotals(i.lines).total)}</option>)}
            </select>
          </Field>
        )}
      </div>
    </Modal>
  )
}
