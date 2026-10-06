import type { Customer, Invoice, InvoiceParty, Settings, VatRegime } from './types'
import { date, euro, invoiceTotals, parseDate } from './format'

export const units = ['uur', 'dag', 'stuk', 'maand', 'jaar']

// Dagen tot de vervaldatum; negatief als hij al verstreken is.
export function daysUntil(due: string) {
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  return Math.round((parseDate(due.slice(0, 10)).getTime() - today.getTime()) / 86400000)
}

// Dezelfde regel als de server: gekoppeld aan een factuur, of (bij oude facturen) een negatief totaal.
export const isCredit = (inv: Pick<Invoice, 'lines' | 'creditForInvoiceId'>) =>
  inv.creditForInvoiceId != null || invoiceTotals(inv.lines).total < 0

// "Factuur 2026-0012", "Creditnota 2026-0013" of, zolang er nog geen nummer is, "Conceptfactuur".
export function invoiceTitle(inv: Pick<Invoice, 'number' | 'lines' | 'creditForInvoiceId'>) {
  const credit = isCredit(inv)
  if (!inv.number) return credit ? 'Conceptcreditnota' : 'Conceptfactuur'
  return `${credit ? 'Creditnota' : 'Factuur'} ${inv.number}`
}

// Landen voor bij een klant. De server gebruikt dezelfde EU-lijst (Countries in Models.cs).
export const netherlands = 'Nederland'
export const euCountries = [
  'België', 'Bulgarije', 'Cyprus', 'Denemarken', 'Duitsland', 'Estland', 'Finland', 'Frankrijk', 'Griekenland', 'Hongarije',
  'Ierland', 'Italië', 'Kroatië', 'Letland', 'Litouwen', 'Luxemburg', 'Malta', 'Nederland', 'Oostenrijk', 'Polen',
  'Portugal', 'Roemenië', 'Slovenië', 'Slowakije', 'Spanje', 'Tsjechië', 'Zweden',
]
export const otherCountries = ['Verenigd Koninkrijk', 'Zwitserland', 'Noorwegen', 'Verenigde Staten', 'Canada', 'Australië']

export const isNetherlands = (country?: string | null) => !country?.trim() || country.trim() === netherlands
export const inEu = (country?: string | null) => isNetherlands(country) || euCountries.includes(country!.trim())

export const vatRegimes: { id: VatRegime; label: string; help: string }[] = [
  { id: 'normaal', label: 'Normaal, met btw', help: 'Btw per regel, voor klanten in Nederland en particulieren.' },
  { id: 'verlegd', label: 'Btw verlegd (EU)', help: 'Voor een zakelijke klant in een ander EU-land met een btw-nummer. De klant draagt de btw zelf af.' },
  { id: 'kor', label: 'Kleineondernemersregeling (KOR)', help: 'Je gebruikt de KOR: er staat geen btw op de factuur, wel de vermelding dat je de KOR gebruikt.' },
  { id: 'vrijgesteld', label: 'Vrijgesteld van btw', help: 'Je werk is vrijgesteld van btw, bijvoorbeeld als zorgverlener of docent. Er staat geen btw op de factuur, wel de vermelding van de vrijstelling.' },
  { id: 'buiten-eu', label: 'Klant buiten de EU', help: 'Geen Nederlandse btw voor een zakelijke klant buiten de EU.' },
]

// Alleen bij normaal staat er btw op de regels.
export const zeroVat = (regime: VatRegime) => regime !== 'normaal'

// De regeling voor een nieuwe factuur, net als op de server: verlegd voor een zakelijke klant in een ander EU-land,
// buiten de EU daarbuiten, en anders wat je bij Instellingen als standaard hebt gekozen.
export function defaultRegime(settings: Pick<Settings, 'vatRegime'> | null | undefined, customer: Customer | null | undefined): VatRegime {
  if (customer && !isNetherlands(customer.country)) {
    if (!inEu(customer.country)) return 'buiten-eu'
    if (customer.vatNumber?.trim()) return 'verlegd'
  }
  return settings?.vatRegime === 'kor' || settings?.vatRegime === 'vrijgesteld' ? settings.vatRegime : 'normaal'
}

// Past de regeling bij de klant? Dezelfde controle als RegimeError op de server.
export function regimeProblem(regime: VatRegime, customer: Customer | null | undefined) {
  if (!customer) return null
  if (regime === 'verlegd' && (isNetherlands(customer.country) || !inEu(customer.country)))
    return 'Btw verlegd kan alleen voor een zakelijke klant in een ander EU-land. Een klant in Nederland betaalt gewoon btw.'
  if (regime === 'verlegd' && !customer.vatNumber?.trim()) return 'Voor btw verlegd moet het btw-nummer van de klant bekend zijn.'
  if (regime === 'buiten-eu' && inEu(customer.country)) return '"Buiten de EU" kan alleen voor een klant buiten de EU.'
  return null
}

type Sendable = Pick<Invoice, 'vatRegime' | 'deliveryFrom' | 'lines' | 'buyer'>

// Wat er nog ontbreekt om te mogen versturen, in dezelfde woorden als de server (Missing in Invoices.cs).
// Een creditnota heeft de klantgegevens van de factuur die hij corrigeert al bij zich; anders tellen die van de klant nu.
export function missingForSending(inv: Sendable, customer: Customer | null | undefined, s: Settings) {
  const buyer = inv.buyer ?? (customer ? buyerFrom(customer) : null)
  const missing: string[] = []
  const need = (value: string | null | undefined, label: string) => { if (!value?.trim()) missing.push(label) }
  need(s.companyName, 'je bedrijfsnaam')
  need(s.address, 'je adres')
  need(s.city, 'je postcode en plaats')
  need(s.kvk, 'je KvK-nummer')
  need(s.btw, 'je btw-id')
  need(buyer?.name, 'de naam van de klant')
  need(buyer?.address, 'het adres van de klant')
  need(buyer?.city, 'de postcode en plaats van de klant')
  if (inv.vatRegime === 'verlegd') need(buyer?.vatNumber, 'het btw-nummer van de klant')
  if (!inv.deliveryFrom) missing.push('de leverdatum of periode')
  if (inv.lines.length === 0) missing.push('minstens één regel')
  return missing
}

// Jij en je klant zoals ze nu zijn; na versturen gebruikt de factuur de vastgelegde gegevens.
export const sellerFrom = (s: Settings): InvoiceParty => ({
  name: s.companyName, contact: s.ownerName, address: s.address, city: s.city, country: netherlands, email: s.email,
  phone: s.phone, website: s.website, kvk: s.kvk, vatNumber: s.btw, iban: s.iban,
})
export const buyerFrom = (c: Customer): InvoiceParty => ({
  name: c.company?.trim() || c.name, contact: c.company?.trim() ? c.name : null, address: c.address, city: c.city,
  country: c.country?.trim() || netherlands, email: c.email, phone: c.phone, vatNumber: c.vatNumber,
})

const greeting = (c?: Customer | null) => `Hoi ${c?.name.split(' ')[0] ?? ''},`.replace(' ,', ',')
const signature = (s: Settings) => `\n\nGroet,\n${s.ownerName || s.companyName}\n${s.companyName}${s.website ? `\n${s.website}` : ''}`

function mailto(to: string | null | undefined, subject: string, body: string) {
  return `mailto:${encodeURIComponent(to ?? '')}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`
}

// Opent je mailprogramma met een kant-en-klare mail. De PDF voeg je zelf als bijlage toe.
export function sendMail(inv: Invoice, c: Customer | null | undefined, s: Settings) {
  const total = invoiceTotals(inv.lines).total
  const kind = isCredit(inv) ? 'creditnota' : 'factuur'
  const named = inv.number ? `${kind} ${inv.number}` : `de ${kind}`
  const iban = inv.seller ? inv.seller.iban : s.iban
  const holder = inv.seller?.name ?? s.companyName
  return mailto(c?.email, `${kind[0]!.toUpperCase()}${kind.slice(1)}${inv.number ? ` ${inv.number}` : ''} van ${holder}`,
    `${greeting(c)}\n\nIn de bijlage vind je ${named} van ${euro(Math.abs(total))}${inv.reference ? ` (jullie referentie ${inv.reference})` : ''}.`
    + (total > 0 ? ` Wil je die vóór ${date(inv.dueDate)} overmaken${iban ? ` op ${iban} t.n.v. ${holder}` : ''}${inv.number ? `, onder vermelding van ${inv.number}` : ''}?` : '')
    + signature(s))
}

// Het bedrag dat nog openstaat: het totaal min wat er al gecrediteerd is.
export const openAmount = (inv: Invoice) => inv.openAmount ?? invoiceTotals(inv.lines).total

export function reminderMail(inv: Invoice, c: Customer | null | undefined, s: Settings) {
  const total = openAmount(inv)
  const late = -daysUntil(inv.dueDate)
  const iban = inv.seller ? inv.seller.iban : s.iban
  const holder = inv.seller?.name ?? s.companyName
  const number = inv.number ?? ''
  return mailto(c?.email, `Herinnering: factuur ${number}`,
    `${greeting(c)}\n\nIk zie dat factuur ${number} van ${date(inv.issueDate)} (${euro(total)}) nog openstaat; de vervaldatum was ${date(inv.dueDate)}${late > 0 ? `, ${late} dagen geleden` : ''}. `
    + `Misschien is hij aan je aandacht ontsnapt. Wil je het bedrag${iban ? ` overmaken op ${iban} t.n.v. ${holder}` : ' overmaken'}, onder vermelding van ${number}?\n\n`
    + 'Is hij inmiddels betaald? Dan kun je deze mail negeren.'
    + signature(s))
}
