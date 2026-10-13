import { useEffect, useRef, useState } from 'react'
import { CheckCircle2, ChevronDown, ChevronRight, Clock, Download, FileWarning, Printer } from 'lucide-react'
import { api } from '../../lib/api'
import { date, euro, invoiceTotals, num } from '../../lib/format'
import { product } from '../../lib/plans'
import type { PublicInvoice, Settings } from '../../lib/types'
import { InvoicePaper } from '../../components/InvoicePaper'
import { HourSpecPaper } from '../../components/HourSpecPaper'
import { usePrivatePage } from '../../lib/privatePage'

// De factuur achter de link in de mail. Werkt zonder inloggen en laat alleen deze ene factuur zien. Het token staat
// achter de #, zodat het niet bij de server of in logboeken belandt; het blijft in de adresbalk staan, zodat je de
// pagina kunt verversen of later opnieuw openen.
export function PublicInvoicePage() {
  usePrivatePage()
  const [token] = useState(() => new URLSearchParams(window.location.hash.slice(1)).get('token') ?? '')
  const [data, setData] = useState<PublicInvoice | null>(null)
  const [error, setError] = useState<string | null>(token ? null : 'Deze link is niet compleet. Kopieer de hele link uit de mail, of vraag de afzender om een nieuwe link.')
  const started = useRef(false)
  const [today] = useState(() => new Date(new Date().toDateString()))
  // De urenspecificatie staat op het scherm ingeklapt, maar gaat altijd mee in de PDF.
  const [showHours, setShowHours] = useState(false)
  const [csvError, setCsvError] = useState<string | null>(null)

  useEffect(() => {
    if (!token || started.current) return
    started.current = true
    api.post<PublicInvoice>('/public/invoice', { token })
      .then(d => {
        setData(d)
        const seller = d.invoice.seller?.name
        document.title = `${d.invoice.isCredit ? 'Creditnota' : 'Factuur'} ${d.invoice.number ?? ''}${seller ? ` van ${seller}` : ''}`
      })
      .catch(e => setError((e as Error).message))
  }, [token])

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
        <div className="public-invoice-bar no-print">
          <div className={`notice ${late ? 'text-red' : ''}`} style={{ margin: 0, flex: 1, minWidth: 220 }}>{status.icon} {status.text}</div>
          <button className="btn btn-primary" onClick={() => window.print()}><Printer size={15} /> Opslaan als PDF of printen</button>
        </div>
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
