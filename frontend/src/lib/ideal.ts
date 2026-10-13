import { api } from './api'

// Na het betalen stuurt Mollie de klant terug naar /factuur?betaling=… zonder het token van de factuur (dat hoort niet bij
// Mollie). Daarom onthoudt dit tabblad het token even, zodat de klant na het betalen de factuur weer ziet.
const returnKey = 'potel:betaal-terug'

// Alleen echte betaalpagina's van Mollie, net als op de server.
const isMollie = (url: string) => {
  try {
    const u = new URL(url)
    return u.protocol === 'https:' && (u.hostname === 'mollie.com' || u.hostname.endsWith('.mollie.com'))
  } catch {
    return false
  }
}

// Start een iDEAL-betaling voor de factuur achter dit token en gaat naar de betaalpagina. Het bedrag bepaalt de server.
export async function payWithIdeal(token: string) {
  const { checkoutUrl } = await api.post<{ checkoutUrl: string }>('/public/invoice/pay', { token })
  if (!isMollie(checkoutUrl)) throw new Error('Betalen met iDEAL lukt nu even niet. Probeer het later opnieuw of maak het bedrag over.')
  try { sessionStorage.setItem(returnKey, token) } catch { /* geen opslag: na het betalen zie je dan alleen de status */ }
  window.location.assign(checkoutUrl)
}

export function returnToken(): string | null {
  try { return sessionStorage.getItem(returnKey) } catch { return null }
}
