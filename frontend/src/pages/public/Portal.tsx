import { useEffect, useRef, useState } from 'react'
import { CalendarClock, Clock, CreditCard, ExternalLink, FolderKanban, Link2Off, Receipt } from 'lucide-react'
import { api } from '../../lib/api'
import { hm, initials } from '../../lib/format'
import { product } from '../../lib/plans'
import { payWithIdeal } from '../../lib/ideal'
import type { Language, PortalInvoice, PortalProject, PublicPortal } from '../../lib/types'
import { Badge } from '../../components/ui'
import { usePrivatePage } from '../../lib/privatePage'
import { docDate, docEuro, docHours, docNum, t } from '../../lib/docTexts'
import { LinkProblem } from './LinkProblem'
import { linkError, type LinkError } from '../../lib/linkError'

// Het klantportaal achter de geheime link: de klant ziet zonder inloggen zijn eigen lopende projecten, recente uren en
// facturen, en verder niets uit de werkruimte dan de naam, het logo en de kleur van de freelancer. Het token staat achter
// de #, zodat het niet bij de server of in logboeken belandt; het blijft in de adresbalk staan voor een volgende keer.
// Het portaal spreekt de taal van de klant.
export function PortalPage() {
  usePrivatePage()
  const [token] = useState(() => new URLSearchParams(window.location.hash.slice(1)).get('token') ?? '')
  const [data, setData] = useState<PublicPortal | null>(null)
  const [error, setError] = useState<LinkError | null>(token ? null : { kind: 'incomplete' })
  const started = useRef(false)

  useEffect(() => {
    if (!token || started.current) return
    started.current = true
    api.post<PublicPortal>('/public/portal', { token })
      .then(d => {
        setData(d)
        document.title = `${d.customer.name} · ${d.company.name || product.name}`
      })
      .catch(e => setError(linkError(e)))
  }, [token])

  if (error) return <LinkProblem icon={<Link2Off size={22} />} title="portal.notFound" gone="portal.gone" incomplete="portal.linkIncomplete" error={error} />
  if (!data) return <div className="portal"><div className="portal-inner muted">{t('nl', 'portal.loading')} · {t('en', 'portal.loading')}</div></div>

  const { company, visible } = data
  const lang = data.language ?? 'nl'
  const euro = (n: number) => docEuro(lang, n)
  const date = (s?: string | null) => docDate(lang, s)
  const invoices = data.invoices ?? []
  const open = invoices.reduce((a, i) => a + i.openAmount, 0)
  const what = [visible.hours && t(lang, 'portal.whatHours'), visible.invoices && t(lang, 'portal.whatInvoices')].filter(Boolean)

  return (
    <div className="portal" lang={lang} style={{ '--brand': company.brandColor } as React.CSSProperties}>
      <div className="portal-inner">
        <header className="card portal-head">
          {company.logoDataUrl
            ? <img className="portal-logo" src={company.logoDataUrl} alt={company.name} />
            : <div className="avatar avatar-lg" style={{ background: company.brandColor }}>{initials(company.name || product.name)}</div>}
          <div className="grow">
            <div className="portal-kicker">{company.name}</div>
            <h1>{t(lang, 'portal.title', { name: data.customer.name })}</h1>
            <p className="muted">{t(lang, 'portal.intro', { what: what.length ? `, ${what.join(` ${t(lang, 'portal.and')} `)}` : '' })}</p>
          </div>
        </header>

        {data.note && <div className="card portal-note"><div className="card-body">{data.note}</div></div>}

        <section>
          <h2 className="portal-title"><FolderKanban size={17} /> {t(lang, 'portal.projects')}</h2>
          {data.projects.length === 0
            ? <div className="card"><div className="card-body muted">{t(lang, 'portal.noProjects')}</div></div>
            : <div className="portal-projects">{data.projects.map(p => <ProjectCard key={p.name} project={p} lang={lang} />)}</div>}
        </section>

        {visible.hours && (
          <section>
            <h2 className="portal-title"><Clock size={17} /> {t(lang, 'portal.recentHours')}</h2>
            <div className="card">
              {!data.entries?.length
                ? <div className="card-body muted">{t(lang, 'portal.noHours')}</div>
                : (
                  <div className="portal-list">
                    {data.entries.map((e, i) => (
                      <div key={i} className="portal-row">
                        <div className="portal-date">{date(e.date)}</div>
                        <div className="grow">
                          <div className="portal-text">{e.description || <span className="muted">{t(lang, 'hours.noDescription')}</span>}</div>
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
            <h2 className="portal-title"><Receipt size={17} /> {t(lang, 'portal.invoices')}</h2>
            {open > 0 && <div className="notice" style={{ margin: '0 0 10px' }}>{t(lang, 'portal.openTotal', { amount: euro(open) })}</div>}
            <div className="card">
              {invoices.length === 0
                ? <div className="card-body muted">{t(lang, 'portal.noInvoices')}</div>
                : <div className="portal-list">{invoices.map((inv, i) => <InvoiceRow key={inv.number ?? i} invoice={inv} lang={lang} />)}</div>}
            </div>
          </section>
        )}

        <footer className="portal-foot">
          {data.expiresAt && <div>{t(lang, 'portal.expires', { date: date(data.expiresAt) })}</div>}
          <div>{t(lang, 'portal.poweredBy')} <a href="/" target="_blank" rel="noreferrer">{product.name}</a></div>
        </footer>
      </div>
    </div>
  )
}

function ProjectCard({ project: p, lang }: { project: PortalProject; lang: Language }) {
  const euro = (n: number) => docEuro(lang, n)
  const date = (s?: string | null) => docDate(lang, s)
  const hours = (minutes: number) => docHours(lang, minutes)
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
        <Badge tone="gray">{t(lang, p.billing === 'uur' ? 'portal.hourly' : 'portal.fixed')}</Badge>
      </div>

      {budget !== null && p.minutes != null && (
        <div>
          <div className="row between"><span>{t(lang, 'portal.ofHours', { used: docNum(lang, used / 60), budget: docNum(lang, p.budgetHours!) })}</span><strong>{Math.round(pct * 100)}%</strong></div>
          <div className="progress" style={{ '--accent': pct > 1 ? 'var(--red)' : 'var(--brand)' } as React.CSSProperties}>
            <div style={{ width: `${Math.min(100, pct * 100)}%` }} />
          </div>
          <div className="cell-sub" style={{ marginTop: 6 }}>
            {left > 0
              ? (average > 0
                ? t(lang, 'portal.leftPace', { hours: hours(left), weeks: Math.max(1, Math.ceil(left / average)) })
                : t(lang, 'portal.left', { hours: hours(left) }))
              : left === 0 ? t(lang, 'portal.budgetExact') : t(lang, 'portal.overBudget', { hours: hours(-left) })}
          </div>
        </div>
      )}
      {budget === null && p.minutes != null && <div>{t(lang, 'portal.worked')} <strong>{hours(used)}</strong></div>}

      {p.weeks && (
        <div>
          <div className="row between cell-sub"><span>{t(lang, 'portal.perWeek')}</span><span>{t(lang, 'portal.average', { hours: hours(average) })}</span></div>
          <div className="portal-bars" role="img"
            aria-label={weeks.map(w => t(lang, 'portal.weekOf', { date: date(w.start), hours: hours(w.minutes) })).join(', ')}>
            {weeks.map(w => (
              <div key={w.start} className="portal-bar" title={t(lang, 'portal.weekOf', { date: date(w.start), hours: hours(w.minutes) })}>
                <div style={{ height: `${(w.minutes / max) * 100}%` }} />
              </div>
            ))}
          </div>
          {weeks.length > 0 && <div className="row between cell-sub"><span>{date(weeks[0]!.start)}</span><span>{t(lang, 'portal.thisWeek')}</span></div>}
        </div>
      )}

      {(p.hourlyRate != null || p.fixedPrice != null) && (
        <div className="cell-sub">
          {p.hourlyRate != null
            ? t(lang, 'portal.rate', { rate: euro(p.hourlyRate), value: euro(p.value ?? 0) })
            : t(lang, 'portal.fixedPrice', { amount: euro(p.fixedPrice!) })}
        </div>
      )}
      {p.deadline && <div className="cell-sub row" style={{ gap: 6 }}><CalendarClock size={13} /> {t(lang, 'portal.deadline', { date: date(p.deadline) })}</div>}
    </div>
  )
}

// Hoe een factuur er voor de klant voor staat, in zijn eigen woorden.
function invoiceState(inv: PortalInvoice, lang: Language): { label: string; tone: string } {
  if (inv.isCredit) return { label: t(lang, 'portal.stateCredit'), tone: 'gray' }
  if (inv.status === 'betaald') return { label: t(lang, 'portal.statePaid'), tone: 'green' }
  if (inv.fullyCredited) return { label: t(lang, 'portal.stateCredited'), tone: 'gray' }
  if (inv.status === 'verlopen') return { label: t(lang, 'portal.stateLate'), tone: 'red' }
  return { label: t(lang, 'portal.stateOpen'), tone: 'blue' }
}

function InvoiceRow({ invoice: inv, lang }: { invoice: PortalInvoice; lang: Language }) {
  const euro = (n: number) => docEuro(lang, n)
  const date = (s?: string | null) => docDate(lang, s)
  const state = invoiceState(inv, lang)
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
      await payWithIdeal(token, lang)
    } catch (e) {
      setPayError((e as Error).message)
      setPaying(false)
    }
  }
  return (
    <div className="portal-row" style={payError ? { flexWrap: 'wrap' } : undefined}>
      <div className="grow">
        <div><strong>{t(lang, inv.isCredit ? 'invoice.creditTitle' : 'invoice.title')} {inv.number}</strong></div>
        <div className="cell-sub">{date(inv.issueDate)}{unpaid ? t(lang, 'portal.dueBy', { date: date(inv.dueDate) }) : ''}</div>
      </div>
      <Badge tone={state.tone}>{state.label}</Badge>
      <div className="portal-amount">
        <strong>{euro(inv.total)}</strong>
        {unpaid && inv.openAmount < inv.total && <div className="cell-sub">{t(lang, 'portal.remaining', { amount: euro(inv.openAmount) })}</div>}
      </div>
      {inv.canPay && token && <button className="btn btn-sm btn-primary" disabled={paying} onClick={pay}><CreditCard size={13} /> {paying ? t(lang, 'public.wait') : t(lang, 'public.payShort')}</button>}
      {inv.link && <a className="btn btn-sm" href={inv.link} target="_blank" rel="noreferrer"><ExternalLink size={13} /> {t(lang, 'portal.view')}</a>}
      {payError && <div className="text-red" style={{ flexBasis: '100%', fontSize: 13 }}>{payError}</div>}
    </div>
  )
}
