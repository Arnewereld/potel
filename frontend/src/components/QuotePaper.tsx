import type { InvoiceParty, Quote, QuoteState, Settings } from '../lib/types'
import { date, euro, invoiceTotals, lineAmount, num, utcDateTime } from '../lib/format'
import { isNetherlands, sellerFrom, zeroVat } from '../lib/invoice'
import { depositAmount } from '../lib/quote'
import { PaperFoot, PaperHead } from './InvoicePaper'

type Paper = Pick<Quote, 'number' | 'version' | 'issueDate' | 'validUntil' | 'vatRegime' | 'subject' | 'intro' | 'terms' | 'depositPercent' | 'lines'
  | 'seller' | 'decidedAt' | 'decidedName'> & { previousNumber?: string | null }

// De offerte zoals hij op papier, in de PDF en op de openbare pagina staat. Een verstuurde offerte toont de gegevens van
// jou en je klant zoals ze bij versturen waren; een concept de huidige. Logo en accentkleur komen uit je instellingen.
export function QuotePaper({ quote, buyer, state, company }: { quote: Paper; buyer: InvoiceParty | null; state: QuoteState; company: Settings }) {
  const totals = invoiceTotals(quote.lines)
  const seller = quote.seller ?? sellerFrom(company)
  const regime = quote.vatRegime ?? 'normaal'
  const showVat = !zeroVat(regime)
  const deposit = depositAmount(quote)

  return (
    <div className="invoice-paper quote-paper" style={{ '--accent': company.brandColor || '#ff6d5a' } as React.CSSProperties}>
      <PaperHead seller={seller} company={company} title="Offerte" />

      <div className="paper-parties">
        <div>
          <div className="paper-label">Aan</div>
          {buyer ? (<>
            <strong>{buyer.name}</strong>
            {buyer.contact && <div>t.a.v. {buyer.contact}</div>}
            {buyer.address && <div>{buyer.address}</div>}
            {buyer.city && <div>{buyer.city}</div>}
            {buyer.country && !isNetherlands(buyer.country) && <div>{buyer.country}</div>}
            {buyer.vatNumber && <div className="paper-muted">Btw-nr. {buyer.vatNumber}</div>}
          </>) : <span className="paper-muted">Nog geen klant of lead gekozen</span>}
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
        <div><span>Offertenummer</span><strong>{quote.number ?? 'Concept'}</strong></div>
        <div><span>Datum</span><strong>{date(quote.issueDate)}</strong></div>
        <div><span>Geldig tot en met</span><strong>{date(quote.validUntil)}</strong></div>
        {quote.version > 1 && <div><span>Versie</span><strong>{quote.version}{quote.previousNumber ? `, vervangt ${quote.previousNumber}` : ''}</strong></div>}
      </div>

      {quote.subject && <h3 className="quote-subject">{quote.subject}</h3>}
      {quote.intro && <p className="quote-text">{quote.intro}</p>}

      <table className="paper-table">
        <thead>
          <tr><th>Omschrijving</th><th className="num">Aantal</th><th className="num">Tarief</th>{showVat && <th className="num">Btw</th>}<th className="num">Bedrag</th></tr>
        </thead>
        <tbody>
          {quote.lines.map((l, i) => (
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
          {state === 'geaccepteerd' && <div className="paper-stamp">Geaccepteerd{quote.decidedAt ? ` · ${date(quote.decidedAt)}` : ''}</div>}
          {state === 'ingetrokken' && <div className="paper-stamp paper-stamp-muted">Ingetrokken</div>}
          <div className="paper-totals">
            {regime !== 'kor' && regime !== 'vrijgesteld' && <div><span>Subtotaal</span><span>{euro(totals.subtotal)}</span></div>}
            {regime === 'normaal' && totals.vatGroups.map(g => <div key={g.rate}><span>Btw {g.rate}% over {euro(g.base)}</span><span>{euro(g.vat)}</span></div>)}
            {regime === 'verlegd' && <div><span>Btw verlegd</span><span>{euro(0)}</span></div>}
            {regime === 'buiten-eu' && <div><span>Btw niet van toepassing</span><span>{euro(0)}</span></div>}
            <div className="paper-total"><span>Totaal</span><span>{euro(totals.total)}</span></div>
          </div>
        </div>
        {deposit && (
          <p className="paper-notes" style={{ margin: 0 }}>
            Bij akkoord vragen we een aanbetaling van {num(quote.depositPercent!)}%: {euro(deposit.total)}{showVat ? ' inclusief btw' : ''}. Het restant factureren we na oplevering.
          </p>
        )}
      </div>

      {regime === 'verlegd' && <p className="paper-reverse">De btw wordt op de factuur verlegd naar de afnemer.</p>}
      {regime === 'kor' && <p className="paper-reverse">{seller.name} maakt gebruik van de kleineondernemersregeling (KOR). Daarom staat er geen btw op deze offerte.</p>}
      {regime === 'vrijgesteld' && <p className="paper-reverse">Vrijgesteld van btw op grond van artikel 11 van de Wet op de omzetbelasting 1968. Daarom staat er geen btw op deze offerte.</p>}
      {regime === 'buiten-eu' && <p className="paper-reverse">Btw niet van toepassing: dienst aan een afnemer buiten de EU.</p>}

      {quote.terms && (<>
        <div className="paper-label quote-terms-label">Voorwaarden</div>
        <p className="quote-text quote-terms">{quote.terms}</p>
      </>)}

      <div className="quote-accept">
        {state === 'geaccepteerd' && quote.decidedName
          ? <>Akkoord gegeven door <strong>{quote.decidedName}</strong>{quote.decidedAt ? ` op ${utcDateTime(quote.decidedAt)}` : ''}.</>
          : state === 'afgewezen' ? <>Deze offerte is afgewezen{quote.decidedAt ? ` op ${utcDateTime(quote.decidedAt)}` : ''}.</>
          : state === 'ingetrokken' ? <>Deze offerte is ingetrokken en geldt niet meer.</>
          : state === 'verlopen' ? <>Deze offerte was geldig tot en met {date(quote.validUntil)}.</>
          : <>Deze offerte is geldig tot en met {date(quote.validUntil)}. Akkoord geven kan online, via de persoonlijke link naar deze offerte.</>}
      </div>

      <PaperFoot seller={seller} />
    </div>
  )
}
