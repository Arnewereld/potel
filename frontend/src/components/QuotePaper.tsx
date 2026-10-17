import type { InvoiceParty, Quote, QuoteState, Settings } from '../lib/types'
import { invoiceTotals } from '../lib/format'
import { sellerFrom, zeroVat } from '../lib/invoice'
import { depositAmount } from '../lib/quote'
import { docDate, docDateTime, docEuro, docNum, t } from '../lib/docTexts'
import { PaperFoot, PaperHead, PaperLines, PaperParties, PaperVatRows } from './InvoicePaper'

type Paper = Pick<Quote, 'number' | 'version' | 'issueDate' | 'validUntil' | 'vatRegime' | 'subject' | 'intro' | 'terms' | 'depositPercent' | 'lines'
  | 'seller' | 'decidedAt' | 'decidedName' | 'language'> & { previousNumber?: string | null }

// De offerte zoals hij op papier, in de PDF en op de openbare pagina staat. Een verstuurde offerte toont de gegevens van
// jou en je klant zoals ze bij versturen waren; een concept de huidige. Logo en accentkleur komen uit je instellingen.
// Alles wat er niet door jou is ingetypt, staat in de taal van de offerte.
export function QuotePaper({ quote, buyer, state, company }: { quote: Paper; buyer: InvoiceParty | null; state: QuoteState; company: Settings }) {
  const lang = quote.language ?? 'nl'
  const euro = (n: number) => docEuro(lang, n)
  const date = (s?: string | null) => docDate(lang, s)
  const totals = invoiceTotals(quote.lines)
  const seller = quote.seller ?? sellerFrom(company)
  const regime = quote.vatRegime ?? 'normaal'
  const showVat = !zeroVat(regime)
  const deposit = depositAmount(quote)
  const [acceptedBefore, acceptedAfter] = t(lang, quote.decidedAt ? 'quote.acceptedByOn' : 'quote.acceptedBy',
    { when: quote.decidedAt ? docDateTime(lang, quote.decidedAt) : '' }).split('{name}')

  return (
    <div className="invoice-paper quote-paper" lang={lang} style={{ '--accent': company.brandColor || '#ff6d5a' } as React.CSSProperties}>
      <PaperHead seller={seller} company={company} title={t(lang, 'quote.title')} />

      <PaperParties lang={lang} seller={seller} buyer={buyer} empty={t(lang, 'paper.noCustomerOrLead')} />

      <div className="paper-meta">
        <div><span>{t(lang, 'quote.number')}</span><strong>{quote.number ?? t(lang, 'paper.draft')}</strong></div>
        <div><span>{t(lang, 'paper.date')}</span><strong>{date(quote.issueDate)}</strong></div>
        <div><span>{t(lang, 'quote.validUntil')}</span><strong>{date(quote.validUntil)}</strong></div>
        {quote.version > 1 && (
          <div>
            <span>{t(lang, 'quote.version')}</span>
            <strong>{quote.previousNumber ? t(lang, 'quote.replaces', { version: quote.version, number: quote.previousNumber }) : quote.version}</strong>
          </div>
        )}
      </div>

      {quote.subject && <h3 className="quote-subject">{quote.subject}</h3>}
      {quote.intro && <p className="quote-text">{quote.intro}</p>}

      <PaperLines lang={lang} lines={quote.lines} showVat={showVat} />

      <div className="paper-summary">
        <div className="paper-summary-row">
          {state === 'geaccepteerd' && <div className="paper-stamp">{t(lang, 'quote.accepted')}{quote.decidedAt ? ` · ${date(quote.decidedAt)}` : ''}</div>}
          {state === 'ingetrokken' && <div className="paper-stamp paper-stamp-muted">{t(lang, 'quote.withdrawn')}</div>}
          <div className="paper-totals">
            <PaperVatRows lang={lang} regime={regime} totals={totals} />
            <div className="paper-total"><span>{t(lang, 'quote.total')}</span><span>{euro(totals.total)}</span></div>
          </div>
        </div>
        {deposit && (
          <p className="paper-notes" style={{ margin: 0 }}>
            {t(lang, 'quote.deposit', {
              percent: docNum(lang, quote.depositPercent!),
              amount: showVat ? t(lang, 'paper.inclVat', { amount: euro(deposit.total) }) : euro(deposit.total),
            })}
          </p>
        )}
      </div>

      {regime === 'verlegd' && <p className="paper-reverse">{t(lang, 'quote.noteReverse')}</p>}
      {regime === 'kor' && <p className="paper-reverse">{t(lang, 'quote.noteKor', { seller: seller.name })}</p>}
      {regime === 'vrijgesteld' && <p className="paper-reverse">{t(lang, 'quote.noteExempt')}</p>}
      {regime === 'buiten-eu' && <p className="paper-reverse">{t(lang, 'invoice.noteOutsideEu')}</p>}

      {quote.terms && (<>
        <div className="paper-label quote-terms-label">{t(lang, 'quote.terms')}</div>
        <p className="quote-text quote-terms">{quote.terms}</p>
      </>)}

      <div className="quote-accept">
        {state === 'geaccepteerd' && quote.decidedName
          ? <>{acceptedBefore}<strong>{quote.decidedName}</strong>{acceptedAfter}</>
          : state === 'afgewezen' ? <>{quote.decidedAt ? t(lang, 'quote.rejectedOn', { when: docDateTime(lang, quote.decidedAt) }) : t(lang, 'quote.rejectedText')}</>
          : state === 'ingetrokken' ? <>{t(lang, 'quote.withdrawnText')}</>
          : state === 'verlopen' ? <>{t(lang, 'quote.expiredText', { date: date(quote.validUntil) })}</>
          : <>{t(lang, 'quote.openText', { date: date(quote.validUntil) })}</>}
      </div>

      <PaperFoot seller={seller} lang={lang} />
    </div>
  )
}
