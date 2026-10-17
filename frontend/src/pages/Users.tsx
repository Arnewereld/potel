import { useState } from 'react'
import { ShieldCheck, Plus, Trash2, ShieldOff, KeyRound, BookOpenCheck, Send } from 'lucide-react'
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
  const [accountant, setAccountant] = useState<Partial<User> | null>(null)
  // Boekhouders tellen niet mee voor de plekken van je abonnement; ze hebben een eigen, vaste grens.
  const team = (data ?? []).filter(u => u.role !== 'boekhouder')
  const accountants = (data ?? []).filter(u => u.role === 'boekhouder')
  const full = !!workspace && !!data && team.length >= workspace.maxUsers
  const maxAccountants = workspace?.maxAccountants ?? 3
  const accountantsFull = accountants.length >= maxAccountants

  if (me.role !== 'beheerder') return <ErrorBox message="Alleen beheerders kunnen gebruikers beheren." />

  return (
    <>
      <PageHeader
        icon={<ShieldCheck size={20} />}
        title="Gebruikers"
        subtitle="Wie er in het portaal mag en wat ze mogen."
        actions={<>
          <button className="btn" disabled={accountantsFull} title={accountantsFull ? `Je kunt hooguit ${maxAccountants} boekhouders uitnodigen` : undefined}
            onClick={() => setAccountant({ role: 'boekhouder', active: true })}><BookOpenCheck size={16} /> Boekhouder uitnodigen</button>
          <button className="btn btn-primary" disabled={full} title={full ? 'Je abonnement zit vol' : undefined} onClick={() => setEditing({ role: 'medewerker', active: true })}><Plus size={16} /> Nieuwe gebruiker</button>
        </>}
      />
      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && <Loading />}
      {data && (
        <div className="card">
          <div className="table-wrap">
            <table>
              <thead><tr><th>Naam</th><th>Rol</th><th>Status</th><th>Laatst ingelogd</th><th>Sinds</th></tr></thead>
              <tbody>
                {team.map(u => (
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
      {data && <AccountantList accountants={accountants} max={maxAccountants} onOpen={setAccountant} />}
      {workspace && <TwoFactorPolicy required={workspace.requireTwoFactorForAdmins} forAccountants={workspace.requireTwoFactorForAccountants === true}
        meOn={me.twoFactor} onChanged={reloadWorkspace} />}
      <p className="muted" style={{ fontSize: 12, marginTop: 14 }}>
        Beheerders kunnen alles. Medewerkers kunnen alles behalve gebruikers beheren, de bedrijfsgegevens en huisstijl wijzigen,
        werkstromen met e-mail of een webhook maken, en gegevens exporteren of de werkruimte verwijderen.
        {workspace && data && <> Je gebruikt {team.length} van de {workspace.maxUsers} {workspace.maxUsers === 1 ? 'plek' : 'plekken'} van je abonnement; uitgeschakelde gebruikers tellen mee, boekhouders niet.</>}
      </p>
      {editing && <UserForm user={editing} isMe={editing.id === me.id} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload() }} />}
      {accountant && <AccountantForm user={accountant} onClose={() => setAccountant(null)} onSaved={() => { setAccountant(null); reload() }} />}
    </>
  )
}

// Een werkruimte-instelling: beheerders moeten inloggen in twee stappen aan hebben. Wie het nog niet heeft, stelt het in bij de volgende keer.
function TwoFactorPolicy({ required, forAccountants, meOn, onChanged }: { required: boolean; forAccountants: boolean; meOn: boolean; onChanged: () => Promise<void> }) {
  const [busy, setBusy] = useState(false)
  const toast = useToast()

  // Voor boekhouders kan het alleen verplicht zijn als het voor beheerders al verplicht is.
  const change = async (admins: boolean, accountants: boolean) => {
    setBusy(true)
    try {
      await api.put('/workspace/two-factor', { requireForAdmins: admins, requireForAccountants: admins && accountants })
      await onChanged()
      toast(admins !== required
        ? admins ? 'Inloggen in twee stappen is nu verplicht voor beheerders' : 'Inloggen in twee stappen is niet meer verplicht voor beheerders en boekhouders'
        : accountants ? 'Inloggen in twee stappen is nu ook verplicht voor boekhouders' : 'Inloggen in twee stappen is niet meer verplicht voor boekhouders')
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
          <input type="checkbox" checked={required} disabled={busy || (!required && !meOn)} onChange={e => change(e.target.checked, forAccountants)} />
          <span>
            <strong>Verplicht voor beheerders.</strong> Beheerders zonder inloggen in twee stappen moeten het eerst instellen voordat
            ze verder kunnen. Medewerkers kiezen het zelf.
          </span>
        </label>
        {required && (
          <label className="check-row check-row-top">
            <input type="checkbox" checked={forAccountants} disabled={busy} onChange={e => change(true, e.target.checked)} />
            <span>
              <strong>Ook verplicht voor boekhouders.</strong> Een boekhouder zonder inloggen in twee stappen stelt het eerst in voordat
              hij je administratie kan bekijken. Het geldt dan ook in de andere werkruimtes waar hij boekhouder is.
            </span>
          </label>
        )}
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

// Boekhouders: meekijken in de administratie zonder iets te wijzigen. Ze tellen niet mee voor de plekken van je abonnement.
function AccountantList({ accountants, max, onOpen }: { accountants: User[]; max: number; onOpen: (u: User) => void }) {
  return (
    <div className="card" style={{ marginTop: 18 }}>
      <div className="card-head"><h3><BookOpenCheck size={16} /> Boekhouders</h3></div>
      {accountants.length === 0 ? (
        <div className="card-body muted">
          Nodig je boekhouder uit om mee te kijken. Die ziet je facturen, kosten met bonnetjes, bank, btw-aangifte en jaaroverzicht,
          en kan ze exporteren. Wijzigen, versturen of verwijderen kan een boekhouder niet. Je kunt er {max} uitnodigen, los van de plekken van je abonnement.
        </div>
      ) : (
        <div className="table-wrap">
          <table>
            <thead><tr><th>Naam</th><th>Status</th><th>Mag ook</th><th>Laatst ingelogd</th></tr></thead>
            <tbody>
              {accountants.map(u => (
                <tr key={u.id} className="clickable" onClick={() => onOpen(u)}>
                  <td>
                    <div className="cell-main">
                      <div className="avatar" style={{ background: colorFor(u.name) }}>{initials(u.name)}</div>
                      <div><div>{u.name}</div><div className="cell-sub">{u.email}</div></div>
                    </div>
                  </td>
                  <td>
                    <div className="row wrap" style={{ gap: 6 }}>
                      {u.invitePending
                        ? <Badge tone="orange">Uitgenodigd</Badge>
                        : <Badge tone={u.active ? 'green' : 'gray'}>{u.active ? 'Actief' : 'Uitgeschakeld'}</Badge>}
                      {u.twoFactor && <span className="badge badge-blue badge-plain">Twee stappen</span>}
                    </div>
                  </td>
                  <td className="muted">{[u.canFileVat && 'btw-aangifte markeren', u.canCompleteExpenses && 'kosten aanvullen'].filter(Boolean).join(', ') || 'Alleen bekijken'}</td>
                  <td className="muted">{u.invitePending ? 'Nog niet' : u.lastLoginAt ? relative(u.lastLoginAt) : 'Nog nooit'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

function AccountantForm({ user, onClose, onSaved }: { user: Partial<User>; onClose: () => void; onSaved: () => void }) {
  const [form, setForm] = useState({
    name: user.name ?? '', email: user.email ?? '', active: user.active ?? true,
    canFileVat: user.canFileVat ?? false, canCompleteExpenses: user.canCompleteExpenses ?? false,
  })
  const [busy, setBusy] = useState(false)
  const toast = useToast()

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    try {
      const body = { ...form, role: 'boekhouder' }
      if (user.id) await api.put(`/users/${user.id}`, body)
      else await api.post('/users', body)
      toast(user.id ? 'Boekhouder opgeslagen' : `Uitnodiging verstuurd naar ${form.email}`)
      onSaved()
    } catch (err) {
      toast((err as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const resend = async () => {
    setBusy(true)
    try {
      await api.post(`/users/${user.id}/invite`)
      toast(`Uitnodiging opnieuw verstuurd naar ${user.email}`)
    } catch (err) {
      toast((err as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    if (!confirm(`De toegang van ${user.name} intrekken? ${user.name} kan je administratie daarna niet meer bekijken.`)) return
    try {
      await api.del(`/users/${user.id}`)
      toast('Toegang ingetrokken')
      onSaved()
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  return (
    <Modal title={user.id ? 'Boekhouder' : 'Boekhouder uitnodigen'} onClose={onClose} footer={<>
      {user.id && <button className="btn btn-danger" type="button" onClick={remove} title="Toegang intrekken"><Trash2 size={15} /></button>}
      {user.id && user.invitePending && <button className="btn" type="button" disabled={busy} onClick={resend}><Send size={15} /> Opnieuw versturen</button>}
      <span className="spacer" />
      <button className="btn" type="button" onClick={onClose}>Annuleren</button>
      <button className="btn btn-primary" form="accountant-form" disabled={busy}>{user.id ? 'Opslaan' : 'Uitnodiging versturen'}</button>
    </>}>
      <form id="accountant-form" className="form-grid" onSubmit={save}>
        <p className="muted field-full" style={{ margin: 0, fontSize: 13 }}>
          Een boekhouder ziet je facturen en creditnota's, offertes, kosten met bonnetjes, vaste kosten, bank, ritten, investeringen,
          btw-aangifte, jaaroverzicht, belastingpot en de factuurgegevens van je klanten, en kan die exporteren. Uren, projecten, leads,
          planning, werkstromen, instellingen en koppelingen ziet hij niet. Elke export komt in je activiteitenlog.
        </p>
        <Field label="Naam *"><input required autoFocus={!user.id} value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} /></Field>
        <Field label="E-mailadres *">
          <input type="email" required disabled={!!user.id} value={form.email} onChange={e => setForm({ ...form, email: e.target.value })} />
        </Field>
        {!user.id && (
          <p className="muted field-full" style={{ margin: 0, fontSize: 13 }}>
            Je boekhouder krijgt een mail met een link. Daarmee kiest hij zelf een wachtwoord. Is hij al boekhouder in een andere werkruimte,
            dan logt hij in met zijn bestaande account en kan hij wisselen tussen de administraties.
          </p>
        )}
        <div className="field-full">
          <div className="field-label">Mag daarnaast</div>
          <label className="check-row check-row-top" style={{ marginTop: 4 }}>
            <input type="checkbox" checked={form.canFileVat} onChange={e => setForm({ ...form, canFileVat: e.target.checked })} />
            <span><strong>De btw-aangifte als gedaan markeren.</strong> Hij krijgt dan ook de melding als een aangifte bijna moet.</span>
          </label>
          <label className="check-row check-row-top">
            <input type="checkbox" checked={form.canCompleteExpenses} onChange={e => setForm({ ...form, canCompleteExpenses: e.target.checked })} />
            <span><strong>Kosten aanvullen.</strong> Een ontbrekend bonnetje toevoegen of de categorie van een uitgave aanpassen. Verder niets aan je kosten.</span>
          </label>
        </div>
        {user.id && !user.invitePending && (
          <label className="row field-full">
            <input type="checkbox" checked={form.active} onChange={e => setForm({ ...form, active: e.target.checked })} /> Heeft toegang (zet uit om tijdelijk te blokkeren)
          </label>
        )}
        {user.id && (
          <p className="muted field-full" style={{ margin: 0, fontSize: 13 }}>
            {user.invitePending
              ? `De uitnodiging is nog niet aangenomen. De link in de mail werkt 7 dagen.`
              : `Laatst ingelogd: ${user.lastLoginAt ? relative(user.lastLoginAt) : 'nog nooit'}. Wachtwoord en inloggen in twee stappen beheert je boekhouder zelf.`}
          </p>
        )}
      </form>
    </Modal>
  )
}
