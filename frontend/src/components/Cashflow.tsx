import { Fragment, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  BanknoteArrowDown, BanknoteArrowUp, Calculator, ChevronDown, ChevronRight, Info, Save, Scale, TrendingDown, WalletMinimal,
} from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import type { Cashflow, CashflowItem, CashflowSourceId, CashflowVat } from '../lib/types'
import { date, euro } from '../lib/format'
import { ErrorBox, Field, Loading } from './ui'
import { MiniStat } from '../pages/Time'

const colors: Record<CashflowSourceId, string> = {
  facturen: 'var(--green)', terugkerend: 'var(--blue)', offertes: 'var(--purple)', 'te-factureren': 'color-mix(in srgb, var(--green) 50%, var(--blue))',
  btw: 'var(--yellow)', belasting: 'var(--primary)', 'vaste-kosten': 'var(--muted)',
}

const monthShort = (m: string) => new Date(`${m}-01T00:00:00`).toLocaleDateString('nl-NL', { month: 'short' })
const monthLong = (m: string) => new Date(`${m}-01T00:00:00`).toLocaleDateString('nl-NL', { month: 'long', year: 'numeric' })
const monthOnly = (d: string) => new Date(`${d.slice(0, 7)}-01T00:00:00`).toLocaleDateString('nl-NL', { month: 'long' })
const cents = (n: number) => Math.round(n * 100) / 100

// Welke bronnen je uitzette, per browser onthouden. Zonder opslag (privévenster) staat alles gewoon aan.
const OFF_KEY = 'potel.cashflow.uit'
function loadOff(): CashflowSourceId[] {
  try {
    const list: unknown = JSON.parse(localStorage.getItem(OFF_KEY) ?? '[]')
    return Array.isArray(list) ? list.filter((x): x is CashflowSourceId => typeof x === 'string' && x in colors) : []
  } catch {
    return []
  }
}
function saveOff(list: CashflowSourceId[]) {
  try { localStorage.setItem(OFF_KEY, JSON.stringify(list)) } catch { /* opslag niet beschikbaar */ }
}

type VatPayment = { vat: CashflowVat; amount: number; parts: number; costs: number }
type MonthRow = { month: string; items: CashflowItem[]; vat: VatPayment[]; inflow: number; outflow: number; net: number; balance: number }

// De btw van een kwartaal: wat er nu in de aangifte staat plus de btw op verwachte facturen van de bronnen die aan staan, min de
// btw die je terugkrijgt op vaste kosten die nog komen. Krijg je geld terug, dan telt het als nul.
function vatPayment(vat: CashflowVat, off: Set<CashflowSourceId>): VatPayment {
  const on = Object.entries(vat.parts).filter(([s]) => !off.has(s as CashflowSourceId))
  const parts = cents(on.filter(([s]) => s !== 'vaste-kosten').reduce((a, [, x]) => a + (x ?? 0), 0))
  const costs = cents(on.filter(([s]) => s === 'vaste-kosten').reduce((a, [, x]) => a + (x ?? 0), 0))
  return { vat, parts, costs, amount: Math.max(0, cents(vat.base + parts + costs)) }
}

// Per maand wat er binnenkomt en eruit gaat, en het saldo: vanaf je banksaldo, of vanaf nul als je dat niet invulde.
function monthsOf(data: Cashflow, off: Set<CashflowSourceId>): MonthRow[] {
  let balance = data.settings.bankBalance ?? 0
  return data.months.map(month => {
    const items = data.items.filter(i => i.month === month && !off.has(i.source))
    const vat = off.has('btw') ? [] : data.vat.filter(v => v.month === month).map(v => vatPayment(v, off)).filter(v => v.amount > 0)
    const inflow = cents(items.filter(i => i.amount > 0).reduce((a, i) => a + i.amount, 0))
    const outflow = cents(-items.filter(i => i.amount < 0).reduce((a, i) => a + i.amount, 0) + vat.reduce((a, v) => a + v.amount, 0))
    const net = cents(inflow - outflow)
    balance = cents(balance + net)
    return { month, items, vat, inflow, outflow, net, balance }
  })
}

export function CashflowTab() {
  const [withDemo, setWithDemo] = useState(false)
  const { data, error, loading, reload } = useApi<Cashflow>(`/insights/cashflow${withDemo ? '?demo=true' : ''}`)
  const [off, setOff] = useState<CashflowSourceId[]>(loadOff)
  const [open, setOpen] = useState<string | null>(null)

  if (error) return <ErrorBox message={error} onRetry={() => reload()} />
  if (loading && !data) return <Loading />
  if (!data) return null

  const offSet = new Set(off)
  const rows = monthsOf(data, offSet)
  const toggle = (id: CashflowSourceId) => {
    const next = offSet.has(id) ? off.filter(x => x !== id) : [...off, id]
    setOff(next)
    saveOff(next)
  }
  const hasBalance = data.settings.bankBalance != null
  const first = rows[0]!
  const last = rows[rows.length - 1]!
  const lowest = rows.reduce((a, r) => (r.balance < a.balance ? r : a), first)
  const firstBelow = rows.find(r => r.balance < 0)
  const totalIn = cents(rows.reduce((a, r) => a + r.inflow, 0))
  const totalOut = cents(rows.reduce((a, r) => a + r.outflow, 0))

  return (
    <>
      <div className="notice">
        <Info size={13} /> Een schatting met wat er nu in Potel staat, geen garantie. Klanten betalen niet altijd op de vervaldatum, en wat nog niet in
        Potel staat, zit er niet in.
      </div>
      {data.demoData && (
        <div className="notice">
          <label className="check-row">
            <input type="checkbox" checked={withDemo} onChange={e => setWithDemo(e.target.checked)} />
            <span>{data.includesDemo
              ? 'Je ziet nu ook de voorbeelddata. De belasting rekent altijd zonder voorbeelden.'
              : 'Voorbeeldfacturen, -offertes en -uren tellen niet mee. Vink aan om te zien hoe het eruitziet met de voorbeelddata.'}</span>
          </label>
        </div>
      )}

      <div className="grid grid-stats">
        <MiniStat icon={<BanknoteArrowDown size={18} />} accent="var(--green)" label={`Binnen in ${monthOnly(first.month)}`} value={euro(first.inflow)}
          sub={`${euro(first.outflow)} gaat eruit`} />
        <MiniStat icon={<BanknoteArrowUp size={18} />} accent="var(--primary)" label="Binnen in zes maanden" value={euro(totalIn)} sub={`${euro(totalOut)} gaat eruit`} />
        <MiniStat icon={<Scale size={18} />} accent="var(--blue)" label={hasBalance ? `Saldo eind ${monthOnly(last.month)}` : 'Verschil over zes maanden'}
          value={euro(hasBalance ? last.balance : cents(totalIn - totalOut))}
          sub={hasBalance ? `Vanaf ${euro(data.settings.bankBalance!)} op ${date(data.settings.bankBalanceDate)}` : 'Vul je banksaldo in voor je verwachte saldo'} />
        <MiniStat icon={<TrendingDown size={18} />} accent={lowest.balance < 0 ? 'var(--red)' : 'var(--purple)'}
          label={hasBalance ? 'Laagste saldo' : 'Laagste stand'} value={euro(lowest.balance)} sub={`Eind ${monthLong(lowest.month)}`} />
      </div>

      {hasBalance && firstBelow && (
        <div className="notice mt">
          Eind {monthLong(firstBelow.month)} zakt je verwachte saldo onder nul. Kijk of je eerder kunt factureren, of zet iets opzij in de maanden ervoor.
        </div>
      )}

      <Sources data={data} off={offSet} onToggle={toggle} />
      <Chart rows={rows} hasBalance={hasBalance} />

      <div className="card mt">
        <div className="card-head"><h3>Per maand</h3><span className="muted">Open een maand voor de posten</span></div>
        <div className="table-wrap">
          <table className="insight-table cf-table">
            <thead>
              <tr><th>Maand</th><th className="num">Binnen</th><th className="num">Eruit</th><th className="num">Verschil</th><th className="num">{hasBalance ? 'Saldo' : 'Opgeteld'}</th></tr>
            </thead>
            <tbody>
              {rows.map(r => {
                const expanded = open === r.month
                const flip = () => setOpen(expanded ? null : r.month)
                return (
                  <Fragment key={r.month}>
                    <tr className="clickable" tabIndex={0} aria-expanded={expanded} onClick={flip}
                      onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); flip() } }}>
                      <td className="insight-name"><span className="cf-month">{expanded ? <ChevronDown size={14} /> : <ChevronRight size={14} />}<span>{monthLong(r.month)}</span></span></td>
                      <td className="num" data-label="Binnen">{euro(r.inflow)}</td>
                      <td className="num" data-label="Eruit">{euro(r.outflow)}</td>
                      <td className={`num ${r.net < 0 ? 'text-red' : ''}`} data-label="Verschil">{euro(r.net)}</td>
                      <td className={`num ${r.balance < 0 ? 'text-red' : ''}`} data-label={hasBalance ? 'Saldo' : 'Opgeteld'}><strong>{euro(r.balance)}</strong></td>
                    </tr>
                    {expanded && (
                      <tr className="cf-detail-row"><td colSpan={5}><MonthDetail row={r} data={data} /></td></tr>
                    )}
                  </Fragment>
                )
              })}
            </tbody>
          </table>
        </div>
      </div>

      <div className="grid grid-1-1 mt">
        <SettingsCard data={data} onSaved={() => reload(true)} />
        <HowCashflowWorks data={data} />
      </div>
    </>
  )
}

// De bronnen om aan en uit te zetten, met hun totaal over de zes maanden.
function Sources({ data, off, onToggle }: { data: Cashflow; off: Set<CashflowSourceId>; onToggle: (id: CashflowSourceId) => void }) {
  const total = (id: CashflowSourceId) => id === 'btw'
    ? cents(data.vat.map(v => vatPayment(v, off).amount).reduce((a, x) => a + x, 0))
    : cents(Math.abs(data.items.filter(i => i.source === id).reduce((a, i) => a + i.amount, 0)))
  const group = (incoming: boolean) => data.sources.filter(s => s.incoming === incoming).map(s => (
    <label key={s.id} className={`cf-source ${off.has(s.id) ? 'off' : ''}`}>
      <input type="checkbox" checked={!off.has(s.id)} onChange={() => onToggle(s.id)} />
      <span className="legend-dot" style={{ background: colors[s.id] }} />
      <span className="grow">{s.label}</span>
      <span className="muted nowrap">{s.id === 'btw' && data.kor ? 'KOR' : euro(total(s.id))}</span>
    </label>
  ))
  return (
    <div className="card mt">
      <div className="card-body cf-sources">
        <div>
          <div className="stat-label">Komt binnen</div>
          {group(true)}
        </div>
        <div>
          <div className="stat-label">Gaat eruit</div>
          {group(false)}
        </div>
      </div>
    </div>
  )
}

// Staven voor wat er binnenkomt en eruit gaat, en een lijn voor het saldo (met een eigen schaal).
function Chart({ rows, hasBalance }: { rows: MonthRow[]; hasBalance: boolean }) {
  const max = Math.max(1, ...rows.flatMap(r => [r.inflow, r.outflow]))
  const lo = Math.min(0, ...rows.map(r => r.balance))
  const hi = Math.max(0, ...rows.map(r => r.balance))
  const y = (b: number) => 8 + (1 - (b - lo) / (hi - lo || 1)) * 84
  const x = (i: number) => ((i + 0.5) / rows.length) * 100
  const label = hasBalance ? 'Verwacht saldo' : 'Opgeteld verschil'
  return (
    <div className="card mt">
      <div className="card-head">
        <h3>Komende zes maanden</h3>
        <div className="row wrap insight-legend" style={{ marginTop: 0 }}>
          <span className="row"><span className="legend-dot" style={{ background: 'var(--green)' }} /> Binnen</span>
          <span className="row"><span className="legend-dot" style={{ background: 'var(--red)' }} /> Eruit</span>
          <span className="row"><span className="cf-legend-line" /> {label}</span>
        </div>
      </div>
      <div className="card-body">
        <div className="cf-plot">
          {rows.map(r => (
            <div key={r.month} className="cf-col" title={`${monthLong(r.month)}: ${euro(r.inflow)} binnen, ${euro(r.outflow)} eruit. ${label}: ${euro(r.balance)}.`}>
              <div className="cf-bars">
                <div className="cf-bar cf-in" style={{ height: `${(r.inflow / max) * 100}%` }} />
                <div className="cf-bar cf-out" style={{ height: `${(r.outflow / max) * 100}%` }} />
              </div>
            </div>
          ))}
          {lo < 0 && <div className="cf-zero" style={{ top: `${y(0)}%` }}><span>{euro(0)}</span></div>}
          <svg className="cf-line" viewBox="0 0 100 100" preserveAspectRatio="none" aria-hidden="true">
            <polyline points={rows.map((r, i) => `${x(i)},${y(r.balance)}`).join(' ')} vectorEffect="non-scaling-stroke" />
          </svg>
          {rows.map((r, i) => (
            <span key={r.month} className={`cf-dot ${r.balance < 0 ? 'below' : ''}`} style={{ left: `${x(i)}%`, top: `${y(r.balance)}%` }}
              title={`${label} eind ${monthLong(r.month)}: ${euro(r.balance)}`} />
          ))}
        </div>
        <div className="cf-labels">{rows.map(r => <span key={r.month}>{monthShort(r.month)}</span>)}</div>
        <p className="muted cf-caption">De lijn heeft een eigen schaal. De bedragen per maand staan in de tabel hieronder.</p>
      </div>
    </div>
  )
}

// De posten van één maand, per bron.
function MonthDetail({ row, data }: { row: MonthRow; data: Cashflow }) {
  const groups = data.sources.map(s => {
    const items = row.items.filter(i => i.source === s.id)
    const vat = s.id === 'btw' ? row.vat : []
    const total = cents(items.reduce((a, i) => a + i.amount, 0) - vat.reduce((a, v) => a + v.amount, 0))
    return { source: s, items, vat, total }
  }).filter(g => g.items.length > 0 || g.vat.length > 0)
  if (groups.length === 0) return <div className="cf-detail muted">Niets verwacht in {monthLong(row.month)}.</div>
  return (
    <div className="cf-detail">
      {groups.map(g => (
        <div key={g.source.id} className="cf-group">
          <div className="cf-group-head">
            <span className="legend-dot" style={{ background: colors[g.source.id] }} />
            <span className="grow">{g.source.label}</span>
            <strong>{euro(g.total)}</strong>
          </div>
          {g.items.map((i, n) => (
            <div key={n} className="cf-item">
              <div className="grow">
                {i.link ? <Link to={i.link}>{i.label}</Link> : i.label}
                <div className="cell-sub">{[i.detail, i.amount > 0 ? `verwacht ${date(i.date)}` : null].filter(Boolean).join(' · ')}</div>
              </div>
              <span className={`nowrap ${i.late ? 'text-red' : ''}`}>{euro(i.amount)}</span>
            </div>
          ))}
          {g.vat.map(v => (
            <div key={`${v.vat.year}-${v.vat.quarter}`} className="cf-item">
              <div className="grow">
                <Link to="/btw-aangifte">Btw {v.vat.label}</Link>
                <div className="cell-sub">
                  Uiterlijk {date(v.vat.dueDate)}. Nu in de aangifte {euro(v.vat.base)}{v.parts !== 0 ? `, plus ${euro(v.parts)} op verwachte facturen` : ''}
                  {v.costs !== 0 ? `, min ${euro(-v.costs)} op vaste kosten die nog komen` : ''}
                  {v.vat.running ? '. Het kwartaal loopt nog.' : '.'}
                </div>
              </div>
              <span className="nowrap">{euro(-v.amount)}</span>
            </div>
          ))}
        </div>
      ))}
    </div>
  )
}

function SettingsCard({ data, onSaved }: { data: Cashflow; onSaved: () => void }) {
  const s = data.settings
  const [balance, setBalance] = useState(s.bankBalance == null ? '' : String(s.bankBalance))
  const [fixed, setFixed] = useState(s.fixedCostsPerMonth ? String(s.fixedCostsPerMonth) : '')
  const [spread, setSpread] = useState(s.quoteSpreadMonths)
  const [busy, setBusy] = useState(false)
  const toast = useToast()
  const hint = data.hint
  const suggestion = hint.suppliers.length > 0 ? hint.suppliersTotal : hint.averageExpenses
  const period = `${monthOnly(hint.from)} t/m ${monthOnly(hint.to)}`

  const save = async () => {
    setBusy(true)
    try {
      await api.put('/insights/cashflow/settings', {
        bankBalance: balance.trim() === '' ? null : Number(balance),
        fixedCostsPerMonth: fixed.trim() === '' ? 0 : Number(fixed),
        quoteSpreadMonths: spread,
      })
      toast('De prognose is bijgewerkt')
      onSaved()
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="card">
      <div className="card-head"><h3><WalletMinimal size={16} /> Je gegevens voor de prognose</h3></div>
      <div className="card-body form-grid">
        <Field label="Banksaldo nu">
          <input type="number" step="0.01" value={balance} placeholder="Niet ingevuld" onChange={e => setBalance(e.target.value)} />
        </Field>
        <Field label={data.subscriptions > 0 ? 'Overige vaste kosten per maand' : 'Vaste kosten per maand'}>
          <input type="number" min={0} step="0.01" value={fixed} placeholder="0" onChange={e => setFixed(e.target.value)} />
        </Field>
        <p className="muted field-full cf-note">
          {s.bankBalanceDate
            ? `Je saldo is ingevuld op ${date(s.bankBalanceDate)}. Potel leest het niet uit je bankafschrift, dus werk het bij als je de prognose bekijkt.`
            : 'Vul in wat er nu op je zakelijke rekening staat. Dan zie je per maand je verwachte saldo.'}
          {' '}Vaste kosten zijn met btw, zoals je ze betaalt: huur, abonnementen, verzekeringen.
          {data.subscriptions > 0
            ? <> Je {data.subscriptions === 1 ? 'vaste kost' : `${data.subscriptions} vaste kosten`} onder <Link to="/kosten?tab=vast">Kosten</Link> ({euro(data.subscriptionsPerMonth)} per maand) tellen al mee. Vul hier alleen in wat daar niet staat.</>
            : <> Zet je abonnementen liever als <Link to="/kosten?tab=vast">vaste kost</Link> onder Kosten: dan telt elke afschrijving mee op de dag dat hij komt.</>}
        </p>
        {suggestion > 0 && (
          <div className="field-full cf-hint">
            {hint.suppliers.length > 0
              ? <>Van {period} kwam elke maand terug: {hint.suppliers.slice(0, 5).map(x => `${x.supplier} (${euro(x.average)})`).join(', ')}
                {hint.suppliers.length > 5 ? ` en ${hint.suppliers.length - 5} meer` : ''}. Samen gemiddeld {euro(hint.suppliersTotal)} per maand.</>
              : <>Van {period} gaf je gemiddeld {euro(hint.averageExpenses)} per maand uit aan kosten{data.subscriptions > 0 ? ', naast je vaste kosten' : ''}. Welk deel daarvan vast is, weet jij het best.</>}
            <button type="button" className="btn btn-sm" onClick={() => setFixed(String(suggestion))}>Gebruik {euro(suggestion)}</button>
          </div>
        )}
        <Field label="Wanneer komt een geaccepteerde offerte binnen?" full>
          <select value={spread} onChange={e => setSpread(Number(e.target.value))}>
            {[1, 2, 3, 4, 5, 6].map(n => <option key={n} value={n}>{n === 1 ? 'Volgende maand' : `Verdeeld over ${n} maanden`}</option>)}
          </select>
        </Field>
        <div className="field-full">
          <button type="button" className="btn btn-primary" disabled={busy} onClick={save}><Save size={15} /> Opslaan</button>
        </div>
      </div>
    </div>
  )
}

function HowCashflowWorks({ data }: { data: Cashflow }) {
  return (
    <div className="card">
      <div className="card-head"><h3><Calculator size={16} /> Hoe we rekenen</h3></div>
      <div className="card-body">
        <ul className="insight-list">
          <li>Alle bedragen zijn met btw, zoals ze op je rekening binnenkomen en eraf gaan. De lopende maand telt vanaf vandaag.</li>
          <li>Openstaande facturen komen binnen op hun vervaldatum, min wat er al betaald of gecrediteerd is. Een factuur die te laat is, staat in deze maand.</li>
          <li>Terugkerende facturen komen binnen {data.paymentTermDays} dagen (je betaaltermijn) na de dag waarop ze gemaakt worden. Gepauzeerde tellen niet mee.</li>
          <li>Een geaccepteerde offerte zonder eindfactuur telt mee vanaf volgende maand, min de aanbetaling en bij een project per uur min de uren die al gefactureerd zijn of openstaan. Maakte je de eindfactuur zelf, buiten de offerte om, dan telt de offerte hier nog mee.</li>
          <li>Nog te factureren zijn open uren op projecten per uur, open ritten met een kilometertarief en conceptfacturen, alsof je ze vandaag factureert. Uren op een project met een vaste prijs tellen niet mee.</li>
          {data.kor
            ? <li>Je doet mee met de kleineondernemersregeling (KOR), dus er staat geen btw-aangifte in.</li>
            : <li>Btw betaal je uiterlijk op de laatste dag van de maand na het kwartaal. We nemen wat er nu in de aangifte staat, plus de btw op de verwachte facturen in dat kwartaal. De btw op vaste kosten die nog komen gaat eraf. Btw op andere kosten die nog komen zit er niet in, een kwartaal waarin je btw terugkrijgt telt als nul, en een aangifte die je als gedaan markeerde telt niet mee.</li>}
          <li>
            Inkomstenbelasting: wat de belastingpot per maand aangeeft om opzij te zetten, en de termijnen van je voorlopige aanslag per maand. We nemen aan
            dat die termijnen in de laatste maanden van het jaar vallen.
            {data.nextYearEstimate && ' Voor volgend jaar schatten we wat je opzij zet met de cijfers van dit jaar.'}
            {data.provisionalOnce && ' Een voorlopige aanslag die je in één keer betaalt, staat er niet in, want wanneer je die betaalt weten we niet.'}
            {' '}De belasting rekent altijd zonder voorbeelddata. Je stelt dit in bij <Link to="/instellingen">Instellingen</Link>.
          </li>
          <li>
            Vaste kosten: elke afschrijving van je <Link to="/kosten?tab=vast">vaste kosten</Link> onder Kosten op de dag dat hij komt, vanaf vandaag. Wat al
            geboekt is, staat al in je kosten. Wat je hiernaast zelf invult, komt er elke maand bij als aparte regel, ook in de lopende maand helemaal.
          </li>
          <li>Zet een bron uit om te zien wat er overblijft zonder die bron. Potel onthoudt dat in deze browser.</li>
        </ul>
      </div>
    </div>
  )
}
