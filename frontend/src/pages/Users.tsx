import { useState } from 'react'
import { ShieldCheck, Plus, Trash2 } from 'lucide-react'
import { useApi } from '../lib/useApi'
import { api } from '../lib/api'
import { useAuth, type User } from '../lib/auth'
import { useToast } from '../lib/toast'
import { date, initials, relative } from '../lib/format'
import { colorFor } from '../lib/status'
import { Badge, ErrorBox, Field, Loading, Modal, PageHeader } from '../components/ui'

export function UsersPage() {
  const { user: me } = useAuth()
  const { data, error, loading, reload } = useApi<User[]>('/users')
  const [editing, setEditing] = useState<Partial<User> | null>(null)

  if (me.role !== 'beheerder') return <ErrorBox message="Alleen beheerders kunnen gebruikers beheren." />

  return (
    <>
      <PageHeader
        icon={<ShieldCheck size={20} />}
        title="Gebruikers"
        subtitle="Wie er in het portaal mag en wat ze mogen."
        actions={<button className="btn btn-primary" onClick={() => setEditing({ role: 'medewerker', active: true })}><Plus size={16} /> Nieuwe gebruiker</button>}
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
                    <td><Badge tone={u.active ? 'green' : 'gray'}>{u.active ? 'Actief' : 'Uitgeschakeld'}</Badge></td>
                    <td className="muted">{u.lastLoginAt ? relative(u.lastLoginAt) : 'Nog nooit'}</td>
                    <td className="muted">{date(u.createdAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
      <p className="muted" style={{ fontSize: 12, marginTop: 14 }}>
        Beheerders kunnen alles, ook gebruikers beheren. Medewerkers kunnen alles behalve gebruikers beheren.
      </p>
      {editing && <UserForm user={editing} isMe={editing.id === me.id} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload() }} />}
    </>
  )
}

function UserForm({ user, isMe, onClose, onSaved }: { user: Partial<User>; isMe: boolean; onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState({ name: user.name ?? '', email: user.email ?? '', role: user.role ?? 'medewerker', active: user.active ?? true, password: '' })
  const toast = useToast()

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
      </form>
    </Modal>
  )
}
