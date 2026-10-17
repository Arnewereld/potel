import type { RecurringAction, RecurringExpenseAction, RecurringInterval, RecurringInvoice, RecurringPeriodRule } from './types'

export const recurringIntervals: { id: RecurringInterval; label: string; short: string }[] = [
  { id: 'maand', label: 'Elke maand', short: 'maandelijks' },
  { id: 'kwartaal', label: 'Elk kwartaal', short: 'per kwartaal' },
  { id: 'jaar', label: 'Elk jaar', short: 'jaarlijks' },
]

export const recurringRules: { id: RecurringPeriodRule; label: string; help: string }[] = [
  { id: 'vooraf', label: 'Vooraf', help: 'De factuur komt op de eerste dag van de periode, zoals bij hosting of een abonnement.' },
  { id: 'achteraf', label: 'Achteraf', help: 'De factuur komt de dag na afloop van de periode, zoals bij een retainer of onderhoud dat je al gedaan hebt.' },
]

export const recurringActions: { id: RecurringAction; label: string; help: string }[] = [
  { id: 'concept', label: 'Concept klaarzetten', help: 'Je krijgt een conceptfactuur die je nog even nakijkt en zelf verstuurt.' },
  { id: 'versturen', label: 'Direct versturen', help: 'De factuur krijgt meteen een nummer en gaat per e-mail naar je klant.' },
]

// Wat Potel elke periode doet met een vaste kost (abonnement).
export const recurringExpenseActions: { id: RecurringExpenseAction; label: string; help: string }[] = [
  { id: 'concept', label: 'Concept klaarzetten', help: 'Potel zet de uitgave elke periode klaar als concept. Jij kijkt hem na en voegt het bonnetje toe.' },
  { id: 'herinneren', label: 'Alleen herinneren', help: 'Potel zet niets klaar, maar laat hier en in het logboek zien dat hij aan de beurt is. Je boekt hem zelf met één klik.' },
]

export const recurringStates: Record<RecurringInvoice['state'], { label: string; tone: string }> = {
  actief: { label: 'Actief', tone: 'green' },
  gepauzeerd: { label: 'Gepauzeerd', tone: 'yellow' },
  afgelopen: { label: 'Afgelopen', tone: 'gray' },
}

// Woorden tussen accolades die Potel per periode invult, in dezelfde vorm als RecurringPeriods.Fill op de server.
export const placeholders: { code: string; example: string }[] = [
  { code: '{maand}', example: 'oktober' },
  { code: '{jaar}', example: '2026' },
  { code: '{kwartaal}', example: 'Q4' },
  { code: '{periode}', example: 'oktober 2026' },
]
