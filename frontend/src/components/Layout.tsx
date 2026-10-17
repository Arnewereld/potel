import { Fragment, useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { NavLink, useLocation, useNavigate } from 'react-router-dom'
import {
  LayoutDashboard, CalendarDays, Users, Target, Receipt, Workflow, Blocks, X, Search, FileSignature,
  ChevronsLeft, ChevronsRight, Moon, Sun, Plus, ShieldCheck, LogOut, KeyRound, Timer, FolderKanban, Settings, Square, Pause, Crown, Wallet, Landmark, CalendarRange,
  ArrowLeftRight, Car, Package, Coffee, ChartNoAxesCombined, Bell, Repeat, Building2, BookOpenCheck,
} from 'lucide-react'
import { clock, useTimer } from '../lib/timer'
import { mmss } from '../lib/focus'
import { useWorkspace } from '../lib/workspace'
import { OnboardingWizard } from './OnboardingWizard'
import { useAuth } from '../lib/auth'
import { isAccountant, accountantSettingsTabIds, switchWorkspace, type WorkspaceChoice } from '../lib/accountant'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import { initials } from '../lib/format'
import { colorFor } from '../lib/status'
import { PasswordModal } from './PasswordModal'
import { useTabs } from '../lib/tabs'
import { useModules } from '../lib/modules'
import { ModuleIcon } from './Icon'
import { CommandPalette, type PaletteLink } from './CommandPalette'
import { VerifyEmailBanner } from './VerifyEmailBanner'
import { UpdateBanner } from './UpdateBanner'
import { FocusView } from './FocusView'
import { NotificationBell } from './Notifications'

// Het menu, per groep in de volgorde waarin het in de zijbalk staat.
// accountant: ook voor een boekhouder, die verder niets ziet. Zijn startpagina heet Overzicht in plaats van Dashboard.
interface NavItem { to: string; label: string; icon: typeof LayoutDashboard; group: string; end?: boolean; admin?: boolean; accountant?: boolean }
const nav: NavItem[] = [
  { to: '/', label: 'Dashboard', icon: LayoutDashboard, end: true, group: 'Werk', accountant: true },
  // Alleen voor beheerders: wat je echt verdient, net als de bank.
  { to: '/inzichten', label: 'Inzichten', icon: ChartNoAxesCombined, group: 'Werk', admin: true },
  { to: '/uren', label: 'Uren', icon: Timer, group: 'Werk' },
  { to: '/projecten', label: 'Projecten', icon: FolderKanban, group: 'Werk' },
  { to: '/facturen', label: 'Facturen', icon: Receipt, group: 'Werk', accountant: true },
  { to: '/kosten', label: 'Kosten', icon: Wallet, group: 'Werk', accountant: true },
  { to: '/ritten', label: 'Ritten', icon: Car, group: 'Werk', accountant: true },
  // Alleen voor beheerders: een bankafschrift laat al je geldzaken zien.
  { to: '/bank', label: 'Bank', icon: ArrowLeftRight, group: 'Werk', admin: true, accountant: true },
  { to: '/planning', label: 'Planning', icon: CalendarDays, group: 'Werk' },
  { to: '/klanten', label: 'Klanten', icon: Users, group: 'Klanten en sales', accountant: true },
  { to: '/leads', label: 'Leads', icon: Target, group: 'Klanten en sales' },
  { to: '/offertes', label: 'Offertes', icon: FileSignature, group: 'Klanten en sales', accountant: true },
  { to: '/btw-aangifte', label: 'Btw-aangifte', icon: Landmark, group: 'Belasting', accountant: true },
  { to: '/jaaroverzicht', label: 'Jaaroverzicht', icon: CalendarRange, group: 'Belasting', accountant: true },
  { to: '/investeringen', label: 'Investeringen', icon: Package, group: 'Belasting', accountant: true },
  { to: '/werkstromen', label: 'Werkstromen', icon: Workflow, group: 'Systeem' },
  { to: '/instellingen', label: 'Instellingen', icon: Settings, group: 'Systeem', accountant: true },
]
const navGroups = [...new Set(nav.map(n => n.group))]

// Wat iemand in het menu ziet. Een boekhouder alleen de administratie, een medewerker niet wat over geld gaat.
const visibleFor = (role: string) => (n: NavItem) => role === 'boekhouder' ? n.accountant === true : !n.admin || role === 'beheerder'
const forRole = (role: string) => nav.filter(visibleFor(role)).map(n => role === 'boekhouder' && n.to === '/' ? { ...n, label: 'Overzicht', icon: BookOpenCheck } : n)

// De tabbladen van Instellingen, om er vanuit het zoekvenster meteen heen te gaan.
const settingsTabs = [
  { id: 'bedrijf', label: 'Bedrijfsgegevens' }, { id: 'huisstijl', label: 'Huisstijl' }, { id: 'email', label: 'E-mail en herinneringen' },
  { id: 'meldingen', label: 'Meldingen instellen' }, { id: 'belastingpot', label: 'Belastingpot' }, { id: 'ideal', label: 'iDEAL' },
  { id: 'github', label: 'GitHub' }, { id: 'telefoon', label: 'Op je telefoon' }, { id: 'agenda', label: 'Agendalink' },
  { id: 'abonnement', label: 'Abonnement' }, { id: 'account', label: 'Account' },
]

// Een veld waarin je typt: daar is een / gewoon een teken en opent hij het zoekvenster niet.
const isTyping = (target: EventTarget | null) =>
  target instanceof HTMLElement && (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName))

const navLinks = (items: NavItem[]) => items.map(item => (
  <NavLink key={item.to} to={item.to} end={item.end} className="nav-item" title={item.label}>
    <item.icon size={18} />
    <span>{item.label}</span>
  </NavLink>
))

function tabIcon(path: string) {
  path = path.split('?')[0]!
  if (path === '/') return <LayoutDashboard size={14} />
  if (path === '/meldingen') return <Bell size={14} />
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
  const { workspace, reload: reloadWorkspace } = useWorkspace()
  const accountant = isAccountant(user)
  const menu = useMemo(() => forRole(user.role), [user.role])

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
      } else if (e.key === '/' && !e.ctrlKey && !e.metaKey && !e.altKey && !e.defaultPrevented && !e.isComposing
        && !isTyping(e.target) && !document.querySelector('.modal-backdrop, [aria-modal="true"]')) {
        // Ook met / als je niet in een veld typt en er geen venster of focusweergave openstaat.
        e.preventDefault()
        setPaletteOpen(true)
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  const toggleTheme = useCallback(() => setTheme(t => (t === 'dark' ? 'light' : 'dark')), [])
  const admin = user.role === 'beheerder'
  const platformAdmin = workspace?.platformAdmin ?? false

  // Alle pagina's voor het zoekvenster, met dezelfde regels als het menu: wat alleen voor beheerders is, zie je alleen als beheerder.
  const pages = useMemo<PaletteLink[]>(() => {
    const list: PaletteLink[] = menu.map(n => ({ to: n.to, label: n.label, icon: <n.icon size={16} />, keywords: [n.group] }))
    list.push({ to: '/meldingen', label: 'Meldingen', icon: <Bell size={16} />, keywords: ['bel', 'notificaties'] })
    if (accountant) {
      // Een boekhouder: geen terugkerende facturen, modules of gebruikers, en van Instellingen alleen zijn eigen account.
      list.push({ to: '/kosten?tab=vast', label: 'Vaste kosten', hint: 'Kosten', icon: <Repeat size={16} /> })
      const ids = accountantSettingsTabIds(user.canFileVat === true)
      for (const t of settingsTabs.filter(t => ids.includes(t.id))) list.push({ to: `/instellingen?tab=${t.id}`, label: t.label, hint: 'Instellingen', icon: <Settings size={16} />, keywords: ['instellingen'] })
      return list
    }
    if (admin) list.push({ to: '/inzichten?tab=cashflow', label: 'Cashflow', hint: 'Inzichten', icon: <ChartNoAxesCombined size={16} /> })
    list.push({ to: '/facturen?tab=terugkerend', label: 'Terugkerende facturen', hint: 'Facturen', icon: <Repeat size={16} /> })
    list.push({ to: '/kosten?tab=vast', label: 'Vaste kosten', hint: 'Kosten', icon: <Repeat size={16} /> })
    if (admin) list.push({ to: '/gebruikers', label: 'Gebruikers', icon: <ShieldCheck size={16} />, keywords: ['medewerkers', 'team'] })
    if (platformAdmin) list.push({ to: '/platform', label: 'Platform', icon: <Crown size={16} /> })
    for (const m of modules) list.push({ to: `/modules/${m.id}`, label: m.name, hint: 'Eigen module', icon: <ModuleIcon name={m.icon} size={16} /> })
    list.push({ to: '/modules', label: 'Modules beheren', hint: 'Eigen modules', icon: <Blocks size={16} /> })
    for (const t of settingsTabs) list.push({ to: `/instellingen?tab=${t.id}`, label: t.label, hint: 'Instellingen', icon: <Settings size={16} />, keywords: ['instellingen'] })
    return list
  }, [admin, accountant, user.canFileVat, menu, platformAdmin, modules])

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
          <kbd title="Of druk op / als je niet in een veld typt">Ctrl K</kbd>
        </button>

        <nav className="nav">
          {navGroups.map(group => (
            <Fragment key={group}>
              {menu.some(n => n.group === group) && <div className="nav-label">{group}</div>}
              {navLinks(menu.filter(n => n.group === group))}
            </Fragment>
          ))}

          {workspace?.platformAdmin && (
            <NavLink to="/platform" className="nav-item" title="Platform">
              <Crown size={18} />
              <span>Platform</span>
            </NavLink>
          )}
          {user.role === 'beheerder' && (
            <NavLink to="/gebruikers" className="nav-item" title="Gebruikers">
              <ShieldCheck size={18} />
              <span>Gebruikers</span>
            </NavLink>
          )}

          {!accountant && <>
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
          </>}
        </nav>

        <div className="sidebar-foot">
          {!accountant && workspace && workspace.plan !== 'proef' && workspace.readOnly && (
            <button className="trial-chip urgent" onClick={() => navigate('/instellingen?tab=abonnement')} title={workspace.readOnly}>
              <span className="trial-days">!</span>
              <span>Alleen-lezen<small>Kies of betaal je abonnement</small></span>
            </button>
          )}
          {!accountant && workspace?.plan === 'proef' && (
            <button className={`trial-chip ${(workspace.trialDaysLeft ?? 0) <= 5 ? 'urgent' : ''}`} onClick={() => navigate('/instellingen?tab=abonnement')} title="Abonnement kiezen">
              <span className="trial-days">{workspace.trialDaysLeft}</span>
              <span>{workspace.trialDaysLeft === 1 ? 'dag' : 'dagen'} proef over<small>Kies een abonnement</small></span>
            </button>
          )}
          <button className="nav-item" onClick={toggleTheme} title="Thema wisselen">
            {theme === 'dark' ? <Sun size={18} /> : <Moon size={18} />}
            <span>{theme === 'dark' ? 'Licht thema' : 'Donker thema'}</span>
          </button>
          <button className="nav-item" onClick={() => setCollapsed(c => !c)} title="Menu in- of uitklappen">
            {collapsed ? <ChevronsRight size={18} /> : <ChevronsLeft size={18} />}
            <span>Inklappen</span>
          </button>
          <button className="user-chip" onClick={() => setUserMenu(o => !o)} title={user.name}>
            <div className="avatar" style={{ background: colorFor(user.name) }}>{initials(user.name)}</div>
            <div className="user-chip-text"><strong className="truncate">{user.name}</strong><span className="truncate">{workspace?.name ?? user.role}</span></div>
          </button>
          {userMenu && (
            <div className="user-menu" onMouseLeave={() => setUserMenu(false)}>
              <div className="cell-sub" style={{ padding: '6px 10px' }}>{user.email}</div>
              {accountant && <WorkspaceSwitcher onPick={() => setUserMenu(false)} />}
              <button className="nav-item" onClick={() => { setUserMenu(false); setChangingPassword(true) }}><KeyRound size={16} /><span>Wachtwoord wijzigen</span></button>
              <button className="nav-item" onClick={() => { setUserMenu(false); navigate('/instellingen?tab=account') }}>
                <ShieldCheck size={16} /><span>Inloggen in twee stappen{user.twoFactor ? ' (aan)' : ''}</span>
              </button>
              {/* De timer staat op de server en loopt na uitloggen gewoon door. */}
              <button className="nav-item" onClick={() => logout()}><LogOut size={16} /><span>Uitloggen</span></button>
            </div>
          )}
        </div>
      </aside>

      <div className="main">
        <UpdateBanner />
        {!user.emailVerified && <VerifyEmailBanner email={user.email} />}
        <div className="topbar">
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
        </div>
        {/* Buiten de tabbladen, zodat de timer en de bel nooit wegschuiven als er veel tabbladen open zijn. */}
        <div className="topbar-actions">
          {!accountant && <TimerChip />}
          <NotificationBell />
        </div>
        </div>
        {(tabs.some(t => t.path === current) ? tabs : [...tabs, { path: current, title: '' }]).map(t => (
          <main key={t.path} className="content" hidden={t.path !== current}>
            {renderTab(t.path, t.path === current)}
          </main>
        ))}
      </div>

      <FocusView />
      {paletteOpen && <CommandPalette onClose={() => setPaletteOpen(false)} theme={theme} onToggleTheme={toggleTheme} pages={pages} />}
      {changingPassword && <PasswordModal onClose={() => setChangingPassword(false)} />}
      {workspace && !workspace.onboarded && user.role === 'beheerder' && <OnboardingWizard onDone={reloadWorkspace} />}
    </div>
  )
}

// Wisselen naar een andere administratie, in het menu onder je naam.
function WorkspaceSwitcher({ onPick }: { onPick: () => void }) {
  const { data } = useApi<WorkspaceChoice[]>('/auth/workspaces')
  const toast = useToast()
  const others = (data ?? []).filter(w => !w.current)
  if (!others.length) return null
  return (
    <>
      <div className="cell-sub" style={{ padding: '6px 10px 2px' }}>Wisselen naar</div>
      {others.map(w => (
        <button key={w.workspaceId} className="nav-item" onClick={() => {
          onPick()
          switchWorkspace(w.workspaceId).catch(e => toast((e as Error).message, 'error'))
        }}>
          <Building2 size={16} /><span className="truncate">{w.name}</span>
        </button>
      ))}
    </>
  )
}

// De lopende timer, altijd zichtbaar rechts in de tabbalk, op elk apparaat dezelfde.
// In focusmodus staat de tijd tot het eind van het blok of de pauze erin, en opent een klik de focusweergave.
function TimerChip() {
  const { running, elapsed, paused, stopping, stop, focus, openFocus } = useTimer()
  const navigate = useNavigate()
  if (!running) return null
  return (
    <div className={`timer-chip ${paused ? 'paused' : ''}`} style={{ '--accent': running.projectColor } as React.CSSProperties}>
      <button className="timer-chip-main" onClick={() => focus ? openFocus() : navigate('/uren')}
        title={focus ? 'Focusweergave openen' : running.description || running.projectName}>
        {focus
          ? focus.phase === 'focus' && !paused ? <Target size={13} className="timer-chip-paused" /> : <Coffee size={13} className="timer-chip-paused" />
          : paused ? <Pause size={12} className="timer-chip-paused" /> : <span className="timer-pulse" />}
        <span className="truncate">{focus && focus.phase !== 'focus' ? (focus.phase === 'ready' ? 'Pauze voorbij' : 'Pauze') : running.projectName}</span>
        <strong>{focus ? mmss(focus.remaining) : clock(elapsed)}</strong>
      </button>
      <button className="timer-chip-stop" disabled={stopping} onClick={() => void stop()} title="Stoppen en boeken" aria-label="Timer stoppen en boeken"><Square size={11} fill="currentColor" /></button>
    </div>
  )
}
