import { Fragment } from 'react'
import type { Customer, HourSpec, Invoice, Settings } from '../lib/types'
import { buyerFrom, sellerFrom } from '../lib/invoice'
import { docDate, docEuro, docNum, t } from '../lib/docTexts'
import { PaperFoot, PaperHead } from './InvoicePaper'

type Draft = Omit<Invoice, 'id'>

// De urenspecificatie als extra pagina na de factuur: per project de boekingen, met subtotalen en een totaal dat gelijk is
// aan de urenregels op de factuur. In de PDF begint hij op een nieuwe A4 en komt de kop van de tabel op elke pagina terug.
// In de taal van de factuur, net als de factuur zelf.
export function HourSpecPaper({ spec, invoice, customer, company }: { spec: HourSpec; invoice: Draft; customer?: Customer | null; company: Settings }) {
  const lang = invoice.language ?? 'nl'
  const euro = (n: number) => docEuro(lang, n)
  const date = (s?: string | null) => docDate(lang, s)
  const num = (n: number) => docNum(lang, n)
  const seller = invoice.seller ?? sellerFrom(company)
  const buyer = invoice.buyer ?? (customer ? buyerFrom(customer) : null)
  const columns = 3 + (spec.showWho ? 1 : 0) + (spec.showAmounts ? 1 : 0)
  const label = spec.showWho ? 3 : 2

  return (
    <div className="invoice-paper hours-paper" lang={lang} style={{ '--accent': company.brandColor || '#ff6d5a' } as React.CSSProperties}>
      <PaperHead seller={seller} company={company} title={t(lang, 'hours.title')} />

      <div className="paper-meta" style={{ marginTop: 28 }}>
        <div><span>{t(lang, 'hours.forInvoice')}</span><strong>{invoice.number ?? t(lang, 'paper.draft')}</strong></div>
        <div><span>{t(lang, 'paper.date')}</span><strong>{date(invoice.issueDate)}</strong></div>
        {buyer && <div><span>{t(lang, 'hours.customer')}</span><strong>{buyer.name}</strong></div>}
        <div><span>{t(lang, 'hours.totalHours')}</span><strong>{num(spec.hours)}</strong></div>
      </div>

      <table className="paper-table hours-table">
        <thead>
          <tr>
            <th>{t(lang, 'paper.date')}</th>
            <th>{t(lang, 'paper.description')}</th>
            {spec.showWho && <th>{t(lang, 'hours.who')}</th>}
            <th className="num">{t(lang, 'hours.hours')}</th>
            {spec.showAmounts && <th className="num">{t(lang, 'paper.amount')}</th>}
          </tr>
        </thead>
        <tbody>
          {spec.projects.map(p => (
            <Fragment key={p.name}>
              <tr className="hours-project"><td colSpan={columns}>{p.name}</td></tr>
              {p.entries.map((e, i) => (
                <tr key={i}>
                  <td className="nowrap">{date(e.date)}</td>
                  <td>{e.description || <span className="paper-muted">{t(lang, 'hours.noDescription')}</span>}</td>
                  {spec.showWho && <td>{e.who ?? ''}</td>}
                  <td className="num">{num(e.hours)}</td>
                  {spec.showAmounts && <td className="num nowrap">{e.amount != null ? euro(e.amount) : ''}</td>}
                </tr>
              ))}
              <tr className="hours-subtotal">
                <td colSpan={label}>{t(lang, 'hours.subtotal', { project: p.name })}</td>
                <td className="num">{num(p.hours)}</td>
                {spec.showAmounts && <td className="num nowrap">{p.amount != null ? euro(p.amount) : ''}</td>}
              </tr>
            </Fragment>
          ))}
          {/* Geen tfoot: die herhaalt de browser in de PDF op elke pagina. */}
          <tr className="hours-total">
            <td colSpan={label}>{t(lang, 'hours.total')}</td>
            <td className="num">{num(spec.hours)}</td>
            {spec.showAmounts && <td className="num nowrap">{spec.amount != null ? euro(spec.amount) : ''}</td>}
          </tr>
        </tbody>
      </table>

      <p className="paper-notes">
        {t(lang, 'hours.note', { number: invoice.number ?? t(lang, 'hours.draftNumber') })}
        {spec.showAmounts ? ` ${t(lang, 'hours.amountsExclVat')}` : ''}
      </p>

      <PaperFoot seller={seller} lang={lang} />
    </div>
  )
}
