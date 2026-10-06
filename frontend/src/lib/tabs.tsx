import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'

export interface Tab {
  path: string
  title: string
}

interface TabsContextValue {
  tabs: Tab[]
  close: (path: string) => void
  closeOthers: (path: string) => void
  setTitle: (path: string, title: string) => void
  retarget: (from: string, to: string) => void
}

const TabsContext = createContext<TabsContextValue | null>(null)
const STORAGE_KEY = 'potel.tabs'
const publicPaths = ['/inloggen', '/login', '/aanmelden']

export function defaultTitle(fullPath: string) {
  const path = fullPath.split('?')[0]!
  if (path === '/') return 'Dashboard'
  if (path === '/uren') return 'Uren'
  if (path === '/projecten') return 'Projecten'
  if (path.startsWith('/projecten/')) return 'Project'
  if (path === '/instellingen') return 'Instellingen'
  if (path === '/platform') return 'Platform'
  if (path.startsWith('/planning')) return 'Planning'
  if (path === '/klanten') return 'Klanten'
  if (path.startsWith('/klanten/')) return 'Klant'
  if (path === '/leads') return 'Leads'
  if (path === '/facturen') return 'Facturen'
  if (path === '/facturen/nieuw') return 'Nieuwe factuur'
  if (path.startsWith('/facturen/')) return 'Factuur'
  if (path === '/werkstromen') return 'Werkstromen'
  if (path.startsWith('/werkstromen/')) return 'Werkstroom'
  if (path === '/modules') return 'Eigen modules'
  if (path.startsWith('/modules/')) return 'Module'
  if (path === '/gebruikers') return 'Gebruikers'
  return 'Pagina'
}

function loadTabs(): Tab[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (raw) return JSON.parse(raw)
  } catch { /* opslag niet beschikbaar */ }
  return [{ path: '/', title: 'Dashboard' }]
}

// Elke pagina die je opent komt als tabblad bovenin, net als in een browser.
export function TabsProvider({ children }: { children: ReactNode }) {
  const [tabs, setTabs] = useState<Tab[]>(loadTabs)
  const location = useLocation()
  const navigate = useNavigate()
  const current = location.pathname + location.search

  // Na inloggen of aanmelden kom je op het dashboard, niet op een tabblad "Inloggen".
  const isPublic = publicPaths.includes(location.pathname)
  useEffect(() => {
    if (isPublic) { navigate('/', { replace: true }); return }
    setTabs(prev => (prev.some(t => t.path === current) ? prev : [...prev, { path: current, title: defaultTitle(current) }]))
  }, [current, isPublic, navigate])

  useEffect(() => {
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify(tabs)) } catch { /* negeren */ }
  }, [tabs])

  const close = useCallback((path: string) => {
    setTabs(prev => {
      const idx = prev.findIndex(t => t.path === path)
      const next = prev.filter(t => t.path !== path)
      if (next.length === 0) next.push({ path: '/', title: 'Dashboard' })
      if (path === current) navigate(next[Math.max(0, idx - 1)]!.path)
      return next
    })
  }, [current, navigate])

  const closeOthers = useCallback((path: string) => {
    setTabs(prev => prev.filter(t => t.path === path))
    if (path !== current) navigate(path)
  }, [current, navigate])

  const setTitle = useCallback((path: string, title: string) => {
    // De pagina kan zijn titel zetten voordat het tabblad is toegevoegd.
    setTabs(prev => prev.some(t => t.path === path)
      ? prev.map(t => (t.path === path && t.title !== title ? { ...t, title } : t))
      : [...prev, { path, title }])
  }, [])

  // Vervangt een tabblad door een ander pad, bijvoorbeeld "Nieuwe factuur" na opslaan.
  const retarget = useCallback((from: string, to: string) => {
    setTabs(prev => {
      const without = prev.filter(t => t.path !== to)
      return without.map(t => (t.path === from ? { path: to, title: defaultTitle(to) } : t))
    })
    navigate(to, { replace: true })
  }, [navigate])

  return <TabsContext.Provider value={{ tabs, close, closeOthers, setTitle, retarget }}>{children}</TabsContext.Provider>
}

export function useTabs() {
  const ctx = useContext(TabsContext)
  if (!ctx) throw new Error('useTabs buiten TabsProvider')
  return ctx
}

// Geeft het huidige tabblad een eigen naam, bijvoorbeeld de naam van de klant.
export function useTabTitle(title: string | null | undefined) {
  const { setTitle } = useTabs()
  const { pathname, search } = useLocation()
  useEffect(() => {
    if (title) setTitle(pathname + search, title)
  }, [title, pathname, search, setTitle])
}

// Of het tabblad waarin een pagina staat op dit moment zichtbaar is.
export const ActiveTabContext = createContext(true)
export const useTabActive = () => useContext(ActiveTabContext)
