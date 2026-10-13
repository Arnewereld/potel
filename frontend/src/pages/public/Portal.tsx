import { useEffect, useRef, useState } from 'react'
import { CalendarClock, Clock, CreditCard, ExternalLink, FolderKanban, Link2Off, Receipt } from 'lucide-react'
import { api } from '../../lib/api'
import { date, euro, hm, hours, initials, num } from '../../lib/format'
import { product } from '../../lib/plans'
import { payWithIdeal } from '../../lib/ideal'
import type { PortalInvoice, PortalProject, PublicPortal } from '../../lib/types'
import { Badge } from '../../components/ui'
import { usePrivatePage } from '../../lib/privatePage'

// Het klantportaal achter de geheime link: de klant ziet zonder inloggen zijn eigen lopende projecten, recente uren en
// facturen, en verder niets uit de werkruimte dan de naam, het logo en de kleur van de freelancer. Het token staat achter
// de #, zodat het niet bij de server of in logboeken belandt; het blijft in de adresbalk staan voor een volgende keer.
export function PortalPage() {
  usePrivatePage()
  const [token] = useState(() => new URLSearchParams(window.location.hash.slice(1)).get('token') ?? '')
  const [data, setData] = useState<PublicPortal | null>(null)
  const [error, setError] = useState<string | null>(token ? null : 'Deze link is niet compleet. Kopieer de hele link, of vraag je contactpersoon om een nieuwe link.')
  const started = useRef(false)

  useEffect(() => {
    if (!token || started.current) return
    started.current = true
    api.post<PublicPortal>('/public/portal', { token })
      .then(d => {
        setData(d)
        document.title = `${d.customer.name} · ${d.company.name || product.name}`
      })
      .catch(e => setError((e as Error).message))
  }, [token])

  if (error) return (
    <div className="login login-single">
      <div className="login-card">
        <h1><Link2Off size={22} /> Portaal niet gevonden</h1>
        <p className="muted" style={{ margin: '6px 0 0' }}>{error}</p>
      </div>
    </div>
  )
  if (!data) return <div className="portal"><div className="portal-inner muted">Portaal laden…</div></div>

  const { company, visible } = data
  const invoices = data.invoices ?? []
  const open = invoices.reduce((a, i) => a + i.openAmount, 0)
  const what = [visible.hours && 'welke uren er zijn gewerkt', visible.invoices && 'welke facturen er zijn'].filter(Boolean)

  return (
    <div className="portal" style={{ '--brand': company.brandColor } as React.CSSProperties}>
      <div className="portal-inner">
        <header className="card portal-head">
          {company.logoDataUrl
            ? <img className="portal-logo" src={company.logoDataUrl} alt={company.name} />
            : <div className="avatar avatar-lg" style={{ background: company.brandColor }}>{initials(company.name || product.name)}</div>}
          <div className="grow">
            <div className="portal-kicker">{company.name}</div>
            <h1>Klantportaal voor {data.customer.name}</h1>
            <p className="muted">
              Hier zie je hoe je projecten ervoor staan{what.length ? `, ${what.join(' en ')}` : ''}. Deze pagina is alleen voor jou; deel de link niet met anderen.
            </p>
          </div>
        </header>

        {data.note && <div className="card portal-note"><div className="card-body">{data.note}</div></div>}

        <section>
          <h2 className="portal-title"><FolderKanban size={17} /> Lopende projecten</h2>
          {data.projects.length === 0
            ? <div className="card"><div className="card-body muted">Er lopen op dit moment geen projecten.</div></div>
            : <div className="portal-projects">{data.projects.map(p => <ProjectCard key={p.name} project={p} />)}</div>}
        </section>

        {visible.hours && (
          <section>
            <h2 className="portal-title"><Clock size={17} /> Recente uren</h2>
            <div className="card">
              {!data.entries?.length
                ? <div className="card-body muted">Er zijn de afgelopen drie maanden geen uren geboekt.</div>
                : (
                  <div className="portal-list">
                    {data.entries.map((e, i) => (
                      <div key={i} className="portal-row">
                        <div className="portal-date">{date(e.date)}</div>
                        <div className="grow">
                          <div className="portal-text">{e.description || <span className="muted">Geen omschrijving</span>}</div>
                          <div className="cell-sub">{e.project}</div>
                        </div>
                        <div className="portal-amount">
                          <strong>{hm(e.minutes)}</strong>
                          {e.amount != null && <div className="cell-sub">{euro(e.amount)}</div>}
                        </div>
                      </div>
                    ))}
                  </div>
                )}
            </div>
          </section>
        )}

        {visible.invoices && (
          <section>
            <h2 className="portal-title"><Receipt size={17} /> Facturen</h2>
            {open > 0 && <div className="notice" style={{ margin: '0 0 10px' }}>Er staat in totaal {euro(open)} open.</div>}
            <div className="card">
              {invoices.length === 0
                ? <div className="card-body muted">Er zijn nog geen facturen.</div>
                : <div className="portal-list">{invoices.map((inv, i) => <InvoiceRow key={inv.number ?? i} invoice={inv} />)}</div>}
            </div>
          </section>
        )}

        <footer className="portal-foot">
          {data.expiresAt && <div>Deze link werkt tot {date(data.expiresAt)}.</div>}
          <div>Powered by <a href="/" target="_blank" rel="noreferrer">{product.name}</a></div>
        </footer>
      </div>
    </div>
  )
}

function ProjectCard({ project: p }: { project: PortalProject }) {
  const used = p.minutes ?? 0
  const budget = p.budgetHours ? p.budgetHours * 60 : null
  const weeks = p.weeks ?? []
  const average = weeks.length ? weeks.reduce((a, w) => a + w.minutes, 0) / weeks.length : 0
  const max = Math.max(...weeks.map(w => w.minutes), 1)
  const left = budget !== null ? budget - used : 0
  const pct = budget ? used / budget : 0

  return (
    <div className="card portal-project">
      <div className="row between" style={{ alignItems: 'flex-start' }}>
        <h3>{p.name}</h3>
        <Badge tone="gray">{p.billing === 'uur' ? 'Per uur' : 'Vaste prijs'}</Badge>
      </div>

      {budget !== null && p.minutes != null && (
        <div>
          <div className="row between"><span>{num(used / 60)} van {num(p.budgetHours!)} uur</span><strong>{Math.round(pct * 100)}%</strong></div>
          <div className="progress" style={{ '--accent': pct > 1 ? 'var(--red)' : 'var(--brand)' } as React.CSSProperties}>
            <div style={{ width: `${Math.min(100, pct * 100)}%` }} />
          </div>
          <div className="cell-sub" style={{ marginTop: 6 }}>
            {left > 0
              ? `Nog ${hours(left)} over${average > 0 ? `. Bij het tempo van de laatste weken is het budget over ongeveer ${Math.max(1, Math.ceil(left / average))} weken op.` : '.'}`
              : left === 0 ? 'Het budget is precies op.' : `Er is ${hours(-left)} meer gewerkt dan het budget.`}
          </div>
        </div>
      )}
      {budget === null && p.minutes != null && <div>Gewerkt: <strong>{hours(used)}</strong></div>}

      {p.weeks && (
        <div>
          <div className="row between cell-sub"><span>Uren per week</span><span>gemiddeld {hours(average)}</span></div>
          <div className="portal-bars" role="img"
            aria-label={weeks.map(w => `week van ${date(w.start)}: ${hours(w.minutes)}`).join(', ')}>
            {weeks.map(w => (
              <div key={w.start} className="portal-bar" title={`Week van ${date(w.start)}: ${hours(w.minutes)}`}>
                <div style={{ height: `${(w.minutes / max) * 100}%` }} />
              </div>
            ))}
          </div>
          {weeks.length > 0 && <div className="row between cell-sub"><span>{date(weeks[0]!.start)}</span><span>deze week</span></div>}
        </div>
      )}

      {(p.hourlyRate != null || p.fixedPrice != null) && (
        <div className="cell-sub">
          {p.hourlyRate != null ? `${euro(p.hourlyRate)} per uur, tot nu toe ${euro(p.value ?? 0)} excl. btw` : `Vaste prijs ${euro(p.fixedPrice!)} excl. btw`}
        </div>
      )}
      {p.deadline && <div className="cell-sub row" style={{ gap: 6 }}><CalendarClock size={13} /> Deadline {date(p.deadline)}</div>}
    </div>
  )
}

// Hoe een factuur er voor de klant voor staat, in zijn eigen woorden.
function invoiceState(inv: PortalInvoice): { label: string; tone: string } {
  if (inv.isCredit) return { label: 'Creditnota', tone: 'gray' }
  if (inv.status === 'betaald') return { label: 'Betaald', tone: 'green' }
  if (inv.fullyCredited) return { label: 'Gecrediteerd', tone: 'gray' }
  if (inv.status === 'verlopen') return { label: 'Te laat', tone: 'red' }
  return { label: 'Open', tone: 'blue' }
}

function InvoiceRow({ invoice: inv }: { invoice: PortalInvoice }) {
  const state = invoiceState(inv)
  const unpaid = !inv.isCredit && inv.openAmount > 0
  const [paying, setPaying] = useState(false)
  const [payError, setPayError] = useState<string | null>(null)
  // Betalen gaat met het token van de factuurlink; het bedrag bepaalt de server.
  const token = inv.link ? new URLSearchParams(inv.link.split('#')[1] ?? '').get('token') : null
  const pay = async () => {
    if (!token) return
    setPaying(true)
    setPayError(null)
    try {
      await payWithIdeal(token)
    } catch (e) {
      setPayError((e as Error).message)
      setPaying(false)
    }
  }
  return (
    <div className="portal-row" style={payError ? { flexWrap: 'wrap' } : undefined}>
      <div className="grow">
        <div><strong>{inv.isCredit ? 'Creditnota' : 'Factuur'} {inv.number}</strong></div>
        <div className="cell-sub">{date(inv.issueDate)}{unpaid ? `, te betalen uiterlijk ${date(inv.dueDate)}` : ''}</div>
      </div>
      <Badge tone={state.tone}>{state.label}</Badge>
      <div className="portal-amount">
        <strong>{euro(inv.total)}</strong>
        {unpaid && inv.openAmount < inv.total && <div className="cell-sub">nog {euro(inv.openAmount)}</div>}
      </div>
      {inv.canPay && token && <button className="btn btn-sm btn-primary" disabled={paying} onClick={pay}><CreditCard size={13} /> {paying ? 'Even geduld…' : 'Betaal nu met iDEAL'}</button>}
      {inv.link && <a className="btn btn-sm" href={inv.link} target="_blank" rel="noreferrer"><ExternalLink size={13} /> Bekijken</a>}
      {payError && <div className="text-red" style={{ flexBasis: '100%', fontSize: 13 }}>{payError}</div>}
    </div>
  )
}
