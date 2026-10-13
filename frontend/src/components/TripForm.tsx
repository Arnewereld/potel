import { useState } from 'react'
import { Lock, Repeat } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import type { Customer, Project, Trip, TripKind, TripRoute } from '../lib/types'
import { euro, toDateInput } from '../lib/format'
import { isBusiness, km, tripKindHelp, tripKinds } from '../lib/trips'
import { Field, Modal } from './ui'

// Een rit vastleggen of aanpassen. Een vaste route vult alles in één keer in; met de kilometerstand rekent het formulier
// de kilometers zelf uit. Een gefactureerde rit, of een rit van iemand anders, kun je alleen bekijken.
export function TripForm({ trip, date, routes, rate, year, onClose, onSaved }: {
  trip?: Trip | null
  date?: string
  routes: TripRoute[]
  // Het bedrag per zakelijke kilometer met je eigen auto in dit jaar; null als het nog niet vaststaat.
  rate: number | null
  year: number
  onClose: () => void
  onSaved: (routesChanged?: boolean) => void
}) {
  const { data: projects } = useApi<Project[]>('/projects')
  const { data: customers } = useApi<Customer[]>('/customers')
  const [form, setForm] = useState(() => ({
    date: trip?.date.slice(0, 10) ?? date ?? toDateInput(new Date()),
    from: trip?.from ?? '',
    to: trip?.to ?? '',
    kilometres: trip ? String(trip.kilometres) : '',
    purpose: trip?.purpose ?? '',
    kind: trip?.kind ?? 'zakelijk' as TripKind,
    projectId: trip?.projectId ?? 0,
    customerId: trip?.customerId ?? 0,
    odometerStart: trip?.odometerStart != null ? String(trip.odometerStart) : '',
    odometerEnd: trip?.odometerEnd != null ? String(trip.odometerEnd) : '',
    billable: trip?.billable ?? false,
  }))
  const [withReturn, setWithReturn] = useState(false)
  const [saveRoute, setSaveRoute] = useState(false)
  const [routeName, setRouteName] = useState('')
  const [busy, setBusy] = useState(false)
  const toast = useToast()

  const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) => setForm(f => ({ ...f, [key]: value }))
  const locked = trip?.invoiceId != null
  const readOnly = locked || (trip != null && !trip.canEdit)
  const business = isBusiness(form.kind)
  const start = form.odometerStart === '' ? null : Number(form.odometerStart)
  const end = form.odometerEnd === '' ? null : Number(form.odometerEnd)
  const fromOdometer = start != null && end != null && end > start
  const kilometres = fromOdometer ? end! - start! : Number(form.kilometres.replace(',', '.'))
  const project = projects?.find(p => p.id === form.projectId)
  const customer = customers?.find(c => c.id === (project?.customerId ?? form.customerId))
  const billRate = project?.kilometreRate ?? customer?.kilometreRate ?? null
  const selectable = (projects ?? []).filter(p => p.status !== 'afgerond' || p.id === form.projectId)

  const pickProject = (id: number) => {
    const p = projects?.find(x => x.id === id)
    setForm(f => ({ ...f, projectId: id, customerId: p ? p.customerId : f.customerId }))
  }

  const pickRoute = (id: number) => {
    const r = routes.find(x => x.id === id)
    if (!r) return
    setForm(f => ({
      ...f, from: r.from, to: r.to, kilometres: String(r.kilometres), purpose: r.purpose ?? f.purpose, kind: r.kind,
      projectId: r.projectId ?? 0, customerId: r.customerId ?? 0, billable: r.billable, odometerStart: '', odometerEnd: '',
    }))
  }

  const body = () => ({
    date: form.date, from: form.from, to: form.to, kilometres: Number.isFinite(kilometres) ? kilometres : 0, purpose: form.purpose || null, kind: form.kind,
    projectId: business ? form.projectId || null : null, customerId: business && !form.projectId ? form.customerId || null : null,
    odometerStart: start, odometerEnd: end, billable: business && form.billable,
  })

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    if (readOnly) return
    setBusy(true)
    try {
      const saved = trip ? await api.put<Trip>(`/trips/${trip.id}`, body()) : await api.post<Trip>('/trips', body())
      if (!trip && withReturn) await api.post(`/trips/${saved.id}/return`)
      if (saveRoute) {
        try {
          await api.post('/trips/routes', { name: routeName || null, from: saved.from, to: saved.to, kilometres: saved.kilometres, purpose: saved.purpose,
            kind: saved.kind, projectId: saved.projectId, customerId: saved.projectId ? null : saved.customerId, billable: saved.billable })
        } catch (err) {
          toast(`De rit is opgeslagen, maar de vaste route niet: ${(err as Error).message}`, 'error')
        }
      }
      toast(trip ? 'Rit aangepast' : withReturn ? 'Rit en retourrit toegevoegd' : 'Rit toegevoegd')
      onSaved(saveRoute)
    } catch (err) {
      toast((err as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const addReturn = async () => {
    if (!trip) return
    try {
      await api.post(`/trips/${trip.id}/return`)
      toast('Retourrit toegevoegd')
      onSaved()
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  const remove = async () => {
    if (!trip || !confirm(`De rit van ${trip.from} naar ${trip.to} verwijderen?`)) return
    try {
      await api.del(`/trips/${trip.id}`)
      toast('Rit verwijderd')
      onSaved()
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  return (
    <Modal
      wide
      title={trip ? (readOnly ? 'Rit bekijken' : 'Rit bewerken') : 'Rit toevoegen'}
      onClose={onClose}
      footer={<>
        {trip && !readOnly && <button className="btn btn-danger" type="button" onClick={remove}>Verwijderen</button>}
        {trip && <button className="btn" type="button" onClick={addReturn}><Repeat size={15} /> Retourrit</button>}
        <span className="spacer" />
        <button className="btn" type="button" onClick={onClose}>{readOnly ? 'Sluiten' : 'Annuleren'}</button>
        {!readOnly && <button className="btn btn-primary" form="trip-form" disabled={busy}>Opslaan</button>}
      </>}
    >
      {locked && (
        <div className="notice"><Lock size={13} /> Deze rit staat op {trip?.invoiceNumber ? `factuur ${trip.invoiceNumber}` : 'een conceptfactuur'} en ligt vast.
          {trip?.invoiceNumber ? '' : ' Haal hem van de factuur af om hem weer vrij te geven.'}</div>
      )}
      {!locked && trip && !trip.canEdit && (
        <div className="notice">Deze rit is vastgelegd door {trip.userName ?? 'iemand anders'}. Alleen die persoon of een beheerder kan hem aanpassen.</div>
      )}
      <form id="trip-form" onSubmit={save}>
        <fieldset className="plain-fieldset" style={{ padding: 0 }} disabled={readOnly}>
          <div className="form-grid">
            {!trip && routes.length > 0 && (
              <Field label="Vaste route" full>
                <select value={0} onChange={e => pickRoute(Number(e.target.value))}>
                  <option value={0}>Kies een vaste route om alles in te vullen…</option>
                  {routes.map(r => <option key={r.id} value={r.id}>{r.name} · {km(r.kilometres)}</option>)}
                </select>
              </Field>
            )}
            <Field label="Soort rit" full>
              <div className="segmented">
                {tripKinds.map(k => (
                  <button type="button" key={k.id} className={form.kind === k.id ? 'active' : ''} onClick={() => set('kind', k.id)}>{k.label}</button>
                ))}
              </div>
            </Field>
            <p className="muted field-full" style={{ fontSize: 13, margin: 0 }}>{tripKindHelp(form.kind, rate, year)}</p>
            <Field label="Datum"><input type="date" required value={form.date} onChange={e => set('date', e.target.value)} /></Field>
            <Field label="Kilometers *">
              <input type="number" required={!fromOdometer} min="0.1" step="0.1" inputMode="decimal" disabled={fromOdometer}
                value={fromOdometer ? String(kilometres) : form.kilometres} onChange={e => set('kilometres', e.target.value)} placeholder="0" />
            </Field>
            <Field label="Van *"><input required maxLength={200} autoFocus={!trip} value={form.from} onChange={e => set('from', e.target.value)} placeholder="Bijv. Keizersgracht 100, Amsterdam" /></Field>
            <Field label="Naar *"><input required maxLength={200} value={form.to} onChange={e => set('to', e.target.value)} placeholder="Bijv. Oudegracht 12, Utrecht" /></Field>
            <Field label="Kilometerstand bij vertrek (optioneel)">
              <input type="number" min="0" step="1" inputMode="numeric" value={form.odometerStart} onChange={e => set('odometerStart', e.target.value)} />
            </Field>
            <Field label="Kilometerstand bij aankomst (optioneel)">
              <input type="number" min="0" step="1" inputMode="numeric" value={form.odometerEnd} onChange={e => set('odometerEnd', e.target.value)} />
            </Field>
            {fromOdometer && <p className="muted field-full" style={{ fontSize: 13, margin: 0 }}>De kilometers komen uit de kilometerstand: {km(kilometres)}.</p>}
            <Field label={business ? 'Doel van de rit *' : 'Doel van de rit (optioneel)'} full>
              <input maxLength={500} required={business} value={form.purpose} onChange={e => set('purpose', e.target.value)}
                placeholder={business ? 'Bijv. overleg met Sanne over de koppeling' : 'Bijv. boodschappen'} />
            </Field>
            {business && (<>
              <Field label="Project (optioneel)">
                <select value={form.projectId} onChange={e => pickProject(Number(e.target.value))}>
                  <option value={0}>Geen project</option>
                  {selectable.map(p => <option key={p.id} value={p.id}>{p.name} · {p.customerName}</option>)}
                </select>
              </Field>
              <Field label="Klant (optioneel)">
                <select value={project ? project.customerId : form.customerId} disabled={!!project} title={project ? 'Hoort bij het gekozen project' : undefined}
                  onChange={e => set('customerId', Number(e.target.value))}>
                  <option value={0}>Geen klant</option>
                  {(customers ?? []).map(c => <option key={c.id} value={c.id}>{c.company || c.name}</option>)}
                </select>
              </Field>
              <label className={`check-row field-full ${customer ? '' : 'muted'}`}>
                <input type="checkbox" disabled={!customer} checked={form.billable && !!customer} onChange={e => set('billable', e.target.checked)} />
                <span>Doorbelasten aan de klant</span>
                <span className="muted">
                  · {!customer ? 'kies eerst een klant of project'
                    : billRate != null ? `tegen ${euro(billRate)} per km${Number.isFinite(kilometres) && kilometres > 0 ? `, samen ${euro(Math.round(kilometres * billRate * 100) / 100)}` : ''}`
                    : 'stel eerst een kilometertarief in bij het project of de klant'}
                </span>
              </label>
            </>)}
            {!trip && (<>
              <label className="check-row field-full">
                <input type="checkbox" checked={withReturn} onChange={e => setWithReturn(e.target.checked)} />
                <span>Retourrit ook vastleggen</span>
                <span className="muted">· dezelfde rit terug, op dezelfde dag</span>
              </label>
              <label className="check-row field-full">
                <input type="checkbox" checked={saveRoute} onChange={e => setSaveRoute(e.target.checked)} />
                <span>Bewaren als vaste route</span>
              </label>
              {saveRoute && (
                <Field label="Naam van de route (optioneel)" full>
                  <input maxLength={100} value={routeName} onChange={e => setRouteName(e.target.value)} placeholder={form.from && form.to ? `${form.from} naar ${form.to}` : 'Bijv. Fietsplein Utrecht'} />
                </Field>
              )}
            </>)}
          </div>
        </fieldset>
      </form>
    </Modal>
  )
}
