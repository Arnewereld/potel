import type { InvoiceLine } from './types'

const euroFmt = new Intl.NumberFormat('nl-NL', { style: 'currency', currency: 'EUR' })
export const euro = (n: number) => euroFmt.format(n || 0)

// De API geeft datums zonder tijdzone terug; we behandelen ze als lokale tijd.
export const parseDate = (s: string) => new Date(s.length === 10 ? `${s}T00:00:00` : s.replace(/Z$/, ''))

export const date = (s?: string | null) =>
  s ? parseDate(s).toLocaleDateString('nl-NL', { day: 'numeric', month: 'short', year: 'numeric' }) : '—'

export const time = (s: string) => parseDate(s).toLocaleTimeString('nl-NL', { hour: '2-digit', minute: '2-digit' })

export const dateTime = (s: string) =>
  parseDate(s).toLocaleString('nl-NL', { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })

export function relative(s: string) {
  const diff = (Date.now() - new Date(s.endsWith('Z') ? s : `${s}Z`).getTime()) / 1000
  if (diff < 60) return 'zojuist'
  if (diff < 3600) return `${Math.floor(diff / 60)} min geleden`
  if (diff < 86400) return `${Math.floor(diff / 3600)} uur geleden`
  return `${Math.floor(diff / 86400)} dagen geleden`
}

const pad = (n: number) => String(n).padStart(2, '0')
export const toDateInput = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
export const toDateTimeInput = (d: Date) => `${toDateInput(d)}T${pad(d.getHours())}:${pad(d.getMinutes())}`

export function invoiceTotals(lines: InvoiceLine[]) {
  let subtotal = 0
  let vat = 0
  for (const l of lines) {
    const net = (Number(l.quantity) || 0) * (Number(l.unitPrice) || 0)
    subtotal += net
    vat += Math.round(net * (Number(l.vatRate) || 0)) / 100
  }
  return { subtotal, vat, total: subtotal + vat }
}

export const initials = (name: string) =>
  name.split(/\s+/).filter(Boolean).slice(0, 2).map(p => p[0]!.toUpperCase()).join('')

// Minuten als "7:30" of, met unit, als "7,5 u".
export const hm = (minutes: number) => `${Math.floor(minutes / 60)}:${pad(Math.round(minutes % 60))}`
export const hours = (minutes: number, digits = 1) =>
  `${(minutes / 60).toLocaleString('nl-NL', { maximumFractionDigits: digits })} u`

// Leest "1:30", "1,5", "1.5" of "90m" als minuten.
export function parseDuration(s: string): number | null {
  s = s.trim().toLowerCase()
  if (!s) return null
  let m = s.match(/^(\d+):(\d{1,2})$/)
  if (m) return Number(m[1]) * 60 + Number(m[2])
  m = s.match(/^(\d+)\s*m(in)?$/)
  if (m) return Number(m[1])
  const n = Number(s.replace(/\s*u(ur)?$/, '').replace(',', '.'))
  return Number.isFinite(n) && n > 0 ? Math.round(n * 60) : null
}

// Maandag van de week waarin de datum valt.
export function weekStart(d: Date) {
  const r = new Date(d.getFullYear(), d.getMonth(), d.getDate())
  r.setDate(r.getDate() - ((r.getDay() + 6) % 7))
  return r
}

export function weekNumber(d: Date) {
  const t = new Date(Date.UTC(d.getFullYear(), d.getMonth(), d.getDate()))
  t.setUTCDate(t.getUTCDate() + 4 - (t.getUTCDay() || 7))
  return Math.ceil(((t.getTime() - Date.UTC(t.getUTCFullYear(), 0, 1)) / 86400000 + 1) / 7)
}
