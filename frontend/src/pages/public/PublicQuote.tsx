import { useCallback, useEffect, useRef, useState } from 'react'
import { Ban, CheckCircle2, Clock, FileWarning, Hourglass, Printer, ThumbsDown, ThumbsUp, XCircle } from 'lucide-react'
import { api } from '../../lib/api'
import { product } from '../../lib/plans'
import { depositAmount } from '../../lib/quote'
import type { PublicQuote, Settings } from '../../lib/types'
import { QuotePaper } from '../../components/QuotePaper'
import { Field } from '../../components/ui'
import { usePrivatePage } from '../../lib/privatePage'
import { docDate, docDateTime, docEuro, docNum, docTitle, t } from '../../lib/docTexts'
import { LinkProblem } from './LinkProblem'
import { linkError, type LinkError } from '../../lib/linkError'

// De offerte achter de link. Werkt zonder inloggen en laat alleen deze ene offerte zien. Onderaan geeft je klant akkoord
// (met zijn naam en een vinkje) of wijst hij de offerte af; dat kan één keer. Het token staat achter de #, zodat het niet
// bij de server of in logboeken belandt. Alles staat in de taal van de offerte.
export function PublicQuotePage() {
  usePrivatePage()
  const [token] = useState(() => new URLSearchParams(window.location.hash.slice(1)).get('token') ?? '')
  const [data, setData] = useState<PublicQuote | null>(null)
  const [error, setError] = useState<LinkError | null>(token ? null : { kind: 'incomplete' })
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
      // De titel wordt ook de bestandsnaam van de PDF.
      document.title = docTitle(d.quote.language, 'quote', d.quote.number, d.quote.seller?.name ?? '')
    })
    .catch(e => setError(linkError(e))), [token])

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

  if (error) return <LinkProblem icon={<FileWarning size={22} />} title="public.quoteNotFound" gone="public.quoteGone" error={error} />
  if (!data) return <div className="public-invoice"><div className="public-invoice-inner muted">{t('nl', 'public.quoteLoading')} · {t('en', 'public.quoteLoading')}</div></div>

  const q = data.quote
  const lang = q.language ?? 'nl'
  const euro = (n: number) => docEuro(lang, n)
  const date = (s?: string | null) => docDate(lang, s)
  const seller = q.seller?.name ?? t(lang, 'public.sender')
  const company: Settings = {
    companyName: q.seller?.name ?? '', defaultHourlyRate: 0, paymentTermDays: 0, weeklyHoursTarget: 0, yearlyHoursTarget: 0,
    logoDataUrl: data.brand.logoDataUrl, brandColor: data.brand.brandColor,
  }
  const deposit = depositAmount(q)

  const when = q.decidedAt ? docDateTime(lang, q.decidedAt) : null
  const sentence = (...parts: (string | false | null | undefined)[]) => parts.filter(Boolean).join(' ')
  const accepted = q.decidedName
    ? (when ? t(lang, 'public.acceptedByOn', { name: q.decidedName, when }) : t(lang, 'public.acceptedBy', { name: q.decidedName }))
    : (when ? t(lang, 'public.acceptedOn', { when }) : t(lang, 'public.accepted'))
  const status = q.state === 'geaccepteerd'
    ? { tone: 'ok', icon: <CheckCircle2 size={16} />, text: sentence(justDecided && t(lang, 'public.thanks'), accepted, justDecided && t(lang, 'public.nextStep', { seller })) }
    : q.state === 'afgewezen'
      ? {
          tone: '', icon: <XCircle size={16} />,
          text: sentence(justDecided && t(lang, 'public.thanksResponse'), when ? t(lang, 'quote.rejectedOn', { when }) : t(lang, 'quote.rejectedText'),
            justDecided && t(lang, 'public.responseReceived', { seller })),
        }
    : q.state === 'ingetrokken'
      ? { tone: '', icon: <Ban size={16} />, text: sentence(data.problem ?? t(lang, 'quote.withdrawnShort'), q.newerNumber && t(lang, 'public.newerVersion', { number: q.newerNumber })) }
    : q.state === 'verlopen'
      ? { tone: '', icon: <Hourglass size={16} />, text: data.problem ?? t(lang, 'quote.expiredText', { date: date(q.validUntil) }) }
      : { tone: '', icon: <Clock size={16} />, text: t(lang, 'public.quoteOpen', { date: date(q.validUntil) }) }

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
    <div className="public-invoice public-quote" lang={lang}>
      <div className="public-invoice-inner">
        <div className="public-invoice-bar no-print">
          <div className={`notice ${status.tone === 'ok' ? 'notice-ok' : ''}`} style={{ margin: 0, flex: 1, minWidth: 220 }}>{status.icon} {status.text}</div>
          {data.canDecide && (
            <button className="btn btn-primary" onClick={() => decide.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })}><ThumbsUp size={15} /> {t(lang, 'public.decide')}</button>
          )}
          <button className="btn" onClick={() => window.print()}><Printer size={15} /> {t(lang, 'public.print')}</button>
        </div>

        <QuotePaper quote={q} buyer={q.buyer ?? null} state={q.state} company={company} />

        {data.canDecide && (
          <div className="card quote-decide no-print" ref={decide}>
            <div className="card-body">
              <h3 style={{ marginTop: 0 }}>{t(lang, 'public.question')}</h3>
              <p className="muted" style={{ marginTop: 0 }}>
                {q.seller?.email
                  ? t(lang, 'public.onceEmail', { date: date(q.validUntil), seller, email: q.seller.email })
                  : t(lang, 'public.once', { date: date(q.validUntil), seller })}
              </p>
              <div className="quote-decide-choices" role="radiogroup" aria-label={t(lang, 'public.choice')}>
                <button type="button" role="radio" aria-checked={mode === 'accept'} className={`quote-choice ${mode === 'accept' ? 'active ok' : ''}`} onClick={() => { setMode('accept'); setFormError(null) }}>
                  <ThumbsUp size={18} /> {t(lang, 'public.accept')}
                </button>
                <button type="button" role="radio" aria-checked={mode === 'reject'} className={`quote-choice ${mode === 'reject' ? 'active no' : ''}`} onClick={() => { setMode('reject'); setFormError(null) }}>
                  <ThumbsDown size={18} /> {t(lang, 'public.reject')}
                </button>
              </div>

              {mode === 'accept' && (
                <form className="quote-decide-form" onSubmit={e => { e.preventDefault(); send(true) }}>
                  <Field label={t(lang, 'public.fullName')} full>
                    <input value={name} maxLength={100} autoComplete="name" placeholder={t(lang, 'public.namePlaceholder')} onChange={e => setName(e.target.value)} autoFocus />
                  </Field>
                  <label className="check-row check-row-top">
                    <input type="checkbox" checked={agree} onChange={e => setAgree(e.target.checked)} />
                    <span>
                      {t(lang, 'public.agree', {
                        number: q.number ?? '', seller,
                        amount: q.vatRegime === 'normaal' ? t(lang, 'paper.inclVat', { amount: euro(q.totals?.total ?? 0) }) : euro(q.totals?.total ?? 0),
                        deposit: deposit ? t(lang, 'public.agreeDeposit', { percent: docNum(lang, q.depositPercent!), amount: euro(deposit.total) }) : '',
                        terms: q.terms ? t(lang, 'public.agreeTerms') : '',
                      })}
                    </span>
                  </label>
                  <p className="cell-sub" style={{ margin: 0 }}>{t(lang, 'public.privacy')}</p>
                  {formError && <p className="text-red" style={{ margin: 0 }}>{formError}</p>}
                  <div><button className="btn btn-success" disabled={busy || name.trim().length < 2 || !agree}><CheckCircle2 size={15} /> {busy ? t(lang, 'public.wait') : t(lang, 'public.acceptButton')}</button></div>
                </form>
              )}

              {mode === 'reject' && (
                <form className="quote-decide-form" onSubmit={e => { e.preventDefault(); send(false) }}>
                  <Field label={t(lang, 'public.reason')} full>
                    <textarea rows={4} maxLength={1000} value={reason} onChange={e => setReason(e.target.value)} placeholder={t(lang, 'public.reasonPlaceholder')} />
                  </Field>
                  {formError && <p className="text-red" style={{ margin: 0 }}>{formError}</p>}
                  <div><button className="btn btn-danger" disabled={busy}><XCircle size={15} /> {busy ? t(lang, 'public.wait') : t(lang, 'public.rejectButton')}</button></div>
                </form>
              )}
            </div>
          </div>
        )}

        <p className="muted public-invoice-foot no-print">
          {t(lang, 'public.quoteFoot', { product: product.name })}
        </p>
      </div>
    </div>
  )
}
