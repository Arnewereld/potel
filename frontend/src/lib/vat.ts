// De vorm van btw-nummers per EU-land, dezelfde als op de server (VatNumbers in Data/VatNumbers.cs; VatNumberTests
// controleert dat ze gelijk blijven). Alleen de vorm: of een nummer echt bestaat, weet alleen VIES.
// Per land de landcode, het patroon na de landcode en een voorbeeld. Griekenland gebruikt EL, niet GR.
export const vatFormats: Record<string, [code: string, pattern: string, example: string]> = {
  'België': ['BE', String.raw`[01]\d{9}`, 'BE0123456789'],
  'Bulgarije': ['BG', String.raw`\d{9,10}`, 'BG123456789'],
  'Cyprus': ['CY', String.raw`\d{8}[A-Z]`, 'CY12345678L'],
  'Denemarken': ['DK', String.raw`\d{8}`, 'DK12345678'],
  'Duitsland': ['DE', String.raw`\d{9}`, 'DE123456789'],
  'Estland': ['EE', String.raw`\d{9}`, 'EE123456789'],
  'Finland': ['FI', String.raw`\d{8}`, 'FI12345678'],
  'Frankrijk': ['FR', String.raw`[0-9A-HJ-NP-Z]{2}\d{9}`, 'FR12345678901'],
  'Griekenland': ['EL', String.raw`\d{9}`, 'EL123456789'],
  'Hongarije': ['HU', String.raw`\d{8}`, 'HU12345678'],
  'Ierland': ['IE', String.raw`\d{7}[A-W][A-IW]?|\d[A-Z+*]\d{5}[A-W]`, 'IE1234567T'],
  'Italië': ['IT', String.raw`\d{11}`, 'IT12345678901'],
  'Kroatië': ['HR', String.raw`\d{11}`, 'HR12345678901'],
  'Letland': ['LV', String.raw`\d{11}`, 'LV12345678901'],
  'Litouwen': ['LT', String.raw`\d{9}|\d{12}`, 'LT123456789'],
  'Luxemburg': ['LU', String.raw`\d{8}`, 'LU12345678'],
  'Malta': ['MT', String.raw`\d{8}`, 'MT12345678'],
  'Nederland': ['NL', String.raw`\d{9}B\d{2}`, 'NL123456789B01'],
  'Oostenrijk': ['AT', String.raw`U\d{8}`, 'ATU12345678'],
  'Polen': ['PL', String.raw`\d{10}`, 'PL1234567890'],
  'Portugal': ['PT', String.raw`\d{9}`, 'PT123456789'],
  'Roemenië': ['RO', String.raw`[1-9]\d{1,9}`, 'RO1234567'],
  'Slovenië': ['SI', String.raw`\d{8}`, 'SI12345678'],
  'Slowakije': ['SK', String.raw`\d{10}`, 'SK1234567890'],
  'Spanje': ['ES', String.raw`[A-Z]\d{7}[A-Z0-9]|\d{8}[A-Z]`, 'ESB12345678'],
  'Tsjechië': ['CZ', String.raw`\d{8,10}`, 'CZ12345678'],
  'Zweden': ['SE', String.raw`\d{10}01`, 'SE123456789001'],
}

const maxLength = 30
const countryOfCode = Object.fromEntries(Object.entries(vatFormats).map(([country, [code]]) => [code, country]))
const patterns = Object.fromEntries(Object.values(vatFormats).map(([code, pattern]) => [code, new RegExp(`^${code}(?:${pattern})$`)]))

// Leeg is Nederland, net als bij isNetherlands in invoice.ts.
const countryName = (country?: string | null) => country?.trim() || 'Nederland'

// Zoals de server het bewaart: hoofdletters, zonder spaties, punten en streepjes, met de landcode van de klant ervoor als
// er geen landcode uit de EU staat.
export function cleanVatNumber(vatNumber: string | null | undefined, country: string | null | undefined) {
  const clean = (vatNumber ?? '').replace(/[\s./_-]/g, '').toUpperCase().slice(0, maxLength + 1)
  if (!clean) return ''
  const format = vatFormats[countryName(country)]
  if (!format) return clean
  const [code] = format
  if (code === 'EL' && clean.startsWith('GR')) return `EL${clean.slice(2)}`
  return countryOfCode[clean.slice(0, 2)] ? clean : code + clean
}

// Heeft het nummer de vorm van een btw-nummer uit het land van de klant? Alleen voor klanten in de EU.
export function vatNumberLooksValid(vatNumber: string | null | undefined, country: string | null | undefined) {
  const format = vatFormats[countryName(country)]
  const clean = cleanVatNumber(vatNumber, country)
  return !!format && !!clean && clean.length <= maxLength && patterns[format[0]]!.test(clean)
}

// Wat er niet klopt aan het btw-nummer, in dezelfde woorden als de server, of null.
export function vatNumberProblem(vatNumber: string | null | undefined, country: string | null | undefined) {
  const name = countryName(country)
  const format = vatFormats[name]
  const clean = cleanVatNumber(vatNumber, country)
  if (!format || !clean || vatNumberLooksValid(vatNumber, country)) return null
  const [code, , example] = format
  const prefix = clean.slice(0, 2)
  if (prefix !== code && countryOfCode[prefix])
    return `Dit btw-nummer begint met ${prefix} en hoort dus bij ${countryOfCode[prefix]}, maar de klant zit in ${name}. Een btw-nummer uit ${name} begint met ${code}, zoals ${example}. Klopt het land van de klant niet? Pas dat dan aan.`
  return `Dit btw-nummer heeft niet de vorm van een btw-nummer uit ${name}. Daar ziet een btw-nummer eruit als ${example}. Kijk het nummer na op de factuur of website van je klant.`
}
