import { useEffect, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Settings as SettingsIcon, Save, Building, Target, Palette, CreditCard, UserCog, Download, Trash2, Check, Mail } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import { useAuth } from '../lib/auth'
import { useWorkspace } from '../lib/workspace'
import { plans, product, type Plan } from '../lib/plans'
import { contactEmail, usePlatformInfo } from '../lib/platform'
import type { Settings, VatRegime } from '../lib/types'
import { date, euro, toDateInput } from '../lib/format'
import { ErrorBox, Field, Loading, Modal, PageHeader, SubTabs } from '../components/ui'
import { BrandFields } from '../components/BrandFields'
import { InvoicePaper } from '../components/InvoicePaper'

type Tab = 'bedrijf' | 'huisstijl' | 'abonnement' | 'account'

export function SettingsPage() {
  const { data, error, loading, setData } = useApi<Settings>('/settings')
  const [form, setForm] = useState<Settings | null>(null)
  const [busy, setBusy] = useState(false)
  const [params, setParams] = useSearchParams()
  const tab = (params.get('tab') as Tab) || 'bedrijf'
  const { user } = useAuth()
  const toast = useToast()

  useEffect(() => { if (data) setForm(data) }, [data])

  if (error) return <ErrorBox message={error} />
  if (loading || !form) return <Loading />

  // Bedrijfsgegevens, IBAN en huisstijl staan op elke factuur; een medewerker kan ze alleen bekijken.
  const admin = user.role === 'beheerder'
  const text = (k: keyof Settings) => ({ value: (form[k] as string | null) ?? '', disabled: !admin, onChange: (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: e.target.value }) })
  const num = (k: keyof Settings) => ({ type: 'number', min: 0, value: form[k] as number, disabled: !admin, onChange: (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: Number(e.target.value) }) })
  const dirty = JSON.stringify(form) !== JSON.stringify(data)

  const save = async (e?: React.FormEvent) => {
    e?.preventDefault()
    setBusy(true)
    try {
      setData(await api.put<Settings>('/settings', form))
      toast('Instellingen opgeslagen')
    } catch (err) {
      toast((err as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const today = toDateInput(new Date())
  const regime: VatRegime = form.vatRegime === 'kor' || form.vatRegime === 'vrijgesteld' ? form.vatRegime : 'normaal'
  const preview = {
    number: `${new Date().getFullYear()}-0001`, customerId: 0, issueDate: today, dueDate: today, deliveryFrom: today, status: 'concept' as const, vatRegime: regime,
    reference: '', notes: 'Bedankt voor de fijne samenwerking!',
    lines: [{ description: 'Ontwikkeling webapplicatie', quantity: 12, unit: 'uur', unitPrice: form.defaultHourlyRate, vatRate: regime === 'normaal' ? 21 : 0 }],
  }

  return (
    <form onSubmit={save}>
      <PageHeader
        icon={<SettingsIcon size={20} />}
        title="Instellingen"
        subtitle="Je gegevens, huisstijl, abonnement en account."
        actions={admin && (tab === 'bedrijf' || tab === 'huisstijl') && <button className="btn btn-primary" disabled={busy || !dirty}><Save size={15} /> Opslaan</button>}
      />
      <SubTabs<Tab> value={tab} onChange={t => setParams({ tab: t }, { replace: true })} tabs={[
        { id: 'bedrijf', label: 'Bedrijf', icon: <Building size={14} /> },
        { id: 'huisstijl', label: 'Huisstijl', icon: <Palette size={14} /> },
        { id: 'abonnement', label: 'Abonnement', icon: <CreditCard size={14} /> },
        { id: 'account', label: 'Account', icon: <UserCog size={14} /> },
      ]} />

      {!admin && (tab === 'bedrijf' || tab === 'huisstijl') && (
        <p className="muted" style={{ marginTop: 0, fontSize: 13 }}>Alleen een beheerder kan de bedrijfsgegevens en de huisstijl wijzigen.</p>
      )}

      {tab === 'bedrijf' && (
        <div className="grid grid-1-1">
          <div className="card">
            <div className="card-head"><h3><Building size={16} /> Bedrijfsgegevens</h3></div>
            <div className="card-body form-grid">
              <Field label="Bedrijfsnaam *" full><input required {...text('companyName')} /></Field>
              <Field label="Je naam"><input {...text('ownerName')} /></Field>
              <Field label="Website"><input {...text('website')} placeholder="jouwnaam.dev" /></Field>
              <Field label="Adres"><input {...text('address')} /></Field>
              <Field label="Postcode en plaats"><input {...text('city')} /></Field>
              <Field label="E-mail"><input type="email" {...text('email')} /></Field>
              <Field label="Telefoon"><input {...text('phone')} /></Field>
              <Field label="KvK-nummer"><input {...text('kvk')} /></Field>
              <Field label="Btw-identificatienummer"><input {...text('btw')} placeholder="NL000000000B01" /></Field>
              <Field label="IBAN" full><input {...text('iban')} /></Field>
              <Field label="Btw op je facturen" full>
                <select value={regime} disabled={!admin} onChange={e => setForm({ ...form, vatRegime: e.target.value as VatRegime })}>
                  <option value="normaal">Normaal: btw op je facturen</option>
                  <option value="kor">Kleineondernemersregeling (KOR): geen btw</option>
                  <option value="vrijgesteld">Vrijgesteld van btw: geen btw</option>
                </select>
              </Field>
              <p className="muted field-full" style={{ margin: 0, fontSize: 13 }}>
                {regime === 'kor'
                  ? 'Je facturen krijgen geen btw, wel de vermelding dat je de KOR gebruikt. Dat mag alleen als je bij de Belastingdienst bent aangemeld voor de KOR.'
                  : regime === 'vrijgesteld'
                  ? 'Je facturen krijgen geen btw, wel de vermelding dat je werk vrijgesteld is (artikel 11 Wet OB). Dat geldt bijvoorbeeld voor zorg of onderwijs met een CRKBO-registratie. Doe je ook ander werk? Kies dan per factuur "Normaal".'
                  : 'Voor een zakelijke klant in een ander EU-land wordt de btw vanzelf verlegd, en voor een klant buiten de EU staat er geen btw op. Per factuur kun je het nog aanpassen.'}
              </p>
            </div>
          </div>
          <div className="card">
            <div className="card-head"><h3><Target size={16} /> Tarief en doelen</h3></div>
            <div className="card-body form-grid">
              <Field label="Standaard uurtarief (excl. btw)"><input step="0.01" {...num('defaultHourlyRate')} /></Field>
              <Field label="Betaaltermijn in dagen"><input {...num('paymentTermDays')} /></Field>
              <Field label="Uren per week (doel)"><input {...num('weeklyHoursTarget')} /></Field>
              <Field label="Uren per jaar (urencriterium)"><input {...num('yearlyHoursTarget')} /></Field>
              <p className="muted field-full" style={{ margin: 0, fontSize: 13 }}>
                Voor de zelfstandigenaftrek moet je minstens 1.225 uur per jaar aan je onderneming besteden. Het dashboard laat zien hoe je ervoor staat.
              </p>
            </div>
          </div>
        </div>
      )}

      {tab === 'huisstijl' && (
        <div className="grid grid-1-1">
          <div className="card">
            <div className="card-head"><h3><Palette size={16} /> Logo en kleur</h3></div>
            <div className="card-body"><BrandFields value={form} disabled={!admin} onChange={patch => setForm({ ...form, ...patch })} /></div>
          </div>
          <div className="settings-preview"><InvoicePaper invoice={preview} company={form} /></div>
        </div>
      )}

      {tab === 'abonnement' && <Subscription admin={admin} />}
      {tab === 'account' && <Account admin={admin} />}
    </form>
  )
}

interface BillingStatus {
  plans: { id: Plan['id']; name: string; net: number; vat: number; gross: number; vatRate: number }[]
  plan: Plan['id']
  subscriptionActive: boolean
  paidUntil?: string | null
  canceledAt?: string | null
  readOnly?: string | null
  lastPayment?: { status: string; plan: string; sequenceType: string; amount: number; createdAt: string; paidAt?: string | null } | null
}

// Wat Mollie over een betaling zegt, in gewone woorden, voor als je terugkomt van de betaalpagina.
function paymentMessage(status?: string): [string, 'ok' | 'error'] | null {
  if (!status) return null
  if (status === 'paid') return ['Gelukt! Je abonnement is betaald en loopt nu.', 'ok']
  if (status === 'open' || status === 'pending' || status === 'authorized')
    return ['We wachten nog op de bevestiging van je bank. Dat duurt meestal maar even; kijk zo nog eens.', 'ok']
  return ['De betaling is niet gelukt. Er is niets afgeschreven. Je kunt het gewoon opnieuw proberen.', 'error']
}

function Subscription({ admin }: { admin: boolean }) {
  const { workspace, reload } = useWorkspace()
  const email = contactEmail(usePlatformInfo())
  const [params, setParams] = useSearchParams()
  const returning = params.get('betaling') === 'terug'
  // Online betalen via Mollie staat alleen aan als de server een sleutel van Mollie heeft.
  const online = workspace?.onlinePayment === true
  const { data: billing, setData: setBilling } = useApi<BillingStatus>(online && !returning ? '/billing' : null)
  const [busy, setBusy] = useState<string | null>(null)
  const [cancelling, setCancelling] = useState(false)
  const toast = useToast()
  const synced = useRef(false)

  // Terug van de betaalpagina: meteen bij Mollie navragen hoe het ging.
  useEffect(() => {
    if (!online || !returning || synced.current) return
    synced.current = true
    api.post<BillingStatus>('/billing/sync')
      .then(b => {
        setBilling(b)
        const message = paymentMessage(b.lastPayment?.status)
        if (message) toast(...message)
        void reload()
      })
      .catch(e => toast((e as Error).message, 'error'))
      .finally(() => setParams(p => { p.delete('betaling'); return p }, { replace: true }))
  }, [online, returning, setBilling, toast, reload, setParams])

  if (!workspace) return <Loading />
  const current = plans.find(p => p.id === workspace.plan)
  const subscribed = online && workspace.subscriptionActive === true

  const checkout = async (plan: Plan['id']) => {
    setBusy(plan)
    try {
      const { checkoutUrl } = await api.post<{ checkoutUrl: string }>('/billing/checkout', { plan })
      window.location.assign(checkoutUrl)
    } catch (e) {
      toast((e as Error).message, 'error')
      setBusy(null)
    }
  }

  const cancel = async () => {
    setBusy('opzeggen')
    try {
      setBilling(await api.post<BillingStatus>('/billing/cancel'))
      setCancelling(false)
      toast('Je abonnement is opgezegd. Er wordt niets meer afgeschreven.')
      await reload()
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(null)
    }
  }

  const status = workspace.plan === 'proef'
    ? `Nog ${workspace.trialDaysLeft} dagen gratis, tot ${date(workspace.trialEndsAt)}.`
    : subscribed && workspace.paidUntil
      ? `Betaald tot ${date(workspace.paidUntil)}. Daarna schrijven we elke maand automatisch af.`
      : workspace.subscriptionCanceledAt && workspace.paidUntil
        ? `Opgezegd. Je kunt nog werken tot ${date(workspace.paidUntil)}; daarna wordt je werkruimte alleen-lezen.`
        : workspace.paidUntil
          ? `Betaald tot ${date(workspace.paidUntil)}.`
          : `Actief sinds ${date(workspace.createdAt)}.`

  return (<>
    <div className="card plan-status">
      <div>
        <div className="stat-label">Je abonnement</div>
        <div className="stat-value">{current?.name ?? workspace.plan}</div>
        <div className="muted">{status}</div>
        {workspace.readOnly && <div className="login-error" style={{ marginTop: 10 }}>{workspace.readOnly}</div>}
      </div>
      {subscribed && admin && (
        <button type="button" className="btn" onClick={() => setCancelling(true)}>Abonnement opzeggen</button>
      )}
    </div>
    <div className="pricing pricing-app">
      {plans.filter(p => p.id !== 'proef').map(p => {
        const quote = billing?.plans.find(q => q.id === p.id)
        const mine = workspace.plan === p.id && (online ? subscribed || (!workspace.readOnly && !workspace.subscriptionCanceledAt) : true)
        return (
          <div key={p.id} className={`price-card ${p.highlight ? 'highlight' : ''}`}>
            <h3>{p.name}</h3>
            <p className="muted">{p.description}</p>
            <div className="price"><strong>{p.price}</strong><span>{p.period}</span></div>
            {quote && <p className="muted" style={{ fontSize: 13 }}>{euro(quote.gross)} per maand inclusief {quote.vatRate}% btw</p>}
            <ul>{p.features.map(f => <li key={f}><Check size={15} /> {f}</li>)}</ul>
            {mine
              ? <button type="button" className="btn" disabled>Je huidige abonnement</button>
              : online
              ? <button type="button" className={`btn ${p.highlight ? 'btn-primary' : ''}`} disabled={!admin || busy !== null || !billing}
                  title={admin ? undefined : 'Alleen een beheerder kan het abonnement betalen.'} onClick={() => checkout(p.id)}>
                  <CreditCard size={15} /> {busy === p.id ? 'Even geduld…' : 'Betalen met iDEAL'}
                </button>
              : !email
              ? <button type="button" className="btn" disabled><Mail size={15} /> Kies {p.name}</button>
              : <a className={`btn ${p.highlight ? 'btn-primary' : ''}`}
                  href={`mailto:${email}?subject=${encodeURIComponent(`${product.name} ${p.name} voor ${workspace.name}`)}&body=${encodeURIComponent(`Hoi,\n\nIk wil graag overstappen op ${product.name} ${p.name} voor werkruimte "${workspace.name}" (nr. ${workspace.id}).\n\nGroet,`)}`}>
                  <Mail size={15} /> Kies {p.name}
                </a>}
          </div>
        )
      })}
    </div>
    <p className="muted" style={{ fontSize: 13 }}>
      {online
        ? 'Je betaalt de eerste maand met iDEAL. Daarna schrijven we elke maand automatisch af via een SEPA-incasso. Opzeggen kan altijd; je abonnement loopt dan door tot het einde van de maand die je al betaald hebt. Van elke betaling krijg je een factuur.'
        : email
          ? 'Online betalen komt eraan. Tot die tijd zetten we je abonnement na je mail binnen één werkdag om.'
          : 'Overstappen kan nog niet, want de beheerder van dit platform heeft nog geen contactadres ingesteld.'}
    </p>
    {cancelling && (
      <Modal title="Abonnement opzeggen" onClose={() => setCancelling(false)} footer={<>
        <button type="button" className="btn" onClick={() => setCancelling(false)}>Toch niet</button>
        <button type="button" className="btn btn-danger" disabled={busy !== null} onClick={cancel}>{busy === 'opzeggen' ? 'Bezig…' : 'Ja, opzeggen'}</button>
      </>}>
        <p>Na het opzeggen schrijven we niets meer af.{workspace.paidUntil && <> Je kunt nog gewoon werken tot {date(workspace.paidUntil)}.</>} Daarna wordt je werkruimte alleen-lezen: je kunt dan nog alles bekijken en exporteren, maar niets meer wijzigen.</p>
        <p className="muted">Bedenk je je, dan kies je later gewoon opnieuw een abonnement.</p>
      </Modal>
    )}
  </>)
}

function Account({ admin }: { admin: boolean }) {
  const { workspace, reload } = useWorkspace()
  const [deleting, setDeleting] = useState(false)
  const [clearing, setClearing] = useState(false)
  const [password, setPassword] = useState('')
  const toast = useToast()

  const exportData = async () => {
    try {
      const data = await api.get<unknown>('/workspace/export')
      const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }))
      const a = Object.assign(document.createElement('a'), { href: url, download: `${product.name.toLowerCase()}-export-${toDateInput(new Date())}.json` })
      a.click()
      URL.revokeObjectURL(url)
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  const remove = async () => {
    try {
      await api.del('/workspace', { password })
      window.location.assign('/')
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  // Alle voorbeelddata uit de welkomstwizard in één keer weg, ook verstuurde voorbeeldfacturen.
  const clearDemo = async () => {
    if (!confirm('Alle voorbeelddata verwijderen? Voorbeeldklanten, -projecten, -uren, -facturen, -leads en -werkstromen gaan weg. Wat je zelf hebt toegevoegd blijft staan.')) return
    setClearing(true)
    try {
      await api.del('/workspace/demo-data')
      toast('Voorbeelddata verwijderd')
      await reload()
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setClearing(false)
    }
  }

  if (!admin) return <div className="card"><div className="card-body muted">Alleen een beheerder kan gegevens exporteren of de werkruimte verwijderen.</div></div>

  return (
    <div className="grid grid-1-1">
      {workspace?.demoData && (
        <div className="card">
          <div className="card-head"><h3><Trash2 size={16} /> Voorbeelddata verwijderen</h3></div>
          <div className="card-body">
            <p className="muted" style={{ marginTop: 0 }}>
              Er staan nog voorbeeldklanten, -projecten en -facturen uit de welkomstwizard in je werkruimte. Haal ze in één keer weg;
              wat je zelf hebt toegevoegd blijft staan. Voorbeeldfacturen tellen niet mee in je echte factuurnummers.
            </p>
            <button type="button" className="btn" disabled={clearing} onClick={clearDemo}><Trash2 size={15} /> Voorbeelddata verwijderen</button>
          </div>
        </div>
      )}
      <div className="card">
        <div className="card-head"><h3><Download size={16} /> Gegevens exporteren</h3></div>
        <div className="card-body">
          <p className="muted" style={{ marginTop: 0 }}>Download al je klanten, projecten, uren, facturen, leads en werkstromen als één JSON-bestand.</p>
          <button type="button" className="btn" onClick={exportData}><Download size={15} /> Exporteren</button>
        </div>
      </div>
      <div className="card danger-card">
        <div className="card-head"><h3><Trash2 size={16} /> Werkruimte verwijderen</h3></div>
        <div className="card-body">
          <p className="muted" style={{ marginTop: 0 }}>Verwijdert {workspace?.name ?? 'je werkruimte'} met alle gegevens en gebruikers. Dit kan niet ongedaan worden gemaakt.</p>
          <button type="button" className="btn btn-danger" onClick={() => setDeleting(true)}><Trash2 size={15} /> Werkruimte verwijderen</button>
        </div>
      </div>
      {deleting && (
        <Modal title="Weet je het zeker?" onClose={() => setDeleting(false)} footer={<>
          <button type="button" className="btn" onClick={() => setDeleting(false)}>Annuleren</button>
          <button type="button" className="btn btn-danger" disabled={!password} onClick={remove}><Trash2 size={15} /> Definitief verwijderen</button>
        </>}>
          <p style={{ marginTop: 0 }}>Exporteer eerst je gegevens als je ze wilt bewaren. Vul je wachtwoord in om te bevestigen.</p>
          <Field label="Wachtwoord"><input type="password" autoFocus value={password} onChange={e => setPassword(e.target.value)} /></Field>
        </Modal>
      )}
    </div>
  )
}
