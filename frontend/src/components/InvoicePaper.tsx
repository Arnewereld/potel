import type { Customer, Invoice, InvoiceParty, Language, Settings } from '../lib/types'
import { invoiceTotals, lineAmount } from '../lib/format'
import { buyerFrom, isCredit, isNetherlands, sellerFrom, zeroVat } from '../lib/invoice'
import { docCountry, docDate, docEuro, docNum, docUnit, isEnglish, t } from '../lib/docTexts'

type Draft = Omit<Invoice, 'id'>

// De factuur zoals hij op papier en in de PDF staat. Een verstuurde factuur toont de gegevens van jou en je klant
// zoals ze bij versturen waren; een concept de huidige. Logo en accentkleur komen altijd uit je instellingen.
// Alles wat er niet door jou is ingetypt, staat in de taal van de factuur.
export function InvoicePaper({ invoice, customer, company }: { invoice: Draft; customer?: Customer | null; company: Settings }) {
  const lang = invoice.language ?? 'nl'
  const euro = (n: number) => docEuro(lang, n)
  const date = (s?: string | null) => docDate(lang, s)
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
    <div className="invoice-paper" lang={lang} style={{ '--accent': company.brandColor || '#ff6d5a' } as React.CSSProperties}>
      <PaperHead seller={seller} company={company} title={t(lang, credit ? 'invoice.creditTitle' : 'invoice.title')} />

      <PaperParties lang={lang} seller={seller} buyer={buyer} empty={t(lang, 'paper.noCustomer')} />

      <div className="paper-meta">
        <div><span>{t(lang, credit ? 'invoice.creditNumber' : 'invoice.number')}</span><strong>{invoice.number ?? t(lang, 'paper.draft')}</strong></div>
        <div><span>{t(lang, 'paper.date')}</span><strong>{date(invoice.issueDate)}</strong></div>
        <div>
          <span>{t(lang, period ? 'invoice.period' : 'invoice.deliveryDate')}</span>
          <strong>{period ? t(lang, 'invoice.periodRange', { from: date(from), to: date(to) }) : date(from)}</strong>
        </div>
        {!credit && <div><span>{t(lang, 'invoice.dueDate')}</span><strong>{date(invoice.dueDate)}</strong></div>}
        {invoice.reference && <div><span>{t(lang, 'invoice.reference')}</span><strong>{invoice.reference}</strong></div>}
      </div>

      {invoice.creditForNumber && (
        <p className="paper-credit-ref">
          {invoice.creditForIssueDate
            ? t(lang, 'invoice.creditForOn', { number: invoice.creditForNumber, date: date(invoice.creditForIssueDate) })
            : t(lang, 'invoice.creditFor', { number: invoice.creditForNumber })}
        </p>
      )}

      <PaperLines lang={lang} lines={invoice.lines} showVat={showVat} />

      <div className="paper-summary">
        <div className="paper-summary-row">
          {invoice.status === 'betaald' && <div className="paper-stamp">{t(lang, 'invoice.paid')}{invoice.paidAt ? ` · ${date(invoice.paidAt)}` : ''}</div>}
          <div className="paper-totals">
            <PaperVatRows lang={lang} regime={regime} totals={totals} />
            <div className="paper-total"><span>{t(lang, credit ? 'invoice.totalCredit' : 'invoice.toPay')}</span><span>{euro(totals.total)}</span></div>
          </div>
        </div>
        {!credit && totals.total > 0 && invoice.status !== 'betaald' && (
          <div className="paper-pay">
            <div><span>{t(lang, 'invoice.payAmount')}</span><strong>{euro(totals.total)}</strong></div>
            <div><span>{t(lang, 'invoice.payBy')}</span><strong>{date(invoice.dueDate)}</strong></div>
            {seller.iban && <div><span>{t(lang, 'invoice.payAccount')}</span><strong>{seller.iban}</strong></div>}
            {seller.iban && <div><span>{t(lang, 'invoice.payHolder')}</span><strong>{seller.name}</strong></div>}
            <div><span>{t(lang, 'invoice.payReference')}</span><strong>{invoice.number ?? t(lang, 'invoice.payReferenceLater')}</strong></div>
          </div>
        )}
        {credit && <p className="paper-notes" style={{ margin: 0 }}>{t(lang, 'invoice.creditSettled', { amount: euro(Math.abs(totals.total)) })}</p>}
      </div>

      {regime === 'verlegd' && (
        <p className="paper-reverse">
          {[
            t(lang, 'invoice.noteReverse'),
            seller.vatNumber && t(lang, 'invoice.noteSupplierVat', { number: seller.vatNumber }),
            buyer?.vatNumber && t(lang, 'invoice.noteCustomerVat', { number: buyer.vatNumber }),
          ].filter(Boolean).join(' ')}
        </p>
      )}
      {regime === 'kor' && <p className="paper-reverse">{t(lang, 'invoice.noteKor', { seller: seller.name })}</p>}
      {regime === 'vrijgesteld' && <p className="paper-reverse">{t(lang, 'invoice.noteExempt')}</p>}
      {regime === 'buiten-eu' && <p className="paper-reverse">{t(lang, 'invoice.noteOutsideEu')}</p>}
      {invoice.notes && <p className="paper-notes">{invoice.notes}</p>}

      <PaperFoot seller={seller} lang={lang} />
    </div>
  )
}

// Aan en van: je klant met zijn adres (en land als hij in het buitenland zit), en jij. Op een Engels document staat bij jou
// ook Nederland, want je klant zit dan meestal niet in Nederland.
export function PaperParties({ lang, seller, buyer, empty }: { lang: Language; seller: InvoiceParty; buyer: InvoiceParty | null; empty: string }) {
  return (
    <div className="paper-parties">
      <div>
        <div className="paper-label">{t(lang, 'paper.to')}</div>
        {buyer ? (<>
          <strong>{buyer.name}</strong>
          {buyer.contact && <div>{t(lang, 'paper.attn', { name: buyer.contact })}</div>}
          {buyer.address && <div>{buyer.address}</div>}
          {buyer.city && <div>{buyer.city}</div>}
          {buyer.country && !isNetherlands(buyer.country) && <div>{docCountry(lang, buyer.country)}</div>}
          {buyer.vatNumber && <div className="paper-muted">{t(lang, 'paper.vatNo', { number: buyer.vatNumber })}</div>}
        </>) : <span className="paper-muted">{empty}</span>}
      </div>
      <div>
        <div className="paper-label">{t(lang, 'paper.from')}</div>
        <strong>{seller.name}</strong>
        {seller.contact && <div>{seller.contact}</div>}
        {seller.address && <div>{seller.address}</div>}
        {seller.city && <div>{seller.city}</div>}
        {isEnglish(lang) && <div>{docCountry(lang, seller.country || 'Nederland')}</div>}
        {seller.email && <div>{seller.email}</div>}
        {seller.phone && <div>{seller.phone}</div>}
      </div>
    </div>
  )
}

type PaperLine = Pick<Draft['lines'][number], 'description' | 'quantity' | 'unit' | 'unitPrice' | 'vatRate'>

// De regels. Omschrijvingen staan er zoals je ze typte; eenheden, getallen en bedragen in de taal van het document.
export function PaperLines({ lang, lines, showVat }: { lang: Language; lines: PaperLine[]; showVat: boolean }) {
  return (
    <table className="paper-table">
      <thead>
        <tr>
          <th>{t(lang, 'paper.description')}</th><th className="num">{t(lang, 'paper.quantity')}</th><th className="num">{t(lang, 'paper.rate')}</th>
          {showVat && <th className="num">{t(lang, 'paper.vat')}</th>}<th className="num">{t(lang, 'paper.amount')}</th>
        </tr>
      </thead>
      <tbody>
        {lines.map((l, i) => (
          <tr key={i}>
            <td>{l.description || <span className="paper-muted">…</span>}</td>
            <td className="num nowrap">{docNum(lang, l.quantity)} {docUnit(lang, l.unit, Number(l.quantity))}</td>
            <td className="num nowrap">{docEuro(lang, l.unitPrice)}</td>
            {showVat && <td className="num">{docNum(lang, l.vatRate)}%</td>}
            <td className="num nowrap">{docEuro(lang, lineAmount(l))}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

// Subtotaal en btw zoals de regeling het wil: btw per tarief, verlegd, niet van toepassing, of (KOR, vrijgesteld) niets.
export function PaperVatRows({ lang, regime, totals }: { lang: Language; regime: Draft['vatRegime']; totals: ReturnType<typeof invoiceTotals> }) {
  const euro = (n: number) => docEuro(lang, n)
  return (<>
    {regime !== 'kor' && regime !== 'vrijgesteld' && <div><span>{t(lang, 'paper.subtotal')}</span><span>{euro(totals.subtotal)}</span></div>}
    {regime === 'normaal' && totals.vatGroups.map(g => (
      <div key={g.rate}><span>{t(lang, 'paper.vatLine', { rate: docNum(lang, g.rate), base: euro(g.base) })}</span><span>{euro(g.vat)}</span></div>
    ))}
    {regime === 'verlegd' && <div><span>{t(lang, 'paper.vatReversed')}</span><span>{euro(0)}</span></div>}
    {regime === 'buiten-eu' && <div><span>{t(lang, 'paper.vatNotApplicable')}</span><span>{euro(0)}</span></div>}
  </>)
}


// Kop en voet van elk document op papier: logo en naam, de soort document, en onderaan je bedrijfsgegevens.
export function PaperHead({ seller, company, title }: { seller: InvoiceParty; company: Settings; title: string }) {
  return (
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
      <h2 className="paper-doc">{title}</h2>
    </header>
  )
}

export function PaperFoot({ seller, lang }: { seller: InvoiceParty; lang: Language }) {
  return (
    <footer className="paper-foot">
      {[
        seller.name, seller.kvk && t(lang, 'paper.footKvk', { number: seller.kvk }), seller.vatNumber && t(lang, 'paper.footVat', { number: seller.vatNumber }),
        seller.iban && t(lang, 'paper.footIban', { number: seller.iban }), seller.email,
      ].filter(Boolean).join('  ·  ')}
    </footer>
  )
}
