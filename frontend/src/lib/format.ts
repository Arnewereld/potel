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
