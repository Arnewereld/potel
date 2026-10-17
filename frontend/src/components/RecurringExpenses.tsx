import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { BellRing, CalendarClock, CalendarRange, Check, ClipboardCheck, Lightbulb, Plus, Repeat, SkipForward, Wallet } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useAuth } from '../lib/auth'
import { useToast } from '../lib/toast'
import type {
  Customer, Expense, ExpenseCategory, Project, RecurringExpense, RecurringExpenseAction, RecurringExpenseOverview, RecurringExpensePreview, RecurringInterval,
} from '../lib/types'
import { date, euro, toDateInput } from '../lib/format'
import { categoryLabel, expenseCategories, expenseVat, expenseVatRates } from '../lib/expenses'
import { recurringExpenseActions, recurringIntervals } from '../lib/recurring'
import { Empty, Field, Modal } from './ui'
import { MiniStat } from '../pages/Time'
import { ExpenseForm } from './ExpenseForm'

const states: Record<RecurringExpense['state'], { label: string; tone: string }> = {
  actief: { label: 'Actief', tone: 'green' },
  wacht: { label: 'Aan de beurt', tone: 'yellow' },
  afgelopen: { label: 'Afgelopen', tone: 'gray' },
}

const intervalShort = (id: string) => recurringIntervals.find(i => i.id === id)?.short ?? id
const day = (s?: string | null) => (s ? s.slice(0, 10) : '')

// Het overzicht van je vaste kosten: wat ze samen kosten, wat aan de beurt is, concepten om na te kijken en de lijst zelf.
export function RecurringExpenseList({ data, query, reload, onEdit, onNew }: {
  data: RecurringExpenseOverview
  query: string
  reload: () => void
  onEdit: (t: RecurringExpense) => void
  onNew: () => void
}) {
  const toast = useToast()
  const [busy, setBusy] = useState(false)
  const [opened, setOpened] = useState<Expense | null>(null)
  const s = query.toLowerCase()
  const items = data.items.filter(t => [t.supplier, t.description, t.projectName, t.customerName, categoryLabel(t.category)].some(v => v?.toLowerCase().includes(s)))
  const due = data.items.filter(t => t.due && !t.demo)
  const running = data.items.filter(t => t.state !== 'afgelopen')
  const bySupplier = (id?: number | null) => data.items.find(t => t.id === id)

  const post = async (url: string, done: string) => {
    setBusy(true)
    try {
      await api.post(url)
      toast(done)
      reload()
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }
  const book = (t: RecurringExpense) => post(`/recurring-expenses/${t.id}/book`, `Uitgave bij ${t.supplier} voor ${t.due?.period} geboekt`)
  const skip = (t: RecurringExpense) => {
    if (!confirm(`${t.due?.period} overslaan voor ${t.supplier}? Er komt voor die periode geen uitgave.`)) return
    post(`/recurring-expenses/${t.id}/skip`, `${t.due?.period} overgeslagen`)
  }
  const open = async (id: number) => {
    try {
      setOpened(await api.get<Expense>(`/expenses/${id}`))
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  return (
    <>
      <div className="grid grid-stats" style={{ marginBottom: 18 }}>
        <MiniStat icon={<Repeat size={18} />} accent="var(--primary)" label="Per maand" value={euro(data.perMonth)}
          sub={`${running.length} ${running.length === 1 ? 'lopende vaste kost' : 'lopende vaste kosten'}, met btw`} />
        <MiniStat icon={<CalendarRange size={18} />} accent="var(--blue)" label="Per jaar" value={euro(data.perYear)} sub="Zoals je ze nu betaalt" />
        <MiniStat icon={<BellRing size={18} />} accent="var(--yellow)" label="Aan de beurt" value={String(due.length)}
          sub={due.length ? 'Boek ze of sla een periode over' : 'Niets om te boeken'} />
        <MiniStat icon={<ClipboardCheck size={18} />} accent="var(--green)" label="Na te kijken" value={String(data.toReview.length)}
          sub={data.toReview.length ? 'Concepten van je vaste kosten' : 'Alles is nagekeken'} />
      </div>

      {due.length > 0 && (
        <div className="card" style={{ marginBottom: 16 }}>
          <div className="card-head"><h3><BellRing size={16} /> Aan de beurt</h3></div>
          <div className="card-body list">
            {due.map(t => (
              <div key={t.id} className="list-item wrap">
                <div className="grow" style={{ flexBasis: 240 }}>
                  <div><strong>{t.supplier}</strong>, {t.due!.period}: {euro(t.amount)}</div>
                  <div className="cell-sub">
                    {t.action === 'herinneren' ? 'Je wilde een herinnering; de uitgave boek je zelf.' : 'De uitgave wordt vandaag nog klaargezet. Je kunt hem ook nu al boeken.'}
                    {t.due!.count > 1 && ` Daarna ${t.due!.count === 2 ? 'staat er nog 1 periode' : `staan er nog ${t.due!.count - 1} perioden`} klaar.`}
                  </div>
                </div>
                <div className="row" style={{ gap: 6 }}>
                  <button className="btn btn-sm" disabled={busy} onClick={() => skip(t)}><SkipForward size={14} /> Overslaan</button>
                  <button className="btn btn-sm btn-primary" disabled={busy} onClick={() => book(t)}><Wallet size={14} /> Boeken</button>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {data.toReview.length > 0 && (
        <div className="card" style={{ marginBottom: 16 }}>
          <div className="card-head"><h3><ClipboardCheck size={16} /> Concepten om na te kijken</h3></div>
          <div className="card-body">
            <p className="muted" style={{ marginTop: 0 }}>
              Deze uitgaven zette een vaste kost voor je klaar. Ze tellen al mee in je kosten en je btw-aangifte. Klopt het bedrag? Klik dan op Klopt,
              of open de uitgave om iets aan te passen of het bonnetje toe te voegen.
            </p>
            <div className="list">
              {data.toReview.map(e => (
                <div key={e.id} className="list-item clickable" onClick={() => open(e.id)}>
                  <div className="grow">
                    <div><strong>{e.supplier}</strong> <span className="muted nowrap">{date(e.date)}</span></div>
                    <div className="cell-sub">{e.description ?? bySupplier(e.recurringExpenseId)?.description}</div>
                  </div>
                  <strong className="nowrap">{euro(e.amount)}</strong>
                  <button className="btn btn-sm" disabled={busy} onClick={ev => { ev.stopPropagation(); post(`/expenses/${e.id}/reviewed`, `Uitgave bij ${e.supplier} nagekeken`) }}>
                    <Check size={14} /> Klopt
                  </button>
                </div>
              ))}
            </div>
          </div>
        </div>
      )}

      <div className="card">
        {items.length === 0 ? (
          <Empty icon={<Repeat size={22} />} title={query ? 'Niets gevonden' : 'Nog geen vaste kosten'}
            text={query ? 'Probeer een andere zoekterm.' : 'Betaal je elke maand, elk kwartaal of elk jaar hetzelfde, zoals software, je telefoon of een flexplek? Zet het hier neer. Potel zet de uitgave dan elke keer voor je klaar, of herinnert je eraan.'}
            action={!query && <button className="btn btn-primary" onClick={onNew}><Plus size={16} /> Vaste kost toevoegen</button>} />
        ) : (
          <div className="table-wrap">
            <table>
              <thead><tr><th>Leverancier</th><th>Hoe vaak</th><th>Volgende afschrijving</th><th>Status</th><th className="num">Per keer</th></tr></thead>
              <tbody>
                {items.map(t => {
                  const st = states[t.state]
                  return (
                    <tr key={t.id} className="clickable" onClick={() => onEdit(t)}>
                      <td>
                        <strong>{t.supplier}</strong>
                        {t.demo && <span className="badge badge-gray" style={{ marginLeft: 8 }}>voorbeeld</span>}
                        {t.action === 'herinneren' && <span className="badge badge-blue" style={{ marginLeft: 8 }}>herinneren</span>}
                        <div className="cell-sub">
                          {[t.description, categoryLabel(t.category), t.projectName].filter(Boolean).join(' · ')}
                        </div>
                        {t.hint && <div className="cell-sub text-yellow"><Lightbulb size={12} /> {t.hint}</div>}
                      </td>
                      <td className="muted">{intervalShort(t.interval)}</td>
                      <td>
                        {t.nextRunDate ? (<>
                          <div>{date(t.nextRunDate)}</div>
                          <div className="cell-sub">{t.nextPeriod}{t.endDate ? `, loopt tot ${date(t.endDate)}` : ''}</div>
                        </>) : <span className="muted">{t.endDate ? `Gestopt op ${date(t.endDate)}` : 'Geen meer'}</span>}
                      </td>
                      <td><span className={`badge badge-${st.tone} badge-plain`}>{st.label}</span></td>
                      <td className="num">
                        <strong>{euro(t.amount)}</strong>
                        <div className="cell-sub">{t.interval === 'maand' ? (t.reverseCharge ? 'btw verlegd' : t.vatRate ? `${t.vatRate}% btw` : 'geen btw') : `${euro(t.perMonth)} per maand`}</div>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {opened && <ExpenseForm expense={opened} onClose={() => setOpened(null)} onSaved={() => { setOpened(null); reload() }} />}
    </>
  )
}

type Form = {
  supplier: string
  description: string
  category: ExpenseCategory
  amount: string
  vatRate: number | null
  reverseCharge: boolean
  supplierRegion: 'eu' | 'buiten-eu'
  interval: RecurringInterval
  action: RecurringExpenseAction
  startDate: string
  endDate: string
  projectId: number
  customerId: number
}

// Wat er bij een regel uit je bank al vaststaat: bedrag en datum. De rest vullen we voor je in.
export type BankStart = { date: string; amount: number; supplier?: string | null; description?: string | null; category?: ExpenseCategory | null; vatRate?: number | null }

// Een vaste kost maken of aanpassen. Ook vanuit een uitgave die er al staat (die wordt de eerste periode) of vanuit een regel uit je
// bank (bedrag en datum uit de bank; de pagina Bank verstuurt hem dan zelf met submit).
export function RecurringExpenseForm({ recurring, fromExpense, fromBank, submit, onClose, onSaved }: {
  recurring?: RecurringExpense | null
  fromExpense?: Expense | null
  fromBank?: BankStart | null
  submit?: (body: Record<string, unknown>) => Promise<boolean>
  onClose: () => void
  onSaved: () => void
}) {
  const { user } = useAuth()
  const admin = user.role === 'beheerder'
  const toast = useToast()
  const { data: projects } = useApi<Project[]>(fromBank ? null : '/projects')
  const { data: customers } = useApi<Customer[]>(fromBank ? null : '/customers')
  const [form, setForm] = useState<Form>(() => {
    const src = recurring ?? fromExpense
    const nextMonth = new Date(new Date().getFullYear(), new Date().getMonth() + 1, 1)
    return {
      supplier: src?.supplier ?? fromBank?.supplier ?? '',
      description: (recurring ? recurring.description : fromExpense?.description ?? fromBank?.description) ?? '',
      category: src?.category ?? fromBank?.category ?? 'software',
      amount: src ? String(src.amount) : fromBank ? String(fromBank.amount) : '',
      vatRate: src ? src.vatRate : fromBank?.vatRate !== undefined ? fromBank.vatRate : 21,
      reverseCharge: src?.reverseCharge ?? false,
      supplierRegion: src?.supplierRegion ?? 'eu',
      interval: recurring?.interval ?? 'maand',
      action: recurring?.action ?? 'concept',
      startDate: day(recurring?.startDate ?? fromExpense?.date ?? fromBank?.date) || toDateInput(nextMonth),
      endDate: day(recurring?.endDate),
      projectId: src?.projectId ?? 0,
      customerId: src?.customerId ?? 0,
    }
  })
  const [preview, setPreview] = useState<RecurringExpensePreview | null>(null)
  const [busy, setBusy] = useState(false)
  const set = <K extends keyof Form>(key: K, value: Form[K]) => setForm(f => ({ ...f, [key]: value }))

  const amount = Number(form.amount)
  const canReverse = form.vatRate === 21 || form.vatRate === 9
  const reverse = form.reverseCharge && canReverse
  const vat = expenseVat(amount, form.vatRate, reverse)
  const project = projects?.find(p => p.id === form.projectId)
  const selectable = (projects ?? []).filter(p => p.status !== 'afgerond' || p.id === form.projectId)
  // De startdatum ligt vast na de eerste uitgave, en bij een uitgave of bankregel is het die datum.
  const startFixed = !!fromExpense || !!fromBank || !!recurring?.locked
  const amountFixed = !!fromBank

  const body = {
    supplier: form.supplier, description: form.description.trim() || null, category: form.category, amount, vatRate: form.vatRate,
    reverseCharge: reverse, supplierRegion: reverse ? form.supplierRegion : null,
    projectId: form.projectId || null, customerId: form.customerId || null,
    interval: form.interval, action: form.action, startDate: form.startDate, endDate: form.endDate || null,
    fromExpenseId: fromExpense?.id ?? null,
  }
  const bodyKey = JSON.stringify(body)

  // Rekent na elke wijziging (even later) uit wanneer de volgende afschrijvingen komen.
  useEffect(() => {
    const query = recurring ? `?id=${recurring.id}` : fromBank ? '?booked=true' : ''
    const timer = setTimeout(() => {
      api.post<RecurringExpensePreview>(`/recurring-expenses/preview${query}`, JSON.parse(bodyKey)).then(setPreview).catch(() => setPreview(null))
    }, 350)
    return () => clearTimeout(timer)
  }, [bodyKey, recurring, fromBank])

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!amount) { toast('Vul het bedrag in', 'error'); return }
    setBusy(true)
    try {
      if (submit) {
        if (await submit(body)) onSaved()
        return
      }
      const saved = recurring
        ? await api.put<RecurringExpense>(`/recurring-expenses/${recurring.id}`, body)
        : await api.post<RecurringExpense>('/recurring-expenses', body)
      toast(recurring ? 'Vaste kost aangepast'
        : saved.expenseCount > (fromExpense ? 1 : 0) ? 'Vaste kost opgeslagen. De uitgave voor de periode die al aan de beurt was, staat klaar.' : 'Vaste kost opgeslagen')
      onSaved()
    } catch (err) {
      toast((err as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    if (!recurring || !confirm(`De vaste kost bij ${recurring.supplier} verwijderen? De uitgaven die hij al maakte, blijven gewoon staan.`)) return
    try {
      await api.del(`/recurring-expenses/${recurring.id}`)
      toast('Vaste kost verwijderd')
      onSaved()
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  return (
    <Modal wide title={recurring ? 'Vaste kost bewerken' : 'Vaste kost toevoegen'} onClose={onClose}
      footer={<>
        {recurring && admin && <button className="btn btn-danger" type="button" onClick={remove}>Verwijderen</button>}
        <span className="spacer" />
        <button className="btn" type="button" onClick={onClose}>Annuleren</button>
        <button className="btn btn-primary" form="recurring-expense-form" disabled={busy}>{fromBank ? 'Opslaan en koppelen' : 'Opslaan'}</button>
      </>}>
      {recurring?.demo && <p className="notice" style={{ marginTop: 0 }}>Dit is een voorbeeld uit de welkomstwizard. Het maakt geen echte uitgaven.</p>}
      {fromExpense && (
        <p className="muted" style={{ marginTop: 0 }}>
          De uitgave bij {fromExpense.supplier} van {date(fromExpense.date)} wordt de eerste periode. Vanaf daar zet Potel elke keer de volgende klaar.
        </p>
      )}
      {fromBank && (
        <p className="muted" style={{ marginTop: 0 }}>
          Je betaalde <strong>{euro(fromBank.amount)}</strong> op {date(fromBank.date)}. Die betaling wordt de uitgave voor de eerste periode, met bedrag
          en datum uit je bank. Daarna komt hij elke keer terug.
        </p>
      )}
      <form id="recurring-expense-form" className="form-grid" onSubmit={save}>
        <Field label="Leverancier *">
          <input required maxLength={200} autoFocus={!recurring} value={form.supplier} onChange={e => set('supplier', e.target.value)} placeholder="Bijv. Adobe, KPN, Spaces" />
        </Field>
        <Field label="Categorie">
          <select value={form.category} onChange={e => set('category', e.target.value as ExpenseCategory)}>
            {expenseCategories.map(c => <option key={c.id} value={c.id}>{c.label}</option>)}
          </select>
        </Field>
        <Field label="Omschrijving" full>
          <input maxLength={900} value={form.description} onChange={e => set('description', e.target.value)} placeholder="Bijv. Creative Cloud. Potel zet de periode erachter." />
        </Field>
        <Field label={reverse ? 'Bedrag per keer, zonder btw *' : 'Bedrag per keer, met btw *'}>
          <input type="number" required min={0.01} step="0.01" inputMode="decimal" readOnly={amountFixed} value={form.amount}
            onChange={e => set('amount', e.target.value)} placeholder="0,00" />
        </Field>
        <Field label="Btw-tarief">
          <div className="segmented">
            {expenseVatRates.map(r => (
              <button type="button" key={String(r.value)} className={form.vatRate === r.value ? 'active' : ''} onClick={() => set('vatRate', r.value)}>{r.label}</button>
            ))}
          </div>
        </Field>
        <label className={`check-row field-full ${canReverse ? '' : 'muted'}`}>
          <input type="checkbox" disabled={!canReverse} checked={reverse} onChange={e => set('reverseCharge', e.target.checked)} />
          <span className="nowrap">Btw verlegd naar mij</span>
          <span className="muted">· een buitenlandse leverancier rekent geen btw, zoals bij veel software-abonnementen</span>
        </label>
        {reverse && (<>
          <Field label="Waar zit de leverancier?">
            <div className="segmented">
              <button type="button" className={form.supplierRegion === 'eu' ? 'active' : ''} onClick={() => set('supplierRegion', 'eu')}>Binnen de EU</button>
              <button type="button" className={form.supplierRegion === 'buiten-eu' ? 'active' : ''} onClick={() => set('supplierRegion', 'buiten-eu')}>Buiten de EU</button>
            </div>
          </Field>
          <div className="notice field-full" style={{ marginBottom: 0 }}>
            Elke keer geef je {euro(vat)} btw zelf aan in je btw-aangifte en trek je hem in dezelfde aangifte weer af. Op de uitgave staat dat al goed.
          </div>
        </>)}

        <Field label="Hoe vaak">
          <div className="segmented">
            {recurringIntervals.map(i => (
              <button type="button" key={i.id} disabled={!!recurring?.locked} className={form.interval === i.id ? 'active' : ''}
                onClick={() => set('interval', i.id)}>{i.label}</button>
            ))}
          </div>
        </Field>
        <Field label={fromExpense || fromBank ? 'Eerste afschrijving' : 'Eerste afschrijving *'}>
          <input type="date" required readOnly={startFixed} value={form.startDate} onChange={e => set('startDate', e.target.value)}
            title={recurring?.locked ? 'Ligt vast omdat er al uitgaven zijn' : undefined} />
        </Field>
        <Field label="Einddatum (optioneel)">
          <input type="date" value={form.endDate} min={form.startDate} onChange={e => set('endDate', e.target.value)} />
        </Field>
        <Field label="Elke keer">
          <div className="segmented">
            {recurringExpenseActions.map(a => (
              <button type="button" key={a.id} className={form.action === a.id ? 'active' : ''} onClick={() => set('action', a.id)}>{a.label}</button>
            ))}
          </div>
        </Field>
        <p className="cell-sub field-full" style={{ margin: 0 }}>{recurringExpenseActions.find(a => a.id === form.action)?.help}</p>

        {!fromBank && (<>
          <Field label="Project (optioneel)">
            <select value={form.projectId} onChange={e => {
              const id = Number(e.target.value)
              const p = projects?.find(x => x.id === id)
              setForm(f => ({ ...f, projectId: id, customerId: p ? p.customerId : f.customerId }))
            }}>
              <option value={0}>Geen project</option>
              {selectable.map(p => <option key={p.id} value={p.id}>{p.name} · {p.customerName}</option>)}
            </select>
          </Field>
          <Field label="Klant (optioneel)">
            <select value={form.customerId} disabled={!!project} onChange={e => set('customerId', Number(e.target.value))}>
              <option value={0}>Geen klant</option>
              {(customers ?? []).map(c => <option key={c.id} value={c.id}>{c.company || c.name}</option>)}
            </select>
          </Field>
        </>)}

        <div className="field-full">
          <div className="field-label"><CalendarClock size={13} /> Zo gaat het</div>
          {preview?.error ? <p className="text-red" style={{ margin: 0 }}>{preview.error}</p>
            : preview && preview.periods.length > 0 ? (<>
              <div className="list">
                {preview.periods.map((p, i) => (
                  <div key={p.from} className="list-item">
                    <div className="grow">
                      <div>{i === 0 ? 'Volgende afschrijving' : 'Daarna'}: <strong>{date(p.runDate)}</strong></div>
                      <div className="cell-sub">Voor {p.label}</div>
                    </div>
                    <strong>{amount ? euro(amount) : ''}</strong>
                  </div>
                ))}
              </div>
              {preview.description && <p className="cell-sub" style={{ marginBottom: 0 }}>Omschrijving op de uitgave: {preview.description}</p>}
              {preview.dueNow > 0 && !recurring?.demo && (
                <p className="notice" style={{ marginTop: 8, marginBottom: 0 }}>
                  {preview.dueNow === 1 ? 'Er is al een periode aan de beurt.' : `Er zijn al ${preview.dueNow} perioden aan de beurt.`}{' '}
                  {form.action === 'concept'
                    ? `Bij opslaan staat ${preview.dueNow === 1 ? 'die uitgave' : 'voor elke periode een uitgave'} meteen klaar als concept. Had je er al een ingevoerd, dan koppelen we die in plaats van een tweede te maken.`
                    : 'Je ziet hem daarna bij Aan de beurt, om te boeken of over te slaan.'}
                </p>
              )}
            </>) : preview ? <p className="muted" style={{ margin: 0 }}>Er komen geen afschrijvingen meer: de einddatum ligt voor de volgende periode.</p>
              : <p className="muted" style={{ margin: 0 }}>Vul de leverancier en het bedrag in, dan zie je hier wanneer de uitgaven komen.</p>}
          {recurring && recurring.expenseCount > 0 && (
            <p className="cell-sub" style={{ marginBottom: 0 }}>
              {recurring.expenseCount === 1 ? 'Er is 1 uitgave' : `Er zijn ${recurring.expenseCount} uitgaven`} van deze vaste kost. Die zie je bij{' '}
              <Link to="/kosten" onClick={onClose}>Uitgaven</Link>, met de periode erbij. Een aanpassing hier geldt vanaf de volgende periode.
            </p>
          )}
        </div>
      </form>
    </Modal>
  )
}
