import { useState } from 'react'
import { Check, CreditCard, ExternalLink, KeyRound, PlugZap, Trash2 } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import { utcDateTime } from '../lib/format'
import type { IdealSettings } from '../lib/types'
import { Badge, ErrorBox, Field, Loading } from './ui'

// iDEAL op facturen via je eigen Mollie-account. Staat los van je abonnement op Potel: dit geld gaat naar jou.
export function IdealSettingsTab({ admin }: { admin: boolean }) {
  const { data, setData, error } = useApi<IdealSettings>('/settings/mollie')
  const [key, setKey] = useState('')
  const [busy, setBusy] = useState(false)
  const [test, setTest] = useState<{ ok: boolean; message: string } | null>(null)
  const toast = useToast()

  if (error) return <ErrorBox message={error} />
  if (!data) return <Loading />

  const runTest = async () => {
    setBusy(true)
    try {
      setTest(await api.post<{ ok: boolean; message: string }>('/settings/mollie/test'))
    } catch (e) {
      setTest({ ok: false, message: (e as Error).message })
    } finally {
      setBusy(false)
    }
  }

  const save = async () => {
    if (!key.trim()) { toast('Plak eerst je API-sleutel in het veld.', 'error'); return }
    setBusy(true)
    setTest(null)
    try {
      setData(await api.put<IdealSettings>('/settings/mollie', { key: key.trim() }))
      setKey('')
      toast('Sleutel opgeslagen')
    } catch (e) {
      toast((e as Error).message, 'error')
      setBusy(false)
      return
    }
    // Meteen kijken of hij werkt, zodat je het niet pas merkt als een klant wil betalen.
    await runTest()
  }

  const remove = async () => {
    if (!confirm('De sleutel van Mollie verwijderen? Daarna kunnen je klanten facturen niet meer met iDEAL betalen.')) return
    setBusy(true)
    try {
      setData(await api.del<IdealSettings>('/settings/mollie'))
      setTest(null)
      toast('Sleutel verwijderd')
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  // Enter in het veld hoort bij dit veld, niet bij het formulier van de instellingen eromheen.
  const onEnter = (e: React.KeyboardEvent) => { if (e.key === 'Enter') { e.preventDefault(); save() } }

  return (
    <div className="grid grid-1-1">
      <div className="card">
        <div className="card-head"><h3><CreditCard size={16} /> iDEAL op je facturen</h3></div>
        <div className="card-body">
          <p className="muted" style={{ marginTop: 0 }}>
            Met je eigen Mollie-account kunnen je klanten een factuur meteen met iDEAL betalen. Op de factuurpagina achter de link in je mail
            en in het klantportaal staat dan een knop <em>Betaal nu met iDEAL</em>. Het geld komt via Mollie op je eigen rekening, en Potel zet
            de betaling vanzelf op de factuur.
          </p>
          <p className="muted">
            Mollie rekent een vergoeding per betaling; de actuele prijs staat op mollie.com. Potel rekent er niets extra voor. Dit staat los
            van je abonnement op Potel.
          </p>

          {data.key ? (
            <p style={{ margin: '0 0 12px' }}>
              <Badge tone="green">Ingesteld</Badge> <Badge tone={data.key.mode === 'live' ? 'blue' : 'yellow'}>{data.key.mode === 'live' ? 'Live' : 'Test'}</Badge>{' '}
              <span className="muted">Eindigt op <code>{data.key.last4}</code>, opgeslagen op {utcDateTime(data.key.updatedAt)}{data.key.updatedBy ? ` door ${data.key.updatedBy}` : ''}.</span>
            </p>
          ) : <p style={{ margin: '0 0 12px' }}><Badge tone="gray">Niet ingesteld</Badge></p>}

          {data.problem && (
            <div className="notice" style={{ marginBottom: 12 }}>
              Online betalen staat in deze installatie uit. {data.problem} Tot die tijd zien je klanten geen knop om te betalen.
            </div>
          )}
          {data.key?.mode === 'test' && (
            <p className="cell-sub" style={{ marginTop: 0 }}>
              Dit is een testsleutel. Op de testbetaalpagina van Mollie kies je zelf of een betaling lukt. Een testbetaling komt ook op de
              factuur, met de notitie dat het geen echt geld is. Werkt alles, vervang hem dan door je live-sleutel.
            </p>
          )}

          {admin ? (<>
            <Field label={data.key ? 'Nieuwe API-sleutel' : 'API-sleutel'}>
              <input type="password" autoComplete="off" spellCheck={false} value={key} placeholder="live_… of test_…"
                onChange={e => setKey(e.target.value)} onKeyDown={onEnter} />
            </Field>
            <div className="row wrap mt" style={{ gap: 8 }}>
              <button type="button" className="btn btn-primary" disabled={busy || !key.trim()} onClick={save}><Check size={15} /> Opslaan</button>
              {data.key && <button type="button" className="btn" disabled={busy} onClick={runTest}><PlugZap size={15} /> Test de verbinding</button>}
              {data.key && <button type="button" className="btn btn-danger" disabled={busy} onClick={remove}><Trash2 size={15} /> Verwijderen</button>}
            </div>
            {test && <p className={test.ok ? 'text-green' : 'text-red'} style={{ fontSize: 14, marginBottom: 0 }} role="status">{test.message}</p>}
            <p className="cell-sub" style={{ marginBottom: 0 }}>
              Je API-sleutel vind je in je <a href="https://my.mollie.com/dashboard" target="_blank" rel="noreferrer">Mollie-dashboard <ExternalLink size={11} /></a> bij
              Ontwikkelaars, API-sleutels. Potel bewaart hem versleuteld en laat hem daarna nooit meer zien, ook niet aan jou.
            </p>
          </>) : (
            <p className="cell-sub" style={{ marginBottom: 0 }}>Alleen een beheerder kan de sleutel instellen, testen of verwijderen.</p>
          )}
        </div>
      </div>

      <div className="card">
        <div className="card-head"><h3><KeyRound size={16} /> Zo werkt het</h3></div>
        <div className="card-body">
          <ol className="cell-sub" style={{ marginTop: 0, paddingLeft: 18, lineHeight: 1.6 }}>
            <li>Maak een account bij Mollie en zet daar iDEAL aan.</li>
            <li>Plak hiernaast je API-sleutel. Begin met de testsleutel (test_…) en kies <em>Test de verbinding</em>.</li>
            <li>In de mail met je factuur staat dan dat je klant ook met iDEAL kan betalen. De knop staat op de factuurpagina en in het klantportaal.</li>
            <li>Betaalt je klant, dan laat Mollie dat aan Potel weten. Potel vraagt bij Mollie na wat er betaald is en zet dat bedrag als betaling op de factuur. Is alles binnen, dan staat de factuur op betaald.</li>
          </ol>
          <p className="cell-sub">
            Je klant betaalt altijd precies wat er nog openstaat. Bij een concept, een creditnota, een voorbeeldfactuur of een betaalde factuur staat
            er geen knop. Een terugbetaling of storno bij Mollie zet Potel niet vanzelf terug: haal de betaling dan zelf weg bij de factuur.
          </p>
        </div>
      </div>
    </div>
  )
}
