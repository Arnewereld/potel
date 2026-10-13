import { useCallback, useEffect, useRef, useState } from 'react'
import { Ban, CheckCircle2, Clock, FileWarning, Hourglass, Printer, ThumbsDown, ThumbsUp, XCircle } from 'lucide-react'
import { api } from '../../lib/api'
import { date, euro, num, utcDateTime } from '../../lib/format'
import { product } from '../../lib/plans'
import { depositAmount } from '../../lib/quote'
import type { PublicQuote, Settings } from '../../lib/types'
import { QuotePaper } from '../../components/QuotePaper'
import { Field } from '../../components/ui'
import { usePrivatePage } from '../../lib/privatePage'

// De offerte achter de link. Werkt zonder inloggen en laat alleen deze ene offerte zien. Onderaan geeft je klant akkoord
// (met zijn naam en een vinkje) of wijst hij de offerte af; dat kan één keer. Het token staat achter de #, zodat het niet
// bij de server of in logboeken belandt.
export function PublicQuotePage() {
  usePrivatePage()
  const [token] = useState(() => new URLSearchParams(window.location.hash.slice(1)).get('token') ?? '')
  const [data, setData] = useState<PublicQuote | null>(null)
  const [error, setError] = useState<string | null>(token ? null : 'Deze link is niet compleet. Kopieer de hele link uit de mail, of vraag de afzender om een nieuwe link.')
  const [mode, setMode] = useState<'accept' | 'reject' | null>(null)
  const [name, setName] = useState('')
  const [agree, setAgree] = useState(false)
  const [reason, setReason] = useState('')
  const [busy, setBusy] = useState(false)
  const [formError, setFormError] = useState<string | null>(null)
  const [justDecided, setJustDecided] = useState(false)
  const started = useRef(false)
  const decide = useRef<HTMLDivElement>(null)

  const load = useCallback(() => api.post<PublicQuote>('/public/quote', { token })
    .then(d => {
      setData(d)
      const seller = d.quote.seller?.name
      document.title = `Offerte ${d.quote.number ?? ''}${seller ? ` van ${seller}` : ''}`
    })
    .catch(e => setError((e as Error).message)), [token])

  useEffect(() => {
    if (!token || started.current) return
    started.current = true
    load()
  }, [token, load])

  // Een andere offertelink in hetzelfde tabblad verandert alleen wat achter de # staat; laad dan die offerte.
  useEffect(() => {
    const reload = () => window.location.reload()
    window.addEventListener('hashchange', reload)
    return () => window.removeEventListener('hashchange', reload)
  }, [])

  if (error) return (
    <div className="login login-single">
      <div className="login-card">
        <h1><FileWarning size={22} /> Offerte niet gevonden</h1>
        <p className="muted" style={{ margin: '6px 0 0' }}>{error}</p>
      </div>
    </div>
  )
  if (!data) return <div className="public-invoice"><div className="public-invoice-inner muted">Offerte laden…</div></div>

  const q = data.quote
  const seller = q.seller?.name ?? 'de afzender'
  const company: Settings = {
    companyName: q.seller?.name ?? '', defaultHourlyRate: 0, paymentTermDays: 0, weeklyHoursTarget: 0, yearlyHoursTarget: 0,
    logoDataUrl: data.brand.logoDataUrl, brandColor: data.brand.brandColor,
  }
  const deposit = depositAmount(q)

  const status = q.state === 'geaccepteerd'
    ? { tone: 'ok', icon: <CheckCircle2 size={16} />, text: `${justDecided ? 'Dank je wel! ' : ''}Deze offerte is geaccepteerd${q.decidedName ? ` door ${q.decidedName}` : ''}${q.decidedAt ? ` op ${utcDateTime(q.decidedAt)}` : ''}.${justDecided ? ` ${seller} neemt contact met je op over de volgende stap.` : ''}` }
    : q.state === 'afgewezen'
      ? { tone: '', icon: <XCircle size={16} />, text: `${justDecided ? 'Dank je wel voor je reactie. ' : ''}Deze offerte is afgewezen${q.decidedAt ? ` op ${utcDateTime(q.decidedAt)}` : ''}.${justDecided ? ` ${seller} heeft je reactie ontvangen.` : ''}` }
    : q.state === 'ingetrokken'
      ? { tone: '', icon: <Ban size={16} />, text: `${data.problem ?? 'Deze offerte is ingetrokken.'}${q.newerNumber ? ` Er is een nieuwere versie (${q.newerNumber}); die krijg je via een eigen link.` : ''}` }
    : q.state === 'verlopen'
      ? { tone: '', icon: <Hourglass size={16} />, text: data.problem ?? `Deze offerte was geldig tot en met ${date(q.validUntil)}.` }
      : { tone: '', icon: <Clock size={16} />, text: `Deze offerte is geldig tot en met ${date(q.validUntil)}. Lees hem rustig door; onderaan geef je akkoord of wijs je hem af.` }

  const send = async (accept: boolean) => {
    setBusy(true)
    setFormError(null)
    try {
      const res = accept
        ? await api.post<{ state: 'geaccepteerd'; decidedAt: string; decidedName: string }>('/public/quote/accept', { token, name, agree })
        : await api.post<{ state: 'afgewezen'; decidedAt: string }>('/public/quote/reject', { token, reason })
      setData({
        ...data, canDecide: false, problem: null,
        quote: { ...q, state: res.state, decidedAt: res.decidedAt, decidedName: accept ? name.trim() : null, rejectReason: accept ? null : reason.trim() || null },
      })
      setJustDecided(true)
      setMode(null)
      window.scrollTo({ top: 0, behavior: 'smooth' })
    } catch (e) {
      setFormError((e as Error).message)
      // Is er intussen al besloten of is de offerte ingetrokken, dan laten we zien hoe hij er nu voor staat.
      load()
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="public-invoice public-quote">
      <div className="public-invoice-inner">
        <div className="public-invoice-bar no-print">
          <div className={`notice ${status.tone === 'ok' ? 'notice-ok' : ''}`} style={{ margin: 0, flex: 1, minWidth: 220 }}>{status.icon} {status.text}</div>
          {data.canDecide && (
            <button className="btn btn-primary" onClick={() => decide.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })}><ThumbsUp size={15} /> Accepteren of afwijzen</button>
          )}
          <button className="btn" onClick={() => window.print()}><Printer size={15} /> Opslaan als PDF of printen</button>
        </div>

        <QuotePaper quote={q} buyer={q.buyer ?? null} state={q.state} company={company} />

        {data.canDecide && (
          <div className="card quote-decide no-print" ref={decide}>
            <div className="card-body">
              <h3 style={{ marginTop: 0 }}>Wat vind je van deze offerte?</h3>
              <p className="muted" style={{ marginTop: 0 }}>
                Je kunt één keer besluiten, tot en met {date(q.validUntil)}. Heb je eerst nog vragen? Neem dan contact op met {seller}
                {q.seller?.email ? ` via ${q.seller.email}` : ''}.
              </p>
              <div className="quote-decide-choices" role="radiogroup" aria-label="Je besluit">
                <button type="button" role="radio" aria-checked={mode === 'accept'} className={`quote-choice ${mode === 'accept' ? 'active ok' : ''}`} onClick={() => { setMode('accept'); setFormError(null) }}>
                  <ThumbsUp size={18} /> Akkoord geven
                </button>
                <button type="button" role="radio" aria-checked={mode === 'reject'} className={`quote-choice ${mode === 'reject' ? 'active no' : ''}`} onClick={() => { setMode('reject'); setFormError(null) }}>
                  <ThumbsDown size={18} /> Afwijzen
                </button>
              </div>

              {mode === 'accept' && (
                <form className="quote-decide-form" onSubmit={e => { e.preventDefault(); send(true) }}>
                  <Field label="Je volledige naam" full>
                    <input value={name} maxLength={100} autoComplete="name" placeholder="Voor- en achternaam" onChange={e => setName(e.target.value)} autoFocus />
                  </Field>
                  <label className="check-row check-row-top">
                    <input type="checkbox" checked={agree} onChange={e => setAgree(e.target.checked)} />
                    <span>
                      Ik ga akkoord met offerte {q.number} van {seller}, voor {euro(q.totals?.total ?? 0)}{q.vatRegime === 'normaal' ? ' inclusief btw' : ''}
                      {deposit ? `, met een aanbetaling van ${num(q.depositPercent!)}% (${euro(deposit.total)})` : ''}{q.terms ? ', en met de voorwaarden die erin staan' : ''}.
                    </span>
                  </label>
                  <p className="cell-sub" style={{ margin: 0 }}>We bewaren je naam, het tijdstip en je IP-adres, zodat jullie allebei kunnen terugzien wanneer het akkoord gegeven is.</p>
                  {formError && <p className="text-red" style={{ margin: 0 }}>{formError}</p>}
                  <div><button className="btn btn-success" disabled={busy || name.trim().length < 2 || !agree}><CheckCircle2 size={15} /> {busy ? 'Even geduld…' : 'Offerte accepteren'}</button></div>
                </form>
              )}

              {mode === 'reject' && (
                <form className="quote-decide-form" onSubmit={e => { e.preventDefault(); send(false) }}>
                  <Field label="Wil je laten weten waarom? Dat mag ook leeg blijven." full>
                    <textarea rows={4} maxLength={1000} value={reason} onChange={e => setReason(e.target.value)} placeholder="Bijvoorbeeld: te duur, andere planning of een andere keuze." />
                  </Field>
                  {formError && <p className="text-red" style={{ margin: 0 }}>{formError}</p>}
                  <div><button className="btn btn-danger" disabled={busy}><XCircle size={15} /> {busy ? 'Even geduld…' : 'Offerte afwijzen'}</button></div>
                </form>
              )}
            </div>
          </div>
        )}

        <p className="muted public-invoice-foot no-print">
          Kies in het afdrukvenster voor "Opslaan als PDF" om de offerte te bewaren. Deze offerte is verstuurd met {product.name}.
        </p>
      </div>
    </div>
  )
}
