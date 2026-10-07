// Prijzen voor de verkooppagina en het abonnementsscherm. Je contactadres en bedrijfsgegevens staan in de sectie
// "Platform" van appsettings.json op de server, zie lib/platform.ts.
export const product = {
  name: 'Potel',
  tagline: 'Het portaal voor freelance developers',
}

export interface Plan {
  id: 'proef' | 'zzp' | 'team'
  name: string
  price: string
  // Bedrag per maand excl. btw, voor de omzet op de platformpagina.
  monthly: number
  period: string
  description: string
  // Hoeveel gebruikers (ook uitgeschakelde) er in de werkruimte passen. De backend dwingt dit af met
  // Plans.MaxUsers in backend/Potel.Api/Data/Models.cs; een test controleert dat de twee gelijk zijn.
  maxUsers: number
  features: string[]
  highlight?: boolean
}

export const seats = (n: number) => (n === 1 ? '1 gebruiker' : `Tot ${n} gebruikers`)

const definitions: Plan[] = [
  {
    id: 'proef', name: 'Proef', price: '€ 0', monthly: 0, period: '30 dagen', maxUsers: 5,
    description: 'Alles uitproberen met je eigen klanten en uren.',
    features: ['Alle functies', 'Voorbeelddata met één klik', 'Geen creditcard nodig'],
  },
  {
    id: 'zzp', name: 'ZZP', price: '€ 12', monthly: 12, period: 'per maand, excl. btw', highlight: true, maxUsers: 1,
    description: 'Voor de zelfstandige developer.',
    features: ['Onbeperkt klanten, projecten en facturen', 'Timer en urenregistratie', 'Urencriterium en btw-overzicht', 'Werkstromen en eigen modules'],
  },
  {
    id: 'team', name: 'Team', price: '€ 29', monthly: 29, period: 'per maand, excl. btw', maxUsers: 5,
    description: 'Voor kleine bureaus en collectieven.',
    features: ['Alles van ZZP', 'Rollen: beheerder en medewerker', 'Hulp bij het overzetten van je data'],
  },
]

// Bij de betaalde abonnementen staat het aantal gebruikers als tweede punt, uit maxUsers, zodat tekst en limiet gelijk blijven.
export const plans: Plan[] = definitions.map(p =>
  p.id === 'proef' ? p : { ...p, features: [...p.features.slice(0, 1), seats(p.maxUsers), ...p.features.slice(1)] })
