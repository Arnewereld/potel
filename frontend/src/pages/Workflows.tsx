import { useNavigate } from 'react-router-dom'
import { Workflow as WorkflowIcon, Plus } from 'lucide-react'
import { useApi } from '../lib/useApi'
import { api } from '../lib/api'
import { useAuth } from '../lib/auth'
import { useToast } from '../lib/toast'
import type { Workflow, WorkflowNode } from '../lib/types'
import { relative } from '../lib/format'
import { needsAdmin, nodeType } from '../lib/nodes'
import { Empty, ErrorBox, Loading, PageHeader } from '../components/ui'

export function WorkflowsPage() {
  const { data, error, loading, reload, setData } = useApi<Workflow[]>('/workflows')
  const navigate = useNavigate()
  const { user } = useAuth()
  const toast = useToast()
  const admin = user.role === 'beheerder'

  const create = async () => {
    const graph = { nodes: [{ id: 'n1', type: 'trigger.manual', label: 'Handmatig starten', x: 120, y: 160 }], edges: [] }
    const w = await api.post<Workflow>('/workflows', { name: 'Nieuwe werkstroom', graphJson: JSON.stringify(graph) })
    navigate(`/werkstromen/${w.id}`)
  }

  const toggle = async (w: Workflow) => {
    setData(prev => prev?.map(x => (x.id === w.id ? { ...x, active: !w.active } : x)) ?? null)
    try {
      await api.put(`/workflows/${w.id}`, { ...w, active: !w.active })
    } catch (e) {
      setData(prev => prev?.map(x => (x.id === w.id ? { ...x, active: w.active } : x)) ?? null)
      toast((e as Error).message, 'error')
    }
  }

  return (
    <>
      <PageHeader
        icon={<WorkflowIcon size={20} />}
        title="Werkstromen"
        subtitle="Bouw je eigen processen door blokken aan elkaar te koppelen."
        actions={<button className="btn btn-primary" onClick={create}><Plus size={16} /> Nieuwe werkstroom</button>}
      />
      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && <Loading />}
      {data && data.length === 0 && (
        <div className="card"><Empty icon={<WorkflowIcon size={24} />} title="Nog geen werkstromen" text="Begin met een lege werkstroom." action={<button className="btn btn-primary" onClick={create}><Plus size={16} /> Maak werkstroom</button>} /></div>
      )}
      <div className="grid grid-3">
        {data?.map(w => {
          let nodes: WorkflowNode[] = []
          try { nodes = JSON.parse(w.graphJson).nodes ?? [] } catch { /* lege grafiek */ }
          const locked = !admin && needsAdmin(nodes)
          return (
            <div key={w.id} className="card wf-card" onClick={() => navigate(`/werkstromen/${w.id}`)}>
              <div className="wf-preview">
                {nodes.slice(0, 5).map((n, i) => {
                  const t = nodeType(n.type)
                  return (
                    <div key={n.id} className="row" style={{ gap: 0 }}>
                      {i > 0 && <span className="wf-link" />}
                      <span className="wf-mini" style={{ background: t.color }} title={n.label}><t.icon size={14} /></span>
                    </div>
                  )
                })}
                {nodes.length > 5 && <span className="muted" style={{ marginLeft: 6 }}>+{nodes.length - 5}</span>}
              </div>
              <div className="card-body row between">
                <div>
                  <strong>{w.name}</strong>
                  <div className="cell-sub">{nodes.length} blokken · bijgewerkt {relative(w.updatedAt)}</div>
                </div>
                <button className={`switch ${w.active ? 'on' : ''}`} disabled={locked} onClick={e => { e.stopPropagation(); toggle(w) }} aria-label="Actief"
                  title={locked ? 'Deze werkstroom stuurt e-mail of roept een webhook aan; alleen een beheerder zet hem aan of uit' : w.active ? 'Actief' : 'Uit'}><span /></button>
              </div>
            </div>
          )
        })}
      </div>
    </>
  )
}
