import { useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import {
  ArrowUpDown, BanknoteArrowDown, ChartNoAxesCombined, ChevronDown, ChevronUp, Gauge, HandCoins, Info, Percent, Scale, Calculator, Users, FolderKanban,
} from 'lucide-react'
import { useApi } from '../lib/useApi'
import { useAuth } from '../lib/auth'
import { useTabTitle } from '../lib/tabs'
import type { InsightCustomer, InsightPeriod, InsightProject, Insights } from '../lib/types'
import { date, euro, hours, num } from '../lib/format'
import { Badge, ErrorBox, Field, Loading, PageHeader, SubTabs } from '../components/ui'
import { CashflowTab } from '../components/Cashflow'
import { MiniStat } from './Time'

const periods: { id: InsightPeriod; label: string }[] = [
  { id: 'maand', label: 'Deze maand' },
  { id: 'kwartaal', label: 'Dit kwartaal' },
  { id: 'jaar', label: 'Dit jaar' },
  { id: '12-maanden', label: '12 maanden' },
  { id: 'eigen', label: 'Eigen periode' },
]

const pct = (n: number) => `${Math.round(n * 100).toLocaleString('nl-NL')}%`
const monthName = (m: string) => new Date(`${m}-01T00:00:00`).toLocaleDateString('nl-NL', { month: 'short' })
const monthLong = (m: string) => new Date(`${m}-01T00:00:00`).toLocaleDateString('nl-NL', { month: 'long', year: 'numeric' })
// Een bedrag kort boven een staaf: 12k, of het hele bedrag onder de duizend.
const short = (n: number) => n === 0 ? '' : Math.abs(n) >= 1000 ? `${Math.round(n / 1000)}k` : String(Math.round(n))

type InsightTab = 'verdienen' | 'cashflow'

export function InsightsPage() {
  const { user } = useAuth()
  const admin = user.role === 'beheerder'
  // Het dashboard opent de cashflow met ?tab=cashflow.
  const [params] = useSearchParams()
  const [tab, setTab] = useState<InsightTab>(params.get('tab') === 'cashflow' ? 'cashflow' : 'verdienen')
  useTabTitle(tab === 'cashflow' ? 'Cashflow' : 'Inzichten')
  const [period, setPeriod] = useState<InsightPeriod>('jaar')
  const [range, setRange] = useState({ from: '', to: '' })
  const [withDemo, setWithDemo] = useState(false)
  // Dezelfde grenzen als de server: tussen 2000 en 2100 en hooguit tien jaar.
  const tenYears = range.from ? `${Number(range.from.slice(0, 4)) + 10}${range.from.slice(4)}` : ''
  const validRange = !!range.from && !!range.to && range.from <= range.to && range.from >= '2000-01-01' && range.to <= '2100-12-31' && range.to <= tenYears
  const query = new URLSearchParams({ period })
  if (period === 'eigen') { query.set('from', range.from); query.set('to', range.to) }
  if (withDemo) query.set('demo', 'true')
  const ready = admin && tab === 'verdienen' && (period !== 'eigen' || validRange)
  const result = useApi<Insights>(ready ? `/insights?${query}` : null)
  const { error, loading, reload } = result
  // Een eigen periode die nog niet klopt, laat niet de cijfers van de vorige keuze staan.
  const data = ready ? result.data : null

  if (!admin) return <ErrorBox message="Alleen een beheerder kan de inzichten in omzet en tarieven bekijken." />

  // Een eigen periode begint met wat je nu ziet, zodat je alleen hoeft bij te stellen.
  const choose = (p: InsightPeriod) => {
    if (p === 'eigen' && !validRange && data) setRange({ from: data.from.slice(0, 10), to: data.to.slice(0, 10) })
    setPeriod(p)
  }

  const tabs = (
    <SubTabs<InsightTab> value={tab} onChange={setTab} tabs={[
      { id: 'verdienen', label: 'Wat je verdient', icon: <HandCoins size={14} /> },
      { id: 'cashflow', label: 'Cashflow', icon: <BanknoteArrowDown size={14} /> },
    ]} />
  )

  if (tab === 'cashflow') return (
    <>
      <PageHeader icon={<ChartNoAxesCombined size={20} />} title="Inzichten" subtitle="Wat er de komende zes maanden binnenkomt en eruit gaat" />
      {tabs}
      <CashflowTab />
    </>
  )

  return (
    <>
      <PageHeader
        icon={<ChartNoAxesCombined size={20} />}
        title="Inzichten"
        subtitle={data ? `${data.label} · ${date(data.from)} t/m ${date(data.to)}` : 'Wat je echt verdient'}
        actions={
          <div className="segmented">
            {periods.map(p => <button key={p.id} className={p.id === period ? 'active' : ''} onClick={() => choose(p.id)}>{p.label}</button>)}
          </div>
        }
      />
      {tabs}

      {period === 'eigen' && (
        <div className="card card-body insight-range">
          <Field label="Van"><input type="date" value={range.from} onChange={e => setRange(r => ({ ...r, from: e.target.value }))} /></Field>
          <Field label="Tot en met"><input type="date" value={range.to} onChange={e => setRange(r => ({ ...r, to: e.target.value }))} /></Field>
          {!validRange && <span className="muted">Kies een begin- en einddatum tussen 2000 en 2100, met het begin op of voor het einde en hooguit tien jaar ertussen.</span>}
        </div>
      )}

      <div className="notice">
        <Info size={13} /> Wat je per uur overhoudt als je al je uren meetelt, ook die je niet factureert. Een hulpmiddel om je tarieven en klanten te bekijken, geen advies.
      </div>

      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && ready && <Loading />}

      {data && (<>
        {data.demoData && (
          <div className="notice">
            <label className="check-row">
              <input type="checkbox" checked={withDemo} onChange={e => setWithDemo(e.target.checked)} />
              <span>{data.includesDemo
                ? 'Je ziet nu ook de voorbeelddata. Zo ziet het eruit als je een tijdje met Potel werkt.'
                : 'Voorbeeldfacturen en -uren tellen niet mee. Vink aan om te zien hoe het eruitziet met de voorbeelddata.'}</span>
            </label>
          </div>
        )}
        <Summary data={data} />

        <div className="grid grid-2-1 mt">
          <MonthsCard data={data} />
          <ConcentrationCard data={data} />
        </div>

        <CustomersCard data={data} />
        <ProjectsCard data={data} />
        <HowWeCount data={data} />
      </>)}
    </>
  )
}

function Summary({ data }: { data: Insights }) {
  const eff = data.effectiveRate
  const billable = data.billableRate
  return (
    <div className="grid grid-stats">
      <MiniStat icon={<HandCoins size={18} />} accent="var(--green)" label="Effectief uurtarief" value={eff != null ? euro(eff) : 'Geen uren'}
        sub={data.minutes > 0 ? `${euro(data.revenue)} omzet over ${hours(data.minutes)}` : 'Nog geen uren in deze periode'} />
      <MiniStat icon={<Scale size={18} />} accent="var(--blue)" label="Factureerbaar uurtarief" value={billable != null ? euro(billable) : 'Geen uren'}
        sub={billable != null && eff != null && billable > 0
          ? `Je effectieve tarief is ${pct(eff / billable)} hiervan`
          : 'Gemiddeld over je factureerbare uren op projecten per uur'} />
      <MiniStat icon={<Percent size={18} />} accent="var(--purple)" label="Factureerbaar" value={data.billableShare != null ? pct(data.billableShare) : 'Geen uren'}
        progress={data.billableShare ?? undefined}
        sub={data.minutes > 0 ? `${hours(data.billableMinutes)} van ${hours(data.minutes)}` : 'Nog geen uren in deze periode'} />
      <MiniStat icon={<Gauge size={18} />} accent="var(--primary)" label="Bezettingsgraad" value={data.occupancy != null ? pct(data.occupancy) : 'Geen doel'}
        progress={data.occupancy ?? undefined}
        sub={data.weeklyTarget <= 0 ? <>Stel een doel per week in bij <Link to="/instellingen">Instellingen</Link></>
          : data.goalHours <= 0 ? 'Deze periode is nog niet begonnen'
          : `${hours(data.billableMinutes)} factureerbaar, doel ${num(data.goalHours)} u (${data.weeklyTarget} per week)`} />
    </div>
  )
}

function MonthsCard({ data }: { data: Insights }) {
  const max = Math.max(1, ...data.months.flatMap(m => [m.revenue, m.previous]))
  const total = data.months.reduce((a, m) => a + m.revenue, 0)
  const previous = data.months.reduce((a, m) => a + m.previous, 0)
  const change = previous > 0 ? (total - previous) / previous : null
  return (
    <div className="card">
      <div className="card-head">
        <h3>Omzet per maand</h3>
        <span className="muted">Laatste 12 maanden, zonder btw</span>
      </div>
      <div className="card-body">
        <div className="bars bars-sm bars-pair">
          {data.months.map(m => (
            <div key={m.month} className="bar-col" title={`${monthLong(m.month)}: ${euro(m.revenue)}. Een jaar eerder: ${euro(m.previous)}.`}>
              <div className="bar-value">{short(m.revenue)}</div>
              <div className="bar-track">
                <div className="bar bar-prev" style={{ height: `${(Math.max(0, m.previous) / max) * 100}%` }} />
                <div className="bar" style={{ height: `${(Math.max(0, m.revenue) / max) * 100}%` }} />
              </div>
              <div className="bar-label">{monthName(m.month)}</div>
            </div>
          ))}
        </div>
        <div className="row wrap insight-legend">
          <span className="row"><span className="legend-dot" style={{ background: 'var(--primary)' }} /> Omzet: {euro(total)}</span>
          <span className="row"><span className="legend-dot legend-prev" /> Een jaar eerder: {euro(previous)}</span>
          {change != null && <span className={change >= 0 ? 'text-green' : 'text-red'}>{change >= 0 ? '+' : ''}{pct(change)}</span>}
        </div>
      </div>
    </div>
  )
}

function ConcentrationCard({ data }: { data: Insights }) {
  const c = data.concentration
  const top = c.customers.slice(0, 5)
  const rest = c.customers.slice(5)
  const restRevenue = rest.reduce((a, x) => a + x.revenue, 0)
  return (
    <div className="card">
      <div className="card-head"><h3>Spreiding over klanten</h3><span className="muted">Laatste 12 maanden</span></div>
      <div className="card-body">
        {c.customers.length === 0 ? <div className="muted">Nog geen omzet in de laatste 12 maanden.</div> : (<>
          {c.warning && c.customers[0] && (
            <div className="notice">
              {pct(c.customers[0].share)} van je omzet kwam van {c.customers[0].name}. Stopt die klant, dan merk je dat meteen. Voor één opdrachtgever
              werken zegt op zichzelf niets over schijnzelfstandigheid, maar het kan wel meewegen als je werkrelatie beoordeeld wordt. Dit is geen juridisch advies.
            </div>
          )}
          <div className="insight-shares">
            {top.map(x => (
              <div key={x.customerId}>
                <div className="row between"><span className="truncate">{x.name}</span><span className="nowrap">{pct(x.share)}</span></div>
                <div className="progress progress-thin"><div style={{ width: `${x.share * 100}%` }} /></div>
                <div className="cell-sub">{euro(x.revenue)}</div>
              </div>
            ))}
            {rest.length > 0 && (
              <div className="row between muted">
                <span>{rest.length === 1 ? '1 andere klant' : `${rest.length} andere klanten`}</span>
                <span className="nowrap">{euro(restRevenue)}</span>
              </div>
            )}
          </div>
        </>)}
      </div>
    </div>
  )
}

type SortKey = 'name' | 'minutes' | 'revenue' | 'openValue' | 'effectiveRate' | 'earnedRate'
type Sort = { key: SortKey; desc: boolean }
type Row = { name: string; minutes: number; revenue: number; openValue: number; effectiveRate: number | null; earnedRate?: number | null }

// Sorteert op de gekozen kolom. Een tarief dat er niet is (geen uren) staat altijd onderaan.
function sorted<T extends Row>(rows: T[], sort: Sort): T[] {
  return [...rows].sort((a, b) => {
    if (sort.key === 'name') return (sort.desc ? -1 : 1) * a.name.localeCompare(b.name, 'nl')
    const x = a[sort.key] ?? null
    const y = b[sort.key] ?? null
    if (x === null || y === null) return x === null ? (y === null ? 0 : 1) : -1
    return sort.desc ? y - x : x - y
  })
}

function useSort(): [Sort, (k: SortKey) => void, (k: SortKey) => void] {
  const [sort, setSort] = useState<Sort>({ key: 'revenue', desc: true })
  // Een andere kolom begint bij de grootste; nog een keer klikken draait de volgorde om. Een naam begint bij A.
  const pick = (k: SortKey) => setSort({ key: k, desc: k !== 'name' })
  return [sort, k => setSort(s => s.key === k ? { key: k, desc: !s.desc } : { key: k, desc: k !== 'name' }), pick]
}

// Op een telefoon staan de kolommen onder elkaar; dan kies je de volgorde hier.
function SortSelect({ sort, onPick, options }: { sort: Sort; onPick: (k: SortKey) => void; options: { key: SortKey; label: string }[] }) {
  return (
    <select className="insight-sort" aria-label="Sorteer op" value={sort.key} onChange={e => onPick(e.target.value as SortKey)}>
      {options.map(o => <option key={o.key} value={o.key}>Sorteer op {o.label.toLowerCase()}</option>)}
    </select>
  )
}

const sortOptions: { key: SortKey; label: string }[] = [
  { key: 'revenue', label: 'Omzet' }, { key: 'minutes', label: 'Uren' }, { key: 'openValue', label: 'Open' },
  { key: 'effectiveRate', label: 'Effectief tarief' }, { key: 'name', label: 'Naam' },
]

function SortTh({ label, k, sort, onSort, num }: { label: string; k: SortKey; sort: Sort; onSort: (k: SortKey) => void; num?: boolean }) {
  const active = sort.key === k
  return (
    <th className={num ? 'num' : undefined} aria-sort={active ? (sort.desc ? 'descending' : 'ascending') : undefined}>
      <button className="sort-btn" onClick={() => onSort(k)}>
        {label}
        {active ? (sort.desc ? <ChevronDown size={12} /> : <ChevronUp size={12} />) : <ArrowUpDown size={12} className="muted" />}
      </button>
    </th>
  )
}

// Het staafje onder een naam: hoe groot deze rij is in de kolom waarop je sorteert (of de omzet, als je op naam sorteert).
function RowBar({ row, rows, sort }: { row: Row; rows: Row[]; sort: Sort }) {
  const key = sort.key === 'name' ? 'revenue' : sort.key
  const value = (r: Row) => Math.max(0, r[key] ?? 0)
  const max = Math.max(...rows.map(value))
  if (max <= 0) return null
  return <div className="progress progress-thin insight-bar"><div style={{ width: `${(value(row) / max) * 100}%` }} /></div>
}

const rate = (n: number | null) => n != null ? euro(n) : <span className="muted">Geen uren</span>

function CustomersCard({ data }: { data: Insights }) {
  const [sort, onSort, onPick] = useSort()
  const navigate = useNavigate()
  const rows = sorted<InsightCustomer>(data.customers, sort)
  return (
    <div className="card mt">
      <div className="card-head">
        <h3><Users size={16} /> Per klant</h3>
        <span className="muted">{data.openValue > 0 ? `${euro(data.openValue)} nog te factureren` : data.label}</span>
        {rows.length > 1 && <SortSelect sort={sort} onPick={onPick} options={sortOptions} />}
      </div>
      {rows.length === 0 ? <div className="card-body muted">Geen uren of omzet in deze periode.</div> : (
        <div className="table-wrap">
          <table className="insight-table">
            <thead>
              <tr>
                <SortTh label="Klant" k="name" sort={sort} onSort={onSort} />
                <SortTh label="Uren" k="minutes" sort={sort} onSort={onSort} num />
                <SortTh label="Omzet" k="revenue" sort={sort} onSort={onSort} num />
                <SortTh label="Open" k="openValue" sort={sort} onSort={onSort} num />
                <SortTh label="Effectief" k="effectiveRate" sort={sort} onSort={onSort} num />
              </tr>
            </thead>
            <tbody>
              {rows.map(c => (
                <tr key={c.id} className="clickable" onClick={() => navigate(`/klanten/${c.id}`)}>
                  <td className="insight-name">{c.name}<RowBar row={c} rows={rows} sort={sort} /></td>
                  <td className="num" data-label="Uren">{hours(c.minutes)}<div className="cell-sub">{hours(c.billableMinutes)} factureerbaar</div></td>
                  <td className="num" data-label="Omzet">{euro(c.revenue)}</td>
                  <td className="num" data-label="Open">{c.openValue > 0 ? euro(c.openValue) : '-'}{c.openMinutes > 0 && <div className="cell-sub">{hours(c.openMinutes)}</div>}</td>
                  <td className="num" data-label="Effectief">{rate(c.effectiveRate)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

function ProjectsCard({ data }: { data: Insights }) {
  const [sort, onSort, onPick] = useSort()
  const navigate = useNavigate()
  const rows = sorted<InsightProject>(data.projects, sort)
  const fixed = rows.some(p => p.billing === 'vast')
  const below = rows.filter(p => p.belowNormal)
  return (
    <div className="card mt">
      <div className="card-head">
        <h3><FolderKanban size={16} /> Per project</h3>
        <span className="muted">Uren en omzet in {data.label}</span>
        {rows.length > 1 && <SortSelect sort={sort} onPick={onPick}
          options={fixed ? [...sortOptions.slice(0, 4), { key: 'earnedRate', label: 'Vaste prijs per uur' }, sortOptions[4]!] : sortOptions} />}
      </div>
      {below.length > 0 && (
        <div className="card-body insight-below">
          <div className="notice">
            {below.length === 1
              ? `Met de vaste prijs van ${below[0]!.name} verdien je tot nu toe minder per uur dan je normale tarief.`
              : `Bij ${below.length} projecten met een vaste prijs verdien je tot nu toe minder per uur dan je normale tarief.`}
          </div>
        </div>
      )}
      {rows.length === 0 && data.unassignedRevenue === 0 ? <div className="card-body muted">Geen uren of omzet in deze periode.</div> : (
        <div className="table-wrap">
          <table className="insight-table">
            <thead>
              <tr>
                <SortTh label="Project" k="name" sort={sort} onSort={onSort} />
                <SortTh label="Uren" k="minutes" sort={sort} onSort={onSort} num />
                <SortTh label="Omzet" k="revenue" sort={sort} onSort={onSort} num />
                <SortTh label="Open" k="openValue" sort={sort} onSort={onSort} num />
                <SortTh label="Effectief" k="effectiveRate" sort={sort} onSort={onSort} num />
                {fixed && <SortTh label="Vaste prijs" k="earnedRate" sort={sort} onSort={onSort} num />}
              </tr>
            </thead>
            <tbody>
              {rows.map(p => (
                <tr key={p.id} className="clickable" onClick={() => navigate(`/projecten/${p.id}`)}>
                  <td className="insight-name">
                    <div className="cell-main"><span className="legend-dot" style={{ background: p.color }} /><span>{p.name}</span></div>
                    <div className="cell-sub">{p.customerName ?? 'Geen klant'} · {p.billing === 'vast' ? 'vaste prijs' : 'per uur'}</div>
                    <RowBar row={p} rows={rows} sort={sort} />
                  </td>
                  <td className="num" data-label="Uren">{hours(p.minutes)}<div className="cell-sub">{hours(p.billableMinutes)} factureerbaar</div></td>
                  <td className="num" data-label="Omzet">{euro(p.revenue)}</td>
                  <td className="num" data-label="Open">{p.openValue > 0 ? euro(p.openValue) : '-'}{p.openMinutes > 0 && <div className="cell-sub">{hours(p.openMinutes)}</div>}</td>
                  <td className="num" data-label="Effectief">{rate(p.effectiveRate)}</td>
                  {fixed && (
                    <td className={`num ${p.billing !== 'vast' ? 'insight-empty' : ''}`} data-label="Vaste prijs">
                      {p.billing !== 'vast' ? <span className="muted">-</span> : p.earnedRate == null ? <span className="muted">Nog geen uren</span> : (<>
                        <span className="nowrap">{euro(p.earnedRate)} per uur</span>
                        <div className="cell-sub">{euro(p.fixedPrice ?? 0)} over {hours(p.minutesTotal)}</div>
                        {p.belowNormal && <span title={`Je normale tarief is ${euro(p.normalRate)} per uur`}><Badge tone="yellow">Onder {euro(p.normalRate)}</Badge></span>}
                      </>)}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
            {data.unassignedRevenue !== 0 && (
              <tfoot>
                <tr>
                  <td className="insight-name">Zonder project<div className="cell-sub">Facturen zonder uren, ritten of offerte met een project</div></td>
                  <td className="num insight-empty">-</td>
                  <td className="num" data-label="Omzet">{euro(data.unassignedRevenue)}</td>
                  <td className="num insight-empty">-</td>
                  <td className="num insight-empty">-</td>
                  {fixed && <td className="num insight-empty">-</td>}
                </tr>
              </tfoot>
            )}
          </table>
        </div>
      )}
    </div>
  )
}

function HowWeCount({ data }: { data: Insights }) {
  return (
    <div className="card mt">
      <div className="card-head"><h3><Calculator size={16} /> Hoe we rekenen</h3></div>
      <div className="card-body">
        <ul className="insight-list">
          <li>Omzet is het bedrag zonder btw op de factuurdatum, net als in het jaaroverzicht. Concepten tellen niet mee; of een factuur al betaald is, maakt niet uit.</li>
          <li>Een creditnota trekt af in de maand van de creditnota, bij de klant en de projecten van de factuur die hij corrigeert.</li>
          <li>Je effectieve uurtarief is je omzet gedeeld door al je uren in de periode, ook de uren die je niet factureert. Omzet telt op de factuurdatum en uren op de dag dat je ze werkte, dus per maand schommelt het; over een kwartaal of een jaar zegt het meer.</li>
          <li>Je factureerbare uurtarief is het tarief van je projecten per uur, gemiddeld over je factureerbare uren daarop.</li>
          <li>De bezettingsgraad zet je factureerbare uren af tegen je doel van {data.weeklyTarget} uur per week, voor de dagen van de periode tot en met vandaag.</li>
          <li>Open is de waarde van factureerbare uren op projecten per uur die nog niet, of alleen op een concept, gefactureerd zijn.</li>
          <li>Een factuur hangt niet aan een project. Een regel met uren of ritten telt voor het project van die uren of ritten, en een factuur uit een offerte met een project telt voor dat project. De rest staat onder Zonder project.</li>
          <li>Bij een vaste prijs delen we de prijs door alle uren die ooit op het project geschreven zijn, ook buiten deze periode. Dat zetten we af tegen het uurtarief van het project, of je standaardtarief van {euro(data.normalRate)} als het project er geen heeft.</li>
          <li>Omzet per maand en de spreiding over klanten gaan altijd over de laatste 12 maanden.</li>
          <li>Voorbeelddata telt niet mee, tenzij je die bovenaan aanvinkt.</li>
        </ul>
      </div>
    </div>
  )
}
