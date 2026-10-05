import { useEffect, useState } from 'react'
import { Settings as SettingsIcon, Save, Building, Target } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import type { Settings } from '../lib/types'
import { ErrorBox, Field, Loading, PageHeader } from '../components/ui'

export function SettingsPage() {
  const { data, error, loading, setData } = useApi<Settings>('/settings')
  const [form, setForm] = useState<Settings | null>(null)
  const [busy, setBusy] = useState(false)
  const toast = useToast()

  useEffect(() => { if (data) setForm(data) }, [data])

  if (error) return <ErrorBox message={error} />
  if (loading || !form) return <Loading />

  const text = (k: keyof Settings) => ({ value: (form[k] as string | null) ?? '', onChange: (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: e.target.value }) })
  const num = (k: keyof Settings) => ({ type: 'number', min: 0, value: form[k] as number, onChange: (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: Number(e.target.value) }) })
  const dirty = JSON.stringify(form) !== JSON.stringify(data)

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
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

  return (
    <form onSubmit={save}>
      <PageHeader
        icon={<SettingsIcon size={20} />}
        title="Instellingen"
        subtitle="Je gegevens voor op de factuur en je doelen."
        actions={<button className="btn btn-primary" disabled={busy || !dirty}><Save size={15} /> Opslaan</button>}
      />
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
              Het standaard uurtarief wordt ingevuld bij nieuwe projecten.
            </p>
          </div>
        </div>
      </div>
    </form>
  )
}
