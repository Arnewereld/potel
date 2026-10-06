import { useEffect, useState } from 'react'
import { ArrowLeft, ArrowRight, Building, Euro, Palette, Rocket, Sparkles, UserPlus, FileX2, Check } from 'lucide-react'
import { api } from '../lib/api'
import type { Customer, Project, Settings } from '../lib/types'
import { useToast } from '../lib/toast'
import { toDateInput } from '../lib/format'
import { product } from '../lib/plans'
import { Field } from './ui'
import { BrandFields } from './BrandFields'
import { InvoicePaper } from './InvoicePaper'

type Start = 'demo' | 'eigen' | 'leeg'

const steps = [
  { icon: Building, title: 'Je bedrijf' },
  { icon: Euro, title: 'Tarief en doelen' },
  { icon: Palette, title: 'Huisstijl' },
  { icon: Rocket, title: 'Starten' },
]

// Eerste keer inloggen in een nieuwe werkruimte: in vier stappen klaar om te factureren.
export function OnboardingWizard({ onDone }: { onDone: () => void }) {
  const [step, setStep] = useState(0)
  const [s, setS] = useState<Settings | null>(null)
  const [start, setStart] = useState<Start>('eigen')
  const [first, setFirst] = useState({ customer: '', project: '' })
  const [busy, setBusy] = useState(false)
  const toast = useToast()

  useEffect(() => { api.get<Settings>('/settings').then(setS).catch(e => toast(e.message, 'error')) }, [toast])
  if (!s) return null

  const set = (patch: Partial<Settings>) => setS({ ...s, ...patch })
  const text = (k: keyof Settings, placeholder?: string) => ({
    value: (s[k] as string | null) ?? '', placeholder, onChange: (e: React.ChangeEvent<HTMLInputElement>) => set({ [k]: e.target.value }),
  })
  const num = (k: keyof Settings) => ({ type: 'number', min: 0, value: s[k] as number, onChange: (e: React.ChangeEvent<HTMLInputElement>) => set({ [k]: Number(e.target.value) }) })

  const finish = async (skip = false) => {
    setBusy(true)
    try {
      let target = '/'
      if (!skip) {
        await api.put('/settings', s)
        if (start === 'demo') await api.post('/workspace/demo-data')
        if (start === 'eigen' && first.customer.trim()) {
          const c = await api.post<Customer>('/customers', { name: first.customer.trim(), company: first.customer.trim() })
          if (first.project.trim()) {
            const p = await api.post<Project>('/projects', {
              name: first.project.trim(), customerId: c.id, status: 'actief', billing: 'uur', hourlyRate: s.defaultHourlyRate, color: s.brandColor || '#ff6d5a',
            })
            target = `/projecten/${p.id}`
          }
        }
      }
      await api.post('/workspace/onboarded')
      onDone()
      // Alles opnieuw laden zodat elk scherm de nieuwe gegevens toont.
      window.location.assign(target)
    } catch (e) {
      toast((e as Error).message, 'error')
      setBusy(false)
    }
  }

  const today = toDateInput(new Date())
  const preview = {
    number: `${new Date().getFullYear()}-0001`, customerId: 0, issueDate: today, dueDate: today, deliveryFrom: today, status: 'concept' as const,
    vatRegime: 'normaal' as const, reference: '', notes: '', lines: [{ description: 'Ontwikkeling webapplicatie', quantity: 12, unit: 'uur', unitPrice: s.defaultHourlyRate, vatRate: 21 }],
  }

  return (
    <div className="modal-backdrop wizard-backdrop">
      <div className="wizard">
        <aside className="wizard-side">
          <div className="wizard-hello"><Sparkles size={18} /> Welkom bij {product.name}</div>
          <ol>
            {steps.map((st, i) => (
              <li key={st.title} className={i === step ? 'active' : i < step ? 'done' : ''}>
                <span>{i < step ? <Check size={14} /> : <st.icon size={14} />}</span>{st.title}
              </li>
            ))}
          </ol>
          <button className="btn btn-ghost btn-sm" disabled={busy} onClick={() => finish(true)}>Later instellen</button>
        </aside>

        <div className="wizard-main">
          {step === 0 && (<>
            <h2>Eerst je bedrijf</h2>
            <p className="muted">Dit komt op je facturen. Je kunt het later altijd aanpassen bij Instellingen.</p>
            <div className="form-grid">
              <Field label="Bedrijfsnaam *" full><input required autoFocus {...text('companyName')} /></Field>
              <Field label="Je naam"><input {...text('ownerName')} /></Field>
              <Field label="Website"><input {...text('website', 'jouwnaam.dev')} /></Field>
              <Field label="Adres"><input {...text('address')} /></Field>
              <Field label="Postcode en plaats"><input {...text('city')} /></Field>
              <Field label="KvK-nummer"><input {...text('kvk')} /></Field>
              <Field label="Btw-nummer"><input {...text('btw', 'NL000000000B01')} /></Field>
              <Field label="IBAN" full><input {...text('iban', 'NL00 BANK 0000 0000 00')} /></Field>
            </div>
          </>)}

          {step === 1 && (<>
            <h2>Je tarief en doelen</h2>
            <p className="muted">Je uurtarief wordt ingevuld bij nieuwe projecten. Met de doelen houdt het dashboard je bij.</p>
            <div className="form-grid">
              <Field label="Uurtarief (excl. btw)"><input step="1" {...num('defaultHourlyRate')} /></Field>
              <Field label="Betaaltermijn in dagen"><input {...num('paymentTermDays')} /></Field>
              <Field label="Uren per week"><input {...num('weeklyHoursTarget')} /></Field>
              <Field label="Uren per jaar"><input {...num('yearlyHoursTarget')} /></Field>
            </div>
            <p className="muted" style={{ fontSize: 13 }}>1.225 uur per jaar is het urencriterium voor de zelfstandigenaftrek.</p>
          </>)}

          {step === 2 && (<>
            <h2>Maak je facturen herkenbaar</h2>
            <p className="muted">Je logo en kleur komen bovenaan elke factuur.</p>
            <div className="wizard-brand">
              <BrandFields value={s} onChange={set} />
              <div className="wizard-preview"><div className="wizard-preview-inner"><InvoicePaper invoice={preview} company={s} /></div></div>
            </div>
          </>)}

          {step === 3 && (<>
            <h2>Hoe wil je beginnen?</h2>
            <p className="muted">Je kunt alles later nog aanpassen of verwijderen.</p>
            <div className="start-options">
              <button type="button" className={start === 'eigen' ? 'active' : ''} onClick={() => setStart('eigen')}>
                <UserPlus size={20} /><strong>Met mijn eerste klant</strong><span>Maak meteen een klant en project aan om uren op te schrijven.</span>
              </button>
              <button type="button" className={start === 'demo' ? 'active' : ''} onClick={() => setStart('demo')}>
                <Sparkles size={20} /><strong>Met voorbeelddata</strong><span>Klanten, projecten, uren en facturen om alles te ontdekken.</span>
              </button>
              <button type="button" className={start === 'leeg' ? 'active' : ''} onClick={() => setStart('leeg')}>
                <FileX2 size={20} /><strong>Leeg beginnen</strong><span>Je bouwt alles zelf op.</span>
              </button>
            </div>
            {start === 'eigen' && (
              <div className="form-grid mt">
                <Field label="Klant"><input autoFocus placeholder="Bijv. Fietsplein B.V." value={first.customer} onChange={e => setFirst({ ...first, customer: e.target.value })} /></Field>
                <Field label="Project"><input placeholder="Bijv. Nieuwe webshop" value={first.project} onChange={e => setFirst({ ...first, project: e.target.value })} /></Field>
              </div>
            )}
          </>)}

          <div className="wizard-foot">
            {step > 0 && <button className="btn" onClick={() => setStep(step - 1)}><ArrowLeft size={16} /> Terug</button>}
            <span className="spacer" />
            {step < steps.length - 1
              ? <button className="btn btn-primary" disabled={!s.companyName.trim()} onClick={() => setStep(step + 1)}>Volgende <ArrowRight size={16} /></button>
              : <button className="btn btn-primary" disabled={busy} onClick={() => finish()}><Rocket size={16} /> {busy ? 'Bezig…' : 'Aan de slag'}</button>}
          </div>
        </div>
      </div>
    </div>
  )
}
