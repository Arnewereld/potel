// Prijzen en contactgegevens voor de verkooppagina en het abonnementsscherm. Pas ze hier aan.
export const product = {
  name: 'Potel',
  tagline: 'Het portaal voor freelance developers',
  salesEmail: 'hallo@potel.nl',
}

export interface Plan {
  id: 'proef' | 'zzp' | 'team'
  name: string
  price: string
  // Bedrag per maand excl. btw, voor de omzet op de platformpagina.
  monthly: number
  period: string
  description: string
  features: string[]
  highlight?: boolean
}

export const plans: Plan[] = [
  {
    id: 'proef', name: 'Proef', price: '€ 0', monthly: 0, period: '30 dagen',
    description: 'Alles uitproberen met je eigen klanten en uren.',
    features: ['Alle functies', 'Voorbeelddata met één klik', 'Geen creditcard nodig'],
  },
  {
    id: 'zzp', name: 'ZZP', price: '€ 12', monthly: 12, period: 'per maand, excl. btw', highlight: true,
    description: 'Voor de zelfstandige developer.',
    features: ['Onbeperkt klanten, projecten en facturen', 'Timer en urenregistratie', 'Urencriterium en btw-overzicht', 'Werkstromen en eigen modules'],
  },
  {
    id: 'team', name: 'Team', price: '€ 29', monthly: 29, period: 'per maand, excl. btw',
    description: 'Voor kleine bureaus en collectieven.',
    features: ['Alles van ZZP', 'Tot 5 gebruikers', 'Rollen: beheerder en medewerker', 'Hulp bij het overzetten van je data'],
  },
]
