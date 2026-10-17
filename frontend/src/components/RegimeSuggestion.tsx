import { Lightbulb } from 'lucide-react'
import { Link } from 'react-router-dom'
import type { Customer, VatRegime } from '../lib/types'
import { inEu, isNetherlands, suggestedRegime } from '../lib/invoice'
import { vatNumberProblem } from '../lib/vat'

// Sommige landen krijgen een lidwoord: je klant zit in de Verenigde Staten.
const articles: Record<string, string> = { 'Verenigde Staten': 'de', 'Verenigd Koninkrijk': 'het', 'Filipijnen': 'de', 'Verenigde Arabische Emiraten': 'de' }
const withArticle = (country: string) => (articles[country] ? `${articles[country]} ${country}` : country)

// Een voorstel voor de btw-regeling als de klant in het buitenland zit en het concept een andere regeling heeft. Met één
// klik staat hij goed. Alleen voor een concept: een verstuurde factuur of offerte verandert nooit.
export function RegimeSuggestion({ regime, customer, onAccept, kind }: {
  regime: VatRegime; customer: Customer | null | undefined; onAccept: (regime: VatRegime) => void; kind: 'factuur' | 'offerte'
}) {
  if (!customer || isNetherlands(customer.country)) return null
  const name = customer.company?.trim() || customer.name
  const country = withArticle(customer.country!.trim())
  const suggestion = suggestedRegime(customer)

  if (suggestion && suggestion !== regime) {
    const text = suggestion === 'verlegd'
      ? `${name} zit in ${country} en heeft een btw-nummer met de juiste vorm. Dan verleg je de btw: er staat geen btw op de ${kind} en je klant draagt de btw zelf af.`
      : `${name} zit in ${country}, buiten de EU. Voor werk voor een bedrijf buiten de EU reken je geen Nederlandse btw.`
    return (
      <div className="notice regime-suggestion field-full">
        <Lightbulb size={15} />
        <span>{text}</span>
        <button type="button" className="btn btn-sm" onClick={() => onAccept(suggestion)}>
          {suggestion === 'verlegd' ? 'Btw verleggen' : 'Kies buiten de EU'}
        </button>
      </div>
    )
  }

  // Een klant in een ander EU-land zonder btw-nummer van de juiste vorm: verleggen kan dan nog niet.
  if (!suggestion && inEu(customer.country) && regime !== 'verlegd') {
    const problem = vatNumberProblem(customer.vatNumber, customer.country)
    const text = problem
      ? `Btw verleggen kan pas als het btw-nummer van ${name} klopt. ${problem}`
      : `${name} zit in ${country}, maar bij de klant staat geen btw-nummer. Is het een bedrijf? Vul dan het btw-nummer in, dan kun je de btw verleggen. Een particulier betaalt gewoon Nederlandse btw.`
    return (
      <div className="notice regime-suggestion field-full">
        <Lightbulb size={15} />
        <span>{text}</span>
        <Link className="btn btn-sm" to={`/klanten/${customer.id}`}>Klant bekijken</Link>
      </div>
    )
  }
  return null
}
