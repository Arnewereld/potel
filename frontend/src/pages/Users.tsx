import { useState } from 'react'
import { ShieldCheck, Plus, Trash2, ShieldOff, KeyRound } from 'lucide-react'
import { useApi } from '../lib/useApi'
import { api } from '../lib/api'
import { useAuth, type User } from '../lib/auth'
import { useToast } from '../lib/toast'
import { useWorkspace } from '../lib/workspace'
import { date, initials, relative } from '../lib/format'
import { colorFor } from '../lib/status'
import { Badge, ErrorBox, Field, Loading, Modal, PageHeader } from '../components/ui'

export function UsersPage() {
  const { user: me } = useAuth()
  const { workspace, reload: reloadWorkspace } = useWorkspace()
  const { data, error, loading, reload } = useApi<User[]>('/users')
  const [editing, setEditing] = useState<Partial<User> | null>(null)
  const full = !!workspace && !!data && data.length >= workspace.maxUsers

  if (me.role !== 'beheerder') return <ErrorBox message="Alleen beheerders kunnen gebruikers beheren." />

  return (
    <>
      <PageHeader
        icon={<ShieldCheck size={20} />}
        title="Gebruikers"
        subtitle="Wie er in het portaal mag en wat ze mogen."
        actions={<button className="btn btn-primary" disabled={full} title={full ? 'Je abonnement zit vol' : undefined} onClick={() => setEditing({ role: 'medewerker', active: true })}><Plus size={16} /> Nieuwe gebruiker</button>}
      />
      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && <Loading />}
      {data && (
        <div className="card">
          <div className="table-wrap">
            <table>
              <thead><tr><th>Naam</th><th>Rol</th><th>Status</th><th>Laatst ingelogd</th><th>Sinds</th></tr></thead>
              <tbody>
                {data.map(u => (
                  <tr key={u.id} className="clickable" onClick={() => setEditing(u)}>
                    <td>
                      <div className="cell-main">
                        <div className="avatar" style={{ background: colorFor(u.name) }}>{initials(u.name)}</div>
                        <div><div>{u.name}{u.id === me.id && <span className="muted"> (jij)</span>}</div><div className="cell-sub">{u.email}</div></div>
                      </div>
                    </td>
                    <td><Badge tone={u.role === 'beheerder' ? 'orange' : 'blue'}>{u.role}</Badge></td>
                    <td>
                      <div className="row wrap" style={{ gap: 6 }}>
                        <Badge tone={u.active ? 'green' : 'gray'}>{u.active ? 'Actief' : 'Uitgeschakeld'}</Badge>
                        {u.twoFactor && <span className="badge badge-blue badge-plain">Twee stappen</span>}
                      </div>
                    </td>
                    <td className="muted">{u.lastLoginAt ? relative(u.lastLoginAt) : 'Nog nooit'}</td>
                    <td className="muted">{date(u.createdAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
      {workspace && <TwoFactorPolicy required={workspace.requireTwoFactorForAdmins} meOn={me.twoFactor} onChanged={reloadWorkspace} />}
      <p className="muted" style={{ fontSize: 12, marginTop: 14 }}>
        Beheerders kunnen alles. Medewerkers kunnen alles behalve gebruikers beheren, de bedrijfsgegevens en huisstijl wijzigen,
        werkstromen met e-mail of een webhook maken, en gegevens exporteren of de werkruimte verwijderen.
        {workspace && data && <> Je gebruikt {data.length} van de {workspace.maxUsers} {workspace.maxUsers === 1 ? 'plek' : 'plekken'} van je abonnement; uitgeschakelde gebruikers tellen mee.</>}
      </p>
      {editing && <UserForm user={editing} isMe={editing.id === me.id} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload() }} />}
    </>
  )
}

// Een werkruimte-instelling: beheerders moeten inloggen in twee stappen aan hebben. Wie het nog niet heeft, stelt het in bij de volgende keer.
function TwoFactorPolicy({ required, meOn, onChanged }: { required: boolean; meOn: boolean; onChanged: () => Promise<void> }) {
  const [busy, setBusy] = useState(false)
  const toast = useToast()

  const change = async (on: boolean) => {
    setBusy(true)
    try {
      await api.put('/workspace/two-factor', { requireForAdmins: on })
      await onChanged()
      toast(on ? 'Inloggen in twee stappen is nu verplicht voor beheerders' : 'Inloggen in twee stappen is niet meer verplicht voor beheerders')
    } catch (err) {
      toast((err as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="card" style={{ marginTop: 18 }}>
      <div className="card-head"><h3><KeyRound size={16} /> Inloggen in twee stappen</h3></div>
      <div className="card-body">
        <label className="check-row check-row-top" style={{ marginTop: 0 }}>
          <input type="checkbox" checked={required} disabled={busy || (!required && !meOn)} onChange={e => change(e.target.checked)} />
          <span>
            <strong>Verplicht voor beheerders.</strong> Beheerders zonder inloggen in twee stappen moeten het eerst instellen voordat
            ze verder kunnen. Medewerkers kiezen het zelf.
          </span>
        </label>
        {!required && !meOn && (
          <p className="muted" style={{ fontSize: 13, marginBottom: 0 }}>Zet het eerst voor jezelf aan bij Instellingen, Account. Daarna kun je het verplicht maken.</p>
        )}
      </div>
    </div>
  )
}

function UserForm({ user, isMe, onClose, onSaved }: { user: Partial<User>; isMe: boolean; onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState({ name: user.name ?? '', email: user.email ?? '', role: user.role ?? 'medewerker', active: user.active ?? true, password: '' })
  const toast = useToast()

  // Telefoon kwijt en geen herstelcodes meer: een beheerder zet het voor een medewerker uit, die stelt het daarna opnieuw in.
  const resetTwoFactor = async () => {
    if (!confirm(`Inloggen in twee stappen uitzetten voor ${user.name}? ${user.name} wordt overal uitgelogd, logt daarna in met alleen het wachtwoord en kan het opnieuw instellen.`)) return
    try {
      await api.del(`/users/${user.id}/two-factor`)
      toast(`Inloggen in twee stappen staat uit voor ${user.name}`)
      onSaved()
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    try {
      if (user.id) await api.put(`/users/${user.id}`, form)
      else await api.post('/users', form)
      toast(user.id ? 'Gebruiker opgeslagen' : 'Gebruiker toegevoegd')
      onSaved()
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  const remove = async () => {
    if (!confirm(`${user.name} verwijderen?`)) return
    try {
      await api.del(`/users/${user.id}`)
      toast('Gebruiker verwijderd')
      onSaved()
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  return (
    <Modal title={user.id ? 'Gebruiker bewerken' : 'Nieuwe gebruiker'} onClose={onClose} footer={<>
      {user.id && !isMe && <button className="btn btn-danger" type="button" onClick={remove}><Trash2 size={15} /></button>}
      <span className="spacer" />
      <button className="btn" type="button" onClick={onClose}>Annuleren</button>
      <button className="btn btn-primary" form="user-form">Opslaan</button>
    </>}>
      <form id="user-form" className="form-grid" onSubmit={save}>
        <Field label="Naam *"><input required autoFocus value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} /></Field>
        <Field label="E-mailadres *"><input type="email" required value={form.email} onChange={e => setForm({ ...form, email: e.target.value })} /></Field>
        <Field label="Rol">
          <select value={form.role} onChange={e => setForm({ ...form, role: e.target.value as User['role'] })}>
            <option value="medewerker">Medewerker</option>
            <option value="beheerder">Beheerder</option>
          </select>
        </Field>
        <Field label={user.id ? 'Nieuw wachtwoord (leeg laten = niet wijzigen)' : 'Wachtwoord *'}>
          <input type="password" autoComplete="new-password" minLength={8} required={!user.id} value={form.password} onChange={e => setForm({ ...form, password: e.target.value })} />
        </Field>
        <label className="row field-full">
          <input type="checkbox" checked={form.active} disabled={isMe} onChange={e => setForm({ ...form, active: e.target.checked })} /> Actief (mag inloggen)
        </label>
        {user.id && user.twoFactor && (
          <div className="field-full">
            <div className="field-label">Inloggen in twee stappen</div>
            {isMe
              ? <p className="muted" style={{ margin: '4px 0 0', fontSize: 13 }}>Staat aan. Je beheert het zelf bij Instellingen, Account.</p>
              : user.role === 'medewerker'
              ? <>
                <p className="muted" style={{ margin: '4px 0 8px', fontSize: 13 }}>Staat aan. Is {user.name} de telefoon en de herstelcodes kwijt? Zet het dan hier uit.</p>
                <button type="button" className="btn btn-sm" onClick={resetTwoFactor}><ShieldOff size={14} /> Uitzetten en opnieuw laten instellen</button>
              </>
              : <p className="muted" style={{ margin: '4px 0 0', fontSize: 13 }}>Staat aan. Een beheerder zet het zelf uit, met een herstelcode als de telefoon kwijt is.</p>}
          </div>
        )}
      </form>
    </Modal>
  )
}
