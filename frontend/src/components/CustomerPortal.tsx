import { useRef, useState } from 'react'
import { Copy, ExternalLink, Eye, Link2, Link2Off, Save } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import { date, relative, utcDateTime } from '../lib/format'
import type { CustomerPortalSettings } from '../lib/types'
import { ErrorBox, Field, Loading } from './ui'

const expiryOptions = [
  { days: 0, label: 'Geen einddatum' },
  { days: 30, label: '30 dagen' },
  { days: 90, label: '90 dagen' },
  { days: 365, label: '1 jaar' },
]

// Het tabblad Klantportaal op de klantpagina: de geheime link maken, kopiëren of intrekken, en kiezen wat de klant ziet.
export function CustomerPortalTab({ customerId, admin, onChanged }: { customerId: number; admin: boolean; onChanged?: (p: CustomerPortalSettings) => void }) {
  const { data, setData, error } = useApi<CustomerPortalSettings>(`/customers/${customerId}/portal`)
  const [edit, setEdit] = useState<CustomerPortalSettings | null>(null)
  const [expiry, setExpiry] = useState(0)
  const [created, setCreated] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  const toast = useToast()

  if (error) return <ErrorBox message={error} />
  if (!data) return <Loading />
  const form = edit ?? data
  const dirty = JSON.stringify(form) !== JSON.stringify(data)
  const link = data.link
  const active = link && !link.expired
  const update = (p: CustomerPortalSettings) => { setData(p); onChanged?.(p) }

  const makeLink = async () => {
    if (active && !confirm('De huidige link werkt daarna niet meer. Een nieuwe link maken?')) return
    setBusy(true)
    try {
      const res = await api.post<{ path: string; portal: CustomerPortalSettings }>(`/customers/${customerId}/portal/link`, { expiresInDays: expiry || null })
      update(res.portal)
      setCreated(`${window.location.origin}${res.path}`)
      toast('Link gemaakt. Kopieer hem nu en stuur hem naar je klant.')
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const revoke = async () => {
    if (!confirm('De link intrekken? Je klant kan het portaal daarna niet meer openen, ook de facturen niet via het portaal.')) return
    setBusy(true)
    try {
      const res = await api.post<{ portal: CustomerPortalSettings }>(`/customers/${customerId}/portal/revoke`)
      update(res.portal)
      setCreated(null)
      toast('Link ingetrokken')
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const copy = async () => {
    if (!created) return
    try {
      await navigator.clipboard.writeText(created)
      toast('Link gekopieerd')
    } catch {
      input.current?.select()
      toast('Kopiëren lukte niet vanzelf. De link is geselecteerd; kopieer hem met Ctrl+C.', 'error')
    }
  }

  const save = async () => {
    setBusy(true)
    try {
      update(await api.put<CustomerPortalSettings>(`/customers/${customerId}/portal`, {
        showHours: form.showHours, showBudget: form.showBudget, showInvoices: form.showInvoices, showAmounts: form.showAmounts, note: form.note ?? '',
      }))
      setEdit(null)
      toast('Instellingen van het klantportaal opgeslagen')
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const check = (key: 'showHours' | 'showBudget' | 'showInvoices' | 'showAmounts', label: string, help: string) => (
    <label className="check-row field-full" style={{ alignItems: 'flex-start' }}>
      <input type="checkbox" checked={form[key]} disabled={!admin} onChange={e => setEdit({ ...form, [key]: e.target.checked })} />
      <span><span>{label}</span><span className="cell-sub" style={{ display: 'block' }}>{help}</span></span>
    </label>
  )

  return (
    <div className="grid grid-1-1">
      <div className="card">
        <div className="card-head"><h3><Link2 size={16} /> Link naar het portaal</h3></div>
        <div className="card-body">
          <p className="muted" style={{ marginTop: 0 }}>
            Met deze link ziet je klant zonder in te loggen hoe zijn projecten ervoor staan. Stuur hem bijvoorbeeld per mail.
          </p>
          {active ? (
            <dl className="dl">
              <dt>Status</dt><dd>Actief</dd>
              <dt>Gemaakt</dt><dd>{utcDateTime(link.createdAt)}{link.createdBy ? ` door ${link.createdBy}` : ''}</dd>
              <dt>Werkt tot</dt><dd>{link.expiresAt ? date(link.expiresAt) : 'Tot je hem intrekt'}</dd>
              <dt>Geopend</dt>
              <dd>{link.lastOpenedAt ? <span title={utcDateTime(link.lastOpenedAt)}>{relative(link.lastOpenedAt)}, {link.opens === 1 ? '1 keer' : `${link.opens} keer`}</span> : 'Nog niet geopend'}</dd>
            </dl>
          ) : (
            <p style={{ margin: 0 }}>
              {link?.expired ? `De link is verlopen op ${date(link.expiresAt)}.` : 'Er is op dit moment geen werkende link.'}
              {data.lastOpenedAt && <span className="muted"> Het portaal is voor het laatst geopend op {utcDateTime(data.lastOpenedAt)}.</span>}
            </p>
          )}

          {created && (
            <div className="portal-created">
              <Field label="De nieuwe link" full>
                <input ref={input} readOnly value={created} onFocus={e => e.target.select()} />
              </Field>
              <div className="row wrap" style={{ gap: 8 }}>
                <button type="button" className="btn btn-primary btn-sm" onClick={copy}><Copy size={14} /> Kopiëren</button>
                <a className="btn btn-sm" href={created} target="_blank" rel="noreferrer"><Eye size={14} /> Bekijken als klant</a>
              </div>
              <p className="cell-sub" style={{ marginBottom: 0 }}>
                Kopieer de link nu. We bewaren hem niet, dus later kun je hem niet meer opvragen. Ben je hem kwijt? Maak dan een nieuwe link; de oude werkt dan niet meer.
              </p>
            </div>
          )}

          {admin ? (
            <div className="row wrap mt" style={{ gap: 8, alignItems: 'flex-end' }}>
              <Field label="Hoe lang werkt de link?">
                <select value={expiry} onChange={e => setExpiry(Number(e.target.value))}>
                  {expiryOptions.map(o => <option key={o.days} value={o.days}>{o.label}</option>)}
                </select>
              </Field>
              <button type="button" className="btn btn-primary" disabled={busy} onClick={makeLink}><Link2 size={15} /> {active ? 'Nieuwe link maken' : 'Link maken'}</button>
              {active && <button type="button" className="btn btn-danger" disabled={busy} onClick={revoke}><Link2Off size={15} /> Intrekken</button>}
            </div>
          ) : (
            <p className="cell-sub" style={{ marginBottom: 0 }}>Alleen een beheerder kan een link maken of intrekken.</p>
          )}
        </div>
      </div>

      <div className="card">
        <div className="card-head"><h3><ExternalLink size={16} /> Wat je klant ziet</h3></div>
        <div className="card-body form-grid">
          {check('showHours', 'Uren', 'Gewerkte uren per project, per week en de recente boekingen met omschrijving.')}
          {check('showBudget', 'Budget', 'Hoeveel van het budget in uren er al op is, per project.')}
          {check('showInvoices', 'Facturen', 'Verstuurde facturen met hun status, wat er nog open staat en een link om ze te openen.')}
          {check('showAmounts', 'Bedragen bij uren en projecten', 'Je uurtarief en de waarde van de uren, of de vaste prijs. Facturen tonen altijd hun eigen bedrag.')}
          <Field label="Bericht voor je klant (mag leeg blijven)" full>
            <textarea rows={3} maxLength={1000} disabled={!admin} value={form.note ?? ''} placeholder="Bijvoorbeeld: deze maand werk ik aan de nieuwe checkout."
              onChange={e => setEdit({ ...form, note: e.target.value })} />
          </Field>
          <p className="cell-sub field-full" style={{ margin: 0 }}>
            Je klant ziet alleen zijn eigen lopende projecten en factureerbare uren. Niet-factureerbare uren, je notities, kosten en andere klanten ziet hij nooit,
            ook niet in een totaal. Uren op een project met een vaste prijs tellen dus alleen mee als ze op factureerbaar staan.
          </p>
          {admin && (
            <div className="row field-full" style={{ gap: 8 }}>
              <button type="button" className="btn btn-primary" disabled={busy || !dirty} onClick={save}><Save size={15} /> Opslaan</button>
              {dirty && <button type="button" className="btn" onClick={() => setEdit(null)}>Wijzigingen weggooien</button>}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
