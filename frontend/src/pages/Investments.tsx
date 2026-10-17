import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Package, Plus, ChevronLeft, ChevronRight, Info, TrendingDown, Landmark, ShoppingCart, Percent, Undo2, Pencil } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useAuth } from '../lib/auth'
import { isAccountant } from '../lib/accountant'
import { useToast } from '../lib/toast'
import type { Expense, Investment, InvestmentYear } from '../lib/types'
import { date, euro, toDateInput } from '../lib/format'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHeader } from '../components/ui'
import { ExpenseForm } from '../components/ExpenseForm'
import { MiniStat } from './Time'

// Wat er met een verkocht of weggedaan bedrijfsmiddel gebeurde, in gewone woorden.
const disposedWhat = (i: Investment) =>
  i.disposalPrice ? `Verkocht op ${date(i.disposedOn!)} voor ${euro(i.disposalPrice)}` : `Weggedaan op ${date(i.disposedOn!)}`

function disposalText(i: Investment) {
  if (!i.disposedOn || i.disposalResult == null) return null
  return i.disposalResult >= 0
    ? `${disposedWhat(i)}: ${euro(i.disposalResult)} boekwinst`
    : `${disposedWhat(i)}: ${euro(-i.disposalResult)} boekwaarde afgeboekt`
}

export function InvestmentsPage() {
  const [today] = useState(() => toDateInput(new Date()))
  const [year, setYear] = useState(() => Number(today.slice(0, 4)))
  const [withDemo, setWithDemo] = useState(false)
  const { data, error, loading, reload } = useApi<InvestmentYear>(`/investments?year=${year}${withDemo ? '&demo=true' : ''}`)
  const [openId, setOpenId] = useState<number | null>(null)
  const [editing, setEditing] = useState<{ expense?: Expense } | null>(null)
  const toast = useToast()
  // Een boekhouder bekijkt investeringen alleen.
  const accountant = isAccountant(useAuth().user)

  const s = data?.summary
  const open = data?.investments.find(i => i.id === openId) ?? null
  const hasDemo = data?.investments.some(i => i.demo) ?? false

  const edit = async (id: number) => {
    try {
      const expense = await api.get<Expense>(`/expenses/${id}`)
      setOpenId(null)
      setEditing({ expense })
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  return (
    <>
      <PageHeader
        icon={<Package size={20} />}
        title="Investeringen"
        subtitle={data ? `${year} · ${data.investments.length} ${data.investments.length === 1 ? 'bedrijfsmiddel' : 'bedrijfsmiddelen'}` : 'Afschrijven en investeringsaftrek'}
        actions={<>
          <div className="btn-group">
            <button className="btn" onClick={() => setYear(y => y - 1)} aria-label="Vorig jaar"><ChevronLeft size={16} /></button>
            <button className="btn" disabled>{year}</button>
            <button className="btn" onClick={() => setYear(y => y + 1)} aria-label="Volgend jaar"><ChevronRight size={16} /></button>
          </div>
          {!accountant && <button className="btn btn-primary" onClick={() => setEditing({})}><Plus size={16} /> Investering toevoegen</button>}
        </>}
      />

      {data && (
        <div className="notice">
          <Info size={13} /> Een hulp bij je afschrijvingen, geen belastingadvies. Een bedrijfsmiddel van € 450 of meer{data.deduction ? ' zonder btw' : ''} schrijf je af
          over de jaren dat je het gebruikt. Markeer het bij <Link to="/kosten">Kosten</Link> als investering, of voeg het hier toe.
          {' '}<a href={data.calculationSource} target="_blank" rel="noreferrer">Bron: Belastingdienst</a>
        </div>
      )}

      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && <Loading />}

      {data && s && (<>
        {hasDemo && (
          <div className="notice">
            <label className="check-row">
              <input type="checkbox" checked={withDemo} onChange={e => setWithDemo(e.target.checked)} />
              <span>{data.includesDemo
                ? 'De totalen bevatten nu ook de voorbeelddata. Op je jaaroverzicht telt die niet mee.'
                : 'Voorbeeldinvesteringen staan in de lijst, maar tellen niet mee in de totalen. Vink aan om te zien hoe het eruitziet met de voorbeelddata.'}</span>
            </label>
          </div>
        )}

        <div className="grid grid-stats" style={{ marginBottom: 18 }}>
          <MiniStat icon={<TrendingDown size={18} />} accent="var(--primary)" label={`Afschrijving ${year}`} value={euro(s.depreciation)}
            sub="Telt als kosten op je jaaroverzicht" />
          <MiniStat icon={<Landmark size={18} />} accent="var(--blue)" label={`Boekwaarde 31 december`} value={euro(s.bookValue)}
            sub="Wat er nog op je balans staat" />
          <MiniStat icon={<ShoppingCart size={18} />} accent="var(--purple)" label={`Gekocht in ${year}`} value={euro(s.invested)}
            sub={`${euro(s.qualifying)} telt mee voor de KIA`} />
          <MiniStat icon={<Percent size={18} />} accent="var(--green)" label={`Investeringsaftrek ${year}`} value={s.kiaKnown ? euro(s.kia) : 'Nog niet bekend'}
            sub={s.kiaKnown ? 'Indicatie van de KIA, gaat van je winst af' : `De bedragen voor ${year} staan nog niet vast`} />
        </div>

        {s.warnings.map(w => <div key={w} className="notice">{w}</div>)}

        <div className="grid grid-2-1">
          <div className="card">
            {data.investments.length === 0 ? (
              <Empty icon={<Package size={22} />} title={`Geen investeringen in ${year}`}
                text="Een laptop, telefoon of bureau van € 450 of meer schrijf je af over een paar jaar. Markeer de uitgave als investering, dan rekenen we de afschrijving uit."
                action={!accountant && <button className="btn btn-primary" onClick={() => setEditing({})}><Plus size={16} /> Investering toevoegen</button>} />
            ) : (
              <div className="table-wrap">
                <table>
                  <thead><tr><th>Bedrijfsmiddel</th><th className="num">Aanschafprijs</th><th className="num">Afschrijving {year}</th><th className="num">Boekwaarde</th></tr></thead>
                  <tbody>
                    {data.investments.map(i => (
                      <tr key={i.id} className="clickable" onClick={() => setOpenId(i.id)}>
                        <td>
                          <strong>{i.description || i.supplier}</strong>
                          {i.demo && <> <Badge tone="gray">voorbeeld</Badge></>}
                          {i.disposedOn && <> <Badge tone="purple">{i.disposalPrice ? 'verkocht' : 'weggedaan'}</Badge></>}
                          {!i.disposedOn && i.bookValue === i.residual && <> <Badge tone="green">afgeschreven</Badge></>}
                          <div className="cell-sub">
                            {i.description ? `${i.supplier} · ` : ''}gekocht op {date(i.date)} · {i.years} jaar{i.residual ? `, restwaarde ${euro(i.residual)}` : ''}
                            {i.passengerCar ? ' · personenauto' : ''}
                          </div>
                          {disposalText(i) && <div className="cell-sub">{disposalText(i)}</div>}
                        </td>
                        <td className="num nowrap">{euro(i.cost)}<div className="cell-sub">{euro(i.perYear)} per jaar</div></td>
                        <td className="num nowrap">{euro(i.depreciation)}</td>
                        <td className="num nowrap"><strong>{euro(i.bookValue)}</strong></td>
                      </tr>
                    ))}
                  </tbody>
                  <tfoot>
                    <tr>
                      <td>Totaal{hasDemo && !data.includesDemo ? ', zonder voorbeelddata' : ''}</td>
                      <td className="num nowrap" />
                      <td className="num nowrap"><strong>{euro(s.depreciation)}</strong></td>
                      <td className="num nowrap"><strong>{euro(s.bookValue)}</strong></td>
                    </tr>
                  </tfoot>
                </table>
              </div>
            )}
          </div>

          <div className="card">
            <div className="card-head"><h3>Hoe we rekenen</h3><span className="muted">{year}</span></div>
            <div className="card-body">
              <ul className="muted" style={{ fontSize: 13, paddingLeft: 18, margin: 0 }}>
                {[...s.assumptions, ...s.kiaNotes].map(n => <li key={n} style={{ marginBottom: 6 }}>{n}</li>)}
                {(s.disposalLoss > 0 || s.disposalGain > 0) && (
                  <li>Verkocht of weggedaan in {year}: {s.disposalLoss > 0 ? `${euro(s.disposalLoss)} boekwaarde afgeboekt (kosten)` : ''}
                    {s.disposalLoss > 0 && s.disposalGain > 0 ? ' en ' : ''}{s.disposalGain > 0 ? `${euro(s.disposalGain)} boekwinst (telt bij je resultaat)` : ''}.</li>
                )}
              </ul>
              <p className="muted" style={{ fontSize: 12, marginBottom: 0 }}>
                Bron: <a href={data.calculationSource} target="_blank" rel="noreferrer">afschrijven</a>,{' '}
                <a href={data.kiaExclusionsSource} target="_blank" rel="noreferrer">wat niet meetelt</a>,{' '}
                <a href={s.kiaSource} target="_blank" rel="noreferrer">{s.kiaKnown ? `KIA ${year}` : 'KIA'}</a>
              </p>
            </div>
          </div>
        </div>
      </>)}

      {open && data && (
        <InvestmentModal investment={open} year={year} onClose={() => setOpenId(null)} onEdit={() => edit(open.id)} onChanged={() => reload(true)} />
      )}

      {editing && (
        <ExpenseForm expense={editing.expense} asInvestment={!editing.expense} date={year === Number(today.slice(0, 4)) ? today : `${year}-01-01`}
          onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload(true) }} />
      )}
    </>
  )
}

function InvestmentModal({ investment: i, year, onClose, onEdit, onChanged }: {
  investment: Investment
  year: number
  onClose: () => void
  onEdit: () => void
  onChanged: () => void
}) {
  const { user } = useAuth()
  const admin = user.role === 'beheerder'
  const accountant = isAccountant(user)
  const [disposedOn, setDisposedOn] = useState(() => toDateInput(new Date()))
  const [price, setPrice] = useState('')
  const [busy, setBusy] = useState(false)
  const toast = useToast()

  const dispose = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    try {
      const p = Number(price.replace(',', '.')) || null
      await api.put(`/investments/${i.id}/disposal`, { date: disposedOn, price: p })
      toast(p ? 'Verkoop vastgelegd' : 'Vastgelegd als weggedaan')
      onChanged()
    } catch (err) {
      toast((err as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const undo = async () => {
    if (!confirm('De verkoop ongedaan maken? Dan loopt de afschrijving weer door.')) return
    try {
      await api.del(`/investments/${i.id}/disposal`)
      toast('Verkoop ongedaan gemaakt')
      onChanged()
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  return (
    <Modal
      wide
      title={i.description || i.supplier}
      onClose={onClose}
      footer={<>
        <button className="btn" type="button" onClick={onEdit}><Pencil size={15} /> {accountant ? 'Uitgave bekijken' : 'Uitgave bewerken'}</button>
        <span className="spacer" />
        <button className="btn" type="button" onClick={onClose}>Sluiten</button>
      </>}
    >
      <div className="grid grid-1-1">
        <div>
          <div className="row between"><span className="muted">Gekocht</span><span>{date(i.date)} bij {i.supplier}</span></div>
          <div className="row between mt"><span className="muted">Aanschafprijs</span><strong>{euro(i.cost)}</strong></div>
          <div className="row between mt"><span className="muted">Restwaarde</span><span>{euro(i.residual)}</span></div>
          <div className="row between mt"><span className="muted">Gebruiksduur</span><span>{i.years} jaar</span></div>
          <div className="row between mt"><span className="muted">Afschrijving per jaar</span><span>{euro(i.perYear)}</span></div>
          <div className="row between mt">
            <span className="muted">Investeringsaftrek (KIA)</span>
            <span>{i.countsForKia ? `telt mee in ${i.date.slice(0, 4)}` : i.passengerCar ? 'telt niet mee (personenauto)' : 'telt niet mee'}</span>
          </div>
          {i.aboveMaximum && (
            <div className="notice mt" style={{ marginBottom: 0 }}>
              Je schrijft meer dan 20% van de aanschafprijs per jaar af. Fiscaal mag dat meestal niet; kies 5 jaar of meer, of een hogere restwaarde.
            </div>
          )}
        </div>
        <div className="table-wrap">
          <table>
            <thead><tr><th>Jaar</th><th className="num">Afschrijving</th><th className="num" title="Op 31 december">Boekwaarde</th></tr></thead>
            <tbody>
              {i.schedule.map(y => (
                <tr key={y.year} className={y.year === year ? 'selected' : ''}>
                  <td>{y.year}</td><td className="num nowrap">{euro(y.depreciation)}</td><td className="num nowrap">{euro(y.bookValue)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <h4 style={{ margin: '18px 0 8px' }}>Verkocht of weggedaan</h4>
      {i.disposedOn ? (
        <div className="row between" style={{ flexWrap: 'wrap', gap: 10 }}>
          <span>
            {disposedWhat(i)}. De boekwaarde was toen {euro(i.bookValueAtDisposal ?? 0)}.
            {' '}{(i.disposalResult ?? 0) >= 0
              ? `Het verschil van ${euro(i.disposalResult ?? 0)} is boekwinst en telt bij je resultaat.`
              : `Het verschil van ${euro(-(i.disposalResult ?? 0))} boek je af; dat telt als kosten.`}
          </span>
          {admin && <button className="btn" type="button" onClick={undo}><Undo2 size={15} /> Ongedaan maken</button>}
        </div>
      ) : admin ? (
        <form onSubmit={dispose} className="form-grid">
          <Field label="Datum"><input type="date" required min={i.date.slice(0, 10)} value={disposedOn} onChange={e => setDisposedOn(e.target.value)} /></Field>
          <Field label="Verkoopprijs zonder btw (leeg als je niets kreeg)">
            <input type="number" min={0} step="0.01" inputMode="decimal" value={price} onChange={e => setPrice(e.target.value)} placeholder="0,00" />
          </Field>
          <p className="muted field-full" style={{ fontSize: 13, margin: 0 }}>
            De afschrijving loopt tot en met die maand. Wat er dan nog aan boekwaarde over is, boek je af; krijg je meer dan de boekwaarde, dan is het verschil boekwinst.
          </p>
          <div className="field-full"><button className="btn btn-primary" disabled={busy}>Verkocht of weggedaan</button></div>
        </form>
      ) : (
        <p className="muted" style={{ fontSize: 13, margin: 0 }}>{accountant ? 'Nog in gebruik.' : 'Alleen een beheerder kan een bedrijfsmiddel als verkocht of weggedaan zetten.'}</p>
      )}
    </Modal>
  )
}
