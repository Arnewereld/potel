import { relative } from './format'
import type { CustomerPortalSettings } from './types'

// Wanneer de klant zijn portaal voor het laatst opende, voor op de klantpagina.
export function portalOpened(p: CustomerPortalSettings | null | undefined) {
  if (!p?.lastOpenedAt) return p?.link ? 'Nog niet geopend' : 'Geen link'
  return `Laatst geopend ${relative(p.lastOpenedAt)}`
}
