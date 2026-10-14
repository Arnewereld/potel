import { Link } from 'react-router-dom'
import { PiggyBank, ShieldCheck, Wallet, CalendarClock, Landmark, Settings as SettingsIcon } from 'lucide-react'
import type { IncomeTaxEstimate, TaxReserve } from '../lib/types'
import { date, euro } from '../lib/format'
import { MiniStat } from '../pages/Time'

const pct = (n: number) => `${n.toLocaleString('nl-NL', { maximumFractionDigits: 3 })}%`

// Eén regel van de som, met een kolom voor tot nu toe (alleen in het lopende jaar) en een voor heel het jaar.
function Line({ label, sub, so, full, pick, strong, minus }: {
  label: string; sub?: React.ReactNode; so: IncomeTaxEstimate | null; full: IncomeTaxEstimate
  pick: (e: IncomeTaxEstimate) => number; strong?: boolean; minus?: boolean
}) {
  const cell = (e: IncomeTaxEstimate) => {
    const v = euro(minus ? -pick(e) : pick(e))
    return <td className="num">{strong ? <strong>{v}</strong> : v}</td>
  }
  return (
    <tr>
      <td>{strong ? <strong>{label}</strong> : label}{sub && <div className="cell-sub">{sub}</div>}</td>
      {so && cell(so)}
      {cell(full)}
    </tr>
  )
}

// De som van de inkomstenbelasting en de bijdrage Zvw, stap voor stap.
function Calculation({ r, full }: { r: TaxReserve; full: IncomeTaxEstimate }) {
  const so = r.soFar
  const hours = Math.floor(r.hours.minutes / 60).toLocaleString('nl-NL')
  const expected = r.expectedHours.toLocaleString('nl-NL')
  const hoursText = r.hours.met ? `Urencriterium gehaald (${hours} uur)`
    : r.hoursMet ? `Urencriterium verwacht (${hours} uur, doorgerekend ${expected})`
    : `Urencriterium niet gehaald (${hours} uur${r.current ? `, doorgerekend ${expected}` : ''})`
  const lost = full.generalCredit + full.labourCredit - full.credits
  // De toelichting gaat over heel het jaar; met twee kolommen zeggen we dat erbij.
  const whole = so ? 'Heel het jaar: ' : ''
  return (
    <div className="table-wrap">
      <table>
        {so && <thead><tr><th /><th className="num">Tot nu toe</th><th className="num">Heel {r.year}</th></tr></thead>}
        <tbody>
          <Line label="Resultaat voor belasting" sub={r.current ? 'Heel het jaar: doorgerekend naar rato van de dagen' : undefined} so={so} full={full} pick={e => e.result} />
          {full.investmentDeduction > 0 && <Line label="Kleinschaligheidsinvesteringsaftrek" so={so} full={full} pick={e => e.investmentDeduction} minus />}
          <Line label="Zelfstandigenaftrek" sub={hoursText} so={so} full={full} pick={e => e.selfEmployedDeduction} minus />
          {full.starter && <Line label="Startersaftrek" so={so} full={full} pick={e => e.starterDeduction} minus />}
          <Line label="MKB-winstvrijstelling" so={so} full={full} pick={e => e.smeExemption} minus />
          <Line label="Belastbare winst" so={so} full={full} pick={e => e.taxableProfit} strong />
          {full.wage > 0 && <Line label="Loon uit loondienst" sub={so ? 'Tot nu toe: naar rato van de dagen' : undefined} so={so} full={full} pick={e => e.wage} />}
          {full.wage > 0 && <Line label="Belastbaar inkomen box 1" so={so} full={full} pick={e => e.taxableIncome} strong />}
          {full.brackets.map((b, i) => (
            <Line key={i} label={`Schijf ${i + 1}: ${pct(b.rate)}`} sub={b.upTo != null ? `Van ${euro(b.from)} tot ${euro(b.upTo)}` : `Boven ${euro(b.from)}`}
              so={so} full={full} pick={e => e.brackets[i]?.tax ?? 0} />
          ))}
          {full.rateAdjustment > 0 && <Line label="Tariefsaanpassing" sub="Je aftrek als ondernemer levert minder op boven de grens" so={so} full={full} pick={e => e.rateAdjustment} />}
          <Line label="Heffingskortingen" so={so} full={full} pick={e => e.credits} minus
            sub={<>{whole}{so ? 'algemene' : 'Algemene'} heffingskorting {euro(full.generalCredit)} en arbeidskorting {euro(full.labourCredit)}
              {lost > 0 ? `; ${euro(lost)} daarvan valt weg, want ze zijn nooit hoger dan de belasting` : ''}</>} />
          <Line label="Inkomstenbelasting" so={so} full={full} pick={e => e.incomeTax} strong />
          <Line label="Bijdrage Zvw" sub={`${whole}${so ? 'over' : 'Over'} ${euro(full.zvwBase)} van je belastbare winst`} so={so} full={full} pick={e => e.zvw} />
          {full.wageTaxWithheld > 0 && <Line label="Ingehouden loonheffing" sub="Die heb je via je werkgever al betaald" so={so} full={full} pick={e => e.wageTaxWithheld} minus />}
        </tbody>
        <tfoot>
          <tr>
            <td>{full.due < 0 ? 'Terug te krijgen' : 'Te betalen'}</td>
            {so && <td className="num"><strong>{euro(so.due)}</strong></td>}
            <td className="num"><strong>{euro(full.due)}</strong></td>
          </tr>
        </tfoot>
      </table>
    </div>
  )
}

// Met een vast percentage: wat er betaald is en het deel dat je daarvan opzij zet.
function PercentageTable({ r }: { r: TaxReserve }) {
  return (
    <div className="table-wrap">
      <table>
        {r.current && <thead><tr><th /><th className="num">Tot nu toe</th><th className="num">Heel {r.year}</th></tr></thead>}
        <tbody>
          <tr>
            <td>Betaald op je facturen<div className="cell-sub">Zonder btw, op de datum van de betaling</div></td>
            {r.current && <td className="num">{euro(r.paidSoFar)}</td>}
            <td className="num">{euro(r.paidFullYear)}</td>
          </tr>
          <tr><td>Percentage</td>{r.current && <td className="num">{pct(r.settings.percentage)}</td>}<td className="num">{pct(r.settings.percentage)}</td></tr>
        </tbody>
        <tfoot>
          <tr><td>Opzij zetten</td>{r.current && <td className="num"><strong>{euro(r.soFarAmount)}</strong></td>}<td className="num"><strong>{euro(r.total)}</strong></td></tr>
        </tfoot>
      </table>
    </div>
  )
}

// De belastingpot op het jaaroverzicht: vier bedragen, de btw die nog betaald moet worden, de som en wat er niet in zit.
export function TaxReserveSection({ r }: { r: TaxReserve }) {
  const s = r.settings
  const calculated = r.mode === 'berekend'
  const provisionalSub = s.provisional === 'per-maand' ? `${euro(s.provisionalAmount)} per maand, ${s.provisionalMonths} ${s.provisionalMonths === 1 ? 'maand' : 'maanden'}`
    : s.provisional === 'totaal' ? 'Je voorlopige aanslag' : 'Geen voorlopige aanslag ingevuld'
  return (
    <section className="tax-reserve" id="belastingpot">
      <div className="row between wrap tax-reserve-head">
        <h3 className="row"><PiggyBank size={18} /> Belastingpot {r.year}</h3>
        <Link className="btn btn-sm" to="/instellingen?tab=belastingpot"><SettingsIcon size={14} /> Instellingen</Link>
      </div>

      {r.missing && <div className="notice">{r.missing}</div>}
      {r.started && (<>
      {r.available && r.current && (
        <p className="tax-reserve-lead">
          {r.vatTotal > 0 && <>Houd <strong>{euro(r.vatTotal)}</strong> apart voor de btw. </>}
          {r.remaining > 0
            ? <>Voor de inkomstenbelasting en de bijdrage Zvw zet je dit jaar nog <strong>{euro(r.remaining)}</strong> opzij
              {r.perMonth != null && <>, dat is <strong>{euro(r.perMonth)}</strong> per maand</>}.</>
            : r.total > 0 ? <>Je voorlopige aanslag dekt de schatting voor de inkomstenbelasting en de bijdrage Zvw.</>
            : <>Voor de inkomstenbelasting en de bijdrage Zvw hoef je volgens deze schatting niets opzij te zetten.</>}
        </p>
      )}

      <div className="grid grid-stats">
        <MiniStat icon={<PiggyBank size={18} />} accent="var(--primary)" label={r.current ? 'Totaal dit jaar' : `Totaal ${r.year}`}
          value={r.available ? euro(r.total) : '–'}
          sub={calculated ? 'Inkomstenbelasting en Zvw, geschat' : `${pct(s.percentage)} van wat je betaald krijgt`} />
        <MiniStat icon={<ShieldCheck size={18} />} accent="var(--green)" label="Al gedekt" value={euro(r.provisional)} sub={provisionalSub} />
        <MiniStat icon={<Wallet size={18} />} accent="var(--yellow)" label="Nog opzij zetten" value={r.available ? euro(r.remaining) : '–'}
          sub="Totaal min je voorlopige aanslag" />
        <MiniStat icon={<CalendarClock size={18} />} accent="var(--blue)" label="Per maand" value={r.available && r.perMonth != null ? euro(r.perMonth) : '–'}
          sub={r.current ? `Over de ${r.monthsLeft} ${r.monthsLeft === 1 ? 'maand' : 'maanden'} die nog komen` : 'Dit jaar is voorbij'} />
      </div>

      <div className="grid grid-2-1 mt" style={{ alignItems: 'start' }}>
        <div className="card">
          <div className="card-head">
            <h3>Zo komen we op dit bedrag</h3>
            <span className="muted">{calculated ? 'Een schatting' : 'Vast percentage'}</span>
          </div>
          {r.available && calculated && r.fullYear ? <Calculation r={r} full={r.fullYear} />
            : r.available && !calculated ? <PercentageTable r={r} />
            : <div className="card-body muted">Zonder de bedragen van {r.year} maken we deze som niet.</div>}
        </div>

        <div className="dash-stack">
          {r.current && (
            <div className="card">
              <div className="card-head"><h3><Landmark size={16} /> Btw om te betalen</h3><Link className="btn btn-sm btn-ghost" to="/btw-aangifte">Btw-aangifte</Link></div>
              <div className="card-body">
                {r.kor ? <p className="muted" style={{ margin: 0, fontSize: 13 }}>Met de kleineondernemersregeling (KOR) reken je geen btw, dus hoef je die ook niet opzij te zetten.</p> : (<>
                  {r.vat.map(v => (
                    <div key={`${v.year}-${v.quarter}`} className="row between tax-reserve-vat">
                      <span>
                        {v.label}
                        <div className="cell-sub">{v.running ? 'Loopt nog, tot en met vandaag' : 'Aangifte nog niet gedaan'} · uiterlijk {date(v.dueDate)}</div>
                      </span>
                      <strong>{v.amount < 0 ? <span className="text-green">{euro(-v.amount)} terug</span> : euro(v.amount)}</strong>
                    </div>
                  ))}
                  <div className="row between tax-reserve-vat"><span>Apart houden</span><strong>{euro(r.vatTotal)}</strong></div>
                </>)}
              </div>
            </div>
          )}
          <div className="card">
            <div className="card-head"><h3>Wat er niet in zit</h3></div>
            <div className="card-body">
              <ul className="muted tax-reserve-list">
                {r.leftOut.map(l => <li key={l}>{l}</li>)}
              </ul>
            </div>
          </div>
        </div>
      </div>

      <div className="card mt">
        <div className="card-head"><h3>Hoe we rekenen</h3></div>
        <div className="card-body">
          <ul className="muted tax-reserve-list">
            <li>Een indicatie om te plannen, geen belastingadvies. Je aangifte en je boekhouder rekenen het precies uit.</li>
            {r.explanation.map(l => <li key={l}>{l}</li>)}
          </ul>
          {r.sources.length > 0 && (
            <p className="muted" style={{ fontSize: 12, marginBottom: 0 }}>
              Bron: Belastingdienst,{' '}
              {r.sources.map((x, i) => <span key={x.label}><a href={x.url} target="_blank" rel="noreferrer">{x.label}</a>{i < r.sources.length - 1 ? ', ' : '.'}</span>)}
            </p>
          )}
        </div>
      </div>
      </>)}
    </section>
  )
}
