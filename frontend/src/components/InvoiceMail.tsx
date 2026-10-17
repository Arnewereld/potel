import { useState } from 'react'
import { BellRing, Link2Off, Mail, Send } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import { date } from '../lib/format'
import { mailStatuses, reminderSteps } from '../lib/mail'
import type { InvoiceMailInfo, MailPreview, MailStep } from '../lib/types'
import { Badge, Field, Loading, Modal } from './ui'

const dateTime = (s: string) => new Date(s).toLocaleString('nl-NL', { day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' })

// De factuur of een herinnering mailen vanuit Potel. Je ziet de mail zoals hij weggaat en kunt de tekst nog aanpassen;
// hij gaat altijd naar het e-mailadres van de klant, met een persoonlijke link naar de factuur.
export function MailDialog({ invoiceId, step: initial, reminder, onClose, onSent }: {
  invoiceId: number
  step: MailStep
  // Bij een herinnering kies je zelf welke stap je stuurt.
  reminder?: boolean
  onClose: () => void
  onSent: () => void
}) {
  const [step, setStep] = useState<MailStep>(initial)
  const { data: preview, error } = useApi<MailPreview>(`/invoices/${invoiceId}/mail-preview?step=${step}`)
  const [edit, setEdit] = useState<{ step: MailStep; subject: string; body: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const toast = useToast()
  // Pas je de tekst aan, dan geldt dat voor deze stap; kies je een andere stap, dan begin je weer met de tekst daarvan.
  const current = preview && preview.step === step ? preview : null
  const text = edit && edit.step === step ? edit : current ? { step, subject: current.subject, body: current.body } : null

  const send = async () => {
    if (!text) return
    setBusy(true)
    try {
      await api.post(`/invoices/${invoiceId}/mail`, { step, subject: text.subject, body: text.body })
      toast(`${step === 0 ? 'De factuur' : reminderSteps.find(s => s.step === step)?.label} is gemaild naar ${current?.to}`)
      onSent()
      onClose()
    } catch (e) {
      toast((e as Error).message, 'error')
      setBusy(false)
    }
  }

  return (
    <Modal wide title={reminder ? 'Herinnering sturen' : 'Per e-mail versturen'} onClose={onClose} footer={<>
      <button className="btn" onClick={onClose}>Annuleren</button>
      <button className="btn btn-primary" disabled={busy || !current || !!current.problem || !text?.subject.trim() || !text.body.trim()} onClick={send}>
        <Send size={15} /> {busy ? 'Bezig met versturen…' : 'Versturen'}
      </button>
    </>}>
      {reminder && (
        <div className="row wrap" style={{ gap: 6, marginBottom: 14 }}>
          {reminderSteps.map(s => (
            <button key={s.step} type="button" className={`chip ${step === s.step ? 'active' : ''}`} onClick={() => setStep(s.step)}>{s.label}</button>
          ))}
        </div>
      )}
      {error ? <p className="text-red">{error}</p> : !current || !text ? <Loading /> : (<>
        {current.problem && <div className="login-error" style={{ marginTop: 0, marginBottom: 14 }}>{current.problem}</div>}
        <div className="form-grid">
          <Field label="Aan" full><input value={current.to ?? 'Geen e-mailadres bij deze klant'} disabled /></Field>
          <Field label="Onderwerp" full><input value={text.subject} maxLength={200} onChange={e => setEdit({ ...text, subject: e.target.value })} /></Field>
          <Field label="Bericht" full><textarea rows={14} value={text.body} maxLength={5000} onChange={e => setEdit({ ...text, body: e.target.value })} /></Field>
        </div>
        <p className="muted" style={{ fontSize: 13, margin: '12px 0 0' }}>
          Op de plek van {'{link}'} komt bij versturen een persoonlijke link naar de factuur. Daar kan je klant hem bekijken, printen en als PDF opslaan, zonder in te loggen.
          {step === 3 && (current.consumer
            ? ' Je klant is een particulier: de aanmaning geeft 14 dagen om te betalen en noemt de incassokosten die daarna komen.'
            : ' Je klant is een bedrijf: de aanmaning noemt de wettelijke handelsrente en de vergoeding voor incassokosten.')}
        </p>
      </>)}
    </Modal>
  )
}

// Wat er over deze factuur gemaild is, wanneer de volgende herinnering komt, en de links die openstaan.
export function MailPanel({ invoiceId, info, onChange, onCustomer, onSettings }: {
  invoiceId: number
  info: InvoiceMailInfo
  onChange: () => void
  onCustomer: () => void
  onSettings: () => void
}) {
  const [busy, setBusy] = useState(false)
  const toast = useToast()

  const toggle = async (off: boolean) => {
    setBusy(true)
    try {
      await api.post(`/invoices/${invoiceId}/reminders`, { off })
      toast(off ? 'Deze factuur krijgt geen automatische herinneringen meer' : 'Automatische herinneringen staan weer aan voor deze factuur')
      onChange()
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const revoke = async () => {
    if (!confirm('Alle links naar deze factuur intrekken? Wie de link uit een eerdere mail opent, ziet dan dat hij niet meer werkt. Mail je de factuur opnieuw, dan komt er een nieuwe link.')) return
    try {
      await api.post(`/invoices/${invoiceId}/links/revoke`)
      toast('De links naar deze factuur zijn ingetrokken')
      onChange()
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  return (
    <div className="card" style={{ marginTop: 16 }}>
      <div className="card-head"><h3><Mail size={16} /> E-mail en herinneringen</h3></div>
      <div className="card-body">
        {!info.remindersEnabled ? (
          <p className="muted" style={{ marginTop: 0, fontSize: 13 }}>
            Automatische herinneringen staan uit. Je zet ze aan bij <a onClick={onSettings} style={{ cursor: 'pointer' }}>Instellingen</a>.
          </p>
        ) : info.customerRemindersOff ? (
          <p className="muted" style={{ marginTop: 0, fontSize: 13 }}>
            Deze klant krijgt geen automatische herinneringen. Dat pas je aan bij de <a onClick={onCustomer} style={{ cursor: 'pointer' }}>klant</a>.
          </p>
        ) : (
          <label className="row" style={{ gap: 8, marginBottom: 10, fontSize: 14 }}>
            <input type="checkbox" checked={info.remindersOff} disabled={busy} onChange={e => toggle(e.target.checked)} />
            Geen automatische herinneringen voor deze factuur
          </label>
        )}
        {info.nextReminder && (
          <p className="muted" style={{ marginTop: 0, fontSize: 13 }}>
            <BellRing size={13} /> Volgende stap: {info.nextReminder.label.toLowerCase()}, vanaf {date(info.nextReminder.date)}.
          </p>
        )}

        {info.mails.length === 0 ? (
          <p className="muted" style={{ margin: 0, fontSize: 13 }}>Er is nog niets over deze factuur gemaild vanuit Potel.</p>
        ) : (
          <ul className="mail-log">
            {info.mails.map(m => (
              <li key={m.id}>
                <div className="row between wrap" style={{ gap: 8 }}>
                  <strong>{m.label}</strong>
                  <Badge tone={mailStatuses[m.status].tone}>{mailStatuses[m.status].label}</Badge>
                </div>
                <div className="muted">
                  {dateTime(m.sentAt ?? m.createdAt)}{m.to ? `, aan ${m.to}` : ''}
                  {m.automatic ? ', automatisch' : m.sentBy ? `, door ${m.sentBy}` : ''}
                </div>
                {m.note && <div className="muted">{m.note}</div>}
              </li>
            ))}
          </ul>
        )}

        {info.activeLinks > 0 && (
          <div className="row between wrap" style={{ gap: 8, marginTop: 12, fontSize: 13 }}>
            <span className="muted">
              {info.activeLinks === 1 ? 'Er is 1 link naar deze factuur actief.' : `Er zijn ${info.activeLinks} links naar deze factuur actief.`}
              {info.lastOpenedAt ? ` Laatst geopend op ${dateTime(info.lastOpenedAt)}.` : ' Nog niet geopend.'}
            </span>
            <button className="btn btn-sm" onClick={revoke}><Link2Off size={14} /> Link intrekken</button>
          </div>
        )}
      </div>
    </div>
  )
}
