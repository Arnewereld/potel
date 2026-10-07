import type { Customer, Invoice, Settings } from '../lib/types'
import { date, euro, invoiceTotals, lineAmount, num } from '../lib/format'
import { buyerFrom, isCredit, isNetherlands, sellerFrom, zeroVat } from '../lib/invoice'

type Draft = Omit<Invoice, 'id'>

// De factuur zoals hij op papier en in de PDF staat. Een verstuurde factuur toont de gegevens van jou en je klant
// zoals ze bij versturen waren; een concept de huidige. Logo en accentkleur komen altijd uit je instellingen.
export function InvoicePaper({ invoice, customer, company }: { invoice: Draft; customer?: Customer | null; company: Settings }) {
  const totals = invoiceTotals(invoice.lines)
  const credit = isCredit(invoice)
  const seller = invoice.seller ?? sellerFrom(company)
  const buyer = invoice.buyer ?? (customer ? buyerFrom(customer) : null)
  const regime = invoice.vatRegime ?? 'normaal'
  const showVat = !zeroVat(regime)
  const from = invoice.deliveryFrom?.slice(0, 10)
  const to = invoice.deliveryTo?.slice(0, 10)
  const period = from && to && to !== from

  return (
    <div className="invoice-paper" style={{ '--accent': company.brandColor || '#ff6d5a' } as React.CSSProperties}>
      <header className="paper-head">
        <div className="paper-brand">
          {company.logoDataUrl
            ? <img className="paper-logo-img" src={company.logoDataUrl} alt="" />
            : <div className="paper-logo">{seller.name.slice(0, 1)}</div>}
          <div>
            <strong>{seller.name}</strong>
            {seller.website && <div className="paper-muted">{seller.website}</div>}
          </div>
        </div>
        <h2 className="paper-doc">{credit ? 'Creditnota' : 'Factuur'}</h2>
      </header>

      <div className="paper-parties">
        <div>
          <div className="paper-label">Aan</div>
          {buyer ? (<>
            <strong>{buyer.name}</strong>
            {buyer.contact && <div>t.a.v. {buyer.contact}</div>}
            {buyer.address && <div>{buyer.address}</div>}
            {buyer.city && <div>{buyer.city}</div>}
            {!isNetherlands(buyer.country) && <div>{buyer.country}</div>}
            {buyer.vatNumber && <div className="paper-muted">Btw-nr. {buyer.vatNumber}</div>}
          </>) : <span className="paper-muted">Nog geen klant gekozen</span>}
        </div>
        <div>
          <div className="paper-label">Van</div>
          <strong>{seller.name}</strong>
          {seller.contact && <div>{seller.contact}</div>}
          {seller.address && <div>{seller.address}</div>}
          {seller.city && <div>{seller.city}</div>}
          {seller.email && <div>{seller.email}</div>}
          {seller.phone && <div>{seller.phone}</div>}
        </div>
      </div>

      <div className="paper-meta">
        <div><span>{credit ? 'Creditnotanummer' : 'Factuurnummer'}</span><strong>{invoice.number ?? 'Concept'}</strong></div>
        <div><span>Datum</span><strong>{date(invoice.issueDate)}</strong></div>
        <div><span>{period ? 'Periode' : 'Leverdatum'}</span><strong>{period ? `${date(from)} t/m ${date(to)}` : date(from)}</strong></div>
        {!credit && <div><span>Vervaldatum</span><strong>{date(invoice.dueDate)}</strong></div>}
        {invoice.reference && <div><span>Uw referentie</span><strong>{invoice.reference}</strong></div>}
      </div>

      {invoice.creditForNumber && (
        <p className="paper-credit-ref">
          Creditnota voor factuur {invoice.creditForNumber}{invoice.creditForIssueDate ? ` van ${date(invoice.creditForIssueDate)}` : ''}.
        </p>
      )}

      <table className="paper-table">
        <thead>
          <tr><th>Omschrijving</th><th className="num">Aantal</th><th className="num">Tarief</th>{showVat && <th className="num">Btw</th>}<th className="num">Bedrag</th></tr>
        </thead>
        <tbody>
          {invoice.lines.map((l, i) => (
            <tr key={i}>
              <td>{l.description || <span className="paper-muted">…</span>}</td>
              <td className="num nowrap">{num(l.quantity)} {l.unit}</td>
              <td className="num nowrap">{euro(l.unitPrice)}</td>
              {showVat && <td className="num">{l.vatRate}%</td>}
              <td className="num nowrap">{euro(lineAmount(l))}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <div className="paper-summary">
        <div className="paper-summary-row">
          {invoice.status === 'betaald' && <div className="paper-stamp">Betaald{invoice.paidAt ? ` · ${date(invoice.paidAt)}` : ''}</div>}
          <div className="paper-totals">
            {regime !== 'kor' && regime !== 'vrijgesteld' && <div><span>Subtotaal</span><span>{euro(totals.subtotal)}</span></div>}
            {regime === 'normaal' && totals.vatGroups.map(g => <div key={g.rate}><span>Btw {g.rate}% over {euro(g.base)}</span><span>{euro(g.vat)}</span></div>)}
            {regime === 'verlegd' && <div><span>Btw verlegd</span><span>{euro(0)}</span></div>}
            {regime === 'buiten-eu' && <div><span>Btw niet van toepassing</span><span>{euro(0)}</span></div>}
            <div className="paper-total"><span>{credit ? 'Totaal credit' : 'Te betalen'}</span><span>{euro(totals.total)}</span></div>
          </div>
        </div>
        {!credit && totals.total > 0 && invoice.status !== 'betaald' && (
          <div className="paper-pay">
            <div><span>Bedrag</span><strong>{euro(totals.total)}</strong></div>
            <div><span>Uiterlijk op</span><strong>{date(invoice.dueDate)}</strong></div>
            {seller.iban && <div><span>Op rekening</span><strong>{seller.iban}</strong></div>}
            {seller.iban && <div><span>T.n.v.</span><strong>{seller.name}</strong></div>}
            <div><span>Kenmerk</span><strong>{invoice.number ?? 'volgt bij versturen'}</strong></div>
          </div>
        )}
        {credit && <p className="paper-notes" style={{ margin: 0 }}>Het bedrag van {euro(Math.abs(totals.total))} wordt verrekend of teruggestort.</p>}
      </div>

      {regime === 'verlegd' && (
        <p className="paper-reverse">
          Btw verlegd naar de afnemer.
          {seller.vatNumber && ` Btw-id leverancier: ${seller.vatNumber}.`}
          {buyer?.vatNumber && ` Btw-nummer afnemer: ${buyer.vatNumber}.`}
        </p>
      )}
      {regime === 'kor' && (
        <p className="paper-reverse">{seller.name} maakt gebruik van de kleineondernemersregeling (KOR). Daarom staat er geen btw op deze factuur.</p>
      )}
      {regime === 'vrijgesteld' && (
        <p className="paper-reverse">Vrijgesteld van btw op grond van artikel 11 van de Wet op de omzetbelasting 1968. Daarom staat er geen btw op deze factuur.</p>
      )}
      {regime === 'buiten-eu' && <p className="paper-reverse">Btw niet van toepassing: dienst aan een afnemer buiten de EU.</p>}
      {invoice.notes && <p className="paper-notes">{invoice.notes}</p>}

      <footer className="paper-foot">
        {[seller.name, seller.kvk && `KvK ${seller.kvk}`, seller.vatNumber && `Btw ${seller.vatNumber}`, seller.iban && `IBAN ${seller.iban}`, seller.email]
          .filter(Boolean).join('  ·  ')}
      </footer>
    </div>
  )
}
