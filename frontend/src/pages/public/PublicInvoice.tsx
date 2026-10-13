import { useCallback, useEffect, useRef, useState } from 'react'
import { CheckCircle2, ChevronDown, ChevronRight, Clock, CreditCard, Download, FileWarning, Printer, XCircle } from 'lucide-react'
import { api } from '../../lib/api'
import { date, euro, invoiceTotals, num } from '../../lib/format'
import { product } from '../../lib/plans'
import { payWithIdeal, returnToken } from '../../lib/ideal'
import type { PublicInvoice, PublicPaymentStatus, Settings } from '../../lib/types'
import { InvoicePaper } from '../../components/InvoicePaper'
import { HourSpecPaper } from '../../components/HourSpecPaper'
import { usePrivatePage } from '../../lib/privatePage'

// De factuur achter de link in de mail. Werkt zonder inloggen en laat alleen deze ene factuur zien. Het token staat
// achter de #, zodat het niet bij de server of in logboeken belandt; het blijft in de adresbalk staan, zodat je de
// pagina kunt verversen of later opnieuw openen.
// Terug van de betaalpagina van Mollie komt de klant op /factuur?betaling=<code>: dan laten we zien of de betaling binnen is.
export function PublicInvoicePage() {
  usePrivatePage()
  const [payRef] = useState(() => new URLSearchParams(window.location.search).get('betaling'))
  const [token] = useState(() => new URLSearchParams(window.location.hash.slice(1)).get('token') ?? (payRef ? returnToken() : null) ?? '')
  const [data, setData] = useState<PublicInvoice | null>(null)
  const [error, setError] = useState<string | null>(token || payRef ? null : 'Deze link is niet compleet. Kopieer de hele link uit de mail, of vraag de afzender om een nieuwe link.')
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
      const seller = d.invoice.seller?.name
      document.title = `${d.invoice.isCredit ? 'Creditnota' : 'Factuur'} ${d.invoice.number ?? ''}${seller ? ` van ${seller}` : ''}`
    })
    .catch(e => setError((e as Error).message)), [token])

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
        setPaymentError((e as Error).message)
      }
      setChecking(false)
    }
    check()
  }, [payRef, token, load])

  const pay = async () => {
    setPaying(true)
    setPayError(null)
    try {
      await payWithIdeal(token)
    } catch (e) {
      setPayError((e as Error).message)
      setPaying(false)
    }
  }

  const paymentNotice = payRef ? paymentText(payment, checking, paymentError) : null

  // Terug van het betalen in een ander tabblad of zonder opslag: dan kennen we de factuur niet, wel de betaling.
  if (payRef && !token) return (
    <div className="login login-single">
      <div className="login-card">
        <h1>{paymentNotice?.tone === 'ok' ? <CheckCircle2 size={22} /> : paymentNotice?.tone === 'bad' ? <XCircle size={22} /> : <Clock size={22} />} Betaling{payment?.number ? ` voor factuur ${payment.number}` : ''}</h1>
        <p className="muted" style={{ margin: '6px 0 0' }}>{paymentNotice?.text}</p>
        <p className="muted" style={{ margin: '12px 0 0', fontSize: 13 }}>Wil je de factuur zien? Open hem dan via de link in de mail{payment?.seller ? ` van ${payment.seller}` : ''}.</p>
      </div>
    </div>
  )

  if (error) return (
    <div className="login login-single">
      <div className="login-card">
        <h1><FileWarning size={22} /> Factuur niet gevonden</h1>
        <p className="muted" style={{ margin: '6px 0 0' }}>{error}</p>
      </div>
    </div>
  )
  if (!data) return <div className="public-invoice"><div className="public-invoice-inner muted">Factuur laden…</div></div>

  const inv = data.invoice
  const seller = inv.seller
  const company: Settings = {
    companyName: seller?.name ?? '', defaultHourlyRate: 0, paymentTermDays: 0, weeklyHoursTarget: 0, yearlyHoursTarget: 0,
    logoDataUrl: data.brand.logoDataUrl, brandColor: data.brand.brandColor,
  }
  const total = invoiceTotals(inv.lines).total
  const late = !inv.isCredit && inv.status !== 'betaald' && new Date(inv.dueDate) < today
  const canPay = !!data.canPay && !payment?.paid && !checking
  const status = inv.isCredit ? { icon: <CheckCircle2 size={16} />, text: `Dit is een creditnota van ${euro(Math.abs(total))}. Het bedrag wordt verrekend of teruggestort.` }
    : inv.status === 'betaald' ? { icon: <CheckCircle2 size={16} />, text: `Deze factuur is betaald${inv.paidAt ? ` op ${date(inv.paidAt)}` : ''}. Dank je wel!` }
    : inv.fullyCredited ? { icon: <CheckCircle2 size={16} />, text: 'Deze factuur is helemaal gecrediteerd. Je hoeft niets meer te betalen.' }
    : {
        icon: <Clock size={16} />,
        text: (inv.paidTotal > 0 ? `Er is al ${euro(inv.paidTotal)} betaald. Er staat nog ${euro(inv.openAmount)} open`
          : inv.openAmount < total ? `Een deel van deze factuur is gecrediteerd. Er staat nog ${euro(inv.openAmount)} open`
          : `Te betalen: ${euro(inv.openAmount)}`)
          + (late ? `. De betaaltermijn is verstreken op ${date(inv.dueDate)}.` : `, uiterlijk op ${date(inv.dueDate)}.`),
      }

  const hours = data.hours
  const paper = { ...inv, customerId: 0, creditForInvoiceId: inv.isCredit ? 0 : null }
  const downloadHours = () => {
    setCsvError(null)
    api.download('/public/invoice/hours', `urenspecificatie-${inv.number ?? 'factuur'}.csv`, { token }).catch(e => setCsvError((e as Error).message))
  }

  return (
    <div className="public-invoice">
      <div className="public-invoice-inner">
        {paymentNotice && (
          <div className={`notice no-print ${paymentNotice.tone === 'ok' ? 'notice-ok' : paymentNotice.tone === 'bad' ? 'text-red' : ''}`} role="status" style={{ fontSize: 14 }}>
            {paymentNotice.tone === 'ok' ? <CheckCircle2 size={16} /> : paymentNotice.tone === 'bad' ? <XCircle size={16} /> : <Clock size={16} />} {paymentNotice.text}
          </div>
        )}
        <div className="public-invoice-bar no-print">
          <div className={`notice ${late ? 'text-red' : ''}`} style={{ margin: 0, flex: 1, minWidth: 220 }}>{status.icon} {status.text}</div>
          {canPay && <button className="btn btn-primary" disabled={paying} onClick={pay}><CreditCard size={15} /> {paying ? 'Even geduld…' : `Betaal nu met iDEAL (${euro(inv.openAmount)})`}</button>}
          <button className={`btn ${canPay ? '' : 'btn-primary'}`} onClick={() => window.print()}><Printer size={15} /> Opslaan als PDF of printen</button>
        </div>
        {payError && <p className="text-red no-print" style={{ margin: '-6px 0 12px', fontSize: 14 }}>{payError}</p>}
        <InvoicePaper invoice={paper} company={company} />
        {hours && (<>
          <div className="card no-print">
            <div className="card-body hours-fold">
              <button className="hours-fold-toggle" aria-expanded={showHours} onClick={() => setShowHours(v => !v)}>
                {showHours ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
                Urenspecificatie ({num(hours.hours)} uur)
              </button>
              <button className="btn btn-sm" onClick={downloadHours}><Download size={14} /> Download als CSV</button>
              {csvError && <p className="text-red" style={{ margin: 0, flexBasis: '100%', fontSize: 13 }}>{csvError}</p>}
            </div>
          </div>
          <div className={showHours ? undefined : 'hours-folded'}>
            <HourSpecPaper spec={hours} invoice={paper} company={company} />
          </div>
        </>)}
        <p className="muted public-invoice-foot no-print">
          Kies in het afdrukvenster voor "Opslaan als PDF" om de factuur te bewaren{hours ? ', met de urenspecificatie als extra pagina' : ''}. Deze factuur is verstuurd met {product.name}.
        </p>
      </div>
    </div>
  )
}

const stillRunning = (status: string) => status === 'open' || status === 'pending' || status === 'authorized'

// Wat de klant na het betalen ziet: binnen, nog niet binnen, of niet gelukt.
function paymentText(p: PublicPaymentStatus | null, checking: boolean, error: string | null): { tone: 'ok' | 'wait' | 'bad'; text: string } {
  if (error) return { tone: 'bad', text: error }
  if (p?.paid) return {
    tone: 'ok',
    text: `Betaling ontvangen, dank je wel! ${euro(p.amount)}${p.number ? ` voor factuur ${p.number}` : ''} is binnen.`
      + (p.test ? ' Dit was een testbetaling: er is geen echt geld overgemaakt.' : ''),
  }
  if (checking || !p) return { tone: 'wait', text: 'We kijken of je betaling binnen is…' }
  if (p.status === 'canceled') return { tone: 'bad', text: 'De betaling is geannuleerd. Er is niets afgeschreven. Je kunt het opnieuw proberen.' }
  if (p.status === 'expired') return { tone: 'bad', text: 'De betaling is verlopen. Er is niets afgeschreven. Je kunt het opnieuw proberen.' }
  if (p.status === 'failed') return { tone: 'bad', text: 'De betaling is niet gelukt. Er is niets afgeschreven. Je kunt het opnieuw proberen.' }
  return { tone: 'wait', text: 'Betaling nog niet binnen. Heb je net betaald? Dan kan het een paar minuten duren voordat hij verwerkt is. Ververs deze pagina straks nog eens.' }
}
