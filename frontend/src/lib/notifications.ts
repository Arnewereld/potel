import { createContext, useContext } from 'react'
import { AlarmClock, Banknote, Bell, DatabaseBackup, Eye, FileSignature, Landmark, Repeat, Wallet, Workflow, type LucideIcon } from 'lucide-react'

// Een melding zoals de server hem geeft. Tijden in UTC.
export interface AppNotification {
  id: number
  kind: string
  title: string
  text: string
  link: string | null
  createdAt: string
  read: boolean
}

export interface NotificationPage { items: AppNotification[]; unread: number; more: boolean }
export interface NotificationSummary { unread: number; latest: number; fresh: AppNotification[] }

export interface NotificationKindSetting { id: string; label: string; description: string; inApp: boolean; email: boolean }
export interface NotificationSettings {
  kinds: NotificationKindSetting[]
  digest: boolean
  // Of er een mailserver is, en of je adres bevestigd is: anders gaat er geen mail weg.
  emailAvailable: boolean
  emailVerified: boolean
  email: string
}

// Per soort een icoon en een kleur, zodat je in één oogopslag ziet waar het over gaat.
const kinds: Record<string, { icon: LucideIcon; tone: string }> = {
  offerte: { icon: FileSignature, tone: 'purple' },
  betaling: { icon: Banknote, tone: 'green' },
  'te-laat': { icon: AlarmClock, tone: 'red' },
  terugkerend: { icon: Repeat, tone: 'blue' },
  'vaste-kosten': { icon: Wallet, tone: 'yellow' },
  portaal: { icon: Eye, tone: 'blue' },
  btw: { icon: Landmark, tone: 'orange' },
  werkstroom: { icon: Workflow, tone: 'red' },
  'back-up': { icon: DatabaseBackup, tone: 'red' },
}

export const kindInfo = (kind: string) => kinds[kind] ?? { icon: Bell, tone: 'gray' }

export interface NotificationsValue {
  unread: number
  // Telt op bij elke verandering, zodat de bel en de pagina weten dat ze opnieuw moeten laden.
  version: number
  refresh: () => Promise<void>
  markRead: (id: number) => Promise<void>
  markAllRead: (kind?: string) => Promise<void>
  // Gelezen markeren en naar waar de melding over gaat.
  open: (n: AppNotification) => void
}

export const NotificationsContext = createContext<NotificationsValue | null>(null)

export function useNotifications() {
  const ctx = useContext(NotificationsContext)
  if (!ctx) throw new Error('useNotifications buiten NotificationsProvider')
  return ctx
}

// Vandaag, gisteren of de datum, voor de koppen in de lijst.
export function dayLabel(iso: string, now = new Date()) {
  const d = new Date(iso)
  const start = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime()
  const days = Math.round((start(now) - start(d)) / 86_400_000)
  if (days === 0) return 'Vandaag'
  if (days === 1) return 'Gisteren'
  return d.toLocaleDateString('nl-NL', { weekday: 'long', day: 'numeric', month: 'long', year: d.getFullYear() === now.getFullYear() ? undefined : 'numeric' })
}

export const clockTime = (iso: string) => new Date(iso).toLocaleTimeString('nl-NL', { hour: '2-digit', minute: '2-digit' })

// De meldingen per dag, nieuwste eerst, in de volgorde waarin ze binnenkwamen.
export function byDay(items: AppNotification[]) {
  const groups: { day: string; items: AppNotification[] }[] = []
  for (const n of items) {
    const day = dayLabel(n.createdAt)
    const last = groups[groups.length - 1]
    if (last && last.day === day) last.items.push(n)
    else groups.push({ day, items: [n] })
  }
  return groups
}
