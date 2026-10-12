import type { MailStatus, MailStep } from './types'

export const mailStatuses: Record<MailStatus, { label: string; tone: string }> = {
  bezig: { label: 'Bezig', tone: 'yellow' },
  verstuurd: { label: 'Verstuurd', tone: 'green' },
  mislukt: { label: 'Mislukt', tone: 'red' },
  overgeslagen: { label: 'Niet verstuurd', tone: 'gray' },
}

export const reminderSteps: { step: MailStep; label: string; key: 'first' | 'second' | 'final'; days: 'firstDays' | 'secondDays' | 'finalDays'; help: string }[] = [
  { step: 1, label: 'Herinnering', key: 'first', days: 'firstDays', help: 'Een vriendelijke herinnering. Vaak is een factuur gewoon vergeten.' },
  { step: 2, label: 'Tweede herinnering', key: 'second', days: 'secondDays', help: 'Iets dringender, met de vraag om binnen 7 dagen te betalen of contact op te nemen.' },
  { step: 3, label: 'Aanmaning', key: 'final', days: 'finalDays', help: 'De laatste stap, met wat de wet zegt over incassokosten en rente.' },
]

// Woorden tussen accolades die Potel per factuur invult, in dezelfde vorm als MailTemplates.Fill op de server.
export const mailPlaceholders: { code: string; text: string }[] = [
  { code: '{klant}', text: 'de contactpersoon of naam van je klant' },
  { code: '{nummer}', text: 'het factuurnummer' },
  { code: '{bedrag}', text: 'het bedrag dat nog open staat' },
  { code: '{factuurdatum}', text: 'de datum van de factuur' },
  { code: '{vervaldatum}', text: 'de uiterste betaaldatum' },
  { code: '{dagen}', text: 'hoeveel dagen de factuur te laat is' },
  { code: '{betaalgegevens}', text: 'je IBAN, je naam en het factuurnummer als kenmerk' },
  { code: '{link}', text: 'de persoonlijke link naar de factuur' },
  { code: '{bedrijf}', text: 'je bedrijfsnaam' },
  { code: '{gevolgen}', text: 'alleen in de aanmaning: wat de wet zegt over incassokosten en rente' },
]
