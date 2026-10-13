import { useState } from 'react'
import { CalendarRange, ChevronLeft, ChevronRight, Download, Euro, Wallet, TrendingUp, Timer, Info } from 'lucide-react'
import { Link } from 'react-router-dom'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import type { AnnualOverview } from '../lib/types'
import { euro, hours } from '../lib/format'
import { km } from '../lib/trips'
import { ErrorBox, Loading, PageHeader } from '../components/ui'
import { MiniStat } from './Time'

const monthName = (m: string) => new Date(`${m}-01T00:00:00`).toLocaleDateString('nl-NL', { month: 'short' })

export function AnnualPage() {
  const [year, setYear] = useState<number | null>(null)
  const [starter, setStarter] = useState(false)
  const params = new URLSearchParams()
  if (year) params.set('year', String(year))
  if (starter) params.set('starter', 'true')
  const { data, error, loading, reload } = useApi<AnnualOverview>(`/tax/annual${params.size ? `?${params}` : ''}`)
  const [exporting, setExporting] = useState(false)
  const toast = useToast()
  const shown = year ?? data?.year

  const exportCsv = async () => {
    if (!shown) return
    setExporting(true)
    try {
      await api.download(`/tax/annual.csv?year=${shown}`, `jaaroverzicht-${shown}.csv`)
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setExporting(false)
    }
  }

  const max = Math.max(1, ...(data?.months.map(m => Math.max(m.revenue, m.costs)) ?? []))
  const ind = data?.indication
  const criterionMinutes = (data?.hours.criterion ?? 1225) * 60
  const inv = data?.investments

  return (
    <>
      <PageHeader
        icon={<CalendarRange size={20} />}
        title="Jaaroverzicht"
        subtitle={data ? `${data.year} · ${data.invoices} facturen en ${data.expenses} uitgaven` : 'Per kalenderjaar'}
        actions={shown ? <>
          <div className="btn-group">
            <button className="btn" onClick={() => setYear(shown - 1)} aria-label="Vorig jaar"><ChevronLeft size={16} /></button>
            <button className="btn" disabled>{shown}</button>
            <button className="btn" onClick={() => setYear(shown + 1)} aria-label="Volgend jaar"><ChevronRight size={16} /></button>
          </div>
          <button className="btn btn-primary" disabled={exporting} onClick={exportCsv}><Download size={16} /> Exporteer voor je boekhouder</button>
        </> : undefined}
      />

      <div className="notice">
        <Info size={13} /> Een overzicht om je aangifte inkomstenbelasting voor te bereiden, geen belastingadvies. Concepten en voorbeelddata tellen niet mee.
      </div>

      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && <Loading />}

      {data && ind && (<>
        <div className="grid grid-stats" style={{ marginBottom: 18 }}>
          <MiniStat icon={<Euro size={18} />} accent="var(--green)" label="Omzet" value={euro(data.revenue)} sub="Zonder btw, op factuurdatum" />
          <MiniStat icon={<Wallet size={18} />} accent="var(--primary)" label="Kosten" value={euro(data.costs)}
            sub={data.deduction ? 'Zonder btw' : 'Met btw, want die trek je niet af'} />
          <MiniStat icon={<TrendingUp size={18} />} accent="var(--blue)" label="Resultaat voor belasting" value={euro(data.result)}
            sub={inv && inv.disposalGain > 0 ? `Omzet min kosten, plus ${euro(inv.disposalGain)} boekwinst` : 'Omzet min kosten'} />
          <MiniStat icon={<Timer size={18} />} accent={data.hours.met ? 'var(--green)' : 'var(--yellow)'} label="Urencriterium"
            value={`${Math.floor(data.hours.minutes / 60).toLocaleString('nl-NL')} uur`} progress={data.hours.minutes / criterionMinutes}
            sub={data.hours.met ? `Gehaald: ${data.hours.criterion.toLocaleString('nl-NL')} uur of meer`
              : `Nog ${Math.ceil((criterionMinutes - data.hours.minutes) / 60).toLocaleString('nl-NL')} uur tot ${data.hours.criterion.toLocaleString('nl-NL')}`} />
        </div>

        {data.possibleInvestments > 0 && (
          <div className="notice">
            Er {data.possibleInvestments === 1 ? 'staat 1 uitgave' : `staan ${data.possibleInvestments} uitgaven`} voor hardware van € 450 of meer in die geen investering
            {data.possibleInvestments === 1 ? ' is' : ' zijn'}. Zo'n bedrijfsmiddel schrijf je af over de jaren dat je het gebruikt; nu telt het nog helemaal mee in dit jaar.
            Markeer het bij <Link to="/kosten">Kosten</Link> als investering. <a href={data.depreciationSource} target="_blank" rel="noreferrer">Bron: Belastingdienst</a>
          </div>
        )}

        <div className="grid grid-2-1">
          <div className="card">
            <div className="card-head"><h3>Omzet per maand</h3><span className="muted">Zonder btw</span></div>
            <div className="card-body">
              <div className="bars bars-sm">
                {data.months.map(m => (
                  <div key={m.month} className="bar-col" title={`Omzet ${euro(m.revenue)}, kosten ${euro(m.costs)}`}>
                    <div className="bar-value">{m.revenue > 0 ? `${Math.round(m.revenue / 1000)}k` : ''}</div>
                    <div className="bar-track"><div className="bar" style={{ height: `${(Math.max(0, m.revenue) / max) * 100}%` }} /></div>
                    <div className="bar-label">{monthName(m.month)}</div>
                  </div>
                ))}
              </div>
            </div>
          </div>

          <div className="card">
            <div className="card-head"><h3>Kosten per categorie</h3><span className="muted">{data.deduction ? 'Zonder btw' : 'Met btw'}</span></div>
            {data.costsByCategory.length === 0 && data.mileage.trips === 0 && data.investments.count === 0 ? (
              <div className="card-body muted">Nog geen kosten in {data.year}.</div>
            ) : (
              <div className="table-wrap">
                <table>
                  <tbody>
                    {data.costsByCategory.map(c => (
                      <tr key={c.category}><td>{c.label}<div className="cell-sub">{c.count} {c.count === 1 ? 'uitgave' : 'uitgaven'}</div></td><td className="num">{euro(c.amount)}</td></tr>
                    ))}
                    {data.mileage.trips > 0 && (
                      <tr>
                        <td>
                          Kilometers eigen auto
                          <div className="cell-sub">
                            {data.mileage.rate != null
                              ? <>{km(data.mileage.businessKm)} à € {data.mileage.rate.toLocaleString('nl-NL', { minimumFractionDigits: 2 })} · <a href={data.mileage.source} target="_blank" rel="noreferrer">Bron</a></>
                              : data.mileage.note}
                          </div>
                        </td>
                        <td className="num">{euro(data.mileage.deduction)}</td>
                      </tr>
                    )}
                    {data.investments.count > 0 && (
                      <tr>
                        <td>
                          Afschrijving
                          <div className="cell-sub">
                            {data.investments.count} {data.investments.count === 1 ? 'bedrijfsmiddel' : 'bedrijfsmiddelen'} · <Link to="/investeringen">Investeringen</Link>
                          </div>
                        </td>
                        <td className="num">{euro(data.investments.depreciation)}</td>
                      </tr>
                    )}
                    {data.investments.disposalLoss > 0 && (
                      <tr><td>Afgeboekte boekwaarde<div className="cell-sub">Verkocht of weggedaan</div></td><td className="num">{euro(data.investments.disposalLoss)}</td></tr>
                    )}
                  </tbody>
                  <tfoot><tr><td>Totaal</td><td className="num"><strong>{euro(data.costs)}</strong></td></tr></tfoot>
                </table>
              </div>
            )}
          </div>
        </div>

        <div className="grid grid-1-1 mt">
          <div className="card">
            <div className="card-head"><h3>Uren</h3><span className="muted">Alle uren tellen mee</span></div>
            <div className="card-body">
              <div className="row between"><span>Factureerbaar</span><strong>{hours(data.hours.billableMinutes)}</strong></div>
              <div className="row between mt"><span>Niet factureerbaar</span><strong>{hours(data.hours.nonBillableMinutes)}</strong></div>
              <div className="row between mt"><span>Totaal</span><strong>{hours(data.hours.minutes)}</strong></div>
              <div className="progress mt"><div style={{ width: `${Math.min(100, (data.hours.minutes / criterionMinutes) * 100)}%` }} /></div>
              <p className="muted" style={{ fontSize: 13, marginBottom: 0 }}>
                Voor de zelfstandigenaftrek werk je minstens {data.hours.criterion.toLocaleString('nl-NL')} uur per kalenderjaar aan je onderneming. Ook tijd voor offertes
                en administratie telt, dus boek die uren ook. <a href={data.hoursSource} target="_blank" rel="noreferrer">Bron: Belastingdienst</a>
              </p>
            </div>
          </div>

          <div className="card">
            <div className="card-head">
              <h3>Indicatie aftrekposten</h3>
              <label className="check-row" style={{ fontSize: 13 }}>
                <input type="checkbox" checked={starter} onChange={e => setStarter(e.target.checked)} />
                <span>Ik ben starter</span>
              </label>
            </div>
            <div className="card-body">
              {ind.available ? (
                <div className="table-wrap">
                  <table>
                    <tbody>
                      <tr><td>Resultaat voor belasting</td><td className="num">{euro(ind.profit)}</td></tr>
                      {(ind.investmentDeduction > 0 || data.investments.invested > 0) && (
                        <tr>
                          <td>
                            Kleinschaligheidsinvesteringsaftrek
                            <div className="cell-sub">
                              {data.investments.kiaKnown ? `Over ${euro(data.investments.qualifying)} aan investeringen` : 'Bedragen nog niet bekend'}
                              {' · '}<a href={data.investments.kiaSource} target="_blank" rel="noreferrer">Bron</a>
                            </div>
                          </td>
                          <td className="num">{euro(-ind.investmentDeduction)}</td>
                        </tr>
                      )}
                      <tr><td>Zelfstandigenaftrek</td><td className="num">{euro(-ind.selfEmployedDeduction)}</td></tr>
                      {starter && <tr><td>Startersaftrek</td><td className="num">{euro(-ind.starterDeduction)}</td></tr>}
                      <tr><td>Na ondernemersaftrek</td><td className="num">{euro(ind.profitAfterDeductions)}</td></tr>
                      <tr>
                        <td>MKB-winstvrijstelling {ind.smePercentage != null ? `(${ind.smePercentage.toLocaleString('nl-NL')}%)` : '(nog niet bekend)'}</td>
                        <td className="num">{euro(-ind.smeExemption)}</td>
                      </tr>
                    </tbody>
                    <tfoot><tr><td>Belastbare winst (indicatie)</td><td className="num"><strong>{euro(ind.taxableProfit)}</strong></td></tr></tfoot>
                  </table>
                </div>
              ) : null}
              <ul className="muted" style={{ fontSize: 13, paddingLeft: 18, marginBottom: 0 }}>
                <li>Alleen een indicatie: je boekhouder of de aangifte rekent het precies uit.</li>
                {ind.notes.map(n => <li key={n}>{n}</li>)}
              </ul>
              {ind.sources.length > 0 && (
                <p className="muted" style={{ fontSize: 12, marginBottom: 0 }}>
                  Bron:{' '}
                  {ind.sources.map((s, i) => <span key={s}><a href={s} target="_blank" rel="noreferrer">{s.includes('mkb') ? 'MKB-winstvrijstelling' : 'zelfstandigenaftrek'} {data.year}</a>{i < ind.sources.length - 1 ? ', ' : ''}</span>)}
                </p>
              )}
            </div>
          </div>
        </div>
      </>)}
    </>
  )
}
