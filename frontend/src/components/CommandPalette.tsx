import { useEffect, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Search, Users, Target, Receipt, CornerDownLeft, Plus, LayoutDashboard, CalendarDays, Workflow, Timer, FolderKanban, Settings } from 'lucide-react'
import { api } from '../lib/api'

interface Hit { type: string; id: number; title: string; subtitle?: string | null }
interface Item { key: string; label: string; hint?: string; icon: React.ReactNode; to: string }

const quick: Item[] = [
  { key: 'q-dash', label: 'Dashboard', icon: <LayoutDashboard size={16} />, to: '/' },
  { key: 'q-uren', label: 'Uren', icon: <Timer size={16} />, to: '/uren' },
  { key: 'q-proj', label: 'Projecten', icon: <FolderKanban size={16} />, to: '/projecten' },
  { key: 'q-plan', label: 'Planning', icon: <CalendarDays size={16} />, to: '/planning' },
  { key: 'q-inv', label: 'Nieuwe factuur', icon: <Plus size={16} />, to: '/facturen/nieuw' },
  { key: 'q-cus', label: 'Klanten', icon: <Users size={16} />, to: '/klanten' },
  { key: 'q-lead', label: 'Leads', icon: <Target size={16} />, to: '/leads' },
  { key: 'q-wf', label: 'Werkstromen', icon: <Workflow size={16} />, to: '/werkstromen' },
  { key: 'q-set', label: 'Instellingen', icon: <Settings size={16} />, to: '/instellingen' },
]

const typeInfo: Record<string, { icon: React.ReactNode; path: (id: number) => string }> = {
  project: { icon: <FolderKanban size={16} />, path: id => `/projecten/${id}` },
  klant: { icon: <Users size={16} />, path: id => `/klanten/${id}` },
  lead: { icon: <Target size={16} />, path: () => '/leads' },
  factuur: { icon: <Receipt size={16} />, path: id => `/facturen/${id}` },
}

export function CommandPalette({ onClose }: { onClose: () => void }) {
  const [q, setQ] = useState('')
  const [hits, setHits] = useState<Hit[]>([])
  const [index, setIndex] = useState(0)
  const navigate = useNavigate()

  useEffect(() => {
    if (q.trim().length < 2) { setHits([]); return }
    const t = setTimeout(() => {
      api.get<Hit[]>(`/search?q=${encodeURIComponent(q)}`).then(setHits).catch(() => setHits([]))
    }, 150)
    return () => clearTimeout(t)
  }, [q])

  const items = useMemo<Item[]>(() => {
    const found = hits.map(h => ({
      key: `${h.type}-${h.id}`, label: h.title, hint: `${h.type}${h.subtitle ? ` · ${h.subtitle}` : ''}`,
      icon: typeInfo[h.type]?.icon, to: typeInfo[h.type]?.path(h.id) ?? '/',
    }))
    const filteredQuick = quick.filter(i => i.label.toLowerCase().includes(q.toLowerCase()))
    return [...found, ...filteredQuick]
  }, [hits, q])

  useEffect(() => setIndex(0), [items.length])

  const go = (item?: Item) => {
    if (!item) return
    navigate(item.to)
    onClose()
  }

  return (
    <div className="modal-backdrop palette-backdrop" onMouseDown={onClose}>
      <div className="palette" onMouseDown={e => e.stopPropagation()}>
        <div className="palette-input">
          <Search size={18} />
          <input
            autoFocus
            placeholder="Zoek projecten, klanten, leads, facturen of ga naar…"
            value={q}
            onChange={e => setQ(e.target.value)}
            onKeyDown={e => {
              if (e.key === 'Escape') onClose()
              if (e.key === 'ArrowDown') { e.preventDefault(); setIndex(i => Math.min(items.length - 1, i + 1)) }
              if (e.key === 'ArrowUp') { e.preventDefault(); setIndex(i => Math.max(0, i - 1)) }
              if (e.key === 'Enter') go(items[index])
            }}
          />
        </div>
        <div className="palette-list">
          {items.length === 0 && <div className="palette-empty">Niets gevonden</div>}
          {items.map((item, i) => (
            <button key={item.key} className={`palette-item ${i === index ? 'active' : ''}`} onMouseEnter={() => setIndex(i)} onClick={() => go(item)}>
              <span className="palette-icon">{item.icon}</span>
              <span className="palette-label">{item.label}</span>
              {item.hint && <span className="palette-hint">{item.hint}</span>}
              {i === index && <CornerDownLeft size={14} className="palette-enter" />}
            </button>
          ))}
        </div>
      </div>
    </div>
  )
}
