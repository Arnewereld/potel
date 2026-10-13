import { Fragment } from 'react'
import type { Customer, HourSpec, Invoice, Settings } from '../lib/types'
import { date, euro, num } from '../lib/format'
import { buyerFrom, sellerFrom } from '../lib/invoice'
import { PaperFoot, PaperHead } from './InvoicePaper'

type Draft = Omit<Invoice, 'id'>

// De urenspecificatie als extra pagina na de factuur: per project de boekingen, met subtotalen en een totaal dat gelijk is
// aan de urenregels op de factuur. In de PDF begint hij op een nieuwe A4 en komt de kop van de tabel op elke pagina terug.
export function HourSpecPaper({ spec, invoice, customer, company }: { spec: HourSpec; invoice: Draft; customer?: Customer | null; company: Settings }) {
  const seller = invoice.seller ?? sellerFrom(company)
  const buyer = invoice.buyer ?? (customer ? buyerFrom(customer) : null)
  const columns = 3 + (spec.showWho ? 1 : 0) + (spec.showAmounts ? 1 : 0)
  const label = spec.showWho ? 3 : 2

  return (
    <div className="invoice-paper hours-paper" style={{ '--accent': company.brandColor || '#ff6d5a' } as React.CSSProperties}>
      <PaperHead seller={seller} company={company} title="Urenspecificatie" />

      <div className="paper-meta" style={{ marginTop: 28 }}>
        <div><span>Bij factuur</span><strong>{invoice.number ?? 'Concept'}</strong></div>
        <div><span>Datum</span><strong>{date(invoice.issueDate)}</strong></div>
        {buyer && <div><span>Klant</span><strong>{buyer.name}</strong></div>}
        <div><span>Totaal uren</span><strong>{num(spec.hours)}</strong></div>
      </div>

      <table className="paper-table hours-table">
        <thead>
          <tr>
            <th>Datum</th>
            <th>Omschrijving</th>
            {spec.showWho && <th>Wie</th>}
            <th className="num">Uren</th>
            {spec.showAmounts && <th className="num">Bedrag</th>}
          </tr>
        </thead>
        <tbody>
          {spec.projects.map(p => (
            <Fragment key={p.name}>
              <tr className="hours-project"><td colSpan={columns}>{p.name}</td></tr>
              {p.entries.map((e, i) => (
                <tr key={i}>
                  <td className="nowrap">{date(e.date)}</td>
                  <td>{e.description || <span className="paper-muted">Geen omschrijving</span>}</td>
                  {spec.showWho && <td>{e.who ?? ''}</td>}
                  <td className="num">{num(e.hours)}</td>
                  {spec.showAmounts && <td className="num nowrap">{e.amount != null ? euro(e.amount) : ''}</td>}
                </tr>
              ))}
              <tr className="hours-subtotal">
                <td colSpan={label}>Subtotaal {p.name}</td>
                <td className="num">{num(p.hours)}</td>
                {spec.showAmounts && <td className="num nowrap">{p.amount != null ? euro(p.amount) : ''}</td>}
              </tr>
            </Fragment>
          ))}
          {/* Geen tfoot: die herhaalt de browser in de PDF op elke pagina. */}
          <tr className="hours-total">
            <td colSpan={label}>Totaal</td>
            <td className="num">{num(spec.hours)}</td>
            {spec.showAmounts && <td className="num nowrap">{spec.amount != null ? euro(spec.amount) : ''}</td>}
          </tr>
        </tbody>
      </table>

      <p className="paper-notes">
        De uren op deze specificatie tellen op tot de urenregels op factuur {invoice.number ?? '(concept)'}.
        {spec.showAmounts ? ' De bedragen zijn zonder btw.' : ''}
      </p>

      <PaperFoot seller={seller} />
    </div>
  )
}
