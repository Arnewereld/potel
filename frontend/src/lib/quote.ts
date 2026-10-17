import type { Customer, InvoiceParty, Lead, Quote, QuoteState, Settings } from './types'
import { invoiceTotals } from './format'
import { buyerFrom } from './invoice'
import { docDate, docEuro, t } from './docTexts'

// "Offerte OFF-2026-001" of, zolang er nog geen nummer is, "Conceptofferte".
export const quoteTitle = (q: Pick<Quote, 'number'>) => (q.number ? `Offerte ${q.number}` : 'Conceptofferte')

// De stand van vandaag; de server stuurt hem mee, anders rekenen we hem zelf uit.
export function quoteState(q: Pick<Quote, 'status' | 'state' | 'validUntil'>): QuoteState {
  if (q.state) return q.state
  if (q.status !== 'verstuurd') return q.status
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  return new Date(`${q.validUntil.slice(0, 10)}T00:00:00`) < today ? 'verlopen' : 'verstuurd'
}

// Een lead op de offerte, net als op de server (BuyerOf in Quotes.cs): naam, bedrijf en contactgegevens.
export const buyerFromLead = (l: Lead): InvoiceParty => ({
  name: l.company?.trim() || l.name, contact: l.company?.trim() ? l.name : null, email: l.email, phone: l.phone,
})

export const quoteBuyer = (q: Pick<Quote, 'buyer'>, customer?: Customer | null, lead?: Lead | null) =>
  q.buyer ?? (customer ? buyerFrom(customer) : lead ? buyerFromLead(lead) : null)

// Wat er nog ontbreekt om te mogen versturen, in dezelfde woorden als de server (Missing in Quotes.cs).
export function missingForQuote(q: Pick<Quote, 'lines'>, buyer: InvoiceParty | null, s: Settings) {
  const missing: string[] = []
  const need = (value: string | null | undefined, label: string) => { if (!value?.trim()) missing.push(label) }
  need(s.companyName, 'je bedrijfsnaam')
  need(s.address, 'je adres')
  need(s.city, 'je postcode en plaats')
  need(s.kvk, 'je KvK-nummer')
  need(buyer?.name, 'de naam van de klant')
  if (q.lines.length === 0) missing.push('minstens één regel')
  return missing
}

// Het bedrag van de aanbetaling, met btw, zoals het straks op de factuur voor de aanbetaling komt.
export function depositAmount(q: Pick<Quote, 'lines' | 'depositPercent'>) {
  if (!q.depositPercent) return null
  const totals = invoiceTotals(q.lines)
  const lines = totals.vatGroups.map(g => ({ quantity: 1, unitPrice: Math.round(g.base * q.depositPercent! + Number.EPSILON) / 100, vatRate: g.rate }))
  return invoiceTotals(lines)
}

// Opent je mailprogramma met een kant-en-klare mail en de link naar de offerte, in de taal van de offerte.
export function quoteMail(q: Quote, link: string, to: string | null | undefined, firstName: string | null | undefined, s: Settings) {
  const lang = q.language
  const seller = q.seller?.name ?? s.companyName
  const total = docEuro(lang, invoiceTotals(q.lines).total)
  const body = t(lang, 'mail.quoteBody', {
    greeting: firstName ? t(lang, 'mail.quoteHi', { name: firstName }) : t(lang, 'mail.quoteHello'),
    number: q.number ?? '', about: q.subject ? t(lang, 'mail.quoteAbout', { subject: q.subject }) : '',
    amount: q.vatRegime === 'normaal' ? t(lang, 'paper.inclVat', { amount: total }) : total,
    validUntil: docDate(lang, q.validUntil), link,
  }) + `\n\n${t(lang, 'mail.regards')}\n${s.ownerName || seller}\n${seller}`
  return `mailto:${encodeURIComponent(to ?? '')}?subject=${encodeURIComponent(t(lang, 'mail.quoteSubject', { number: q.number ?? '', seller }))}&body=${encodeURIComponent(body)}`
}
