import { useMemo, useState } from 'react'
import { Wallet, Plus, Search, ChevronLeft, ChevronRight, Paperclip, Receipt, Percent, FileWarning } from 'lucide-react'
import { useApi } from '../lib/useApi'
import type { Expense, ExpenseCategory, Settings } from '../lib/types'
import { date, euro, toDateInput } from '../lib/format'
import { categoryLabel, costForProfit, expenseCategories } from '../lib/expenses'
import { Badge, Empty, ErrorBox, Loading, PageHeader } from '../components/ui'
import { ExpenseForm } from '../components/ExpenseForm'
import { MiniStat } from './Time'

const firstOfMonth = (d: Date) => new Date(d.getFullYear(), d.getMonth(), 1)

export function ExpensesPage() {
  const [today] = useState(() => toDateInput(new Date()))
  const [month, setMonth] = useState(() => firstOfMonth(new Date()))
  const from = toDateInput(month)
  const to = toDateInput(new Date(month.getFullYear(), month.getMonth() + 1, 0))
  const { data, error, loading, reload } = useApi<Expense[]>(`/expenses?from=${from}&to=${to}`)
  const { data: settings } = useApi<Settings>('/settings')
  const [q, setQ] = useState('')
  const [category, setCategory] = useState<'alle' | ExpenseCategory>('alle')
  const [missingOnly, setMissingOnly] = useState(false)
  const [editing, setEditing] = useState<{ expense?: Expense } | null>(null)

  const shift = (months: number) => setMonth(m => new Date(m.getFullYear(), m.getMonth() + months, 1))
  const isThisMonth = from === `${today.slice(0, 8)}01`
  const label = month.toLocaleDateString('nl-NL', { month: 'long', year: 'numeric' })

  const filtered = useMemo(() => {
    const s = q.toLowerCase()
    return (data ?? []).filter(e =>
      (category === 'alle' || e.category === category) && (!missingOnly || !e.receipt) &&
      [e.supplier, e.description, e.projectName, e.customerName, categoryLabel(e.category)].some(v => v?.toLowerCase().includes(s)))
  }, [data, q, category, missingOnly])

  const sum = (pick: (e: Expense) => number) => filtered.reduce((a, e) => a + pick(e), 0)
  const missing = filtered.filter(e => !e.receipt).length
  // Met de KOR of alleen vrijgestelde omzet vraag je geen btw terug; dan zijn de kosten inclusief btw je echte kosten.
  const noDeduction = settings?.vatRegime === 'kor' || settings?.vatRegime === 'vrijgesteld'
  const newDate = isThisMonth ? today : from

  return (
    <>
      <PageHeader
        icon={<Wallet size={20} />}
        title="Kosten"
        subtitle={`${label} · ${filtered.length} ${filtered.length === 1 ? 'uitgave' : 'uitgaven'} · ${euro(sum(e => e.amount))}`}
        actions={<>
          <div className="search-input"><Search size={16} /><input placeholder="Zoek leverancier, omschrijving of project…" value={q} onChange={e => setQ(e.target.value)} /></div>
          <select value={category} onChange={e => setCategory(e.target.value as 'alle' | ExpenseCategory)} aria-label="Categorie">
            <option value="alle">Alle categorieën</option>
            {expenseCategories.map(c => <option key={c.id} value={c.id}>{c.label}</option>)}
          </select>
          <div className="btn-group">
            <button className="btn" onClick={() => shift(-1)} aria-label="Vorige maand"><ChevronLeft size={16} /></button>
            <button className="btn" disabled={isThisMonth} onClick={() => setMonth(firstOfMonth(new Date(`${today}T00:00:00`)))}>Deze maand</button>
            <button className="btn" onClick={() => shift(1)} aria-label="Volgende maand"><ChevronRight size={16} /></button>
          </div>
          <button className="btn btn-primary" onClick={() => setEditing({})}><Plus size={16} /> Uitgave toevoegen</button>
        </>}
      />

      {data && (
        <div className="grid grid-stats" style={{ marginBottom: 18 }}>
          <MiniStat icon={<Wallet size={18} />} accent="var(--primary)" label="Betaald" value={euro(sum(e => e.amount))}
            sub="Wat je overmaakte, met btw" />
          <MiniStat icon={<Receipt size={18} />} accent="var(--blue)" label="Kosten voor je winst" value={euro(sum(e => costForProfit(e, noDeduction)))}
            sub={noDeduction ? 'Met btw, want die vraag je niet terug' : 'Zonder btw, want die vraag je terug'} />
          <MiniStat icon={<Percent size={18} />} accent="var(--green)" label="Btw op je kosten" value={euro(sum(e => e.vatAmount))}
            sub={noDeduction ? 'Met jouw btw-regeling trek je dit niet af' : 'Voorbelasting voor je btw-aangifte'} />
          <MiniStat icon={<FileWarning size={18} />} accent="var(--yellow)" label="Zonder bonnetje" value={String(missing)}
            sub={missing ? (missingOnly ? 'Klik om alles weer te zien' : 'Klik om ze te tonen') : 'Alles heeft een bonnetje'}
            onClick={missing || missingOnly ? () => setMissingOnly(m => !m) : undefined} />
        </div>
      )}

      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && <Loading />}

      {data && (
        <div className="card">
          {filtered.length === 0 ? (
            <Empty icon={<Wallet size={22} />} title={`Geen kosten in ${label}`}
              text={q || category !== 'alle' || missingOnly ? 'Probeer een andere zoekterm of categorie.' : 'Zet hier je zakelijke uitgaven neer, met het bonnetje erbij.'}
              action={!q && category === 'alle' && !missingOnly && <button className="btn btn-primary" onClick={() => setEditing({})}><Plus size={16} /> Uitgave toevoegen</button>} />
          ) : (
            <div className="table-wrap">
              <table>
                <thead><tr><th>Datum</th><th>Leverancier</th><th>Categorie</th><th>Project of klant</th><th className="num">Btw</th><th className="num">Bedrag</th><th /></tr></thead>
                <tbody>
                  {filtered.map(e => (
                    <tr key={e.id} className="clickable" onClick={() => setEditing({ expense: e })}>
                      <td className="muted nowrap">{date(e.date)}</td>
                      <td>
                        <strong>{e.supplier}</strong>
                        {e.demo && <span className="badge badge-gray" style={{ marginLeft: 8 }}>voorbeeld</span>}
                        {e.description && <div className="cell-sub">{e.description}</div>}
                      </td>
                      <td className="muted">{categoryLabel(e.category)}</td>
                      <td>
                        {e.projectName ? <><div>{e.projectName}</div><div className="cell-sub">{e.customerName}</div></>
                          : e.customerName ? <div>{e.customerName}</div> : <span className="muted">·</span>}
                      </td>
                      <td className="num">
                        {e.vatRate == null ? <span className="muted">geen</span> : <div>{euro(e.vatAmount)}</div>}
                        {e.reverseCharge
                          ? <div className="cell-sub"><Badge tone="purple">verlegd {e.supplierRegion === 'eu' ? 'EU' : 'buiten EU'}</Badge></div>
                          : e.vatRate != null && <div className="cell-sub">{e.vatRate}%</div>}
                      </td>
                      <td className="num"><strong>{euro(e.amount)}</strong>{e.vatRate ? <div className="cell-sub">{euro(e.amountExclVat)} zonder btw</div> : null}</td>
                      <td className="row-actions" onClick={ev => ev.stopPropagation()}>
                        {e.receipt
                          ? <a className="icon-btn" title={`Bonnetje downloaden (${e.receipt.fileName})`} href={`/api/expenses/${e.id}/receipt`}><Paperclip size={16} /></a>
                          : <span className="icon-btn muted" title="Nog geen bonnetje" style={{ opacity: 0.35 }}><Paperclip size={16} /></span>}
                      </td>
                    </tr>
                  ))}
                </tbody>
                <tfoot>
                  <tr>
                    <td colSpan={4}>Totaal</td>
                    <td className="num"><strong>{euro(sum(e => e.vatAmount))}</strong></td>
                    <td className="num"><strong>{euro(sum(e => e.amount))}</strong></td>
                    <td />
                  </tr>
                </tfoot>
              </table>
            </div>
          )}
        </div>
      )}

      {editing && (
        <ExpenseForm expense={editing.expense} date={newDate}
          onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload(true) }} />
      )}
    </>
  )
}
