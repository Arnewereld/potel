import { useMemo, useState } from 'react'
import { Car, Plus, ChevronLeft, ChevronRight, Repeat, Info, Briefcase, Home, Receipt, Trash2, MapPin, Lock } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import { useNewRequest } from '../lib/palette'
import type { Trip, TripRoute, TripYear } from '../lib/types'
import { date, euro, toDateInput } from '../lib/format'
import { km, tripKinds } from '../lib/trips'
import { Badge, Empty, ErrorBox, Loading, PageHeader } from '../components/ui'
import { TripForm } from '../components/TripForm'
import { MiniStat } from './Time'

const monthName = (m: string) => new Date(`${m}-01T00:00:00`).toLocaleDateString('nl-NL', { month: 'long' })
const kindTone = { zakelijk: 'blue', 'auto-van-de-zaak': 'purple', prive: 'gray' } as const

export function TripsPage() {
  const [today] = useState(() => toDateInput(new Date()))
  const [year, setYear] = useState(() => Number(today.slice(0, 4)))
  const [month, setMonth] = useState<string>('alle')
  const { data, error, loading, reload } = useApi<TripYear>(`/trips?year=${year}`)
  const { data: routes, reload: reloadRoutes } = useApi<TripRoute[]>('/trips/routes')
  const [editing, setEditing] = useState<{ trip?: Trip } | null>(null)
  // "Nieuwe rit" vanuit het zoekvenster.
  useNewRequest('rit', () => setEditing({}))
  const toast = useToast()

  const trips = useMemo(() => (data?.trips ?? []).filter(t => month === 'alle' || t.date.startsWith(month)), [data, month])
  const sum = (kind: Trip['kind']) => trips.filter(t => t.kind === kind).reduce((a, t) => a + t.kilometres, 0)
  // Nog niet gefactureerd en door te belasten, tegen het tarief van het project of de klant.
  const open = (data?.trips ?? []).filter(t => t.billable && t.invoiceId == null)
  const openValue = open.reduce((a, t) => a + (t.rate != null ? Math.round(t.kilometres * t.rate * 100) / 100 : 0), 0)
  const shownMonths = (data?.months ?? []).filter(m => m.trips > 0)
  const newDate = year === Number(today.slice(0, 4)) ? today : `${year}-01-01`
  const rateText = data?.rate != null ? `€ ${data.rate.toLocaleString('nl-NL', { minimumFractionDigits: 2 })}` : null

  const addReturn = async (t: Trip) => {
    try {
      await api.post(`/trips/${t.id}/return`)
      toast(`Retourrit ${t.to} naar ${t.from} toegevoegd`)
      reload(true)
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  const removeRoute = async (r: TripRoute) => {
    if (!confirm(`De vaste route "${r.name}" verwijderen? Je ritten blijven staan.`)) return
    try {
      await api.del(`/trips/routes/${r.id}`)
      toast('Vaste route verwijderd')
      reloadRoutes(true)
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  return (
    <>
      <PageHeader
        icon={<Car size={20} />}
        title="Ritten"
        subtitle={data ? `${year} · ${data.count} ${data.count === 1 ? 'rit' : 'ritten'} · ${km(data.businessKm + data.companyCarKm)} zakelijk` : 'Kilometerregistratie'}
        actions={<>
          <select value={month} onChange={e => setMonth(e.target.value)} aria-label="Maand">
            <option value="alle">Hele jaar</option>
            {(data?.months ?? []).map(m => <option key={m.month} value={m.month}>{monthName(m.month)}</option>)}
          </select>
          <div className="btn-group">
            <button className="btn" onClick={() => { setYear(y => y - 1); setMonth('alle') }} aria-label="Vorig jaar"><ChevronLeft size={16} /></button>
            <button className="btn" disabled>{year}</button>
            <button className="btn" onClick={() => { setYear(y => y + 1); setMonth('alle') }} aria-label="Volgend jaar"><ChevronRight size={16} /></button>
          </div>
          <button className="btn btn-primary" onClick={() => setEditing({})}><Plus size={16} /> Rit toevoegen</button>
        </>}
      />

      {data && (
        <div className="notice">
          <Info size={13} /> Een hulp bij je kilometeradministratie, geen belastingadvies.{' '}
          {rateText ? `Zakelijke kilometers met je eigen auto trek je in ${year} af tegen ${rateText} per kilometer.` : data.note}
          {' '}Woon-werkverkeer met je eigen auto telt daarbij als zakelijk. Met een auto van de zaak trek je de autokosten zelf af, dus die kilometers leggen we alleen vast.
          {' '}<a href={data.rateSource} target="_blank" rel="noreferrer">Bron: Belastingdienst</a>
        </div>
      )}

      {data && (
        <div className="grid grid-stats" style={{ marginBottom: 18 }}>
          <MiniStat icon={<Car size={18} />} accent="var(--blue)" label="Zakelijk, eigen auto" value={km(sum('zakelijk'))}
            sub={month !== 'alle' ? monthName(month)
              : !rateText ? 'Bedrag per km nog niet bekend'
              : `Aftrek ${euro(data.deduction)} (${rateText} per km)${data.trips.some(t => t.demo) ? ', zonder voorbeeldritten' : ''}`} />
          <MiniStat icon={<Briefcase size={18} />} accent="var(--purple)" label="Auto van de zaak" value={km(sum('auto-van-de-zaak'))} sub="Alleen vastgelegd" />
          <MiniStat icon={<Home size={18} />} accent="var(--muted)" label="Privé" value={km(sum('prive'))} sub="Telt niet mee voor je winst" />
          <MiniStat icon={<Receipt size={18} />} accent="var(--green)" label="Nog door te belasten" value={euro(openValue)}
            sub={open.length ? `${open.length} ${open.length === 1 ? 'rit' : 'ritten'} via Factureer of de factuur` : 'Alles is gefactureerd'} />
        </div>
      )}

      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && <Loading />}

      {data && (
        <div className="grid grid-2-1">
          <div className="card">
            {trips.length === 0 ? (
              <Empty icon={<Car size={22} />} title={month === 'alle' ? `Nog geen ritten in ${year}` : `Geen ritten in ${monthName(month)}`}
                text="Leg je ritten vast met datum, adressen, kilometers en doel. Met een vaste route gaat dat in een paar tellen."
                action={<button className="btn btn-primary" onClick={() => setEditing({})}><Plus size={16} /> Rit toevoegen</button>} />
            ) : (
              <div className="table-wrap">
                <table>
                  <thead><tr><th>Datum</th><th>Rit</th><th className="num">Km</th><th /></tr></thead>
                  <tbody>
                    {trips.map(t => (
                      <tr key={t.id} className="clickable" onClick={() => setEditing({ trip: t })}>
                        <td className="muted nowrap">{date(t.date)}</td>
                        <td>
                          <div>{t.from} <span className="muted">naar</span> {t.to}</div>
                          <div className="cell-sub">
                            <Badge tone={kindTone[t.kind]}>{tripKinds.find(k => k.id === t.kind)?.short}</Badge>
                            {t.demo && <> <Badge tone="gray">voorbeeld</Badge></>}
                            {t.purpose ? ` ${t.purpose}` : ''}
                          </div>
                          {(t.projectName || t.customerName) && (
                            <div className="cell-sub">
                              {[t.projectName, t.customerName].filter(Boolean).join(' · ')}
                              {t.billable && (t.invoiceId != null
                                ? <> · <Lock size={11} /> {t.invoiceNumber ? `op factuur ${t.invoiceNumber}` : 'op een conceptfactuur'}</>
                                : t.rate != null ? ` · door te belasten à ${euro(t.rate)}` : ' · door te belasten, nog zonder tarief')}
                            </div>
                          )}
                        </td>
                        <td className="num nowrap"><strong>{km(t.kilometres)}</strong></td>
                        <td className="row-actions" onClick={ev => ev.stopPropagation()}>
                          <button className="icon-btn" title={`Retourrit: ${t.to} naar ${t.from}`} onClick={() => addReturn(t)}><Repeat size={16} /></button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                  <tfoot>
                    <tr><td colSpan={2}>Totaal</td><td className="num nowrap"><strong>{km(trips.reduce((a, t) => a + t.kilometres, 0))}</strong></td><td /></tr>
                  </tfoot>
                </table>
              </div>
            )}
          </div>

          <div>
            <div className="card">
              <div className="card-head"><h3>Per maand</h3><span className="muted">{year}</span></div>
              {shownMonths.length === 0 ? <div className="card-body muted">Nog geen ritten in {year}.</div> : (
                <div className="table-wrap">
                  <table>
                    <thead><tr><th>Maand</th><th className="num">Eigen auto</th><th className="num">Van de zaak</th><th className="num">Privé</th></tr></thead>
                    <tbody>
                      {shownMonths.map(m => (
                        <tr key={m.month} className={`clickable ${month === m.month ? 'selected' : ''}`} onClick={() => setMonth(month === m.month ? 'alle' : m.month)}>
                          <td>{monthName(m.month)}<div className="cell-sub">{m.trips} {m.trips === 1 ? 'rit' : 'ritten'}</div></td>
                          <td className="num nowrap">{km(m.businessKm)}</td>
                          <td className="num nowrap">{km(m.companyCarKm)}</td>
                          <td className="num nowrap">{km(m.privateKm)}</td>
                        </tr>
                      ))}
                    </tbody>
                    <tfoot>
                      <tr><td>{year}</td><td className="num nowrap"><strong>{km(data.businessKm)}</strong></td><td className="num nowrap"><strong>{km(data.companyCarKm)}</strong></td><td className="num nowrap"><strong>{km(data.privateKm)}</strong></td></tr>
                    </tfoot>
                  </table>
                </div>
              )}
            </div>

            <div className="card mt">
              <div className="card-head"><h3>Vaste routes</h3><span className="muted">Kies ze bij een nieuwe rit</span></div>
              {(routes ?? []).length === 0 ? (
                <div className="card-body muted" style={{ fontSize: 13 }}>Nog geen vaste routes. Vink bij een nieuwe rit "Bewaren als vaste route" aan.</div>
              ) : (
                <div className="table-wrap">
                  <table>
                    <tbody>
                      {routes!.map(r => (
                        <tr key={r.id}>
                          <td><MapPin size={13} className="muted" /> <strong>{r.name}</strong><div className="cell-sub">{r.from} naar {r.to}</div></td>
                          <td className="num nowrap">{km(r.kilometres)}</td>
                          <td className="row-actions"><button className="icon-btn danger" title="Vaste route verwijderen" onClick={() => removeRoute(r)}><Trash2 size={15} /></button></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          </div>
        </div>
      )}

      {editing && data && (
        <TripForm trip={editing.trip} date={newDate} routes={routes ?? []} rate={data.rate} year={year}
          onClose={() => setEditing(null)}
          onSaved={routesChanged => { setEditing(null); reload(true); if (routesChanged) reloadRoutes(true) }} />
      )}
    </>
  )
}
