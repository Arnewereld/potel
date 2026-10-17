import { useState } from 'react'
import { Bell, Info, Mail, Save } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import type { NotificationKindSetting, NotificationSettings } from '../lib/notifications'
import { ErrorBox, Loading } from './ui'
import { NotificationIcon } from './Notifications'

// Het tabblad Meldingen bij Instellingen: per soort in de app en per e-mail, en of de mails als één overzicht per dag komen.
// Iedereen stelt dit voor zichzelf in; je ziet alleen de soorten die bij je rol horen.
export function NotificationSettingsTab() {
  const { data, setData, error } = useApi<NotificationSettings>('/notifications/settings')
  const [edit, setEdit] = useState<NotificationSettings | null>(null)
  const [busy, setBusy] = useState(false)
  const toast = useToast()

  if (error) return <ErrorBox message={error} />
  if (!data) return <Loading />
  const form = edit ?? data
  const dirty = JSON.stringify(form) !== JSON.stringify(data)
  const canMail = form.emailAvailable

  const set = (id: string, change: Partial<NotificationKindSetting>) =>
    setEdit({ ...form, kinds: form.kinds.map(k => k.id === id ? { ...k, ...change } : k) })

  const save = async () => {
    setBusy(true)
    try {
      setData(await api.put<NotificationSettings>('/notifications/settings', {
        digest: form.digest, kinds: form.kinds.map(k => ({ id: k.id, inApp: k.inApp, email: k.email })),
      }))
      setEdit(null)
      toast('Je instellingen voor meldingen zijn opgeslagen')
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="card">
      <div className="card-head">
        <h3><Bell size={16} /> Meldingen</h3>
        <button type="button" className="btn btn-primary btn-sm" disabled={busy || !dirty} onClick={save}><Save size={14} /> Opslaan</button>
      </div>
      <div className="card-body">
        <p className="muted" style={{ marginTop: 0 }}>
          Kies per soort of je een melding wilt in de bel rechtsboven en of je hem ook per e-mail krijgt. Dit geldt alleen voor jou.
        </p>
        {!canMail && (
          <p className="notif-note"><Info size={15} /> Er is geen mailserver ingesteld, dus meldingen per e-mail kunnen nu niet. In de app krijg je ze wel.</p>
        )}
        {canMail && !form.emailVerified && (
          <p className="notif-note"><Info size={15} /> Bevestig eerst je e-mailadres ({form.email}). Daarna kunnen we je meldingen ook mailen.</p>
        )}
        <div className="notif-settings">
          <div className="notif-settings-head">
            <span>Soort</span>
            <span>In de app</span>
            <span>Per e-mail</span>
          </div>
          {form.kinds.map(k => (
            <div key={k.id} className="notif-settings-row">
              <div className="notif-settings-kind">
                <NotificationIcon kind={k.id} />
                <span><strong>{k.label}</strong><span className="cell-sub">{k.description}</span></span>
              </div>
              <label className="notif-check"><input type="checkbox" checked={k.inApp} onChange={e => set(k.id, { inApp: e.target.checked })} /><span>In de app</span></label>
              <label className="notif-check"><input type="checkbox" checked={k.email} disabled={!canMail} onChange={e => set(k.id, { email: e.target.checked })} /><span>Per e-mail</span></label>
            </div>
          ))}
        </div>
        <div className="notif-digest">
          <div className="row" style={{ gap: 8 }}><Mail size={15} /> <strong>Hoe de mails komen</strong></div>
          <label className="check-row">
            <input type="radio" name="notif-digest" checked={!form.digest} disabled={!canMail} onChange={() => setEdit({ ...form, digest: false })} />
            <span>Meteen, een mail per melding</span>
          </label>
          <label className="check-row">
            <input type="radio" name="notif-digest" checked={form.digest} disabled={!canMail} onChange={() => setEdit({ ...form, digest: true })} />
            <span>Eén overzicht per dag, rond acht uur 's ochtends</span>
          </label>
          <p className="cell-sub" style={{ margin: 0 }}>
            Wat je in de app al gelezen hebt, staat niet meer in het overzicht. Komen er op één dag heel veel meldingen per e-mail, dan krijg je de rest in een overzicht.
          </p>
        </div>
      </div>
    </div>
  )
}
