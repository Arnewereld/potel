import { useEffect, useRef, useState } from 'react'
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import {
  Receipt, Plus, Trash2, Printer, Save, Building, ChevronUp, ChevronDown, Send, CheckCircle2, Copy, Undo2, BellRing, Clock, Mail, Lock, Repeat, Download, Car,
} from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useTabs, useTabTitle } from '../lib/tabs'
import { useToast } from '../lib/toast'
import { useAuth } from '../lib/auth'
import { isAccountant } from '../lib/accountant'
import type {
  BillableTrips, Customer, Invoice, InvoiceHours, InvoiceLine, InvoiceMailInfo, Language, MailServerStatus, MailStep, Project, RecurringInvoice, Settings, VatRegime,
} from '../lib/types'
import { km } from '../lib/trips'
import { date, euro, hours, invoiceTotals, lineAmount, num, toDateInput } from '../lib/format'
import { invoiceStatuses } from '../lib/status'
import {
  daysUntil, defaultRegime, invoiceTitle, isCredit, missingForSending, regimeProblem, reminderMail, sendMail, units, vatRegimes, zeroVat,
} from '../lib/invoice'
import { Badge, ErrorBox, Field, Loading, Modal, PageHeader } from '../components/ui'
import { InvoicePaper } from '../components/InvoicePaper'
import { HourSpecPaper } from '../components/HourSpecPaper'
import { MailDialog, MailPanel } from '../components/InvoiceMail'
import { PaymentsPanel } from '../components/InvoicePayments'
import { LanguageBadge, LanguageField } from '../components/DocLanguage'
import { RegimeSuggestion } from '../components/RegimeSuggestion'
import { usePrintTitle } from '../lib/printTitle'
import { docTitle, t } from '../lib/docTexts'

type Draft = Omit<Invoice, 'id'> & { id?: number }

const newLine = (unit = 'uur', unitPrice = 0, vatRate = 21): InvoiceLine => ({ description: '', quantity: 1, unit, unitPrice, vatRate })
const emptySettings: Settings = { companyName: '…', defaultHourlyRate: 0, paymentTermDays: 14, weeklyHoursTarget: 0, yearlyHoursTarget: 0 }
const addDays = (d: string, n: number) => { const x = new Date(`${d}T00:00:00`); x.setDate(x.getDate() + n); return toDateInput(x) }
const day = (s?: string | null) => (s ? s.slice(0, 10) : null)

export function InvoiceEditorPage() {
  const { id } = useParams()
  const isNew = id === 'nieuw'
  // Een boekhouder bekijkt de factuur en maakt de PDF, maar wijzigt, verstuurt of mailt niets.
  const { user } = useAuth()
  const accountant = isAccountant(user)
  const [params] = useSearchParams()
  const { pathname, search } = useLocation()
  const { data: customers } = useApi<Customer[]>('/customers')
  const { data: settings } = useApi<Settings>('/settings')
  const [draft, setDraft] = useState<Draft | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [dirty, setDirty] = useState(false)
  const [busy, setBusy] = useState(false)
  // Opslaan loopt: niet nog een keer tegelijk, anders gaat er een verouderde versie achteraan.
  const savingRef = useRef(false)
  const [saving, setSaving] = useState(false)
  // Vandaag: bij versturen wordt dat de factuurdatum.
  const [sendDate] = useState(() => toDateInput(new Date()))
  const [dialog, setDialog] = useState<'send' | 'paid' | null>(null)
  const { data: projects, reload: reloadProjects } = useApi<Project[]>(draft?.customerId && !accountant ? `/projects?customerId=${draft.customerId}` : null)
  // Open ritten van deze klant met een kilometertarief, om door te belasten. Alleen bij een concept.
  const { data: openTrips, reload: reloadTrips } =  useApi<BillableTrips>(draft?.customerId && draft.status === 'concept' && !accountant ? `/trips/billable?customerId=${draft.customerId}` : null)
  // Het nummer dat deze factuur bij versturen krijgt. Hij krijgt dan de datum van die dag, dus ook het nummer van dit jaar.
  const { data: next } = useApi<{ number: string }>(draft && draft.status === 'concept' && !accountant ? '/invoices/next-number' : null)
  // De terugkerende factuur die deze factuur maakte, voor de link terug.
  const { data: recurring } = useApi<RecurringInvoice>(draft?.recurringInvoiceId && !accountant ? `/recurring-invoices/${draft.recurringInvoiceId}` : null)
  // Kan Potel zelf mailen? Anders opent een mail in je eigen mailprogramma.
  const { data: mailStatus } = useApi<MailServerStatus>(accountant ? null : '/invoices/mail-status')
  // Wat er over deze factuur gemaild is; alleen voor een verstuurde, echte factuur.
  const { data: mailInfo, reload: reloadMails } =  useApi<InvoiceMailInfo>(!isNew && !accountant && draft && draft.status !== 'concept' && !draft.demo ? `/invoices/${id}/mails` : null)
  // De urenspecificatie: bij een concept uit de gekoppelde uren van nu, bij een verstuurde factuur zoals hij toen was.
  const { data: hourInfo, reload: reloadHours } = useApi<InvoiceHours>(!isNew ? `/invoices/${id}/hours` : null)
  // Vanuit de lijst met "Herinnering sturen" staat de herinnering meteen klaar, zodra bekend is welke stap de volgende is.
  const [mailing, setMailing] = useState<{ step: MailStep | null; reminder: boolean } | null>(() => params.get('herinnering') ? { step: null, reminder: true } : null)
  const { retarget, close } = useTabs()
  const navigate = useNavigate()
  const toast = useToast()
  useTabTitle(isNew ? 'Nieuwe factuur' : draft ? invoiceTitle(draft) : null)
  // De PDF heet zoals je klant hem leest: "Invoice 2026-0012 from ..." bij een Engelse factuur.
  usePrintTitle(draft ? docTitle(draft.language, isCredit(draft) ? 'credit' : 'invoice', draft.number, draft.seller?.name ?? settings?.companyName ?? '') : document.title)

  useEffect(() => {
    if (isNew && accountant) return
    if (isNew) {
      const today = toDateInput(new Date())
      const customerId = Number(params.get('klant')) || 0
      Promise.all([api.get<Settings>('/settings'), api.get<Customer[]>('/customers')]).then(([s, list]) => {
        const chosen = list.find(c => c.id === customerId)
        const regime = defaultRegime(s, chosen)
        // In de taal van de klant, ook de standaardopmerking.
        const language = chosen?.language ?? 'nl'
        setDraft({
          number: null, customerId, issueDate: today, dueDate: addDays(today, s.paymentTermDays), status: 'concept', reference: '',
          vatRegime: regime, language, deliveryFrom: today, deliveryTo: null, notes: t(language, 'note.default'),
          lines: [newLine('uur', s.defaultHourlyRate, zeroVat(regime) ? 0 : 21)],
        })
      }).catch(e => setError(e.message))
    } else {
      api.get<Invoice>(`/invoices/${id}`).then(inv => setDraft(fromServer(inv))).catch(e => setError(e.message))
    }
  }, [id, isNew, params, accountant])

  if (isNew && accountant) return <ErrorBox message="Als boekhouder kun je geen facturen maken. Je kunt ze wel bekijken en als PDF opslaan." />
  if (error) return <ErrorBox message={error} />
  if (!draft) return <Loading />

  const company = settings ?? emptySettings
  const customer = customers?.find(c => c.id === draft.customerId)
  const totals = invoiceTotals(draft.lines)
  const credit = isCredit(draft)
  const linked = draft.creditForInvoiceId != null
  // Alleen een concept kan nog veranderen; een verstuurde factuur ligt vast.
  const readOnly = draft.status !== 'concept'
  // Niets aan te passen: verstuurd, of je kijkt alleen mee.
  const locked = readOnly || accountant
  const noVat = zeroVat(draft.vatRegime)
  const openProjects = (projects ?? []).filter(p => p.billing === 'uur' && p.minutesUnbilled > 0)
  const due = daysUntil(draft.dueDate)
  const regimeInfo = vatRegimes.find(r => r.id === draft.vatRegime)
  // Een creditnota volgt de regeling van de factuur die hij corrigeert, ook als de klant inmiddels veranderd is.
  const regimeError = linked ? null : regimeProblem(draft.vatRegime, customer)
  const creditNotes = draft.creditNotes ?? []
  const fullyCredited = !credit && !!draft.fullyCredited
  const demo = !!draft.demo
  const canMail = mailStatus?.canSend === true && readOnly && !demo
  // Bij versturen wordt de factuurdatum vandaag en schuift de vervaldatum mee, zodat de betaaltermijn gelijk blijft.
  const termDays = Math.max(0, Math.round((new Date(`${draft.dueDate}T00:00:00`).getTime() - new Date(`${draft.issueDate}T00:00:00`).getTime()) / 86400000))

  const update = (patch: Partial<Draft>) => { setDraft({ ...draft, ...patch }); setDirty(true) }
  const updateLine = (idx: number, patch: Partial<InvoiceLine>) =>
    update({ lines: draft.lines.map((l, i) => (i === idx ? { ...l, ...patch } : l)) })
  const moveLine = (idx: number, dir: -1 | 1) => {
    const lines = [...draft.lines]
    ;[lines[idx], lines[idx + dir]] = [lines[idx + dir]!, lines[idx]!]
    update({ lines })
  }
  // Andere regeling: zonder btw gaan alle regels op 0%, terug naar normaal weer op 21%.
  const withRegime = (regime: VatRegime): Partial<Draft> => ({
    vatRegime: regime,
    lines: zeroVat(regime) === noVat ? draft.lines : draft.lines.map(l => ({ ...l, vatRate: zeroVat(regime) ? 0 : 21 })),
  })
  // Andere taal: staat de standaardopmerking er nog, dan gaat die mee naar de nieuwe taal.
  const withLanguage = (language: Language): Partial<Draft> => ({
    language,
    notes: draft.notes === t(draft.language, 'note.default') ? t(language, 'note.default') : draft.notes,
  })
  const chooseCustomer = (customerId: number) => {
    const chosen = customers?.find(c => c.id === customerId)
    update({ customerId, ...withRegime(defaultRegime(settings, chosen)), ...(chosen ? withLanguage(chosen.language ?? 'nl') : {}) })
  }

  // Slaat op en geeft het id terug; een nieuwe factuur krijgt daarbij een eigen tabblad.
  // Met stay blijft een nieuwe factuur nog even op dit tabblad, zodat er eerst iets aan toegevoegd kan worden.
  const save = async (patch: Partial<Draft> = {}, quiet = false, stay = false): Promise<number | null> => {
    if (savingRef.current) return null
    savingRef.current = true
    setSaving(true)
    const body = { ...draft, ...patch }
    try {
      if (isNew) {
        const saved = await api.post<Invoice>('/invoices', body)
        if (!quiet) toast('Conceptfactuur aangemaakt')
        setDirty(false)
        if (!stay) retarget(pathname + search, `/facturen/${saved.id}`)
        return saved.id
      }
      const saved = await api.put<Invoice>(`/invoices/${id}`, body)
      setDraft(fromServer(saved))
      if (!quiet) toast('Factuur opgeslagen')
      setDirty(false)
      // Regels met uren of ritten die eraf gingen geven die weer vrij.
      reloadProjects(true)
      reloadTrips(true)
      await reloadHours(true)
      return saved.id
    } catch (e) {
      toast((e as Error).message, 'error')
      return null
    } finally {
      savingRef.current = false
      setSaving(false)
    }
  }

  const setStatus = async (status: Draft['status'], paidAt?: string): Promise<Invoice | null> => {
    if (savingRef.current) return null
    if (dirty && !(await save({}, true))) return null
    try {
      const saved = await api.post<Invoice>(`/invoices/${id}/status`, { status, paidAt })
      setDraft(fromServer(saved))
      reloadMails(true)
      reloadHours(true)
      toast(`${invoiceTitle(saved)} is ${invoiceStatuses.find(s => s.id === saved.status)?.label.toLowerCase()}`)
      return saved
    } catch (e) {
      toast((e as Error).message, 'error')
      return null
    }
  }

  const copy = async (kind: 'duplicate' | 'credit') => {
    try {
      const inv = await api.post<Invoice>(`/invoices/${id}/${kind}`)
      toast(kind === 'credit' ? 'Conceptcreditnota aangemaakt. Pas de regels aan als je maar een deel crediteert.' : 'Kopie aangemaakt als concept')
      navigate(`/facturen/${inv.id}`)
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  const addHours = async (p: Project) => {
    if (busy) return
    setBusy(true)
    try {
      // Lege regels mogen weg; blijft er niets over, dan houdt een tijdelijke regel de factuur geldig tot de uren erop staan.
      const filled = draft.lines.filter(l => l.description.trim())
      const lines = filled.length ? filled : [{ ...newLine(), description: '-', unitPrice: 0 }]
      const invoiceId = isNew || dirty ? await save({ lines }, true, true) : Number(id)
      if (!invoiceId) return
      try {
        const saved = await api.post<Invoice>(`/invoices/${invoiceId}/hours`, { projectId: p.id, detailed: false })
        toast(`${hours(p.minutesUnbilled)} van ${p.name} toegevoegd`)
        setDraft(fromServer(saved))
        setDirty(false)
        if (!isNew) reloadHours(true)
      } catch (e) {
        toast((e as Error).message, 'error')
      }
      reloadProjects(true)
      if (isNew) retarget(pathname + search, `/facturen/${invoiceId}`)
    } finally {
      setBusy(false)
    }
  }

  // Zet de open ritten van de klant erbij, net als open uren: eerst opslaan, dan koppelt de server ze aan hun regel.
  const addTrips = async () => {
    if (busy || !openTrips) return
    setBusy(true)
    try {
      const filled = draft.lines.filter(l => l.description.trim())
      const lines = filled.length ? filled : [{ ...newLine(), description: '-', unitPrice: 0 }]
      const invoiceId = isNew || dirty ? await save({ lines }, true, true) : Number(id)
      if (!invoiceId) return
      try {
        const saved = await api.post<Invoice>(`/invoices/${invoiceId}/trips`, {})
        toast(`${km(openTrips.kilometres)} aan ritten toegevoegd`)
        setDraft(fromServer(saved))
        setDirty(false)
      } catch (e) {
        toast((e as Error).message, 'error')
      }
      reloadTrips(true)
      if (isNew) retarget(pathname + search, `/facturen/${invoiceId}`)
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    const freed = [
      draft.lines.some(l => l.unit === 'uur') ? ' Uren op deze factuur komen weer open te staan.' : '',
      draft.lines.some(l => l.unit === 'km') ? ' Ritten op deze factuur komen weer open te staan.' : '',
    ].join('')
    if (!confirm(`${demo ? 'Voorbeeldfactuur' : invoiceTitle(draft)} verwijderen?${freed}`)) return
    try {
      await api.del(`/invoices/${id}`)
      toast(demo ? 'Voorbeeldfactuur verwijderd' : 'Concept verwijderd')
      close(pathname)
      navigate('/facturen')
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  // Eerst opslaan, zodat de controles (ook die van de urenspecificatie) gaan over de regels die je ziet.
  const openSend = async () => {
    if (dirty && !(await save({}, true))) return
    setDialog('send')
  }

  // De schakelaars van de urenspecificatie slaan het concept meteen op, zodat het voorbeeld klopt.
  const spec = hourInfo?.spec ?? null
  const specOn = draft.hourSpecification !== false
  const hoursProblem = specOn && !locked && spec ? hourInfo?.problem ?? null : null
  const setHourOption = async (patch: Pick<Draft, 'hourSpecification'> | Pick<Draft, 'hourAmounts'>) => {
    if (locked) return
    update(patch)
    await save(patch, true)
  }
  const downloadHours = () =>
    api.download(`/invoices/${id}/hours.csv`, t(draft.language, 'public.csvFile', { number: draft.number ?? 'concept' })).catch(e => toast((e as Error).message, 'error'))

  const statusInfo = invoiceStatuses.find(s => s.id === draft.status)!
  // Al een deel betaald: dat staat erbij, met wat er nog openstaat.
  const partly = !credit && !fullyCredited && (draft.status === 'verzonden' || draft.status === 'verlopen') && (draft.paidTotal ?? 0) > 0
    ? `, nog ${euro(draft.openAmount ?? 0)} open` : ''
  const subtitle = dirty ? 'Niet opgeslagen wijzigingen'
    : fullyCredited ? 'Helemaal gecrediteerd'
    : draft.status === 'betaald' ? `Betaald${draft.paidAt ? ` op ${date(draft.paidAt)}` : ''}`
    : draft.status === 'verlopen' ? `${-due} dagen te laat${partly}`
    : draft.status === 'verzonden' ? (credit ? `Verstuurd${draft.sentAt ? ` op ${date(draft.sentAt)}` : ''}` : `${due >= 0 ? `Vervalt over ${due} dagen` : 'Vervallen'}${partly}`)
    : 'Concept, nog niet verstuurd'

  return (
    <>
      <div className="no-print">
        <PageHeader
          icon={<Receipt size={20} />}
          title={isNew ? 'Nieuwe factuur' : invoiceTitle(draft)}
          subtitle={subtitle}
          actions={<>
            {!isNew && <Badge tone={statusInfo.tone}>{statusInfo.label}</Badge>}
            {!isNew && demo && <Badge tone="gray">Voorbeeld</Badge>}
            {!isNew && !accountant && (!readOnly || demo) && <button className="btn btn-danger" title={demo ? 'Voorbeeldfactuur verwijderen' : 'Concept verwijderen'} onClick={remove}><Trash2 size={15} /></button>}
            {!isNew && !credit && !accountant && <button className="btn" title="Kopie maken als nieuw concept" onClick={() => copy('duplicate')}><Copy size={15} /></button>}
            {readOnly && !accountant && !credit && !demo && !fullyCredited && <button className="btn" onClick={() => copy('credit')}><Undo2 size={15} /> Creditnota maken</button>}
            <button className="btn" onClick={() => window.print()}><Printer size={15} /> PDF</button>
            {!locked && <button className="btn" onClick={() => save()} disabled={saving || (!dirty && !isNew)}><Save size={15} /> Opslaan</button>}
            {!locked && !isNew && <button className="btn btn-primary" disabled={saving} onClick={openSend}><Send size={15} /> Versturen</button>}
            {canMail && !fullyCredited && <button className="btn" onClick={() => setMailing({ step: 0, reminder: false })}><Mail size={15} /> Mailen</button>}
            {draft.status === 'verlopen' && !fullyCredited && !accountant && (canMail
              ? <button className="btn" onClick={() => setMailing({ step: mailInfo?.nextStep || 1, reminder: true })}><BellRing size={15} /> Herinnering sturen</button>
              : <a className="btn" href={reminderMail({ ...draft, id: Number(id) }, customer, company)}><BellRing size={15} /> Herinnering</a>)}
            {(draft.status === 'verzonden' || draft.status === 'verlopen') && !fullyCredited && !accountant && <button className="btn btn-success" onClick={() => setDialog('paid')}><CheckCircle2 size={15} /> {credit ? 'Verrekend' : 'Betaald'}</button>}
          </>}
        />
      </div>

      <div className="invoice-layout">
        <div className="no-print">
          {demo && (
            <div className="notice row between wrap" style={{ gap: 10 }}>
              <span>Dit is een voorbeeldfactuur uit de welkomstwizard. Hij telt niet mee in je echte nummering en je kunt hem gewoon verwijderen.</span>
              {!accountant && <button className="btn btn-sm" onClick={remove}><Trash2 size={14} /> Verwijderen</button>}
            </div>
          )}
          {readOnly && !demo && (
            <div className="notice row between wrap" style={{ gap: 10 }}>
              <span>
                <Lock size={13} /> Deze {credit ? 'creditnota' : 'factuur'} is verstuurd en ligt vast: je moet hem 7 jaar bewaren zoals hij is.
                {!credit && !fullyCredited && !accountant && ' Klopt er iets niet? Maak dan een creditnota en zo nodig een nieuwe factuur.'}
              </span>
              {!credit && !fullyCredited && !accountant && <button className="btn btn-sm" onClick={() => copy('credit')}><Undo2 size={14} /> Creditnota maken</button>}
            </div>
          )}
          {draft.recurringInvoiceId != null && (
            <div className="notice">
              <Repeat size={13} /> Gemaakt door de terugkerende factuur{' '}
              {accountant ? 'van deze klant' : <a onClick={() => navigate(`/facturen/terugkerend/${draft.recurringInvoiceId}`)} style={{ cursor: 'pointer' }}>{recurring ? `"${recurring.name}"` : 'van deze klant'}</a>}
              {draft.deliveryFrom ? `, voor de periode ${date(draft.deliveryFrom)}${draft.deliveryTo ? ` t/m ${date(draft.deliveryTo)}` : ''}` : ''}.
              {!locked && ' Kijk hem na en verstuur hem als alles klopt.'}
            </div>
          )}
          {!credit && creditNotes.length > 0 && (
            <div className="notice">
              {fullyCredited ? 'Helemaal gecrediteerd' : 'Gedeeltelijk gecrediteerd'} met{' '}
              {creditNotes.map((c, i) => (
                <span key={c.id}>
                  {i > 0 && (i === creditNotes.length - 1 ? ' en ' : ', ')}
                  <a onClick={() => navigate(`/facturen/${c.id}`)} style={{ cursor: 'pointer' }}>creditnota {c.number}</a> ({euro(c.total)})
                </span>
              ))}.
              {fullyCredited
                ? ' De klant hoeft niets meer te betalen.'
                : draft.status === 'betaald' ? '' : ` Nog te betalen: ${euro(draft.openAmount ?? totals.total)}.`}
            </div>
          )}
          {readOnly && !credit && !isNew && (!fullyCredited || (draft.payments?.length ?? 0) > 0) && (
            <PaymentsPanel
              invoice={{ ...draft, id: Number(id) }}
              total={totals.total}
              admin={user.role === 'beheerder'}
              view={accountant}
              onChange={inv => { setDraft(fromServer(inv)); reloadMails(true) }}
            />
          )}
          {!locked && linked && (
            <div className="notice">
              Creditnota voor factuur {draft.creditForNumber}{draft.creditForIssueDate ? ` van ${date(draft.creditForIssueDate)}` : ''}.
              Klant en btw-regeling horen bij die factuur. Crediteer je maar een deel? Pas dan de aantallen aan.
            </div>
          )}

          <div className="card invoice-form">
            <fieldset className="card-body plain-fieldset" disabled={locked}>
              <div className="form-grid">
                <Field label="Klant *">
                  <select value={draft.customerId} disabled={linked} onChange={e => chooseCustomer(Number(e.target.value))}>
                    <option value={0}>Kies een klant…</option>
                    {customers?.map(c => <option key={c.id} value={c.id}>{c.company || c.name}{c.company ? ` (${c.name})` : ''}</option>)}
                  </select>
                </Field>
                <Field label={credit ? 'Creditnotanummer' : 'Factuurnummer'}>
                  <input disabled value={draft.number ?? (next ? `Krijgt bij versturen nummer ${next.number}` : 'Krijgt een nummer bij versturen')} />
                </Field>
                <Field label={readOnly ? 'Factuurdatum' : 'Factuurdatum (wordt de dag van versturen)'}><input type="date" value={draft.issueDate} onChange={e => update({ issueDate: e.target.value })} /></Field>
                <Field label="Vervaldatum">
                  <div className="input-with-chips">
                    <input type="date" value={draft.dueDate} onChange={e => update({ dueDate: e.target.value })} />
                    {!locked && [14, 30].map(n => (
                      <button key={n} type="button" className={`chip ${draft.dueDate === addDays(draft.issueDate, n) ? 'active' : ''}`} onClick={() => update({ dueDate: addDays(draft.issueDate, n) })}>{n} d</button>
                    ))}
                  </div>
                </Field>
                <Field label="Leverdatum *">
                  <input type="date" value={draft.deliveryFrom ?? ''} onChange={e => update({ deliveryFrom: e.target.value || null })} />
                </Field>
                <Field label="Tot en met (bij een periode)">
                  <input type="date" value={draft.deliveryTo ?? ''} min={draft.deliveryFrom ?? undefined} onChange={e => update({ deliveryTo: e.target.value || null })} />
                </Field>
                <Field label="Referentie van de klant"><input value={draft.reference ?? ''} placeholder="Inkoopnummer of PO" onChange={e => update({ reference: e.target.value })} /></Field>
                <Field label="Btw">
                  <select value={draft.vatRegime} disabled={linked} onChange={e => update(withRegime(e.target.value as VatRegime))}>
                    {vatRegimes.map(r => <option key={r.id} value={r.id}>{r.label}</option>)}
                  </select>
                </Field>
                {(regimeError || draft.vatRegime !== 'normaal') && (
                  <p className={regimeError ? 'text-red' : 'muted'} style={{ fontSize: 13, margin: 0, gridColumn: '1 / -1' }}>
                    {regimeError ?? regimeInfo?.help}
                  </p>
                )}
                {!locked && !linked && (
                  <RegimeSuggestion kind="factuur" regime={draft.vatRegime} customer={customer} onAccept={regime => update(withRegime(regime))} />
                )}
                {/* Een creditnota volgt de taal van de factuur die hij corrigeert. */}
                <LanguageField kind={credit ? 'creditnota' : 'factuur'} value={draft.language} disabled={linked} onChange={language => update(withLanguage(language))} />
                {draft.language === 'en' && (
                  <p className="muted" style={{ fontSize: 13, margin: 0, alignSelf: 'end' }}>
                    Je klant krijgt de {credit ? 'creditnota' : 'factuur'}, de mails en de pagina achter de link in het Engels. Je eigen omschrijvingen en opmerking vertaalt Potel niet.
                  </p>
                )}
              </div>

              <h4 className="section-title">Regels</h4>
              <div className="lines">
                <div className="line line-head"><span>Omschrijving</span><span>Aantal</span><span>Eenheid</span><span>Tarief</span><span>Btw</span><span className="num">Bedrag</span><span /></div>
                {draft.lines.map((l, i) => (
                  <div key={i} className="line">
                    <input placeholder="Bijv. Ontwikkeling API-koppeling" value={l.description} onChange={e => updateLine(i, { description: e.target.value })} />
                    <input type="number" step="0.25" value={l.quantity} onChange={e => updateLine(i, { quantity: Number(e.target.value) })} />
                    <select value={l.unit} onChange={e => updateLine(i, { unit: e.target.value })}>
                      {(units.includes(l.unit) ? units : [l.unit, ...units]).map(u => <option key={u}>{u}</option>)}
                    </select>
                    <input type="number" step="0.01" value={l.unitPrice} onChange={e => updateLine(i, { unitPrice: Number(e.target.value) })} />
                    <select value={l.vatRate} disabled={noVat} onChange={e => updateLine(i, { vatRate: Number(e.target.value) })}>
                      <option value={21}>21%</option><option value={9}>9%</option><option value={0}>0%</option>
                    </select>
                    <span className="num">{euro(lineAmount(l))}</span>
                    <span className="line-actions">
                      {!locked && <>
                        <button className="icon-btn tiny" disabled={i === 0} onClick={() => moveLine(i, -1)} aria-label="Omhoog"><ChevronUp size={14} /></button>
                        <button className="icon-btn tiny" disabled={i === draft.lines.length - 1} onClick={() => moveLine(i, 1)} aria-label="Omlaag"><ChevronDown size={14} /></button>
                        <button className="icon-btn tiny danger" disabled={draft.lines.length === 1} onClick={() => update({ lines: draft.lines.filter((_, x) => x !== i) })} aria-label="Regel verwijderen"><Trash2 size={14} /></button>
                      </>}
                    </span>
                  </div>
                ))}
              </div>
              <div className="row mt between wrap">
                {locked ? <span /> : (
                  <button className="btn btn-sm" onClick={() => update({ lines: [...draft.lines, newLine(draft.lines.at(-1)?.unit, draft.lines.at(-1)?.unitPrice, noVat ? 0 : 21)] })}><Plus size={14} /> Regel toevoegen</button>
                )}
                <div className="line-totals">
                  <span>Subtotaal <strong>{euro(totals.subtotal)}</strong></span>
                  {!noVat && <span>Btw <strong>{euro(totals.vat)}</strong></span>}
                  <span>Totaal <strong>{euro(totals.total)}</strong></span>
                </div>
              </div>

              {!locked && !linked && openProjects.length > 0 && (
                <div className="open-hours">
                  <div className="open-hours-head"><Clock size={15} /> Open uren van {customer?.company || customer?.name}</div>
                  {openProjects.map(p => (
                    <div key={p.id} className="open-hours-row">
                      <span className="legend-dot" style={{ background: p.color }} />
                      <span className="grow truncate">{p.name}</span>
                      <span className="muted">{hours(p.minutesUnbilled)} · {euro(p.unbilledValue)}</span>
                      <button className="btn btn-sm" disabled={busy} onClick={() => addHours(p)}><Plus size={13} /> Toevoegen</button>
                    </div>
                  ))}
                </div>
              )}

              {!locked && !linked && openTrips && (openTrips.trips > 0 || openTrips.withoutRate > 0) && (
                <div className="open-hours">
                  <div className="open-hours-head"><Car size={15} /> Open ritten van {customer?.company || customer?.name}</div>
                  {openTrips.trips > 0 && (
                    <div className="open-hours-row">
                      <span className="grow truncate">
                        {openTrips.trips} {openTrips.trips === 1 ? 'rit' : 'ritten'}, {openTrips.lines.map(l => `${km(l.kilometres)} à ${euro(l.rate)}`).join(' en ')}
                      </span>
                      <span className="muted">{euro(openTrips.amount)}</span>
                      <button className="btn btn-sm" disabled={busy} onClick={addTrips}><Plus size={13} /> Toevoegen</button>
                    </div>
                  )}
                  {openTrips.withoutRate > 0 && (
                    <div className="open-hours-row muted" style={{ fontSize: 13 }}>
                      {openTrips.withoutRate} {openTrips.withoutRate === 1 ? 'rit heeft' : 'ritten hebben'} nog geen kilometertarief. Stel dat in bij de klant of het project.
                    </div>
                  )}
                </div>
              )}

              <Field label="Opmerking op factuur" full><textarea value={draft.notes ?? ''} onChange={e => update({ notes: e.target.value })} /></Field>
              {!locked && !settings?.iban && (
                <p className="muted" style={{ fontSize: 13, marginBottom: 0 }}>
                  Tip: vul je IBAN, KvK en btw-id in bij <a onClick={() => navigate('/instellingen')} style={{ cursor: 'pointer' }}><Building size={12} /> Instellingen</a>, dan staan ze op elke factuur.
                </p>
              )}
            </fieldset>
          </div>
          {spec && (
            <div className="card">
              <div className="card-body">
                <div className="hours-fold">
                  <h4 className="section-title" style={{ margin: 0 }}><Clock size={15} /> Urenspecificatie</h4>
                  <button className="btn btn-sm" onClick={downloadHours}><Download size={14} /> CSV</button>
                </div>
                <p className="muted" style={{ fontSize: 13 }}>
                  {num(spec.hours)} uur in {spec.projects.reduce((n, p) => n + p.entries.length, 0)} boekingen
                  {spec.projects.length > 1 ? ` van ${spec.projects.length} projecten` : ` van ${spec.projects[0]?.name}`}.
                  {readOnly
                    ? (specOn ? ' Meegestuurd als extra pagina en vastgelegd bij versturen: latere wijzigingen aan de uren veranderen hem niet.' : ' Niet meegestuurd met deze factuur.')
                    : ' Je klant krijgt hem als extra pagina na de factuur en kan hem via de link als CSV downloaden.'}
                </p>
                {!locked && (<>
                  <label className="check-row">
                    <input type="checkbox" checked={specOn} disabled={saving} onChange={e => setHourOption({ hourSpecification: e.target.checked })} />
                    Urenspecificatie meesturen
                  </label>
                  <label className="check-row" style={{ marginTop: 8 }}>
                    <input type="checkbox" checked={!!draft.hourAmounts} disabled={saving || !specOn} onChange={e => setHourOption({ hourAmounts: e.target.checked })} />
                    Toon bedragen per regel (alleen bij regels per uur)
                  </label>
                </>)}
                {hoursProblem && <p className="text-red" style={{ fontSize: 13, marginBottom: 0 }}>{hoursProblem}</p>}
              </div>
            </div>
          )}
          {readOnly && !demo && mailInfo && (
            <MailPanel
              invoiceId={Number(id)}
              info={mailInfo}
              onChange={() => reloadMails(true)}
              onCustomer={() => customer && navigate(`/klanten/${customer.id}`)}
              onSettings={() => navigate('/instellingen?tab=email')}
            />
          )}
        </div>

        <div className="invoice-papers">
          <LanguageBadge lang={draft.language} kind={credit ? 'creditnota' : 'factuur'} />
          <InvoicePaper invoice={draft} customer={customer} company={company} />
          {spec && specOn && <HourSpecPaper spec={spec} invoice={draft} customer={customer} company={company} />}
        </div>
      </div>

      {dialog === 'send' && (
        <SendDialog
          title={credit ? 'Creditnota versturen' : 'Factuur versturen'}
          missing={missingForSending(draft, customer, company)}
          problem={regimeError ?? hoursProblem}
          number={next?.number}
          issueDate={sendDate}
          dueDate={credit ? null : addDays(sendDate, termDays)}
          email={customer?.email}
          canMail={mailStatus?.canSend === true && !demo}
          onMail={() => { setDialog(null); setMailing({ step: 0, reminder: false }) }}
          mailHref={inv => sendMail(inv, customer, company)}
          onSend={() => setStatus('verzonden')}
          onSettings={() => navigate('/instellingen')}
          onCustomer={() => customer && navigate(`/klanten/${customer.id}`)}
          onClose={() => setDialog(null)}
        />
      )}
      {dialog === 'paid' && (
        <PaidDialog total={credit ? totals.total : draft.openAmount ?? totals.total} partly={!credit && (draft.paidTotal ?? 0) > 0} onClose={() => setDialog(null)} onConfirm={paidAt => { setDialog(null); setStatus('betaald', paidAt) }} />
      )}
      {mailing && canMail && (mailing.step !== null || mailInfo) && (
        <MailDialog invoiceId={Number(id)} step={mailing.step ?? mailInfo?.nextStep ?? 1} reminder={mailing.reminder} onClose={() => setMailing(null)} onSent={() => reloadMails(true)} />
      )}
    </>
  )
}

const fromServer = (inv: Invoice): Draft => ({
  ...inv, issueDate: inv.issueDate.slice(0, 10), dueDate: inv.dueDate.slice(0, 10), reference: inv.reference ?? '',
  deliveryFrom: day(inv.deliveryFrom), deliveryTo: day(inv.deliveryTo),
})

// Eerst versturen (dan krijgt de factuur zijn nummer en ligt hij vast), daarna de PDF maken en mailen.
function SendDialog({ title, missing, problem, number, issueDate, dueDate, email, canMail, onMail, mailHref, onSend, onSettings, onCustomer, onClose }: {
  title: string
  missing: string[]
  problem: string | null
  number?: string
  issueDate: string
  dueDate: string | null
  email?: string | null
  // Potel kan zelf mailen: dan mail je de factuur meteen met een link, zonder PDF als bijlage.
  canMail: boolean
  onMail: () => void
  mailHref: (inv: Invoice) => string
  onSend: () => Promise<Invoice | null>
  onSettings: () => void
  onCustomer: () => void
  onClose: () => void
}) {
  const [sent, setSent] = useState<Invoice | null>(null)
  const [busy, setBusy] = useState(false)
  const blocked = missing.length > 0 || problem !== null
  const mine = missing.filter(m => m.startsWith('je '))
  const theirs = missing.filter(m => m.includes('klant'))

  const send = async () => {
    setBusy(true)
    const inv = await onSend()
    setBusy(false)
    if (inv) setSent(inv)
  }

  if (sent && canMail) return (
    <Modal title={`${invoiceTitle(sent)} is verstuurd`} onClose={onClose} footer={<>
      <button className="btn" onClick={onClose}>Later</button>
      <button className="btn btn-primary" onClick={onMail}><Mail size={15} /> Per e-mail versturen</button>
    </>}>
      <p style={{ marginTop: 0 }}>
        Hij heeft nu een nummer en ligt vast. Mail hem meteen naar je klant{email ? ` (${email})` : ''}: die krijgt een persoonlijke link waarmee hij de factuur kan bekijken, printen en als PDF opslaan.
        Je ziet de mail eerst en kunt de tekst nog aanpassen.
      </p>
    </Modal>
  )

  if (sent) return (
    <Modal title={`${invoiceTitle(sent)} is verstuurd`} onClose={onClose} footer={<button className="btn btn-primary" onClick={onClose}>Klaar</button>}>
      <ol className="steps">
        <li>
          <div><strong>Sla hem op als PDF</strong><div className="muted">Kies in het afdrukvenster voor "Opslaan als PDF".</div></div>
          <button className="btn btn-sm" onClick={() => window.print()}><Printer size={14} /> PDF</button>
        </li>
        <li>
          <div><strong>Mail hem naar je klant</strong><div className="muted">{email ? `Er staat een mail klaar voor ${email}. Voeg de PDF als bijlage toe.` : 'Deze klant heeft nog geen e-mailadres.'}</div></div>
          <a className="btn btn-sm" href={mailHref(sent)}><Mail size={14} /> Mail openen</a>
        </li>
      </ol>
    </Modal>
  )

  return (
    <Modal title={title} onClose={onClose} footer={<>
      <button className="btn" onClick={onClose}>Annuleren</button>
      <button className="btn btn-primary" disabled={blocked || busy} onClick={send}><Send size={15} /> Versturen{number ? ` als ${number}` : ''}</button>
    </>}>
      {blocked ? (<>
        {missing.length > 0 && (<>
          <p style={{ marginTop: 0 }}>Een factuur moet aan de regels van de Belastingdienst voldoen. Dit ontbreekt nog:</p>
          <ul>{missing.map(m => <li key={m}>{m[0]!.toUpperCase()}{m.slice(1)}</li>)}</ul>
          <div className="row wrap" style={{ gap: 8 }}>
            {mine.length > 0 && <button className="btn btn-sm" onClick={onSettings}><Building size={14} /> Naar instellingen</button>}
            {theirs.length > 0 && <button className="btn btn-sm" onClick={onCustomer}>Klant bewerken</button>}
          </div>
        </>)}
        {problem && <p className="text-red">{problem}</p>}
      </>) : (
        <p style={{ marginTop: 0 }}>
          Bij versturen krijgt hij {number ? `nummer ${number}` : 'het volgende nummer'} en de factuurdatum van vandaag, {date(issueDate)}
          {dueDate ? `, met vervaldatum ${date(dueDate)}` : ''}. We leggen dan ook jouw gegevens en die van je klant vast.
          Daarna kun je hem niet meer aanpassen of verwijderen; een fout herstel je met een creditnota.
          {' '}{canMail ? 'Daarna kun je hem meteen mailen.' : 'Daarna maak je de PDF en mail je hem.'}
        </p>
      )}
    </Modal>
  )
}

function PaidDialog({ total, partly, onClose, onConfirm }: { total: number; partly: boolean; onClose: () => void; onConfirm: (paidAt: string) => void }) {
  const [paidAt, setPaidAt] = useState(toDateInput(new Date()))
  return (
    <Modal title={total < 0 ? 'Creditnota verrekend' : 'Betaling ontvangen'} onClose={onClose} footer={<>
      <button className="btn" onClick={onClose}>Annuleren</button>
      <button className="btn btn-success" onClick={() => onConfirm(paidAt)}><CheckCircle2 size={15} /> {euro(Math.abs(total))} {total < 0 ? 'verrekend' : 'ontvangen'}</button>
    </>}>
      {total > 0 && (
        <p className="muted" style={{ marginTop: 0, fontSize: 13 }}>
          {partly ? `Er is al een deel betaald. We leggen de rest van ${euro(total)} vast als betaling.` : `We leggen ${euro(total)} vast als betaling.`}
          {' '}Kreeg je maar een deel? Gebruik dan "Betaling toevoegen" bij de betalingen.
        </p>
      )}
      <Field label={total < 0 ? 'Verrekend op' : 'Betaald op'}><input type="date" value={paidAt} onChange={e => setPaidAt(e.target.value)} /></Field>
    </Modal>
  )
}
