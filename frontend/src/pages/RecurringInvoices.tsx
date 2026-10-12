import { useEffect, useMemo, useState } from 'react'
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { Repeat, Plus, Trash2, Save, Pause, Play, FilePlus2, ChevronUp, ChevronDown, Lock, CalendarClock } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useAuth } from '../lib/auth'
import { useTabs, useTabTitle } from '../lib/tabs'
import { useToast } from '../lib/toast'
import type { Customer, RecurringInvoice, RecurringLine, RecurringPreview, Settings, VatRegime } from '../lib/types'
import { date, euro, invoiceTotals, lineAmount, toDateInput } from '../lib/format'
import { invoiceStatuses } from '../lib/status'
import { defaultRegime, regimeProblem, units, vatRegimes, zeroVat } from '../lib/invoice'
import { placeholders, recurringActions, recurringIntervals, recurringRules, recurringStates } from '../lib/recurring'
import { Badge, Empty, ErrorBox, Field, Loading, PageHeader } from '../components/ui'

type Form = Pick<RecurringInvoice, 'name' | 'customerId' | 'vatRegime' | 'interval' | 'periodRule' | 'action' | 'startDate' | 'reference' | 'notes'> & {
  endDate: string | null
  lines: RecurringLine[]
}

const day = (s?: string | null) => (s ? s.slice(0, 10) : null)
const newLine = (unitPrice = 0, vatRate = 21, unit = 'maand'): RecurringLine => ({ description: '', quantity: 1, unit, unitPrice, vatRate })
const intervalLabel = (id: string) => recurringIntervals.find(i => i.id === id)?.short ?? id

// De eerste dag van volgende maand: dan komt er niet meteen bij opslaan al een factuur.
function nextMonth() {
  const d = new Date()
  return toDateInput(new Date(d.getFullYear(), d.getMonth() + 1, 1))
}

const fromServer = (t: RecurringInvoice): Form => ({
  name: t.name, customerId: t.customerId, vatRegime: t.vatRegime, interval: t.interval, periodRule: t.periodRule, action: t.action,
  startDate: day(t.startDate)!, endDate: day(t.endDate), reference: t.reference ?? '', notes: t.notes ?? '',
  lines: t.lines.map(l => ({ description: l.description, quantity: l.quantity, unit: l.unit, unitPrice: l.unitPrice, vatRate: l.vatRate })),
})

// Pauzeren, hervatten en "Nu aanmaken", zowel vanuit de lijst als vanuit de terugkerende factuur zelf.
function useRecurringActions(onChanged: (t: RecurringInvoice) => void) {
  const toast = useToast()
  const navigate = useNavigate()
  const [busy, setBusy] = useState(false)

  const act = async (t: RecurringInvoice, kind: 'pause' | 'resume') => {
    setBusy(true)
    try {
      const saved = await api.post<RecurringInvoice>(`/recurring-invoices/${t.id}/${kind}`)
      onChanged(saved)
      toast(kind === 'pause' ? `"${t.name}" is gepauzeerd` : saved.invoiceCount > t.invoiceCount
        ? `"${t.name}" loopt weer. De factuur voor deze periode staat klaar.`
        : `"${t.name}" loopt weer${saved.nextRunDate ? `. De volgende factuur komt op ${date(saved.nextRunDate)}.` : '.'}`)
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const run = async (t: RecurringInvoice) => {
    if (!t.nextPeriod) return
    if (!confirm(`Nu de factuur voor ${t.nextPeriod.label} maken? De volgende factuur schuift dan een periode op.`)) return
    setBusy(true)
    try {
      const res = await api.post<{ invoiceId: number; status: string; fallbackReason?: string | null; recurring: RecurringInvoice }>(`/recurring-invoices/${t.id}/run`)
      onChanged(res.recurring)
      toast(res.fallbackReason
        ? `De factuur staat als concept klaar en is niet verstuurd. ${res.fallbackReason}.`
        : res.status === 'concept' ? `Conceptfactuur voor ${t.nextPeriod.label} staat klaar` : `Factuur voor ${t.nextPeriod.label} is verstuurd`)
      navigate(`/facturen/${res.invoiceId}`)
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  return { busy, pause: (t: RecurringInvoice) => act(t, 'pause'), resume: (t: RecurringInvoice) => act(t, 'resume'), run }
}

// De lijst onder Facturen > Terugkerend.
export function RecurringList({ list, query, reload }: { list: RecurringInvoice[]; query: string; reload: () => void }) {
  const { user } = useAuth()
  const admin = user.role === 'beheerder'
  const navigate = useNavigate()
  const { busy, pause, resume, run } = useRecurringActions(() => reload())
  const s = query.toLowerCase()
  const filtered = list.filter(t => [t.name, t.customerName, t.reference].some(v => v?.toLowerCase().includes(s)))

  return (
    <div className="card">
      {filtered.length === 0 ? (
        <Empty icon={<Repeat size={22} />} title={query ? 'Niets gevonden' : 'Nog geen terugkerende facturen'}
          text={query ? 'Probeer een andere zoekterm.' : 'Factureer je elke maand, elk kwartaal of elk jaar hetzelfde, zoals hosting, onderhoud of een retainer? Dan zet Potel de factuur vanzelf voor je klaar.'}
          action={!query && admin && <button className="btn btn-primary" onClick={() => navigate('/facturen/terugkerend/nieuw')}><Plus size={16} /> Terugkerende factuur maken</button>} />
      ) : (
        <div className="table-wrap">
          <table>
            <thead><tr><th>Naam</th><th>Klant</th><th>Hoe vaak</th><th>Volgende factuur</th><th>Status</th><th className="num">Per keer</th><th /></tr></thead>
            <tbody>
              {filtered.map(t => {
                const st = recurringStates[t.state]
                return (
                  <tr key={t.id} className="clickable" onClick={() => navigate(`/facturen/terugkerend/${t.id}`)}>
                    <td>
                      <strong>{t.name}</strong>
                      {t.demo && <span className="badge badge-gray" style={{ marginLeft: 8 }}>voorbeeld</span>}
                      <div className="cell-sub">{t.invoiceCount === 1 ? '1 factuur gemaakt' : `${t.invoiceCount} facturen gemaakt`}</div>
                    </td>
                    <td>{t.customerName}</td>
                    <td className="muted">{intervalLabel(t.interval)}, {t.periodRule}</td>
                    <td>
                      {t.demo ? <span className="muted">Voorbeeld, maakt geen facturen</span> : t.nextPeriod && t.state === 'actief' ? (<>
                        <div>{date(t.nextPeriod.runDate)}</div>
                        <div className="cell-sub">{t.nextPeriod.label}</div>
                      </>) : <span className="muted">{t.state === 'gepauzeerd' ? 'Gepauzeerd' : 'Geen meer'}</span>}
                    </td>
                    <td><Badge tone={st.tone}>{st.label}</Badge></td>
                    <td className="num"><strong>{euro(t.totals.total)}</strong></td>
                    <td className="row-actions" onClick={e => e.stopPropagation()}>
                      {admin && !t.demo && t.state !== 'afgelopen' && <button className="icon-btn" disabled={busy} title={`Nu de factuur voor ${t.nextPeriod?.label} maken`} onClick={() => run(t)}><FilePlus2 size={16} /></button>}
                      {admin && t.state === 'actief' && <button className="icon-btn" disabled={busy} title="Pauzeren" onClick={() => pause(t)}><Pause size={16} /></button>}
                      {admin && t.state === 'gepauzeerd' && <button className="icon-btn" disabled={busy} title="Hervatten" onClick={() => resume(t)}><Play size={16} /></button>}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

// Een terugkerende factuur maken of bekijken, met een voorbeeld van de eerstvolgende facturen.
export function RecurringEditorPage() {
  const { id } = useParams()
  const isNew = id === 'nieuw'
  const [params] = useSearchParams()
  const { pathname, search } = useLocation()
  const { user } = useAuth()
  const admin = user.role === 'beheerder'
  const { data: customers } = useApi<Customer[]>('/customers')
  const { data: settings } = useApi<Settings>('/settings')
  const { data: template, setData: setTemplate, error } = useApi<RecurringInvoice>(isNew ? null : `/recurring-invoices/${id}`)
  // Wat je aan het bewerken bent. Zolang je niets wijzigt, is het de terugkerende factuur zoals hij op de server staat.
  const [edit, setEdit] = useState<Form | null>(null)
  const [dirty, setDirty] = useState(false)
  const stored = useMemo(() => (template ? fromServer(template) : null), [template])
  const form = edit ?? stored
  const [saving, setSaving] = useState(false)
  const [preview, setPreview] = useState<RecurringPreview | null>(null)
  const { retarget, close } = useTabs()
  const navigate = useNavigate()
  const toast = useToast()
  const { busy, pause, resume, run } = useRecurringActions(t => setTemplate(t))
  useTabTitle(isNew ? 'Nieuwe terugkerende factuur' : template?.name)

  // Een nieuwe begint met de klant uit de link (vanaf de klantpagina) en de btw-regeling die bij die klant hoort.
  useEffect(() => {
    if (!isNew) return
    const customerId = Number(params.get('klant')) || 0
    Promise.all([api.get<Settings>('/settings'), api.get<Customer[]>('/customers')]).then(([s, list]) => {
      const regime = defaultRegime(s, list.find(c => c.id === customerId))
      setEdit({
        name: '', customerId, vatRegime: regime, interval: 'maand', periodRule: 'vooraf', action: 'concept', startDate: nextMonth(), endDate: null,
        reference: '', notes: '', lines: [newLine(0, zeroVat(regime) ? 0 : 21)],
      })
    }).catch(e => toast((e as Error).message, 'error'))
  }, [isNew, params, toast])

  // Rekent na elke wijziging (even later) uit wanneer de facturen komen en hoe de regels eruit gaan zien.
  useEffect(() => {
    if (!form) return
    const timer = setTimeout(() => {
      api.post<RecurringPreview>(`/recurring-invoices/preview${isNew ? '' : `?id=${id}`}`, form).then(setPreview).catch(() => setPreview(null))
    }, 350)
    return () => clearTimeout(timer)
  }, [form, id, isNew])

  if (error) return <ErrorBox message={error} />
  if (!form || (!isNew && !template)) return <Loading />

  const customer = customers?.find(c => c.id === form.customerId)
  const noVat = zeroVat(form.vatRegime)
  const totals = invoiceTotals(form.lines.map(l => ({ ...l, vatRate: noVat ? 0 : l.vatRate })))
  const locked = !!template?.locked
  const demo = !!template?.demo
  const regimeError = regimeProblem(form.vatRegime, customer)
  const regimeInfo = vatRegimes.find(r => r.id === form.vatRegime)
  // Direct versturen kan nog niet; een bestaande die er toch op staat, laten we zien zoals hij is.
  const sendPossible = false

  const update = (patch: Partial<Form>) => { setEdit({ ...form, ...patch }); setDirty(true) }
  const updateLine = (idx: number, patch: Partial<RecurringLine>) => update({ lines: form.lines.map((l, i) => (i === idx ? { ...l, ...patch } : l)) })
  const moveLine = (idx: number, dir: -1 | 1) => {
    const lines = [...form.lines]
    ;[lines[idx], lines[idx + dir]] = [lines[idx + dir]!, lines[idx]!]
    update({ lines })
  }
  const withRegime = (regime: VatRegime): Partial<Form> => ({
    vatRegime: regime,
    lines: zeroVat(regime) === noVat ? form.lines : form.lines.map(l => ({ ...l, vatRate: zeroVat(regime) ? 0 : 21 })),
  })

  const save = async () => {
    setSaving(true)
    try {
      if (isNew) {
        const saved = await api.post<RecurringInvoice>('/recurring-invoices', form)
        setDirty(false)
        toast(saved.invoiceCount > 0
          ? 'Terugkerende factuur opgeslagen. De eerste factuur staat al klaar bij je concepten.'
          : `Terugkerende factuur opgeslagen.${saved.nextRunDate ? ` De eerste factuur komt op ${date(saved.nextRunDate)}.` : ''}`)
        retarget(pathname + search, `/facturen/terugkerend/${saved.id}`)
        return
      }
      const saved = await api.put<RecurringInvoice>(`/recurring-invoices/${id}`, form)
      setTemplate(saved)
      setEdit(null)
      setDirty(false)
      toast(saved.invoiceCount > (template?.invoiceCount ?? 0) ? 'Opgeslagen. De factuur voor deze periode staat klaar.' : 'Terugkerende factuur opgeslagen')
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setSaving(false)
    }
  }

  const remove = async () => {
    if (!template || !confirm(`Terugkerende factuur "${template.name}" verwijderen? De facturen die hij al maakte blijven gewoon bestaan.`)) return
    try {
      await api.del(`/recurring-invoices/${template.id}`)
      toast('Terugkerende factuur verwijderd')
      close(pathname + search)
      navigate('/facturen?tab=terugkerend')
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  const st = template ? recurringStates[template.state] : null
  const subtitle = dirty ? 'Niet opgeslagen wijzigingen'
    : !template ? 'Een factuur die vanzelf terugkomt'
    : template.state === 'actief' && template.nextPeriod ? `Volgende factuur op ${date(template.nextPeriod.runDate)}, voor ${template.nextPeriod.label}`
    : template.state === 'gepauzeerd' ? 'Gepauzeerd: er komen geen nieuwe facturen'
    : 'Afgelopen: de einddatum is voorbij'

  return (
    <>
      <PageHeader
        icon={<Repeat size={20} />}
        title={isNew ? 'Nieuwe terugkerende factuur' : template!.name}
        subtitle={subtitle}
        actions={<>
          {st && <Badge tone={st.tone}>{st.label}</Badge>}
          {demo && <Badge tone="gray">Voorbeeld</Badge>}
          {admin && template && <button className="btn btn-danger" title="Terugkerende factuur verwijderen" onClick={remove}><Trash2 size={15} /></button>}
          {admin && template?.state === 'actief' && <button className="btn" disabled={busy} onClick={() => pause(template)}><Pause size={15} /> Pauzeren</button>}
          {admin && template?.state === 'gepauzeerd' && <button className="btn" disabled={busy} onClick={() => resume(template)}><Play size={15} /> Hervatten</button>}
          {admin && template && !demo && template.state !== 'afgelopen' && (
            <button className="btn" disabled={busy || dirty} title={dirty ? 'Sla eerst je wijzigingen op' : undefined} onClick={() => run(template)}><FilePlus2 size={15} /> Nu aanmaken</button>
          )}
          {admin && <button className="btn btn-primary" onClick={save} disabled={saving || (!dirty && !isNew)}><Save size={15} /> Opslaan</button>}
        </>}
      />

      <div className="invoice-layout">
        <div>
          {!admin && <div className="notice">Alleen een beheerder kan terugkerende facturen maken of wijzigen. Je kunt ze wel bekijken.</div>}
          {demo && <div className="notice">Dit is een voorbeeld uit de welkomstwizard. Het laat zien hoe een terugkerende factuur werkt, maar maakt geen echte facturen.</div>}
          {locked && admin && (
            <div className="notice"><Lock size={13} /> Er zijn al facturen gemaakt. De klant, de startdatum en hoe vaak hij komt liggen daarom vast. De regels, de btw en de rest kun je nog aanpassen; dat geldt dan voor de volgende facturen.</div>
          )}

          <div className="card invoice-form">
            <fieldset className="card-body plain-fieldset" disabled={!admin}>
              <div className="form-grid">
                <Field label="Naam" full>
                  <input value={form.name} placeholder="Bijv. Hosting en onderhoud webshop" maxLength={200} onChange={e => update({ name: e.target.value })} />
                </Field>
                <Field label="Klant *">
                  <select value={form.customerId} disabled={locked} onChange={e => {
                    const customerId = Number(e.target.value)
                    update({ customerId, ...withRegime(defaultRegime(settings, customers?.find(c => c.id === customerId))) })
                  }}>
                    <option value={0}>Kies een klant…</option>
                    {customers?.map(c => <option key={c.id} value={c.id}>{c.company || c.name}{c.company ? ` (${c.name})` : ''}</option>)}
                  </select>
                </Field>
                <Field label="Btw">
                  <select value={form.vatRegime} onChange={e => update(withRegime(e.target.value as VatRegime))}>
                    {vatRegimes.map(r => <option key={r.id} value={r.id}>{r.label}</option>)}
                  </select>
                </Field>
                {(regimeError || form.vatRegime !== 'normaal') && (
                  <p className={regimeError ? 'text-red' : 'muted'} style={{ fontSize: 13, margin: 0, gridColumn: '1 / -1' }}>{regimeError ?? regimeInfo?.help}</p>
                )}
                <Field label="Hoe vaak">
                  <select value={form.interval} disabled={locked} onChange={e => update({ interval: e.target.value as Form['interval'] })}>
                    {recurringIntervals.map(i => <option key={i.id} value={i.id}>{i.label}</option>)}
                  </select>
                </Field>
                <Field label="Factureren">
                  <select value={form.periodRule} onChange={e => update({ periodRule: e.target.value as Form['periodRule'] })}>
                    {recurringRules.map(r => <option key={r.id} value={r.id}>{r.label}</option>)}
                  </select>
                </Field>
                <p className="muted" style={{ fontSize: 13, margin: 0, gridColumn: '1 / -1' }}>{recurringRules.find(r => r.id === form.periodRule)?.help}</p>
                <Field label="Startdatum (eerste periode) *">
                  <input type="date" value={form.startDate} disabled={locked} onChange={e => update({ startDate: e.target.value })} />
                </Field>
                <Field label="Einddatum (laat leeg als hij doorloopt)">
                  <input type="date" value={form.endDate ?? ''} min={form.startDate} onChange={e => update({ endDate: e.target.value || null })} />
                </Field>
                <Field label="Referentie van de klant"><input value={form.reference ?? ''} placeholder="Inkoopnummer of PO" onChange={e => update({ reference: e.target.value })} /></Field>
                <Field label="Wat gebeurt er met elke factuur?">
                  <select value={form.action} onChange={e => update({ action: e.target.value as Form['action'] })}>
                    {recurringActions.map(a => <option key={a.id} value={a.id} disabled={a.id === 'versturen' && !sendPossible && form.action !== 'versturen'}>{a.label}</option>)}
                  </select>
                </Field>
                <p className="muted" style={{ fontSize: 13, margin: 0, gridColumn: '1 / -1' }}>
                  {recurringActions.find(a => a.id === form.action)?.help}
                  {!sendPossible && ' Direct versturen kan nog niet: Potel kan facturen nog niet zelf per e-mail versturen.'}
                </p>
              </div>

              <h4 className="section-title">Regels</h4>
              <div className="lines">
                {form.lines.map((l, i) => (
                  <div key={i} className="line">
                    <input placeholder="Bijv. Hosting {maand} {jaar}" value={l.description} onChange={e => updateLine(i, { description: e.target.value })} />
                    <input type="number" step="0.25" value={l.quantity} aria-label="Aantal" onChange={e => updateLine(i, { quantity: Number(e.target.value) })} />
                    <select value={l.unit} aria-label="Eenheid" onChange={e => updateLine(i, { unit: e.target.value })}>
                      {(units.includes(l.unit) ? units : [l.unit, ...units]).map(u => <option key={u}>{u}</option>)}
                    </select>
                    <input type="number" step="0.01" value={l.unitPrice} aria-label="Tarief" onChange={e => updateLine(i, { unitPrice: Number(e.target.value) })} />
                    <select value={noVat ? 0 : l.vatRate} disabled={noVat} aria-label="Btw" onChange={e => updateLine(i, { vatRate: Number(e.target.value) })}>
                      <option value={21}>21%</option><option value={9}>9%</option><option value={0}>0%</option>
                    </select>
                    <span className="num">{euro(lineAmount(l))}</span>
                    <span className="line-actions">
                      {admin && <>
                        <button className="icon-btn tiny" disabled={i === 0} onClick={() => moveLine(i, -1)} aria-label="Omhoog"><ChevronUp size={14} /></button>
                        <button className="icon-btn tiny" disabled={i === form.lines.length - 1} onClick={() => moveLine(i, 1)} aria-label="Omlaag"><ChevronDown size={14} /></button>
                        <button className="icon-btn tiny danger" disabled={form.lines.length === 1} onClick={() => update({ lines: form.lines.filter((_, x) => x !== i) })} aria-label="Regel verwijderen"><Trash2 size={14} /></button>
                      </>}
                    </span>
                  </div>
                ))}
              </div>
              <div className="row mt between wrap">
                {admin ? <button className="btn btn-sm" onClick={() => update({ lines: [...form.lines, newLine(form.lines.at(-1)?.unitPrice, noVat ? 0 : 21, form.lines.at(-1)?.unit)] })}><Plus size={14} /> Regel toevoegen</button> : <span />}
                <div className="line-totals">
                  <span>Subtotaal <strong>{euro(totals.subtotal)}</strong></span>
                  {!noVat && <span>Btw <strong>{euro(totals.vat)}</strong></span>}
                  <span>Totaal <strong>{euro(totals.total)}</strong></span>
                </div>
              </div>
              <p className="muted" style={{ fontSize: 13 }}>
                Tip: {placeholders.map((p, i) => <span key={p.code}>{i > 0 && (i === placeholders.length - 1 ? ' en ' : ', ')}<code>{p.code}</code></span>)} worden
                per factuur ingevuld, bijvoorbeeld "Hosting {'{maand}'} {'{jaar}'}" wordt "Hosting oktober 2026". Begin je met een hoofdletter, zoals {'{Maand}'}, dan krijgt de maand er ook een.
              </p>

              <Field label="Opmerking op factuur" full>
                <textarea value={form.notes ?? ''} placeholder="Bijv. Dit is de factuur voor {periode}." onChange={e => update({ notes: e.target.value })} />
              </Field>
            </fieldset>
          </div>
        </div>

        <div className="grid" style={{ alignContent: 'start' }}>
          <div className="card">
            <div className="card-head"><h3><CalendarClock size={16} /> Zo gaat het</h3></div>
            <div className="card-body">
              {template?.state === 'gepauzeerd' && <p className="muted" style={{ marginTop: 0 }}>Hij is gepauzeerd. Bij hervatten gaat hij verder bij de eerstvolgende periode; perioden uit de pauze slaan we over.</p>}
              {template?.state === 'afgelopen' && <p className="muted" style={{ marginTop: 0 }}>De einddatum is voorbij, dus er komen geen facturen meer. Schuif de einddatum op om verder te gaan.</p>}
              {preview?.error ? <p className="text-red" style={{ marginTop: 0 }}>{preview.error}</p> : preview && preview.periods.length > 0 ? (<>
                <div className="list">
                  {preview.periods.map((p, i) => (
                    <div key={p.from} className="list-item">
                      <div className="grow">
                        <div>{i === 0 ? 'Eerstvolgende factuur' : 'Daarna'}: <strong>{date(p.runDate)}</strong></div>
                        <div className="cell-sub">Voor {p.label} ({date(p.from)} t/m {date(p.to)})</div>
                      </div>
                      <strong>{euro(totals.total)}</strong>
                    </div>
                  ))}
                </div>
                {preview.dueNow > 0 && !demo && (template?.state ?? 'actief') === 'actief' && (
                  <p className="notice" style={{ marginTop: 12, marginBottom: 0 }}>
                    {preview.dueNow === 1 ? 'Er is al een periode aan de beurt.' : `Er zijn al ${preview.dueNow} perioden aan de beurt.`} Bij opslaan staat
                    {preview.dueNow === 1 ? ' die factuur' : ' voor elke periode een factuur'} meteen klaar als concept.
                  </p>
                )}
                {preview.lines.length > 0 && (<>
                  <h4 className="section-title">Regels op de eerstvolgende factuur</h4>
                  <ul style={{ margin: 0, paddingLeft: 18 }}>{preview.lines.map((l, i) => <li key={i}>{l}</li>)}</ul>
                </>)}
              </>) : preview ? <p className="muted" style={{ margin: 0 }}>Er komen geen facturen meer: de einddatum ligt voor de volgende periode.</p>
                : <p className="muted" style={{ margin: 0 }}>Kies een klant en vul de regels in, dan zie je hier wanneer de facturen komen.</p>}
              <p className="muted" style={{ fontSize: 13, marginBottom: 0 }}>
                Potel maakt elke periode precies één factuur. De factuurdatum en vervaldatum worden de dag waarop hij klaarstaat,
                met je betaaltermijn van {settings?.paymentTermDays ?? 14} dagen.
              </p>
            </div>
          </div>

          {template && (
            <div className="card">
              <div className="card-head"><h3>Gemaakte facturen</h3></div>
              {(template.invoices ?? []).length === 0 ? <Empty icon={<Repeat size={22} />} title="Nog geen facturen" text="Zodra een periode aan de beurt is, verschijnt de factuur hier." /> : (
                <div className="table-wrap">
                  <table>
                    <thead><tr><th>Periode</th><th>Nummer</th><th>Status</th><th className="num">Bedrag</th></tr></thead>
                    <tbody>
                      {template.invoices!.map(i => {
                        const s = invoiceStatuses.find(x => x.id === i.status)!
                        return (
                          <tr key={i.id} className="clickable" onClick={() => navigate(`/facturen/${i.id}`)}>
                            <td>{i.period ?? date(i.periodStart)}</td>
                            <td>{i.number ?? <span className="muted">Concept</span>}</td>
                            <td><Badge tone={s.tone}>{s.label}</Badge></td>
                            <td className="num">{euro(i.total)}</td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          )}
        </div>
      </div>
    </>
  )
}
