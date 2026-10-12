import { useEffect, useRef, useState } from 'react'
import { CheckCircle2, Clock, FileWarning, Printer } from 'lucide-react'
import { api } from '../../lib/api'
import { date, euro, invoiceTotals } from '../../lib/format'
import { product } from '../../lib/plans'
import type { PublicInvoice, Settings } from '../../lib/types'
import { InvoicePaper } from '../../components/InvoicePaper'

// Niet in zoekmachines en het adres niet doorgeven aan andere sites. De server stuurt dezelfde regels mee als header.
function usePrivatePage() {
  useEffect(() => {
    const tags = [['robots', 'noindex, nofollow'], ['referrer', 'no-referrer']].map(([name, content]) => {
      const meta = Object.assign(document.createElement('meta'), { name, content })
      document.head.appendChild(meta)
      return meta
    })
    return () => tags.forEach(t => t.remove())
  }, [])
}

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
        text: `${inv.openAmount < total ? `Een deel van deze factuur is gecrediteerd. Er staat nog ${euro(inv.openAmount)} open` : `Te betalen: ${euro(inv.openAmount)}`}`
          + (late ? `. De betaaltermijn is verstreken op ${date(inv.dueDate)}.` : `, uiterlijk op ${date(inv.dueDate)}.`),
      }

  return (
    <div className="public-invoice">
      <div className="public-invoice-inner">
        <div className="public-invoice-bar no-print">
          <div className={`notice ${late ? 'text-red' : ''}`} style={{ margin: 0, flex: 1, minWidth: 220 }}>{status.icon} {status.text}</div>
          <button className="btn btn-primary" onClick={() => window.print()}><Printer size={15} /> Opslaan als PDF of printen</button>
        </div>
        <InvoicePaper invoice={{ ...inv, customerId: 0, creditForInvoiceId: inv.isCredit ? 0 : null }} company={company} />
        <p className="muted public-invoice-foot no-print">
          Kies in het afdrukvenster voor "Opslaan als PDF" om de factuur te bewaren. Deze factuur is verstuurd met {product.name}.
        </p>
      </div>
    </div>
  )
}
