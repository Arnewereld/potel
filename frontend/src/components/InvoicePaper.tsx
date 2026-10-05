import type { Customer, Invoice, Settings } from '../lib/types'
import { date, euro, invoiceTotals, num } from '../lib/format'

type Draft = Omit<Invoice, 'id'>

// De factuur zoals hij op papier en in de PDF staat.
export function InvoicePaper({ invoice, customer, company }: { invoice: Draft; customer?: Customer | null; company: Settings }) {
  const totals = invoiceTotals(invoice.lines)
  const credit = totals.total < 0

  // Btw per tarief voor de specificatie onder de regels.
  const vatGroups = invoice.lines.reduce<Record<string, { base: number; vat: number }>>((acc, l) => {
    const net = (Number(l.quantity) || 0) * (Number(l.unitPrice) || 0)
    const g = acc[l.vatRate] ?? { base: 0, vat: 0 }
    acc[l.vatRate] = { base: g.base + net, vat: g.vat + Math.round(net * l.vatRate) / 100 }
    return acc
  }, {})

  return (
    <div className="invoice-paper" style={{ '--accent': company.brandColor || '#ff6d5a' } as React.CSSProperties}>
      <header className="paper-head">
        <div className="paper-brand">
          {company.logoDataUrl
            ? <img className="paper-logo-img" src={company.logoDataUrl} alt="" />
            : <div className="paper-logo">{company.companyName.slice(0, 1)}</div>}
          <div>
            <strong>{company.companyName}</strong>
            {company.website && <div className="paper-muted">{company.website}</div>}
          </div>
        </div>
        <h2 className="paper-doc">{credit ? 'Creditnota' : 'Factuur'}</h2>
      </header>

      <div className="paper-parties">
        <div>
          <div className="paper-label">Aan</div>
          {customer ? (<>
            <strong>{customer.company || customer.name}</strong>
            {customer.company && <div>t.a.v. {customer.name}</div>}
            {customer.address && <div>{customer.address}</div>}
            {customer.city && <div>{customer.city}</div>}
            {customer.vatNumber && <div className="paper-muted">Btw-nr. {customer.vatNumber}</div>}
          </>) : <span className="paper-muted">Nog geen klant gekozen</span>}
        </div>
        <div>
          <div className="paper-label">Van</div>
          <strong>{company.companyName}</strong>
          {company.ownerName && <div>{company.ownerName}</div>}
          {company.address && <div>{company.address}</div>}
          {company.city && <div>{company.city}</div>}
          {company.email && <div>{company.email}</div>}
          {company.phone && <div>{company.phone}</div>}
        </div>
      </div>

      <div className="paper-meta">
        <div><span>{credit ? 'Creditnotanummer' : 'Factuurnummer'}</span><strong>{invoice.number}</strong></div>
        <div><span>Datum</span><strong>{date(invoice.issueDate)}</strong></div>
        {!credit && <div><span>Vervaldatum</span><strong>{date(invoice.dueDate)}</strong></div>}
        {invoice.reference && <div><span>Uw referentie</span><strong>{invoice.reference}</strong></div>}
      </div>

      <table className="paper-table">
        <thead>
          <tr><th>Omschrijving</th><th className="num">Aantal</th><th className="num">Tarief</th>{!invoice.reverseCharge && <th className="num">Btw</th>}<th className="num">Bedrag</th></tr>
        </thead>
        <tbody>
          {invoice.lines.map((l, i) => (
            <tr key={i}>
              <td>{l.description || <span className="paper-muted">…</span>}</td>
              <td className="num nowrap">{num(l.quantity)} {l.unit}</td>
              <td className="num nowrap">{euro(l.unitPrice)}</td>
              {!invoice.reverseCharge && <td className="num">{l.vatRate}%</td>}
              <td className="num nowrap">{euro(l.quantity * l.unitPrice)}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <div className="paper-summary">
        <div className="paper-summary-row">
          {invoice.status === 'betaald' && <div className="paper-stamp">Betaald{invoice.paidAt ? ` · ${date(invoice.paidAt)}` : ''}</div>}
          <div className="paper-totals">
            <div><span>Subtotaal</span><span>{euro(totals.subtotal)}</span></div>
            {invoice.reverseCharge
              ? <div><span>Btw verlegd</span><span>{euro(0)}</span></div>
              : Object.entries(vatGroups).map(([rate, g]) => <div key={rate}><span>Btw {rate}% over {euro(g.base)}</span><span>{euro(g.vat)}</span></div>)}
            <div className="paper-total"><span>{credit ? 'Totaal credit' : 'Te betalen'}</span><span>{euro(totals.total)}</span></div>
          </div>
        </div>
        {!credit && totals.total > 0 && invoice.status !== 'betaald' && (
          <div className="paper-pay">
            <div><span>Bedrag</span><strong>{euro(totals.total)}</strong></div>
            <div><span>Uiterlijk op</span><strong>{date(invoice.dueDate)}</strong></div>
            {company.iban && <div><span>Op rekening</span><strong>{company.iban}</strong></div>}
            {company.iban && <div><span>T.n.v.</span><strong>{company.companyName}</strong></div>}
            <div><span>Kenmerk</span><strong>{invoice.number}</strong></div>
          </div>
        )}
        {credit && <p className="paper-notes" style={{ margin: 0 }}>Het bedrag van {euro(Math.abs(totals.total))} wordt verrekend of teruggestort.</p>}
      </div>

      {invoice.reverseCharge && (
        <p className="paper-reverse">Btw verlegd naar de afnemer{customer?.vatNumber ? ` (btw-nr. ${customer.vatNumber})` : ''}.</p>
      )}
      {invoice.notes && <p className="paper-notes">{invoice.notes}</p>}

      <footer className="paper-foot">
        {[company.companyName, company.kvk && `KvK ${company.kvk}`, company.btw && `Btw ${company.btw}`, company.iban && `IBAN ${company.iban}`, company.email]
          .filter(Boolean).join('  ·  ')}
      </footer>
    </div>
  )
}
