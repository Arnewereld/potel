import { useEffect, useRef, useState } from 'react'
import { useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import {
  FileSignature, Plus, Trash2, Printer, Save, Building, ChevronUp, ChevronDown, Send, Copy, Mail, Lock, Link2, Link2Off, Ban, FolderKanban, Receipt,
  CheckCircle2, XCircle, Eye, Hourglass, History,
} from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useAuth } from '../lib/auth'
import { useTabs, useTabTitle } from '../lib/tabs'
import { useToast } from '../lib/toast'
import type { Customer, InvoiceLine, Language, Lead, MailServerStatus, Quote, QuoteLinks, QuoteMailPreview, Settings, VatRegime } from '../lib/types'
import { date, euro, invoiceTotals, lineAmount, num, toDateInput, utcDateTime } from '../lib/format'
import { quoteStatuses } from '../lib/status'
import { daysUntil, defaultRegime, regimeProblem, units, vatRegimes, zeroVat } from '../lib/invoice'
import { depositAmount, missingForQuote, quoteBuyer, quoteMail, quoteState, quoteTitle } from '../lib/quote'
import { Badge, ErrorBox, Field, Loading, Modal, PageHeader } from '../components/ui'
import { QuotePaper } from '../components/QuotePaper'
import { LanguageBadge, LanguageField } from '../components/DocLanguage'
import { RegimeSuggestion } from '../components/RegimeSuggestion'
import { usePrintTitle } from '../lib/printTitle'
import { docTitle } from '../lib/docTexts'

type Draft = Omit<Quote, 'id'> & { id?: number }

const newLine = (unit = 'uur', unitPrice = 0, vatRate = 21): InvoiceLine => ({ description: '', quantity: 1, unit, unitPrice, vatRate })
const emptySettings: Settings = { companyName: '…', defaultHourlyRate: 0, paymentTermDays: 14, weeklyHoursTarget: 0, yearlyHoursTarget: 0 }
const addDays = (d: string, n: number) => { const x = new Date(`${d}T00:00:00`); x.setDate(x.getDate() + n); return toDateInput(x) }
const termOf = (from: string, to: string) => Math.max(0, Math.round((new Date(`${to}T00:00:00`).getTime() - new Date(`${from}T00:00:00`).getTime()) / 86400000))
const leadRegimeError = 'Van een lead weten we het land en het btw-nummer nog niet. Kies normaal, KOR of vrijgesteld, of maak eerst een klant van de lead.'

export function QuoteEditorPage() {
  const { id } = useParams()
  const isNew = id === 'nieuw'
  const [params] = useSearchParams()
  const { pathname, search } = useLocation()
  const { data: customers } = useApi<Customer[]>('/customers')
  const { data: leads } = useApi<Lead[]>('/leads')
  const { data: settings } = useApi<Settings>('/settings')
  // Kan Potel zelf mailen? Dezelfde voorwaarden als bij facturen.
  const { data: mailStatus } = useApi<MailServerStatus>('/invoices/mail-status')
  const [draft, setDraft] = useState<Draft | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [dirty, setDirty] = useState(false)
  const savingRef = useRef(false)
  const [saving, setSaving] = useState(false)
  const [dialog, setDialog] = useState<'send' | 'mail' | 'project' | null>(null)
  const [sendDate] = useState(() => toDateInput(new Date()))
  const { data: next } = useApi<{ number: string }>(draft && draft.status === 'concept' && !draft.demo ? '/quotes/next-number' : null)
  const { data: links, reload: reloadLinks } = useApi<QuoteLinks>(!isNew && draft && draft.status !== 'concept' && !draft.demo ? `/quotes/${id}/links` : null)
  const { user } = useAuth()
  const admin = user.role === 'beheerder'
  const { retarget, close } = useTabs()
  const navigate = useNavigate()
  const toast = useToast()
  useTabTitle(isNew ? 'Nieuwe offerte' : draft ? quoteTitle(draft) : null)
  // De PDF heet zoals je klant hem leest: "Quote OFF-2026-004 from ..." bij een Engelse offerte.
  usePrintTitle(draft ? docTitle(draft.language, 'quote', draft.number, draft.seller?.name ?? settings?.companyName ?? '') : document.title)

  useEffect(() => {
    if (isNew) {
      const today = toDateInput(new Date())
      const customerId = Number(params.get('klant')) || null
      const leadId = customerId ? null : Number(params.get('lead')) || null
      Promise.all([api.get<Settings>('/settings'), api.get<Customer[]>('/customers'), api.get<Lead[]>('/leads')]).then(([s, list, leadList]) => {
        const chosen = list.find(c => c.id === customerId)
        const regime = defaultRegime(s, chosen)
        const lead = leadList.find(l => l.id === leadId)
        setDraft({
          number: null, customerId, leadId, version: 1, issueDate: today, validUntil: addDays(today, s.quoteValidDays ?? 30), status: 'concept',
          vatRegime: regime, language: chosen?.language ?? lead?.language ?? 'nl', subject: lead?.notes?.split('\n')[0]?.slice(0, 200) ?? '', intro: '', terms: '', depositPercent: null,
          lines: [newLine('uur', s.defaultHourlyRate, zeroVat(regime) ? 0 : 21)],
        })
      }).catch(e => setError(e.message))
    } else {
      api.get<Quote>(`/quotes/${id}`).then(q => setDraft(fromServer(q))).catch(e => setError(e.message))
    }
  }, [id, isNew, params])

  if (error) return <ErrorBox message={error} />
  if (!draft) return <Loading />

  const company = settings ?? emptySettings
  const customer = draft.customerId ? customers?.find(c => c.id === draft.customerId) ?? draft.customer : null
  const lead = !draft.customerId && draft.leadId ? leads?.find(l => l.id === draft.leadId) ?? draft.lead : null
  const buyer = quoteBuyer(draft, customer, lead)
  const totals = invoiceTotals(draft.lines)
  const state = quoteState(draft)
  const readOnly = draft.status !== 'concept'
  const demo = !!draft.demo
  const noVat = zeroVat(draft.vatRegime)
  const regimeInfo = vatRegimes.find(r => r.id === draft.vatRegime)
  const regimeError = customer ? regimeProblem(draft.vatRegime, customer)
    : lead && (draft.vatRegime === 'verlegd' || draft.vatRegime === 'buiten-eu') ? leadRegimeError : null
  const canMail = mailStatus?.canSend === true && readOnly && !demo && admin
  const deposit = depositAmount(draft)
  const st = quoteStatuses.find(s => s.id === state)!

  const update = (patch: Partial<Draft>) => { setDraft({ ...draft, ...patch }); setDirty(true) }
  const updateLine = (idx: number, patch: Partial<InvoiceLine>) => update({ lines: draft.lines.map((l, i) => (i === idx ? { ...l, ...patch } : l)) })
  const moveLine = (idx: number, dir: -1 | 1) => {
    const lines = [...draft.lines]
    ;[lines[idx], lines[idx + dir]] = [lines[idx + dir]!, lines[idx]!]
    update({ lines })
  }
  const withRegime = (regime: VatRegime): Partial<Draft> => ({
    vatRegime: regime,
    lines: zeroVat(regime) === noVat ? draft.lines : draft.lines.map(l => ({ ...l, vatRate: zeroVat(regime) ? 0 : 21 })),
  })
  // Een klant (k12) of een lead (l7). Bij een lead kan de btw niet verlegd of buiten de EU zijn.
  // De offerte neemt de taal van de klant of lead over.
  const choose = (value: string) => {
    const n = Number(value.slice(1))
    if (value.startsWith('k')) {
      const chosen = customers?.find(c => c.id === n)
      update({ customerId: n, leadId: null, ...withRegime(defaultRegime(settings, chosen)), language: chosen?.language ?? draft.language })
    } else if (value.startsWith('l')) {
      const chosen = leads?.find(l => l.id === n)
      update({
        customerId: null, leadId: n, language: chosen?.language ?? draft.language,
        ...(draft.vatRegime === 'verlegd' || draft.vatRegime === 'buiten-eu' ? withRegime(defaultRegime(settings, null)) : {}),
      })
    } else update({ customerId: null, leadId: null })
  }

  const save = async (quiet = false): Promise<number | null> => {
    if (savingRef.current) return null
    savingRef.current = true
    setSaving(true)
    const body = { ...draft, customer: undefined, lead: undefined }
    try {
      if (isNew) {
        const saved = await api.post<Quote>('/quotes', body)
        if (!quiet) toast('Conceptofferte aangemaakt')
        setDirty(false)
        retarget(pathname + search, `/offertes/${saved.id}`)
        return saved.id
      }
      const saved = await api.put<Quote>(`/quotes/${id}`, body)
      setDraft(fromServer(saved))
      if (!quiet) toast('Offerte opgeslagen')
      setDirty(false)
      return saved.id
    } catch (e) {
      toast((e as Error).message, 'error')
      return null
    } finally {
      savingRef.current = false
      setSaving(false)
    }
  }

  // Een actie op de server die de offerte teruggeeft, met een melding als het lukt.
  const act = async (path: string, done: string) => {
    try {
      const saved = await api.post<Quote>(`/quotes/${id}/${path}`)
      setDraft(fromServer(saved))
      reloadLinks(true)
      toast(done)
      return saved
    } catch (e) {
      toast((e as Error).message, 'error')
      return null
    }
  }

  const duplicate = async () => {
    try {
      const copy = await api.post<Quote>(`/quotes/${id}/duplicate`)
      toast(copy.version > 1 ? `Versie ${copy.version} aangemaakt. Pas hem aan en verstuur hem; dan vervangt hij deze offerte.` : 'Kopie aangemaakt als concept')
      navigate(`/offertes/${copy.id}`)
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  const remove = async () => {
    if (!confirm(`${demo ? 'Voorbeeldofferte' : 'Conceptofferte'} verwijderen?`)) return
    try {
      await api.del(`/quotes/${id}`)
      toast(demo ? 'Voorbeeldofferte verwijderd' : 'Concept verwijderd')
      close(pathname)
      navigate('/offertes')
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  const withdraw = async () => {
    if (!confirm('Deze offerte intrekken? Je klant kan hem daarna niet meer accepteren. De link blijft werken, zodat hij ziet dat de offerte is ingetrokken.')) return
    await act('withdraw', `${quoteTitle(draft)} is ingetrokken`)
  }

  const openSend = async () => {
    if (dirty && !(await save(true))) return
    setDialog('send')
  }

  const makeInvoice = async (depositOnly: boolean) => {
    try {
      const res = await api.post<{ id: number }>(`/quotes/${id}/invoice`, { deposit: depositOnly })
      toast(depositOnly ? 'Conceptfactuur voor de aanbetaling gemaakt. Kijk hem na en verstuur hem.' : 'Conceptfactuur gemaakt. Kijk hem na en verstuur hem.')
      api.get<Quote>(`/quotes/${id}`).then(q => setDraft(fromServer(q))).catch(() => undefined)
      navigate(`/facturen/${res.id}`)
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  const subtitle = dirty ? 'Niet opgeslagen wijzigingen'
    : state === 'concept' ? 'Concept, nog niet verstuurd'
    : state === 'verstuurd' ? `Wacht op je klant, nog ${daysUntil(draft.validUntil)} dagen geldig`
    : state === 'geaccepteerd' ? `Geaccepteerd${draft.decidedAt ? ` op ${date(draft.decidedAt)}` : ''}`
    : state === 'afgewezen' ? `Afgewezen${draft.decidedAt ? ` op ${date(draft.decidedAt)}` : ''}`
    : state === 'verlopen' ? `Verlopen na ${date(draft.validUntil)}`
    : 'Ingetrokken'

  return (
    <>
      <div className="no-print">
        <PageHeader
          icon={<FileSignature size={20} />}
          title={isNew ? 'Nieuwe offerte' : `${quoteTitle(draft)}${draft.version > 1 ? ` (versie ${draft.version})` : ''}`}
          subtitle={subtitle}
          actions={<>
            {!isNew && <Badge tone={st.tone}>{st.label}</Badge>}
            {!isNew && demo && <Badge tone="gray">Voorbeeld</Badge>}
            {!isNew && (!readOnly || demo) && <button className="btn btn-danger" title={demo ? 'Voorbeeldofferte verwijderen' : 'Concept verwijderen'} onClick={remove}><Trash2 size={15} /></button>}
            {!isNew && !draft.newerVersionId && (readOnly && !demo
              ? <button className="btn" onClick={duplicate}><History size={15} /> Nieuwe versie</button>
              : <button className="btn" title="Kopie maken als nieuw concept" onClick={duplicate}><Copy size={15} /></button>)}
            <button className="btn" onClick={() => window.print()}><Printer size={15} /> PDF</button>
            {!readOnly && <button className="btn" onClick={() => save()} disabled={saving || (!dirty && !isNew)}><Save size={15} /> Opslaan</button>}
            {!readOnly && !isNew && admin && <button className="btn btn-primary" disabled={saving} onClick={openSend}><Send size={15} /> Versturen</button>}
            {canMail && state === 'verstuurd' && <button className="btn" onClick={() => setDialog('mail')}><Mail size={15} /> Mailen</button>}
            {state === 'verstuurd' && admin && !demo && <button className="btn" onClick={withdraw}><Ban size={15} /> Intrekken</button>}
          </>}
        />
      </div>

      <div className="invoice-layout">
        <div className="no-print">
          {demo && (
            <div className="notice row between wrap" style={{ gap: 10 }}>
              <span>Dit is een voorbeeldofferte uit de welkomstwizard. Hij telt niet mee in je echte nummering en Potel mailt of deelt hem niet. Maak een kopie als je er een echte offerte van wilt maken.</span>
              <button className="btn btn-sm" onClick={remove}><Trash2 size={14} /> Verwijderen</button>
            </div>
          )}
          {readOnly && !demo && (
            <div className="notice row between wrap" style={{ gap: 10 }}>
              <span><Lock size={13} /> Deze offerte is verstuurd en ligt vast. Wil je iets veranderen? Maak dan een nieuwe versie; die vervangt deze offerte als je hem verstuurt.</span>
              {!draft.newerVersionId && <button className="btn btn-sm" onClick={duplicate}><History size={14} /> Nieuwe versie</button>}
            </div>
          )}
          {draft.newerVersionId && (
            <div className="notice">
              Er is een nieuwere versie van deze offerte. <a onClick={() => navigate(`/offertes/${draft.newerVersionId}`)} style={{ cursor: 'pointer' }}>Open de nieuwste versie</a>.
            </div>
          )}
          {!readOnly && draft.previousVersionId && (
            <div className="notice">
              Dit is versie {draft.version} van offerte{' '}
              <a onClick={() => navigate(`/offertes/${draft.previousVersionId}`)} style={{ cursor: 'pointer' }}>{draft.previousNumber ?? 'de vorige versie'}</a>.
              {' '}Verstuur je hem, dan krijgt hij een eigen nummer en trekken we de vorige versie in, zodat je klant niet per ongeluk de oude accepteert.
            </div>
          )}

          {!isNew && readOnly && <DecisionCard quote={draft} state={state} onProject={() => setDialog('project')} onInvoice={makeInvoice} onOpen={navigate} onNewVersion={duplicate} />}

          <div className="card invoice-form">
            <fieldset className="card-body plain-fieldset" disabled={readOnly}>
              <div className="form-grid">
                <Field label="Voor *">
                  <select value={draft.customerId ? `k${draft.customerId}` : draft.leadId ? `l${draft.leadId}` : ''} onChange={e => choose(e.target.value)}>
                    <option value="">Kies een klant of lead…</option>
                    <optgroup label="Klanten">
                      {customers?.map(c => <option key={c.id} value={`k${c.id}`}>{c.company || c.name}{c.company ? ` (${c.name})` : ''}</option>)}
                    </optgroup>
                    <optgroup label="Leads">
                      {leads?.filter(l => !l.customerId || l.id === draft.leadId).map(l => <option key={l.id} value={`l${l.id}`}>{l.company || l.name}{l.company ? ` (${l.name})` : ''}</option>)}
                    </optgroup>
                  </select>
                </Field>
                <Field label="Offertenummer">
                  <input disabled value={draft.number ?? (demo ? 'Voorbeeld, krijgt geen nummer' : next ? `Krijgt bij versturen nummer ${next.number}` : 'Krijgt een nummer bij versturen')} />
                </Field>
                <Field label={readOnly ? 'Offertedatum' : 'Offertedatum (wordt de dag van versturen)'}>
                  <input type="date" value={draft.issueDate} onChange={e => update({ issueDate: e.target.value })} />
                </Field>
                <Field label="Geldig tot en met">
                  <div className="input-with-chips">
                    <input type="date" value={draft.validUntil} min={draft.issueDate} onChange={e => update({ validUntil: e.target.value })} />
                    {!readOnly && [14, 30, 60].map(n => (
                      <button key={n} type="button" className={`chip ${draft.validUntil === addDays(draft.issueDate, n) ? 'active' : ''}`} onClick={() => update({ validUntil: addDays(draft.issueDate, n) })}>{n} d</button>
                    ))}
                  </div>
                </Field>
                <Field label="Onderwerp"><input value={draft.subject ?? ''} maxLength={200} placeholder="Bijv. Nieuwe webshop in Next.js" onChange={e => update({ subject: e.target.value })} /></Field>
                <Field label="Aanbetaling bij akkoord (%)">
                  <input type="number" min={0} max={100} step={5} value={draft.depositPercent ?? ''} placeholder="Geen aanbetaling"
                    onChange={e => update({ depositPercent: e.target.value === '' ? null : Number(e.target.value) })} />
                </Field>
                <Field label="Btw">
                  <select value={draft.vatRegime} onChange={e => update(withRegime(e.target.value as VatRegime))}>
                    {vatRegimes.map(r => <option key={r.id} value={r.id} disabled={!!lead && (r.id === 'verlegd' || r.id === 'buiten-eu')}>{r.label}</option>)}
                  </select>
                </Field>
                {lead && !readOnly && (
                  <p className="muted" style={{ fontSize: 13, margin: 0, alignSelf: 'end' }}>
                    Accepteert de lead de offerte, dan wordt hij vanzelf klant. Daarna kun je er een project of factuur van maken.
                  </p>
                )}
                {(regimeError || draft.vatRegime !== 'normaal') && (
                  <p className={regimeError ? 'text-red' : 'muted'} style={{ fontSize: 13, margin: 0, gridColumn: '1 / -1' }}>{regimeError ?? regimeInfo?.help}</p>
                )}
                {!readOnly && (
                  <RegimeSuggestion kind="offerte" regime={draft.vatRegime} customer={customer} onAccept={regime => update(withRegime(regime))} />
                )}
                <LanguageField kind="offerte" value={draft.language} onChange={(language: Language) => update({ language })} />
                {draft.language === 'en' && (
                  <p className="muted" style={{ fontSize: 13, margin: 0, alignSelf: 'end' }}>
                    Je klant krijgt de offerte, de mail en de pagina achter de link in het Engels. Je eigen teksten vertaalt Potel niet.
                  </p>
                )}
                <Field label="Inleiding" full>
                  <textarea rows={3} maxLength={5000} value={draft.intro ?? ''} placeholder="Bijv. Naar aanleiding van ons gesprek stuur ik je graag dit voorstel." onChange={e => update({ intro: e.target.value })} />
                </Field>
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
                      {!readOnly && <>
                        <button className="icon-btn tiny" disabled={i === 0} onClick={() => moveLine(i, -1)} aria-label="Omhoog"><ChevronUp size={14} /></button>
                        <button className="icon-btn tiny" disabled={i === draft.lines.length - 1} onClick={() => moveLine(i, 1)} aria-label="Omlaag"><ChevronDown size={14} /></button>
                        <button className="icon-btn tiny danger" disabled={draft.lines.length === 1} onClick={() => update({ lines: draft.lines.filter((_, x) => x !== i) })} aria-label="Regel verwijderen"><Trash2 size={14} /></button>
                      </>}
                    </span>
                  </div>
                ))}
              </div>
              <div className="row mt between wrap">
                {readOnly ? <span /> : (
                  <button className="btn btn-sm" onClick={() => update({ lines: [...draft.lines, newLine(draft.lines.at(-1)?.unit, draft.lines.at(-1)?.unitPrice, noVat ? 0 : 21)] })}><Plus size={14} /> Regel toevoegen</button>
                )}
                <div className="line-totals">
                  <span>Subtotaal <strong>{euro(totals.subtotal)}</strong></span>
                  {!noVat && <span>Btw <strong>{euro(totals.vat)}</strong></span>}
                  <span>Totaal <strong>{euro(totals.total)}</strong></span>
                </div>
              </div>
              {deposit && <p className="muted" style={{ fontSize: 13 }}>Aanbetaling bij akkoord: {euro(deposit.total)}{noVat ? '' : ' inclusief btw'}.</p>}

              <Field label="Voorwaarden" full>
                <textarea rows={4} maxLength={5000} value={draft.terms ?? ''} placeholder="Bijv. hoe je meer- en minderwerk afrekent, de betaaltermijn en wie eigenaar is van de code." onChange={e => update({ terms: e.target.value })} />
              </Field>
              {!readOnly && !admin && <p className="muted" style={{ fontSize: 13, marginBottom: 0 }}>Alleen een beheerder kan de offerte versturen. Sla hem op, dan staat hij klaar voor een beheerder.</p>}
              {!readOnly && !settings?.kvk && (
                <p className="muted" style={{ fontSize: 13, marginBottom: 0 }}>
                  Tip: vul je adres en KvK-nummer in bij <a onClick={() => navigate('/instellingen')} style={{ cursor: 'pointer' }}><Building size={12} /> Instellingen</a>, dan staan ze op elke offerte.
                </p>
              )}
            </fieldset>
          </div>

          {readOnly && !demo && links && (
            <ShareCard quote={draft} links={links} admin={admin} canMail={canMail && state === 'verstuurd'} company={company} customer={customer} lead={lead}
              onMail={() => setDialog('mail')} onChange={() => reloadLinks(true)} />
          )}
        </div>

        <div className="invoice-papers">
          <LanguageBadge lang={draft.language} kind="offerte" />
          <QuotePaper quote={draft} buyer={buyer} state={state} company={company} />
        </div>
      </div>

      {dialog === 'send' && (
        <SendDialog
          quote={draft}
          missing={missingForQuote(draft, buyer, company)}
          problem={regimeError}
          number={next?.number}
          issueDate={sendDate}
          validUntil={addDays(sendDate, termOf(draft.issueDate, draft.validUntil))}
          email={customer?.email ?? lead?.email}
          canMail={mailStatus?.canSend === true}
          onSend={() => act('send', 'De offerte is verstuurd en ligt nu vast')}
          onMail={() => setDialog('mail')}
          onSettings={() => navigate('/instellingen')}
          onClose={() => setDialog(null)}
        />
      )}
      {dialog === 'mail' && <MailDialog quoteId={Number(id)} onClose={() => setDialog(null)} onSent={() => { reloadLinks(true); api.get<Quote>(`/quotes/${id}`).then(q => setDraft(fromServer(q))).catch(() => undefined) }} />}
      {dialog === 'project' && (
        <ProjectDialog quote={draft} subtotal={totals.subtotal} onClose={() => setDialog(null)} onDone={q => { setDraft(fromServer(q)); setDialog(null) }} />
      )}
    </>
  )
}

const fromServer = (q: Quote): Draft => ({ ...q, issueDate: q.issueDate.slice(0, 10), validUntil: q.validUntil.slice(0, 10) })

// Wat de klant besloot en wat je daarna doet. Een project of factuur maak je zelf; Potel doet dat nooit vanzelf.
function DecisionCard({ quote, state, onProject, onInvoice, onOpen, onNewVersion }: {
  quote: Draft
  state: string
  onProject: () => void
  onInvoice: (deposit: boolean) => void
  onOpen: (path: string) => void
  onNewVersion: () => void
}) {
  const days = daysUntil(quote.validUntil)
  if (state === 'verstuurd') return (
    <div className="card quote-decision">
      <div className="card-body">
        <h4 className="section-title" style={{ margin: 0 }}><Hourglass size={15} /> Wacht op je klant</h4>
        <p className="muted" style={{ margin: '6px 0 0', fontSize: 13 }}>
          Je klant kan deze offerte tot en met {date(quote.validUntil)} accepteren of afwijzen via de link{days <= 7 ? `, nog ${days === 0 ? 'vandaag' : `${days} dagen`}` : ''}.
          {quote.demo ? '' : ' Je krijgt bericht zodra hij besluit.'}
        </p>
      </div>
    </div>
  )
  if (state === 'geaccepteerd') {
    const noCustomer = !quote.customerId
    return (
      <div className="card quote-decision quote-decision-ok">
        <div className="card-body">
          <h4 className="section-title" style={{ margin: 0 }}><CheckCircle2 size={15} /> Geaccepteerd</h4>
          <p style={{ margin: '6px 0 0', fontSize: 14 }}>
            {quote.decidedName ? <>Akkoord gegeven door <strong>{quote.decidedName}</strong></> : 'Akkoord gegeven'}
            {quote.decidedAt ? ` op ${utcDateTime(quote.decidedAt)}` : ''}{quote.decidedIp ? `, vanaf IP-adres ${quote.decidedIp}` : ''}.
          </p>
          <p className="muted" style={{ margin: '6px 0 10px', fontSize: 13 }}>
            {noCustomer ? 'Deze offerte hoort nog niet bij een klant.' : 'Maak er nu een project of factuur van. Dat doet Potel niet vanzelf, zodat jij bepaalt wanneer.'}
          </p>
          {!noCustomer && (
            <div className="row wrap" style={{ gap: 8 }}>
              {quote.projectId
                ? <button className="btn btn-sm" onClick={() => onOpen(`/projecten/${quote.projectId}`)}><FolderKanban size={14} /> Project bekijken</button>
                : <button className="btn btn-sm btn-primary" onClick={onProject}><FolderKanban size={14} /> Maak project</button>}
              {quote.depositPercent ? (quote.depositInvoiceId
                ? <button className="btn btn-sm" onClick={() => onOpen(`/facturen/${quote.depositInvoiceId}`)}><Receipt size={14} /> Factuur aanbetaling bekijken</button>
                : <button className="btn btn-sm" onClick={() => onInvoice(true)}><Receipt size={14} /> Maak factuur voor aanbetaling ({num(quote.depositPercent)}%)</button>) : null}
              {quote.invoiceId
                ? <button className="btn btn-sm" onClick={() => onOpen(`/facturen/${quote.invoiceId}`)}><Receipt size={14} /> Factuur bekijken</button>
                : <button className="btn btn-sm" onClick={() => onInvoice(false)}><Receipt size={14} /> Maak factuur{quote.depositInvoiceId ? ' voor het restant' : ''}</button>}
            </div>
          )}
        </div>
      </div>
    )
  }
  return (
    <div className={`card quote-decision ${state === 'afgewezen' ? 'quote-decision-no' : ''}`}>
      <div className="card-body">
        <h4 className="section-title" style={{ margin: 0 }}>
          {state === 'afgewezen' ? <><XCircle size={15} /> Afgewezen</> : state === 'verlopen' ? <><Hourglass size={15} /> Verlopen</> : <><Ban size={15} /> Ingetrokken</>}
        </h4>
        <p style={{ margin: '6px 0 0', fontSize: 14 }}>
          {state === 'afgewezen' && <>Je klant heeft deze offerte afgewezen{quote.decidedAt ? ` op ${utcDateTime(quote.decidedAt)}` : ''}.</>}
          {state === 'verlopen' && <>Deze offerte was geldig tot en met {date(quote.validUntil)}. Je klant kan hem niet meer accepteren.</>}
          {state === 'ingetrokken' && <>Deze offerte is ingetrokken{quote.withdrawnAt ? ` op ${utcDateTime(quote.withdrawnAt)}` : ''}. Je klant kan hem niet meer accepteren.</>}
        </p>
        {state === 'afgewezen' && quote.rejectReason && <blockquote className="quote-reason">{quote.rejectReason}</blockquote>}
        {!quote.newerVersionId && !quote.demo && (
          <button className="btn btn-sm mt" onClick={onNewVersion}><History size={14} /> Nieuwe versie maken</button>
        )}
      </div>
    </div>
  )
}

// Delen met je klant: mailen vanuit Potel, of zelf een link maken en die kopiëren. Het token zie je alleen bij het maken.
function ShareCard({ quote, links, admin, canMail, company, customer, lead, onMail, onChange }: {
  quote: Draft
  links: QuoteLinks
  admin: boolean
  canMail: boolean
  company: Settings
  customer?: Customer | null
  lead?: Lead | null
  onMail: () => void
  onChange: () => void
}) {
  const [created, setCreated] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  const toast = useToast()
  const contact = customer ?? lead

  const makeLink = async () => {
    setBusy(true)
    try {
      const res = await api.post<{ path: string }>(`/quotes/${quote.id}/link`)
      setCreated(`${window.location.origin}${res.path}`)
      toast('Link gemaakt. Kopieer hem nu en stuur hem naar je klant.')
      onChange()
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const revoke = async () => {
    if (!confirm('Alle links naar deze offerte intrekken? Wie de link uit een eerdere mail opent, ziet dan dat hij niet meer werkt.')) return
    try {
      await api.post(`/quotes/${quote.id}/links/revoke`)
      setCreated(null)
      toast('De links naar deze offerte zijn ingetrokken')
      onChange()
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  const copy = async () => {
    if (!created) return
    try {
      await navigator.clipboard.writeText(created)
      toast('Link gekopieerd')
    } catch {
      input.current?.select()
      toast('Kopiëren lukte niet vanzelf. De link is geselecteerd; kopieer hem met Ctrl+C.', 'error')
    }
  }

  return (
    <div className="card" style={{ marginTop: 16 }}>
      <div className="card-head"><h3><Link2 size={16} /> Delen met je klant</h3></div>
      <div className="card-body">
        <p className="muted" style={{ marginTop: 0, fontSize: 13 }}>
          Via een persoonlijke link bekijkt je klant de offerte zonder in te loggen, en accepteert of wijst hij hem af.
          {links.mailedAt ? ` Gemaild op ${utcDateTime(links.mailedAt)}${links.mailedTo ? ` aan ${links.mailedTo}` : ''}.` : ''}
        </p>
        <p style={{ fontSize: 13, margin: '0 0 10px' }}>
          {links.active === 0 ? 'Er is geen werkende link.' : links.active === 1 ? 'Er is 1 link actief.' : `Er zijn ${links.active} links actief.`}
          {links.lastOpenedAt ? ` Laatst geopend op ${utcDateTime(links.lastOpenedAt)} (${links.opens === 1 ? '1 keer' : `${links.opens} keer`}).` : links.active > 0 ? ' Nog niet geopend.' : ''}
        </p>
        {created && (
          <div className="portal-created">
            <Field label="De nieuwe link" full><input ref={input} readOnly value={created} onFocus={e => e.target.select()} /></Field>
            <div className="row wrap" style={{ gap: 8 }}>
              <button type="button" className="btn btn-primary btn-sm" onClick={copy}><Copy size={14} /> Kopiëren</button>
              <a className="btn btn-sm" href={quoteMail({ ...quote, id: quote.id ?? 0 }, created, contact?.email, contact?.name.split(' ')[0], company)}><Mail size={14} /> Mail openen</a>
              <a className="btn btn-sm" href={created} target="_blank" rel="noreferrer"><Eye size={14} /> Bekijken als klant</a>
            </div>
            <p className="cell-sub" style={{ marginBottom: 0 }}>Kopieer de link nu. We bewaren hem niet, dus later kun je hem niet meer opvragen. Ben je hem kwijt? Maak dan een nieuwe link.</p>
          </div>
        )}
        <div className="row wrap mt" style={{ gap: 8 }}>
          {admin && canMail && <button className="btn btn-sm btn-primary" onClick={onMail}><Mail size={14} /> Mailen vanuit Potel</button>}
          {admin && <button className="btn btn-sm" disabled={busy} onClick={makeLink}><Link2 size={14} /> Link maken</button>}
          {links.active > 0 && <button className="btn btn-sm" onClick={revoke}><Link2Off size={14} /> Links intrekken</button>}
        </div>
        {!admin && <p className="muted" style={{ fontSize: 13, margin: '10px 0 0' }}>Alleen een beheerder maakt links en mailt offertes.</p>}
      </div>
    </div>
  )
}

// Eerst versturen (dan krijgt de offerte een nummer en ligt hij vast), daarna mailen of zelf een link delen.
function SendDialog({ quote, missing, problem, number, issueDate, validUntil, email, canMail, onSend, onMail, onSettings, onClose }: {
  quote: Draft
  missing: string[]
  problem: string | null
  number?: string
  issueDate: string
  validUntil: string
  email?: string | null
  canMail: boolean
  onSend: () => Promise<Quote | null>
  onMail: () => void
  onSettings: () => void
  onClose: () => void
}) {
  const [sent, setSent] = useState<Quote | null>(null)
  const [busy, setBusy] = useState(false)
  const blocked = missing.length > 0 || problem !== null
  const mine = missing.filter(m => m.startsWith('je '))

  const send = async () => {
    setBusy(true)
    const q = await onSend()
    setBusy(false)
    if (q) setSent(q)
  }

  if (sent) return (
    <Modal title={`${quoteTitle(sent)} is verstuurd`} onClose={onClose} footer={<>
      <button className="btn" onClick={onClose}>{canMail ? 'Later' : 'Klaar'}</button>
      {canMail && <button className="btn btn-primary" onClick={onMail}><Mail size={15} /> Per e-mail versturen</button>}
    </>}>
      <p style={{ marginTop: 0 }}>
        Hij heeft nu een nummer en ligt vast.{' '}
        {canMail
          ? `Mail hem meteen naar je klant${email ? ` (${email})` : ''}: die krijgt een persoonlijke link waarmee hij de offerte bekijkt en accepteert. Je ziet de mail eerst en kunt de tekst nog aanpassen.`
          : 'Maak hieronder bij "Delen met je klant" een persoonlijke link en stuur die naar je klant. Daar bekijkt hij de offerte en accepteert hij hem. Een PDF maak je met de knop PDF.'}
      </p>
    </Modal>
  )

  return (
    <Modal title="Offerte versturen" onClose={onClose} footer={<>
      <button className="btn" onClick={onClose}>Annuleren</button>
      <button className="btn btn-primary" disabled={blocked || busy || !!quote.demo} onClick={send}><Send size={15} /> Versturen{number ? ` als ${number}` : ''}</button>
    </>}>
      {quote.demo ? <p style={{ marginTop: 0 }}>Dit is een voorbeeldofferte. Maak er een kopie van als je hem echt wilt versturen.</p>
        : blocked ? (<>
          {missing.length > 0 && (<>
            <p style={{ marginTop: 0 }}>Dit ontbreekt nog op de offerte:</p>
            <ul>{missing.map(m => <li key={m}>{m[0]!.toUpperCase()}{m.slice(1)}</li>)}</ul>
            {mine.length > 0 && <button className="btn btn-sm" onClick={onSettings}><Building size={14} /> Naar instellingen</button>}
          </>)}
          {problem && <p className="text-red">{problem}</p>}
        </>) : (
          <p style={{ marginTop: 0 }}>
            Bij versturen krijgt hij {number ? `nummer ${number}` : 'het volgende nummer'} en de offertedatum van vandaag, {date(issueDate)}. Hij is dan geldig tot en met {date(validUntil)}.
            We leggen jouw gegevens en die van je klant vast. Daarna kun je hem niet meer aanpassen; wil je iets veranderen, dan maak je een nieuwe versie.
          </p>
        )}
    </Modal>
  )
}

// De mail zoals hij weggaat, met {link} op de plek van de persoonlijke link. Hij gaat altijd naar het adres van de klant of lead.
function MailDialog({ quoteId, onClose, onSent }: { quoteId: number; onClose: () => void; onSent: () => void }) {
  const { data: preview, error } = useApi<QuoteMailPreview>(`/quotes/${quoteId}/mail-preview`)
  const [edit, setEdit] = useState<{ subject: string; body: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const toast = useToast()
  const text = edit ?? (preview ? { subject: preview.subject, body: preview.body } : null)

  const send = async () => {
    if (!text) return
    setBusy(true)
    try {
      const res = await api.post<{ mailedTo: string }>(`/quotes/${quoteId}/mail`, text)
      toast(`De offerte is gemaild naar ${res.mailedTo}`)
      onSent()
      onClose()
    } catch (e) {
      toast((e as Error).message, 'error')
      setBusy(false)
    }
  }

  return (
    <Modal wide title="Offerte mailen" onClose={onClose} footer={<>
      <button className="btn" onClick={onClose}>Annuleren</button>
      <button className="btn btn-primary" disabled={busy || !preview || !!preview.problem || !text?.subject.trim() || !text.body.trim()} onClick={send}>
        <Send size={15} /> {busy ? 'Bezig met versturen…' : 'Versturen'}
      </button>
    </>}>
      {error ? <p className="text-red">{error}</p> : !preview || !text ? <Loading /> : (<>
        {preview.problem && <div className="login-error" style={{ marginTop: 0, marginBottom: 14 }}>{preview.problem}</div>}
        <div className="form-grid">
          <Field label="Aan" full><input value={preview.to ?? 'Geen e-mailadres bekend'} disabled /></Field>
          <Field label="Onderwerp" full><input value={text.subject} maxLength={200} onChange={e => setEdit({ ...text, subject: e.target.value })} /></Field>
          <Field label="Bericht" full><textarea rows={14} value={text.body} maxLength={5000} onChange={e => setEdit({ ...text, body: e.target.value })} /></Field>
        </div>
        <p className="muted" style={{ fontSize: 13, margin: '12px 0 0' }}>
          Op de plek van {'{link}'} komt bij versturen een persoonlijke link naar de offerte. Daar bekijkt je klant hem zonder in te loggen en accepteert of wijst hij hem af.
        </p>
      </>)}
    </Modal>
  )
}

// Een project uit een geaccepteerde offerte: vaste prijs (het bedrag zonder btw) of per uur (zoveel uur als in het bedrag past).
function ProjectDialog({ quote, subtotal, onClose, onDone }: { quote: Draft; subtotal: number; onClose: () => void; onDone: (q: Quote) => void }) {
  const [billing, setBilling] = useState<'vast' | 'uur'>('vast')
  const [name, setName] = useState(quote.subject || `Project uit offerte ${quote.number}`)
  const [busy, setBusy] = useState(false)
  const toast = useToast()
  const rate = quote.lines.find(l => l.unit === 'uur' && l.unitPrice > 0)?.unitPrice
  const hourCount = quote.lines.filter(l => l.unit === 'uur').reduce((a, l) => a + l.quantity, 0)

  const create = async () => {
    setBusy(true)
    try {
      await api.post(`/quotes/${quote.id}/project`, { billing, name })
      toast(`Project ${name} gemaakt`)
      onDone(await api.get<Quote>(`/quotes/${quote.id}`))
    } catch (e) {
      toast((e as Error).message, 'error')
      setBusy(false)
    }
  }

  return (
    <Modal title="Project maken" onClose={onClose} footer={<>
      <button className="btn" onClick={onClose}>Annuleren</button>
      <button className="btn btn-primary" disabled={busy || !name.trim()} onClick={create}><FolderKanban size={15} /> Project maken</button>
    </>}>
      <div className="form-grid">
        <Field label="Naam" full><input value={name} maxLength={200} onChange={e => setName(e.target.value)} /></Field>
      </div>
      <div className="row wrap" style={{ gap: 6, margin: '12px 0' }}>
        <button type="button" className={`chip ${billing === 'vast' ? 'active' : ''}`} onClick={() => setBilling('vast')}>Vaste prijs</button>
        <button type="button" className={`chip ${billing === 'uur' ? 'active' : ''}`} onClick={() => setBilling('uur')}>Per uur</button>
      </div>
      <p className="muted" style={{ margin: 0, fontSize: 13 }}>
        {billing === 'vast'
          ? `Vaste prijs van ${euro(subtotal)} zonder btw${hourCount > 0 ? `, met ${num(hourCount)} uur als budget` : ''}.`
          : rate ? `Per uur tegen ${euro(rate)}, met een budget van ${num(Math.round(subtotal / rate * 10) / 10)} uur (${euro(subtotal)} zonder btw).`
          : `Per uur tegen je standaardtarief, met als budget zoveel uur als in ${euro(subtotal)} past.`}
      </p>
    </Modal>
  )
}
