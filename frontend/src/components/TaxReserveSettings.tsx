import { useState } from 'react'
import { Link } from 'react-router-dom'
import { ChevronLeft, ChevronRight, PiggyBank, Save, Wallet, Info } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import { euro, utcDateTime } from '../lib/format'
import type { ProvisionalKind, ReserveMode, TaxReserveSettings } from '../lib/types'
import { ErrorBox, Field, Loading } from './ui'

type Form = Pick<TaxReserveSettings, 'wage' | 'wageTaxWithheld' | 'starter' | 'provisional' | 'provisionalAmount' | 'provisionalMonths' | 'mode' | 'percentage'>

const formOf = (s: TaxReserveSettings): Form => ({
  wage: s.wage, wageTaxWithheld: s.wageTaxWithheld, starter: s.starter, provisional: s.provisional,
  provisionalAmount: s.provisionalAmount, provisionalMonths: s.provisionalMonths, mode: s.mode, percentage: s.percentage,
})

// De instellingen voor de belastingpot, per jaar. Je loon en je voorlopige aanslag zijn privé: alleen beheerders zien ze.
// Staat los van het formulier met je bedrijfsgegevens, en heeft daarom een eigen knop om op te slaan.
export function TaxReserveSettingsTab({ admin }: { admin: boolean }) {
  if (!admin) return <p className="muted" style={{ marginTop: 0, fontSize: 13 }}>Alleen een beheerder kan de belastingpot zien en instellen.</p>
  return <TaxReserveForm />
}

function TaxReserveForm() {
  const [year, setYear] = useState(() => new Date().getFullYear())
  const { data, error, setData } = useApi<TaxReserveSettings>(`/tax/reserve/settings?year=${year}`)
  const [edit, setEdit] = useState<Form | null>(null)
  const [busy, setBusy] = useState(false)
  const toast = useToast()

  if (error) return <ErrorBox message={error} />
  if (!data || data.year !== year) return <Loading />
  const form = edit ?? formOf(data)
  const dirty = JSON.stringify(form) !== JSON.stringify(formOf(data))
  const set = (patch: Partial<Form>) => setEdit({ ...form, ...patch })
  const amount = (k: 'wage' | 'wageTaxWithheld' | 'provisionalAmount' | 'percentage') => ({
    type: 'number', min: 0, step: '0.01', value: form[k] || '', placeholder: '0',
    onChange: (e: React.ChangeEvent<HTMLInputElement>) => set({ [k]: e.target.value === '' ? 0 : Number(e.target.value) }),
  })
  const provisionalTotal = form.provisional === 'per-maand' ? form.provisionalAmount * form.provisionalMonths : form.provisional === 'totaal' ? form.provisionalAmount : 0

  const switchYear = (y: number) => {
    if (dirty && !confirm('Je wijzigingen zijn nog niet opgeslagen. Toch naar een ander jaar?')) return
    setEdit(null)
    setYear(y)
  }

  const save = async () => {
    setBusy(true)
    try {
      setData(await api.put<TaxReserveSettings>(`/tax/reserve/settings?year=${year}`, form))
      setEdit(null)
      toast(`Belastingpot voor ${year} opgeslagen`)
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  // Enter in een veld slaat deze instellingen op, niet het formulier van de bedrijfsgegevens eromheen.
  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && (e.target as HTMLElement).tagName === 'INPUT') { e.preventDefault(); if ((dirty || !data.saved) && !busy) save() }
  }

  return (
    <div onKeyDown={onKeyDown}>
      <div className="notice">
        <Info size={13} /> De belastingpot is een indicatie om te plannen, geen belastingadvies. Je ziet hem op het dashboard en op het{' '}
        <Link to="/jaaroverzicht#belastingpot">jaaroverzicht</Link>. Alleen beheerders zien deze bedragen.
      </div>

      <div className="row between wrap" style={{ marginBottom: 12 }}>
        <div className="btn-group">
          <button type="button" className="btn" onClick={() => switchYear(year - 1)} aria-label="Vorig jaar"><ChevronLeft size={16} /></button>
          <button type="button" className="btn" disabled>{year}</button>
          <button type="button" className="btn" onClick={() => switchYear(year + 1)} aria-label="Volgend jaar"><ChevronRight size={16} /></button>
        </div>
        <span className="muted" style={{ fontSize: 13 }}>
          {data.saved && data.updatedAt ? `Laatst bijgewerkt ${utcDateTime(data.updatedAt)}${data.updatedBy ? ` door ${data.updatedBy}` : ''}` : `Nog niets opgeslagen voor ${year}`}
        </span>
      </div>

      <div className="grid grid-1-1">
        <div className="card">
          <div className="card-head"><h3><PiggyBank size={16} /> Hoe je reserveert</h3></div>
          <div className="card-body form-grid">
            <div className="field-full">
              <label className="check-row">
                <input type="radio" name="reserve-mode" checked={form.mode === 'berekend'} onChange={() => set({ mode: 'berekend' as ReserveMode })} />
                <span>Potel rekent het uit</span>
              </label>
              <p className="muted" style={{ margin: '4px 0 0 24px', fontSize: 13 }}>
                Een schatting van de inkomstenbelasting en de bijdrage Zvw, met je resultaat, je uren, de ondernemersaftrek en je loon.
              </p>
            </div>
            <div className="field-full">
              <label className="check-row">
                <input type="radio" name="reserve-mode" checked={form.mode === 'percentage'} onChange={() => set({ mode: 'percentage' as ReserveMode })} />
                <span>Reserveer een vast percentage van elke betaalde factuur</span>
              </label>
              <p className="muted" style={{ margin: '4px 0 0 24px', fontSize: 13 }}>
                Eenvoudig: van elk bedrag dat binnenkomt zet je een vast deel opzij, gerekend over het bedrag zonder btw.
              </p>
            </div>
            {form.mode === 'percentage' && (
              <Field label="Percentage van elke betaalde factuur"><input {...amount('percentage')} max={100} step="0.5" /></Field>
            )}
            <p className="muted field-full" style={{ margin: 0, fontSize: 13 }}>
              De btw die je nog moet betalen staat altijd apart in je pot, ook met een vast percentage.
            </p>
          </div>
        </div>

        <div className="card">
          <div className="card-head"><h3><Wallet size={16} /> Inkomen en voorlopige aanslag in {year}</h3></div>
          <div className="card-body form-grid">
            <label className="check-row field-full">
              <input type="checkbox" checked={form.starter} onChange={e => set({ starter: e.target.checked })} />
              <span>Ik ben starter en krijg de startersaftrek</span>
            </label>
            <Field label="Loon uit loondienst over het jaar"><input {...amount('wage')} /></Field>
            <Field label="Ingehouden loonheffing"><input {...amount('wageTaxWithheld')} /></Field>
            <p className="muted field-full" style={{ margin: 0, fontSize: 13 }}>
              Werk je daarnaast in loondienst? Vul je fiscaal loon en de ingehouden loonheffing in, zoals op je jaaropgave of loonstrook. Weet je het nog
              niet precies, schat het dan voor het hele jaar.
            </p>
            <Field label="Voorlopige aanslag" full>
              <select value={form.provisional} onChange={e => set({ provisional: e.target.value as ProvisionalKind })}>
                <option value="geen">Geen voorlopige aanslag</option>
                <option value="totaal">In één keer betaald of te betalen</option>
                <option value="per-maand">Per maand</option>
              </select>
            </Field>
            {form.provisional !== 'geen' && (
              <Field label={form.provisional === 'per-maand' ? 'Bedrag per maand' : 'Bedrag van de aanslag'}><input {...amount('provisionalAmount')} /></Field>
            )}
            {form.provisional === 'per-maand' && (
              <Field label="Aantal maanden">
                <input type="number" min={1} max={12} step={1} value={form.provisionalMonths || ''}
                  onChange={e => set({ provisionalMonths: e.target.value === '' ? 0 : Number(e.target.value) })} />
              </Field>
            )}
            <p className="muted field-full" style={{ margin: 0, fontSize: 13 }}>
              {form.provisional === 'geen'
                ? 'Een voorlopige aanslag inkomstenbelasting betaal je al vooruit aan de Belastingdienst. Wat die dekt, hoef je niet zelf opzij te zetten.'
                : `Je voorlopige aanslag dekt ${euro(provisionalTotal)} van ${year}. Dat deel hoef je niet zelf opzij te zetten.`}
            </p>
          </div>
        </div>
      </div>

      <div className="row wrap mt" style={{ gap: 8 }}>
        <button type="button" className="btn btn-primary" disabled={busy || (!dirty && data.saved)} onClick={save}><Save size={15} /> Opslaan</button>
        {dirty && <button type="button" className="btn" onClick={() => setEdit(null)}>Wijzigingen weggooien</button>}
      </div>
    </div>
  )
}
