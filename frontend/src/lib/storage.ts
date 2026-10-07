// Wat Potel per browser bewaart en wanneer het weg moet. Op een gedeelde computer mag de volgende
// gebruiker niets zien van de vorige.

const TIMER_PREFIX = 'potel.timer'

// Een lopende timer hoort bij één gebruiker in één werkruimte.
export const timerKey = (user: { id: number; workspaceId: number }) => `${TIMER_PREFIX}:${user.workspaceId}:${user.id}`

function remove(match: (key: string) => boolean) {
  try {
    for (let i = localStorage.length - 1; i >= 0; i--) {
      const key = localStorage.key(i)
      if (key && match(key)) localStorage.removeItem(key)
    }
  } catch { /* negeren */ }
}

// Bij inloggen of aanmelden: tabbladen van een vorige sessie kunnen naar een andere werkruimte wijzen,
// en een timer uit een oudere versie (zonder gebruiker erbij) weten we niet aan iemand toe te wijzen.
export const resetSession = () => remove(key => key === 'potel.tabs' || key === TIMER_PREFIX)

// Bij uitloggen gaan ook alle lopende timers weg.
export const clearSession = () => remove(key => key === 'potel.tabs' || key === TIMER_PREFIX || key.startsWith(`${TIMER_PREFIX}:`))
