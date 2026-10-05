// Je eigen bedrijfsgegevens voor op de factuur. Ze worden in deze browser bewaard.
export interface Company { name: string; address: string; city: string; email: string; kvk: string; btw: string; iban: string }

const KEY = 'potel.company'
export const emptyCompany: Company = { name: 'Jouw Bedrijf B.V.', address: 'Straat 1', city: '1234 AB Plaats', email: 'info@jouwbedrijf.nl', kvk: '', btw: '', iban: '' }

export function loadCompany(): Company {
  try {
    const raw = localStorage.getItem(KEY)
    if (raw) return { ...emptyCompany, ...JSON.parse(raw) }
  } catch { /* opslag niet beschikbaar */ }
  return emptyCompany
}

export function saveCompany(c: Company) {
  try { localStorage.setItem(KEY, JSON.stringify(c)) } catch { /* negeren */ }
}
