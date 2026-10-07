import { useEffect, useState } from 'react'
import { api } from './api'

// De gegevens van de eigenaar van het platform, uit de sectie "Platform" in appsettings.json op de server.
export interface PlatformInfo {
  name: string
  company: string
  address: string
  city: string
  email: string
  phone: string
  kvk: string
  vatId: string
  subProcessors: { name: string; purpose: string; location: string }[]
  // Alles ingevuld, geen [Vul in: …] meer.
  complete: boolean
  // De versie van de voorwaarden en verwerkersovereenkomst die je bij het aanmelden accepteert.
  termsVersion: string
}

let cached: Promise<PlatformInfo> | null = null

function load() {
  cached ??= api.get<PlatformInfo>('/public/platform').catch(e => { cached = null; throw e })
  return cached
}

export function usePlatformInfo() {
  const [info, setInfo] = useState<PlatformInfo | null>(null)
  useEffect(() => {
    let alive = true
    load().then(i => alive && setInfo(i)).catch(() => {})
    return () => { alive = false }
  }, [])
  return info
}

// Een adres om naar te mailen, of null zolang er nog een placeholder staat.
export const contactEmail = (info: PlatformInfo | null) =>
  info && info.email.includes('@') && !info.email.startsWith('[') ? info.email : null

// De juridische teksten, zie pages/public/Legal.tsx.
export const legalLinks = [
  { to: '/voorwaarden', label: 'Algemene voorwaarden' },
  { to: '/privacy', label: 'Privacyverklaring' },
  { to: '/verwerkersovereenkomst', label: 'Verwerkersovereenkomst' },
]
