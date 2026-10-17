import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import { Bell, CheckCheck, Settings } from 'lucide-react'
import { api } from '../lib/api'
import { useToast } from '../lib/toast'
import {
  byDay, clockTime, kindInfo, NotificationsContext, useNotifications,
  type AppNotification, type NotificationPage, type NotificationSummary, type NotificationsValue,
} from '../lib/notifications'

// Rustig bij de server kijken: elke minuut als Potel in beeld is, en meteen als je terugkomt.
const POLL_MS = 60_000
const CHANNEL = 'potel-meldingen'

export function NotificationsProvider({ children }: { children: ReactNode }) {
  const [unread, setUnread] = useState(0)
  const [version, setVersion] = useState(0)
  const latest = useRef<number | null>(null)
  const busy = useRef(false)
  const lastCheck = useRef(0)
  const channel = useRef<BroadcastChannel | null>(null)
  const navigate = useNavigate()
  const toast = useToast()

  // Andere tabbladen met Potel horen het als je iets als gelezen markeert.
  const changed = useCallback(() => {
    setVersion(v => v + 1)
    try { channel.current?.postMessage('veranderd') } catch { /* negeren */ }
  }, [])

  const markRead = useCallback(async (id: number) => {
    const res = await api.post<{ unread: number }>(`/notifications/${id}/read`)
    setUnread(res.unread)
    changed()
  }, [changed])

  const markAllRead = useCallback(async (kind?: string) => {
    const res = await api.post<{ unread: number }>(`/notifications/read${kind ? `?kind=${encodeURIComponent(kind)}` : ''}`)
    setUnread(res.unread)
    changed()
  }, [changed])

  const open = useCallback((n: AppNotification) => {
    if (!n.read) void markRead(n.id).catch(() => { /* de volgende keer kijken lukt het wel */ })
    if (n.link) navigate(n.link)
  }, [markRead, navigate])

  const refresh = useCallback(async () => {
    if (busy.current || Date.now() - lastCheck.current < 2000) return
    busy.current = true
    lastCheck.current = Date.now()
    try {
      const after = latest.current
      const s = await api.get<NotificationSummary>(`/notifications/summary${after ? `?after=${after}` : ''}`)
      setUnread(s.unread)
      // De eerste keer alleen onthouden waar we zijn; daarna een melding in beeld voor wat er nieuw binnenkwam.
      if (after !== null && s.latest > after) {
        setVersion(v => v + 1)
        for (const n of [...s.fresh].reverse().slice(-3)) {
          const Icon = kindInfo(n.kind).icon
          toast(n.text, 'info', { icon: <Icon size={16} />, onClick: () => open(n), duration: 6000 })
        }
      }
      latest.current = Math.max(after ?? 0, s.latest)
    } catch {
      // Offline of even geen server: de volgende keer opnieuw.
    } finally {
      busy.current = false
    }
  }, [open, toast])

  useEffect(() => {
    void refresh()
    const tick = () => { if (document.visibilityState === 'visible') void refresh() }
    const timer = setInterval(tick, POLL_MS)
    window.addEventListener('focus', tick)
    window.addEventListener('online', tick)
    document.addEventListener('visibilitychange', tick)
    try {
      channel.current = new BroadcastChannel(CHANNEL)
      channel.current.onmessage = () => { lastCheck.current = 0; void refresh(); setVersion(v => v + 1) }
    } catch { /* geen BroadcastChannel in deze browser */ }
    return () => {
      clearInterval(timer)
      window.removeEventListener('focus', tick)
      window.removeEventListener('online', tick)
      document.removeEventListener('visibilitychange', tick)
      channel.current?.close()
      channel.current = null
    }
  }, [refresh])

  const value = useMemo<NotificationsValue>(() => ({ unread, version, refresh, markRead, markAllRead, open }), [unread, version, refresh, markRead, markAllRead, open])
  return <NotificationsContext.Provider value={value}>{children}</NotificationsContext.Provider>
}

export function NotificationIcon({ kind }: { kind: string }) {
  const { icon: Icon, tone } = kindInfo(kind)
  return <span className={`notif-icon tone-${tone}`}><Icon size={15} /></span>
}

// Eén melding: icoon, de zin, de tijd en een stip zolang hij ongelezen is.
export function NotificationRow({ n, onOpen }: { n: AppNotification; onOpen: (n: AppNotification) => void }) {
  return (
    <button type="button" className={`notif-row ${n.read ? '' : 'unread'}`} onClick={() => onOpen(n)} title={n.title}>
      <NotificationIcon kind={n.kind} />
      <span className="notif-text">{n.text}</span>
      <span className="notif-time">{clockTime(n.createdAt)}</span>
      {!n.read && <span className="notif-dot" aria-label="Ongelezen" />}
    </button>
  )
}

export function NotificationGroups({ items, onOpen }: { items: AppNotification[]; onOpen: (n: AppNotification) => void }) {
  return (
    <>
      {byDay(items).map(g => (
        <div key={g.day} className="notif-group">
          <div className="notif-day">{g.day}</div>
          {g.items.map(n => <NotificationRow key={n.id} n={n} onOpen={onOpen} />)}
        </div>
      ))}
    </>
  )
}

// De bel rechtsboven, met het aantal ongelezen meldingen en een paneel met de nieuwste.
export function NotificationBell() {
  const { unread, version, open, markAllRead } = useNotifications()
  const [show, setShow] = useState(false)
  const [items, setItems] = useState<AppNotification[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const wrap = useRef<HTMLDivElement>(null)
  const navigate = useNavigate()
  const toast = useToast()

  useEffect(() => {
    if (!show) return
    let cancelled = false
    api.get<NotificationPage>('/notifications?limit=20')
      .then(p => { if (!cancelled) { setItems(p.items); setError(null) } })
      .catch(e => { if (!cancelled) setError((e as Error).message) })
    return () => { cancelled = true }
  }, [show, version])

  // Sluiten met Escape of met een klik ernaast.
  useEffect(() => {
    if (!show) return
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setShow(false) }
    const onDown = (e: MouseEvent) => { if (wrap.current && !wrap.current.contains(e.target as Node)) setShow(false) }
    window.addEventListener('keydown', onKey)
    window.addEventListener('mousedown', onDown)
    return () => { window.removeEventListener('keydown', onKey); window.removeEventListener('mousedown', onDown) }
  }, [show])

  const go = (path: string) => { setShow(false); navigate(path) }
  const allRead = async () => {
    try { await markAllRead() } catch (e) { toast((e as Error).message, 'error') }
  }

  return (
    <div className="notif-wrap" ref={wrap}>
      <button type="button" className={`icon-btn notif-bell ${show ? 'open' : ''}`} onClick={() => setShow(s => !s)} aria-expanded={show}
        aria-label={unread ? `Meldingen, ${unread} ongelezen` : 'Meldingen'} title="Meldingen">
        <Bell size={18} />
        {unread > 0 && <span className="notif-count">{unread > 99 ? '99+' : unread}</span>}
      </button>
      {show && (
        <div className="notif-panel" role="dialog" aria-label="Meldingen">
          <div className="notif-head">
            <strong>Meldingen</strong>
            <button type="button" className="btn btn-sm" disabled={unread === 0} onClick={allRead}><CheckCheck size={14} /> Alles gelezen</button>
          </div>
          <div className="notif-list">
            {error && <div className="notif-empty">De meldingen konden niet worden geladen. {error}</div>}
            {!error && items === null && <div className="notif-empty">Even laden…</div>}
            {!error && items?.length === 0 && (
              <div className="notif-empty">
                Je hebt nog geen meldingen. Hier zie je straks bijvoorbeeld een betaling die binnenkomt of een offerte waar je klant op reageert.
              </div>
            )}
            {!error && items && items.length > 0 && <NotificationGroups items={items} onOpen={n => { setShow(false); open(n) }} />}
          </div>
          <div className="notif-foot">
            <button type="button" className="link-btn" onClick={() => go('/meldingen')}>Alle meldingen</button>
            <button type="button" className="link-btn" onClick={() => go('/instellingen?tab=meldingen')}><Settings size={13} /> Instellingen</button>
          </div>
        </div>
      )}
    </div>
  )
}
