import { Globe } from 'lucide-react'
import type { Language } from '../lib/types'
import { languages } from '../lib/docTexts'
import { Field } from './ui'

const label = (lang?: Language | null) => languages.find(l => l.id === (lang ?? 'nl'))?.label ?? 'Nederlands'

// De taal van een factuur of offerte kiezen. Alles wat je klant ziet staat in die taal; wat je zelf typt vertaalt Potel niet.
export function LanguageField({ value, onChange, disabled, kind }: {
  value?: Language | null; onChange: (lang: Language) => void; disabled?: boolean; kind: 'factuur' | 'creditnota' | 'offerte'
}) {
  return (
    <Field label={`Taal van de ${kind}`}>
      <select value={value ?? 'nl'} disabled={disabled} onChange={e => onChange(e.target.value as Language)}>
        {languages.map(l => <option key={l.id} value={l.id}>{l.label}</option>)}
      </select>
    </Field>
  )
}

// Een klein label boven het document in de editor, zodat je ziet in welke taal je klant hem krijgt. Niet in de PDF.
export function LanguageBadge({ lang, kind }: { lang?: Language | null; kind: 'factuur' | 'creditnota' | 'offerte' }) {
  return (
    <div className="paper-language no-print">
      <span className="badge badge-blue" title={`Je klant krijgt deze ${kind}, de mail en de pagina achter de link in het ${label(lang)}.`}>
        <Globe size={12} /> {label(lang)}
      </span>
    </div>
  )
}
