import { api } from './api'
import { resetSession } from './storage'

// Een boekhouder kijkt alleen mee: hij ziet de administratie maar wijzigt niets, behalve wat de beheerder hem extra gaf.
export const isAccountant = (u: { role: string } | null | undefined) => u?.role === 'boekhouder'

// Een administratie waar je boekhouder bent. current: daar ben je nu ingelogd.
export interface WorkspaceChoice { workspaceId: number; name: string; current: boolean }

// Van Instellingen ziet een boekhouder alleen zijn eigen account, en meldingen als hij de btw-aangifte mag markeren.
export const accountantSettingsTabIds = (canFileVat: boolean) => canFileVat ? ['account', 'meldingen'] : ['account']

// Wissel naar een andere administratie waar je boekhouder bent. De server geeft een nieuwe sessie voor die werkruimte;
// daarna laden we alles opnieuw, zodat er niets van de vorige werkruimte in beeld blijft.
export async function switchWorkspace(workspaceId: number) {
  await api.post('/auth/switch', { workspaceId })
  resetSession()
  window.location.assign('/')
}
