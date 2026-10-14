import { useCallback, useEffect, useRef, useState } from 'react'
import { CheckCircle2, ChevronDown, ChevronRight, Clock, CreditCard, Download, FileWarning, Printer, XCircle } from 'lucide-react'
import { api } from '../../lib/api'
import { invoiceTotals } from '../../lib/format'
import { product } from '../../lib/plans'
import { payWithIdeal, returnToken } from '../../lib/ideal'
import type { Language, PublicInvoice, PublicPaymentStatus, Settings } from '../../lib/types'
import { InvoicePaper } from '../../components/InvoicePaper'
import { HourSpecPaper } from '../../components/HourSpecPaper'
import { usePrivatePage } from '../../lib/privatePage'
import { docDate, docEuro, docNum, docTitle, t } from '../../lib/docTexts'
import { LinkProblem } from './LinkProblem'
import { linkError, type LinkError } from '../../lib/linkError'

// De factuur achter de link in de mail. Werkt zonder inloggen en laat alleen deze ene factuur zien. Het token staat
// achter de #, zodat het niet bij de server of in logboeken belandt; het blijft in de adresbalk staan, zodat je de
// pagina kunt verversen of later opnieuw openen.
// Terug van de betaalpagina van Mollie komt de klant op /factuur?betaling=<code>: dan laten we zien of de betaling binnen is.
// Alles staat in de taal van de factuur; zolang we die niet kennen in het Nederlands en het Engels.
export function PublicInvoicePage() {
  usePrivatePage()
  const [payRef] = useState(() => new URLSearchParams(window.location.search).get('betaling'))
  const [token] = useState(() => new URLSearchParams(window.location.hash.slice(1)).get('token') ?? (payRef ? returnToken() : null) ?? '')
  const [data, setData] = useState<PublicInvoice | null>(null)
  const [error, setError] = useState<LinkError | null>(token || payRef ? null : { kind: 'incomplete' })
  const started = useRef(false)
  const [payment, setPayment] = useState<PublicPaymentStatus | null>(null)
  const [paymentError, setPaymentError] = useState<string | null>(null)
  const [checking, setChecking] = useState(!!payRef)
  const polled = useRef(false)
  const [paying, setPaying] = useState(false)
  const [payError, setPayError] = useState<string | null>(null)
  const [today] = useState(() => new Date(new Date().toDateString()))
  // De urenspecificatie staat op het scherm ingeklapt, maar gaat altijd mee in de PDF.
  const [showHours, setShowHours] = useState(false)
  const [csvError, setCsvError] = useState<string | null>(null)

  const load = useCallback(() => api.post<PublicInvoice>('/public/invoice', { token })
    .then(d => {
      setData(d)
      // De titel wordt ook de bestandsnaam van de PDF.
      document.title = docTitle(d.invoice.language, d.invoice.isCredit ? 'credit' : 'invoice', d.invoice.number, d.invoice.seller?.name ?? '')
    })
    .catch(e => setError(linkError(e))), [token])

  useEffect(() => {
    if (!token || started.current) return
    started.current = true
    load()
  }, [token, load])

  // De status van de betaling zoals de webhook van Mollie hem doorgaf. Een paar keer opnieuw kijken, want die melding kan
  // net na de klant binnenkomen. Is hij binnen, dan de factuur opnieuw ophalen voor de nieuwe stand.
  useEffect(() => {
    if (!payRef || polled.current) return
    polled.current = true
    let tries = 0
    const check = async () => {
      try {
        const s = await api.post<PublicPaymentStatus>('/public/invoice/payment-status', { ref: payRef })
        setPayment(s)
        if (s.paid) {
          if (token) load()
        } else if (stillRunning(s.status) && ++tries < 6) {
          setTimeout(check, 2500)
          return
        }
      } catch (e) {
        setPaymentError(linkError(e).kind === 'gone' ? 'unknown' : (e as Error).message)
      }
      setChecking(false)
    }
    check()
  }, [payRef, token, load])

  // De taal van de factuur, of als we alleen de betaling kennen die van de betaling.
  const lang: Language = data?.invoice.language ?? payment?.language ?? 'nl'
  const euro = (n: number) => docEuro(lang, n)
  const date = (s?: string | null) => docDate(lang, s)

  const pay = async () => {
    setPaying(true)
    setPayError(null)
    try {
      await payWithIdeal(token, lang)
    } catch (e) {
      setPayError((e as Error).message)
      setPaying(false)
    }
  }

  const paymentNotice = payRef ? paymentText(lang, payment, checking, paymentError) : null

  // Terug van het betalen in een ander tabblad of zonder opslag: dan kennen we de factuur niet, wel de betaling.
  if (payRef && !token) return (
    <div className="login login-single" lang={lang}>
      <div className="login-card">
        <h1>
          {paymentNotice?.tone === 'ok' ? <CheckCircle2 size={22} /> : paymentNotice?.tone === 'bad' ? <XCircle size={22} /> : <Clock size={22} />}{' '}
          {payment?.number ? t(lang, 'pay.titleFor', { number: payment.number }) : t(lang, 'pay.title')}
        </h1>
        <p className="muted" style={{ margin: '6px 0 0' }}>{paymentNotice?.text}</p>
        <p className="muted" style={{ margin: '12px 0 0', fontSize: 13 }}>
          {payment?.seller ? t(lang, 'pay.openInvoiceFrom', { seller: payment.seller }) : t(lang, 'pay.openInvoice')}
        </p>
      </div>
    </div>
  )

  if (error) return <LinkProblem icon={<FileWarning size={22} />} title="public.invoiceNotFound" gone="public.invoiceGone" error={error} />
  if (!data) return <div className="public-invoice"><div className="public-invoice-inner muted">{t('nl', 'public.invoiceLoading')} · {t('en', 'public.invoiceLoading')}</div></div>

  const inv = data.invoice
  const seller = inv.seller
  const company: Settings = {
    companyName: seller?.name ?? '', defaultHourlyRate: 0, paymentTermDays: 0, weeklyHoursTarget: 0, yearlyHoursTarget: 0,
    logoDataUrl: data.brand.logoDataUrl, brandColor: data.brand.brandColor,
  }
  const total = invoiceTotals(inv.lines).total
  const late = !inv.isCredit && inv.status !== 'betaald' && new Date(inv.dueDate) < today
  const canPay = !!data.canPay && !payment?.paid && !checking
  const status = inv.isCredit ? { icon: <CheckCircle2 size={16} />, text: t(lang, 'public.creditStatus', { amount: euro(Math.abs(total)) }) }
    : inv.status === 'betaald' ? { icon: <CheckCircle2 size={16} />, text: inv.paidAt ? t(lang, 'public.paidOn', { date: date(inv.paidAt) }) : t(lang, 'public.paid') }
    : inv.fullyCredited ? { icon: <CheckCircle2 size={16} />, text: t(lang, 'public.fullyCredited') }
    : {
        icon: <Clock size={16} />,
        text: (inv.paidTotal > 0 ? t(lang, 'public.partlyPaid', { paid: euro(inv.paidTotal), open: euro(inv.openAmount) })
          : inv.openAmount < total ? t(lang, 'public.partlyCredited', { open: euro(inv.openAmount) })
          : t(lang, 'public.toPay', { open: euro(inv.openAmount) }))
          + t(lang, late ? 'public.late' : 'public.dueBy', { date: date(inv.dueDate) }),
      }

  const hours = data.hours
  const paper = { ...inv, customerId: 0, creditForInvoiceId: inv.isCredit ? 0 : null }
  const downloadHours = () => {
    setCsvError(null)
    api.download('/public/invoice/hours', t(lang, 'public.csvFile', { number: inv.number ?? '' }), { token }).catch(e => setCsvError((e as Error).message))
  }

  return (
    <div className="public-invoice" lang={lang}>
      <div className="public-invoice-inner">
        {paymentNotice && (
          <div className={`notice no-print ${paymentNotice.tone === 'ok' ? 'notice-ok' : paymentNotice.tone === 'bad' ? 'text-red' : ''}`} role="status" style={{ fontSize: 14 }}>
            {paymentNotice.tone === 'ok' ? <CheckCircle2 size={16} /> : paymentNotice.tone === 'bad' ? <XCircle size={16} /> : <Clock size={16} />} {paymentNotice.text}
          </div>
        )}
        <div className="public-invoice-bar no-print">
          <div className={`notice ${late ? 'text-red' : ''}`} style={{ margin: 0, flex: 1, minWidth: 220 }}>{status.icon} {status.text}</div>
          {canPay && (
            <button className="btn btn-primary" disabled={paying} onClick={pay}>
              <CreditCard size={15} /> {paying ? t(lang, 'public.wait') : t(lang, 'public.pay', { amount: euro(inv.openAmount) })}
            </button>
          )}
          <button className={`btn ${canPay ? '' : 'btn-primary'}`} onClick={() => window.print()}><Printer size={15} /> {t(lang, 'public.print')}</button>
        </div>
        {payError && <p className="text-red no-print" style={{ margin: '-6px 0 12px', fontSize: 14 }}>{payError}</p>}
        <InvoicePaper invoice={paper} company={company} />
        {hours && (<>
          <div className="card no-print">
            <div className="card-body hours-fold">
              <button className="hours-fold-toggle" aria-expanded={showHours} onClick={() => setShowHours(v => !v)}>
                {showHours ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                {t(lang, 'public.hoursFold', { hours: docNum(lang, hours.hours) })}
              </button>
              <button className="btn btn-sm" onClick={downloadHours}><Download size={14} /> {t(lang, 'public.downloadCsv')}</button>
              {csvError && <p className="text-red" style={{ margin: 0, flexBasis: '100%', fontSize: 13 }}>{csvError}</p>}
            </div>
          </div>
          <div className={showHours ? undefined : 'hours-folded'}>
            <HourSpecPaper spec={hours} invoice={paper} company={company} />
          </div>
        </>)}
        <p className="muted public-invoice-foot no-print">
          {t(lang, hours ? 'public.invoiceFootHours' : 'public.invoiceFoot', { product: product.name })}
        </p>
      </div>
    </div>
  )
}

const stillRunning = (status: string) => status === 'open' || status === 'pending' || status === 'authorized'

// Wat de klant na het betalen ziet: binnen, nog niet binnen, of niet gelukt. Een onbekende betaling kennen we niet,
// dus ook zijn taal niet: dan in beide talen.
function paymentText(lang: Language, p: PublicPaymentStatus | null, checking: boolean, error: string | null): { tone: 'ok' | 'wait' | 'bad'; text: string } {
  if (error === 'unknown') return { tone: 'bad', text: `${t('nl', 'pay.unknown')} ${t('en', 'pay.unknown')}` }
  if (error) return { tone: 'bad', text: error }
  if (p?.paid) {
    const amount = docEuro(lang, p.amount)
    return {
      tone: 'ok',
      text: (p.number ? t(lang, 'pay.receivedFor', { amount, number: p.number }) : t(lang, 'pay.received', { amount }))
        + (p.test ? ` ${t(lang, 'pay.test')}` : ''),
    }
  }
  if (checking || !p) return { tone: 'wait', text: t(lang, 'pay.checking') }
  if (p.status === 'canceled') return { tone: 'bad', text: t(lang, 'pay.canceled') }
  if (p.status === 'expired') return { tone: 'bad', text: t(lang, 'pay.expired') }
  if (p.status === 'failed') return { tone: 'bad', text: t(lang, 'pay.failed') }
  return { tone: 'wait', text: t(lang, 'pay.pending') }
}
