import type { TripKind } from './types'

// Dezelfde soorten als de server (TripKinds in backend/Potel.Api/Data/Models.cs).
export const tripKinds: { id: TripKind; label: string; short: string }[] = [
  { id: 'zakelijk', label: 'Zakelijk, eigen auto', short: 'Eigen auto' },
  { id: 'auto-van-de-zaak', label: 'Zakelijk, auto van de zaak', short: 'Auto van de zaak' },
  { id: 'prive', label: 'Privé', short: 'Privé' },
]

export const isBusiness = (kind: TripKind) => kind !== 'prive'

export const tripKindLabel = (kind: TripKind) => tripKinds.find(k => k.id === kind)?.label ?? kind

// 45 km of 12,5 km.
export const km = (n: number) => `${(n || 0).toLocaleString('nl-NL', { maximumFractionDigits: 1 })} km`

// Wat een soort rit voor je winst betekent, in gewone woorden.
export function tripKindHelp(kind: TripKind, rate: number | null | undefined, year: number) {
  if (kind === 'zakelijk') {
    return `${rate != null ? `Je trekt € ${rate.toLocaleString('nl-NL', { minimumFractionDigits: 2 })} per kilometer af van je winst (${year}).` : `Het bedrag per kilometer voor ${year} staat nog niet vast.`}`
      + ' Andere kosten van je eigen auto, zoals brandstof en parkeren, trek je dan niet apart af. Woon-werkverkeer met je eigen auto telt ook als zakelijk.'
  }
  if (kind === 'auto-van-de-zaak') return 'De auto staat op je onderneming, dus de autokosten zitten al in je kosten. We leggen deze kilometers alleen vast.'
  return 'Telt niet mee voor je winst. Handig als je al je ritten bijhoudt, bijvoorbeeld voor een sluitende kilometeradministratie.'
}
