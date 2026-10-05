import { useEffect, useState, type ReactNode } from 'react'
import { NavLink, useLocation, useNavigate } from 'react-router-dom'
import {
  LayoutDashboard, CalendarDays, Users, Target, Receipt, Workflow, Blocks, X, Search,
  ChevronsLeft, ChevronsRight, Moon, Sun, Plus, ShieldCheck, LogOut, KeyRound, Timer, FolderKanban, Settings, Square,
} from 'lucide-react'
import { clock, useTimer } from '../lib/timer'
import { useAuth } from '../lib/auth'
import { initials } from '../lib/format'
import { colorFor } from '../lib/status'
import { PasswordModal } from './PasswordModal'
import { useTabs } from '../lib/tabs'
import { useModules } from '../lib/modules'
import { ModuleIcon } from './Icon'
import { CommandPalette } from './CommandPalette'

const nav = [
  { to: '/', label: 'Dashboard', icon: LayoutDashboard, end: true },
  { to: '/uren', label: 'Uren', icon: Timer },
  { to: '/projecten', label: 'Projecten', icon: FolderKanban },
  { to: '/facturen', label: 'Facturen', icon: Receipt },
  { to: '/planning', label: 'Planning', icon: CalendarDays },
  { to: '/klanten', label: 'Klanten', icon: Users },
  { to: '/leads', label: 'Leads', icon: Target },
  { to: '/werkstromen', label: 'Werkstromen', icon: Workflow },
  { to: '/instellingen', label: 'Instellingen', icon: Settings },
]

const navLinks = (items: typeof nav) => items.map(item => (
  <NavLink key={item.to} to={item.to} end={item.end} className="nav-item" title={item.label}>
    <item.icon size={18} />
    <span>{item.label}</span>
  </NavLink>
))

function tabIcon(path: string) {
  path = path.split('?')[0]!
  if (path === '/') return <LayoutDashboard size={14} />
  const item = nav.find(n => n.to !== '/' && path.startsWith(n.to))
  if (item) return <item.icon size={14} />
  return <Blocks size={14} />
}

export function Layout({ renderTab }: { renderTab: (path: string, active: boolean) => ReactNode }) {
  const { tabs, close, closeOthers } = useTabs()
  const { modules } = useModules()
  const location = useLocation()
  const navigate = useNavigate()
  const current = location.pathname + location.search
  const [collapsed, setCollapsed] = useState(() => localStorage.getItem('potel.collapsed') === '1')
  const [theme, setTheme] = useState(() => localStorage.getItem('potel.theme') ?? 'light')
  const [paletteOpen, setPaletteOpen] = useState(false)
  const [userMenu, setUserMenu] = useState(false)
  const [changingPassword, setChangingPassword] = useState(false)
  const { user, logout } = useAuth()

  useEffect(() => {
    document.documentElement.dataset.theme = theme
    try { localStorage.setItem('potel.theme', theme) } catch { /* negeren */ }
  }, [theme])

  useEffect(() => {
    try { localStorage.setItem('potel.collapsed', collapsed ? '1' : '0') } catch { /* negeren */ }
  }, [collapsed])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault()
        setPaletteOpen(o => !o)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  return (
    <div className={`app ${collapsed ? 'collapsed' : ''}`}>
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-logo">
            <svg viewBox="0 0 32 32" width="22" height="22"><circle cx="9" cy="16" r="3.4" fill="currentColor" /><circle cx="23" cy="9" r="3.4" fill="currentColor" /><circle cx="23" cy="23" r="3.4" fill="currentColor" /><path d="M12 16h3c3 0 3-7 5-7M12 16h3c3 0 3 7 5 7" stroke="currentColor" strokeWidth="2.2" fill="none" /></svg>
          </div>
          <span className="brand-name">Potel<small>freelance dev</small></span>
        </div>

        <button className="search-trigger" onClick={() => setPaletteOpen(true)}>
          <Search size={16} />
          <span>Zoeken…</span>
          <kbd>Ctrl K</kbd>
        </button>

        <nav className="nav">
          <div className="nav-label">Werk</div>
          {navLinks(nav.slice(0, 5))}
          <div className="nav-label">Klanten en sales</div>
          {navLinks(nav.slice(5, 7))}
          <div className="nav-label">Systeem</div>
          {navLinks(nav.slice(7))}

          {user.role === 'beheerder' && (
            <NavLink to="/gebruikers" className="nav-item" title="Gebruikers">
              <ShieldCheck size={18} />
              <span>Gebruikers</span>
            </NavLink>
          )}

          <div className="nav-label nav-label-row">
            <span>Eigen modules</span>
            <button className="icon-btn tiny" title="Module beheren" onClick={() => navigate('/modules')}><Plus size={14} /></button>
          </div>
          {modules.map(m => (
            <NavLink key={m.id} to={`/modules/${m.id}`} className="nav-item" title={m.name}>
              <span className="nav-dot" style={{ background: m.color }}><ModuleIcon name={m.icon} size={12} /></span>
              <span>{m.name}</span>
            </NavLink>
          ))}
          <NavLink to="/modules" end className="nav-item muted" title="Modules beheren">
            <Blocks size={18} />
            <span>Modules beheren</span>
          </NavLink>
        </nav>

        <div className="sidebar-foot">
          <button className="nav-item" onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')} title="Thema wisselen">
            {theme === 'dark' ? <Sun size={18} /> : <Moon size={18} />}
            <span>{theme === 'dark' ? 'Licht thema' : 'Donker thema'}</span>
          </button>
          <button className="nav-item" onClick={() => setCollapsed(c => !c)} title="Menu in- of uitklappen">
            {collapsed ? <ChevronsRight size={18} /> : <ChevronsLeft size={18} />}
            <span>Inklappen</span>
          </button>
          <button className="user-chip" onClick={() => setUserMenu(o => !o)} title={user.name}>
            <div className="avatar" style={{ background: colorFor(user.name) }}>{initials(user.name)}</div>
            <div className="user-chip-text"><strong className="truncate">{user.name}</strong><span>{user.role}</span></div>
          </button>
          {userMenu && (
            <div className="user-menu" onMouseLeave={() => setUserMenu(false)}>
              <div className="cell-sub" style={{ padding: '6px 10px' }}>{user.email}</div>
              <button className="nav-item" onClick={() => { setUserMenu(false); setChangingPassword(true) }}><KeyRound size={16} /><span>Wachtwoord wijzigen</span></button>
              <button className="nav-item" onClick={() => logout()}><LogOut size={16} /><span>Uitloggen</span></button>
            </div>
          )}
        </div>
      </aside>

      <div className="main">
        <div className="tabbar" role="tablist">
          {tabs.map(t => {
            const active = t.path === current
            return (
              <div
                key={t.path}
                role="tab"
                aria-selected={active}
                className={`tab ${active ? 'active' : ''}`}
                onClick={() => navigate(t.path)}
                onAuxClick={e => e.button === 1 && close(t.path)}
                onDoubleClick={() => closeOthers(t.path)}
                title={`${t.title} (dubbelklik: andere tabbladen sluiten)`}
              >
                {tabIcon(t.path)}
                <span className="tab-title">{t.title}</span>
                <button className="tab-close" onClick={e => { e.stopPropagation(); close(t.path) }} aria-label="Tabblad sluiten">
                  <X size={12} />
                </button>
              </div>
            )
          })}
          <TimerChip />
        </div>
        {(tabs.some(t => t.path === current) ? tabs : [...tabs, { path: current, title: '' }]).map(t => (
          <main key={t.path} className="content" hidden={t.path !== current}>
            {renderTab(t.path, t.path === current)}
          </main>
        ))}
      </div>

      {paletteOpen && <CommandPalette onClose={() => setPaletteOpen(false)} />}
      {changingPassword && <PasswordModal onClose={() => setChangingPassword(false)} />}
    </div>
  )
}

// De lopende timer, altijd zichtbaar rechts in de tabbalk.
function TimerChip() {
  const { running, elapsed, stop } = useTimer()
  const navigate = useNavigate()
  if (!running) return null
  return (
    <div className="timer-chip" style={{ '--accent': running.projectColor } as React.CSSProperties}>
      <button className="timer-chip-main" onClick={() => navigate('/uren')} title={running.description || running.projectName}>
        <span className="timer-pulse" />
        <span className="truncate">{running.projectName}</span>
        <strong>{clock(elapsed)}</strong>
      </button>
      <button className="timer-chip-stop" onClick={stop} title="Stoppen en boeken"><Square size={11} fill="currentColor" /></button>
    </div>
  )
}
