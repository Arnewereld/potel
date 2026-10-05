import type { Customer, Invoice, Settings } from './types'
import { date, euro, invoiceTotals, parseDate } from './format'

export const units = ['uur', 'dag', 'stuk', 'maand', 'jaar']

// Dagen tot de vervaldatum; negatief als hij al verstreken is.
export function daysUntil(due: string) {
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  return Math.round((parseDate(due.slice(0, 10)).getTime() - today.getTime()) / 86400000)
}

export const isCredit = (inv: Pick<Invoice, 'lines'>) => invoiceTotals(inv.lines).total < 0

const greeting = (c?: Customer | null) => `Hoi ${c?.name.split(' ')[0] ?? ''},`.replace(' ,', ',')
const signature = (s: Settings) => `\n\nGroet,\n${s.ownerName || s.companyName}\n${s.companyName}${s.website ? `\n${s.website}` : ''}`

function mailto(to: string | null | undefined, subject: string, body: string) {
  return `mailto:${encodeURIComponent(to ?? '')}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`
}

// Opent je mailprogramma met een kant-en-klare mail. De PDF voeg je zelf als bijlage toe.
export function sendMail(inv: Invoice, c: Customer | null | undefined, s: Settings) {
  const total = invoiceTotals(inv.lines).total
  const kind = total < 0 ? 'creditnota' : 'factuur'
  return mailto(c?.email, `${kind[0]!.toUpperCase()}${kind.slice(1)} ${inv.number} van ${s.companyName}`,
    `${greeting(c)}\n\nIn de bijlage vind je ${kind} ${inv.number} van ${euro(Math.abs(total))}${inv.reference ? ` (jullie referentie ${inv.reference})` : ''}.`
    + (total > 0 ? ` Wil je die vóór ${date(inv.dueDate)} overmaken${s.iban ? ` op ${s.iban} t.n.v. ${s.companyName}` : ''}, onder vermelding van ${inv.number}?` : '')
    + signature(s))
}

export function reminderMail(inv: Invoice, c: Customer | null | undefined, s: Settings) {
  const total = invoiceTotals(inv.lines).total
  const late = -daysUntil(inv.dueDate)
  return mailto(c?.email, `Herinnering: factuur ${inv.number}`,
    `${greeting(c)}\n\nIk zie dat factuur ${inv.number} van ${date(inv.issueDate)} (${euro(total)}) nog openstaat; de vervaldatum was ${date(inv.dueDate)}${late > 0 ? `, ${late} dagen geleden` : ''}. `
    + `Misschien is hij aan je aandacht ontsnapt. Wil je het bedrag${s.iban ? ` overmaken op ${s.iban} t.n.v. ${s.companyName}` : ' overmaken'}, onder vermelding van ${inv.number}?\n\n`
    + 'Is hij inmiddels betaald? Dan kun je deze mail negeren.'
    + signature(s))
}
