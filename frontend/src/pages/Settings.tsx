import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Settings as SettingsIcon, Save, Building, Target, Palette, CreditCard, UserCog, Download, Trash2, Check, Mail } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import { useAuth } from '../lib/auth'
import { useWorkspace } from '../lib/workspace'
import { plans, product } from '../lib/plans'
import type { Settings } from '../lib/types'
import { date, toDateInput } from '../lib/format'
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
  const preview = {
    number: `${new Date().getFullYear()}-0001`, customerId: 0, issueDate: today, dueDate: today, status: 'concept' as const, reverseCharge: false,
    reference: '', notes: 'Bedankt voor de fijne samenwerking!', lines: [{ description: 'Ontwikkeling webapplicatie', quantity: 12, unit: 'uur', unitPrice: form.defaultHourlyRate, vatRate: 21 }],
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

      {tab === 'abonnement' && <Subscription />}
      {tab === 'account' && <Account admin={admin} />}
    </form>
  )
}

function Subscription() {
  const { workspace } = useWorkspace()
  if (!workspace) return <Loading />
  const current = plans.find(p => p.id === workspace.plan)
  return (<>
    <div className="card plan-status">
      <div>
        <div className="stat-label">Je abonnement</div>
        <div className="stat-value">{current?.name ?? workspace.plan}</div>
        <div className="muted">
          {workspace.plan === 'proef'
            ? `Nog ${workspace.trialDaysLeft} dagen gratis, tot ${date(workspace.trialEndsAt)}.`
            : `Actief sinds ${date(workspace.createdAt)}.`}
        </div>
      </div>
    </div>
    <div className="pricing pricing-app">
      {plans.filter(p => p.id !== 'proef').map(p => (
        <div key={p.id} className={`price-card ${p.highlight ? 'highlight' : ''}`}>
          <h3>{p.name}</h3>
          <p className="muted">{p.description}</p>
          <div className="price"><strong>{p.price}</strong><span>{p.period}</span></div>
          <ul>{p.features.map(f => <li key={f}><Check size={15} /> {f}</li>)}</ul>
          {workspace.plan === p.id
            ? <button type="button" className="btn" disabled>Je huidige abonnement</button>
            : <a className={`btn ${p.highlight ? 'btn-primary' : ''}`}
                href={`mailto:${product.salesEmail}?subject=${encodeURIComponent(`${product.name} ${p.name} voor ${workspace.name}`)}&body=${encodeURIComponent(`Hoi,\n\nIk wil graag overstappen op ${product.name} ${p.name} voor werkruimte "${workspace.name}" (nr. ${workspace.id}).\n\nGroet,`)}`}>
                <Mail size={15} /> Kies {p.name}
              </a>}
        </div>
      ))}
    </div>
    <p className="muted" style={{ fontSize: 13 }}>Online betalen komt eraan. Tot die tijd zetten we je abonnement na je mail binnen één werkdag om.</p>
  </>)
}

function Account({ admin }: { admin: boolean }) {
  const { workspace } = useWorkspace()
  const [deleting, setDeleting] = useState(false)
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

  if (!admin) return <div className="card"><div className="card-body muted">Alleen een beheerder kan gegevens exporteren of de werkruimte verwijderen.</div></div>

  return (
    <div className="grid grid-1-1">
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
