import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Landmark, ChevronLeft, ChevronRight, CalendarClock, Euro, Receipt, Info } from 'lucide-react'
import { useApi } from '../lib/useApi'
import type { VatReturn } from '../lib/types'
import { date, euro } from '../lib/format'
import { ErrorBox, Loading, PageHeader } from '../components/ui'
import { MiniStat } from './Time'

// Waar de rubrieken en regels vandaan komen; gecontroleerd op belastingdienst.nl op 12 oktober 2026.
const bd = 'https://www.belastingdienst.nl/wps/wcm/connect'
const sources: { label: string; url: string }[] = [
  { label: 'aangifte invullen (1a, 5a, 5b)', url: `${bd}/nl/btw/content/ik-moet-btw-aangifte-doen-hoe-vul-ik-die-in` },
  { label: 'btw verleggen (1e, 3b)', url: `${bd}/bldcontentnl/belastingdienst/zakelijk/btw/btw_berekenen_aan_uw_klanten/waarover_btw_berekenen/verleggingsregeling/hoe_werkt_btw_verleggen` },
  { label: 'rubriek 4a en 4b', url: `${bd}/nl/btw/content/wat-betekent-brexit-voor-de-btw` },
  { label: 'diensten uit de EU', url: `${bd}/bldcontentnl/belastingdienst/zakelijk/btw/zakendoen_met_het_buitenland/goederen_en_diensten_afnemen_uit_andere_eu_landen/aangifte_doen` },
  { label: 'opgaaf ICP', url: `${bd}/bldcontentnl/belastingdienst/zakelijk/btw/zakendoen_met_het_buitenland/goederen_en_diensten_naar_andere_eu_landen/opgaaf_icp/opgaaf_icp` },
  { label: 'diensten buiten de EU', url: `${bd}/bldcontentnl/belastingdienst/zakelijk/btw/zakendoen_met_het_buitenland/zakendoen_buiten_de_eu/aangifte_doen_als_u_zakendoet_buiten_de_eu/aangifte_doen_als_u_diensten_levert_aan_afnemers_in_niet_eu_landen` },
  { label: 'vrijgestelde omzet', url: `${bd}/bldcontentnl/belastingdienst/zakelijk/btw/tarieven_en_vrijstellingen/vrijstellingen/wat_moet_u_doen_als_u_vrijgestelde_goederen_of_diensten_levert` },
  { label: 'KOR', url: `${bd}/bldcontentnl/belastingdienst/zakelijk/btw/hoe_werkt_de_btw/kleineondernemersregeling/wat-betekent-meedoen-met-de-kleineondernemersregeling` },
  { label: 'incidentele aangifte', url: `${bd}/bldcontentnl/belastingdienst/zakelijk/btw/btw_aangifte_doen_en_betalen/btw-aangifte-waar-moet-u-aan-denken/incidentele-btw-aangifte` },
  { label: 'uiterste data', url: `${bd}/nl/btw/content/uiterste-aangifte-en-betaaldatums` },
]

const rubricLabel: Record<string, string> = {
  'buiten-eu': 'Buiten de EU', vrijgesteld: 'Vrijgesteld', kor: 'KOR',
}

export function VatReturnPage() {
  const [selected, setSelected] = useState<{ year: number; quarter: number } | null>(null)
  const [withDemo, setWithDemo] = useState(false)
  const params = new URLSearchParams()
  if (selected) { params.set('year', String(selected.year)); params.set('quarter', String(selected.quarter)) }
  if (withDemo) params.set('demo', 'true')
  const { data, error, loading, reload } = useApi<VatReturn>(`/tax/vat-return${params.size ? `?${params}` : ''}`)
  const navigate = useNavigate()

  const year = selected?.year ?? data?.year
  const quarter = selected?.quarter ?? data?.quarter
  const pick = (y: number, q: number) => setSelected({ year: y, quarter: q })
  const refund = (data?.total ?? 0) < 0
  const outside = data ? data.outsideEu + data.exempt + data.korRevenue : 0
  const rubric3b = data?.rubrics.find(r => r.code === '3b')?.base ?? 0

  return (
    <>
      <PageHeader
        icon={<Landmark size={20} />}
        title="Btw-aangifte"
        subtitle={data ? `${data.label} · ${date(data.from)} t/m ${date(data.to)}` : 'Per kwartaal'}
        actions={year && quarter ? <>
          <div className="btn-group">
            <button className="btn" onClick={() => pick(year - 1, quarter)} aria-label="Vorig jaar"><ChevronLeft size={16} /></button>
            <button className="btn" disabled>{year}</button>
            <button className="btn" onClick={() => pick(year + 1, quarter)} aria-label="Volgend jaar"><ChevronRight size={16} /></button>
          </div>
          <div className="segmented">
            {[1, 2, 3, 4].map(q => <button key={q} className={q === quarter ? 'active' : ''} onClick={() => pick(year, q)}>Q{q}</button>)}
          </div>
        </> : undefined}
      />

      <div className="notice">
        <Info size={13} /> Hulp bij het invullen van je btw-aangifte, geen belastingadvies. Controleer de bedragen voordat je ze in Mijn Belastingdienst Zakelijk overneemt.
      </div>

      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && <Loading />}

      {data && (<>
        {data.kor && (
          <div className="notice">
            <strong>Je doet mee met de kleineondernemersregeling (KOR).</strong> Dan doe je geen btw-aangifte, behalve in een aantal gevallen: dan doe je een
            incidentele aangifte. Staat hieronder btw in rubriek 4a of 4b, kijk dan met het hulpmiddel van de Belastingdienst of dat voor jou geldt.
            De cijfers hieronder zijn ter informatie.
          </div>
        )}
        {data.pending && (data.pending.year !== data.year || data.pending.quarter !== data.quarter) && (
          <div className="notice row between">
            <span>De aangifte over {data.pending.label} moet uiterlijk {date(data.pending.dueDate)} binnen zijn.</span>
            <button className="btn btn-sm" onClick={() => pick(data.pending!.year, data.pending!.quarter)}>Bekijk {data.pending.label}</button>
          </div>
        )}
        {data.demoData && (
          <div className="notice">
            <label className="check-row">
              <input type="checkbox" checked={withDemo} onChange={e => setWithDemo(e.target.checked)} />
              <span>{data.includesDemo
                ? 'Je ziet nu ook de voorbeelddata. Neem deze bedragen niet over in je aangifte.'
                : 'Voorbeeldfacturen en -kosten tellen niet mee. Vink aan om te zien hoe het eruitziet met de voorbeelddata.'}</span>
            </label>
          </div>
        )}

        <div className="grid grid-stats" style={{ marginBottom: 18 }}>
          <MiniStat icon={<Euro size={18} />} accent={refund ? 'var(--green)' : 'var(--purple)'} label={refund ? 'Terug te vragen' : 'Te betalen'}
            value={euro(Math.abs(data.total))} sub="Rubriek 5a min 5b" />
          <MiniStat icon={<CalendarClock size={18} />} accent="var(--yellow)" label={data.kor ? 'Aangifte' : 'Uiterlijk aangeven en betalen'}
            value={data.kor ? 'Niet nodig' : date(data.dueDate)} sub={data.kor ? 'Met de KOR doe je geen btw-aangifte' : 'Laatste dag van de maand na het kwartaal'} />
          <MiniStat icon={<Receipt size={18} />} accent="var(--blue)" label="Omzet dit kwartaal" value={euro(data.revenue)} sub="Zonder btw, alle facturen" />
          <MiniStat icon={<Landmark size={18} />} accent="var(--green)" label="Voorbelasting" value={euro(data.deductible)}
            sub={data.deduction ? 'Btw op je kosten, rubriek 5b' : 'Met jouw btw-regeling trek je geen btw af'} onClick={() => navigate('/kosten')} />
        </div>

        <div className="grid grid-2-1">
          <div className="card">
            <div className="card-head"><h3>Rubrieken</h3><span className="muted">{data.kor ? 'Ter informatie' : 'Zo vul je de aangifte in'}</span></div>
            <div className="table-wrap">
              <table>
                <thead><tr><th>Rubriek</th><th>Omschrijving</th><th className="num">Bedrag</th><th className="num">Btw</th></tr></thead>
                <tbody>
                  {data.rubrics.map(r => (
                    <tr key={r.code}>
                      <td><strong>{r.code}</strong></td>
                      <td>{r.title}</td>
                      <td className="num">{r.code === '5a' || r.code === '5b' ? '' : euro(r.base)}</td>
                      <td className="num">{r.vat === null ? <span className="muted">·</span> : euro(r.vat)}</td>
                    </tr>
                  ))}
                </tbody>
                <tfoot>
                  <tr>
                    <td colSpan={3}>{refund ? 'Terug te vragen' : 'Te betalen'}</td>
                    <td className="num"><strong className={refund ? 'text-green' : ''}>{euro(Math.abs(data.total))}</strong></td>
                  </tr>
                </tfoot>
              </table>
            </div>
          </div>

          <div>
            <div className="card">
              <div className="card-head"><h3>Hoe we tellen</h3></div>
              <div className="card-body muted" style={{ fontSize: 13, lineHeight: 1.55 }}>
                <p style={{ marginTop: 0 }}>Facturen tellen op hun factuurdatum; een creditnota trekt het bedrag af in het kwartaal van de creditnota. Concepten en voorbeeldfacturen tellen niet mee.</p>
                <p>Diensten aan ondernemers in een ander EU-land (3b en de opgaaf ICP) tellen in het kwartaal waarin je ze leverde: het einde van de leverperiode op de factuur.</p>
                <p style={{ marginBottom: 0 }}>Kosten tellen op de datum van de uitgave. Verlegde btw geef je aan in 4a of 4b en trek je in 5b weer af{data.deduction ? '' : ', behalve met de KOR of vrijgestelde omzet'}.</p>
              </div>
            </div>
            {outside !== 0 && (
              <div className="card mt">
                <div className="card-head"><h3>Niet in de aangifte</h3></div>
                <div className="card-body" style={{ fontSize: 13 }}>
                  {data.outsideEu !== 0 && <div className="row between"><span>Diensten aan ondernemers buiten de EU</span><strong>{euro(data.outsideEu)}</strong></div>}
                  {data.exempt !== 0 && <div className="row between"><span>Vrijgestelde omzet</span><strong>{euro(data.exempt)}</strong></div>}
                  {data.korRevenue !== 0 && <div className="row between"><span>Omzet met de KOR</span><strong>{euro(data.korRevenue)}</strong></div>}
                  {data.exempt !== 0 && <p className="muted" style={{ marginBottom: 0 }}>Over vrijgestelde omzet doe je geen btw-aangifte. De btw op kosten die daarbij horen trek je niet af; heb je ook omzet met btw, dan mag je een deel aftrekken.</p>}
                </div>
              </div>
            )}
          </div>
        </div>

        {(data.icp.length > 0 || rubric3b !== 0) && (
          <div className="card mt">
            <div className="card-head"><h3>Opgaaf ICP</h3><span className="muted">Het totaal hoort gelijk te zijn aan rubriek 3b</span></div>
            <div className="table-wrap">
              <table>
                <thead><tr><th>Btw-nummer</th><th>Klant</th><th>Land</th><th className="num">Bedrag</th></tr></thead>
                <tbody>
                  {data.icp.map(l => (
                    <tr key={l.vatNumber}><td><strong>{l.vatNumber}</strong></td><td>{l.customer}</td><td className="muted">{l.country}</td><td className="num">{euro(l.amount)}</td></tr>
                  ))}
                </tbody>
                <tfoot><tr><td colSpan={3}>Totaal</td><td className="num"><strong>{euro(data.icp.reduce((a, l) => a + l.amount, 0))}</strong></td></tr></tfoot>
              </table>
            </div>
          </div>
        )}

        {data.sources.length > 0 && (
          <div className="card mt">
            <div className="card-head"><h3>Waar de bedragen vandaan komen</h3><span className="muted">{data.sources.length} regels</span></div>
            <div className="table-wrap">
              <table>
                <thead><tr><th>Datum</th><th>Factuur of uitgave</th><th>Rubriek</th><th className="num">Bedrag</th><th className="num">Btw</th></tr></thead>
                <tbody>
                  {data.sources.map((s, i) => (
                    <tr key={`${s.kind}-${s.id}-${i}`} className="clickable" onClick={() => navigate(s.kind === 'factuur' ? `/facturen/${s.id}` : '/kosten')}>
                      <td className="muted nowrap">{date(s.date)}</td>
                      <td>{s.kind === 'factuur' ? <><strong>{s.number}</strong> <span className="muted">{s.name}</span></> : <><strong>{s.name}</strong> <span className="muted">uitgave</span></>}</td>
                      <td>{rubricLabel[s.rubric] ?? s.rubric}</td>
                      <td className="num">{euro(s.base)}</td>
                      <td className="num">{euro(s.vat)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}

        <p className="muted" style={{ fontSize: 12, marginTop: 16 }}>
          Bron: Belastingdienst,{' '}
          {sources.map((s, i) => <span key={s.url}><a href={s.url} target="_blank" rel="noreferrer">{s.label}</a>{i < sources.length - 1 ? ', ' : '.'}</span>)}
        </p>
      </>)}
    </>
  )
}
