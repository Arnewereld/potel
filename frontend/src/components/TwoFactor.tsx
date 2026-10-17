import { useState } from 'react'
import { ArrowLeft, Check, Copy, Download, KeyRound, LogIn, LogOut, RefreshCw, ShieldCheck, ShieldOff, Smartphone } from 'lucide-react'
import { api, ApiError } from '../lib/api'
import { useAuth, type CodeLogin, type User } from '../lib/auth'
import { useApi } from '../lib/useApi'
import { isAccountant, switchWorkspace, type WorkspaceChoice } from '../lib/accountant'
import { useToast } from '../lib/toast'
import { date } from '../lib/format'
import { Badge, Field, Loading, Modal } from './ui'

interface TwoFactorStatus {
  enabled: boolean
  enabledAt?: string | null
  recoveryCodesLeft: number
  setupRequired: boolean
  requiredForAdmins: boolean
  // Verplicht voor jou: als beheerder, of als boekhouder als de werkruimte het ook voor boekhouders verplicht.
  requiredForMe?: boolean
}

interface SetupInfo { key: string; otpauthUrl: string; issuer: string; account: string }

// Enter in een veld doet hetzelfde als de knop. Deze stukken staan soms binnen het formulier van Instellingen, dus zonder eigen <form>.
const onEnter = (fn: () => void) => (e: React.KeyboardEvent) => {
  if (e.key === 'Enter') {
    e.preventDefault()
    fn()
  }
}

function useCopy() {
  const toast = useToast()
  return async (text: string, done: string) => {
    try {
      await navigator.clipboard.writeText(text)
      toast(done)
    } catch {
      toast('Kopiëren lukt niet in deze browser. Selecteer de tekst en kopieer hem zelf.', 'error')
    }
  }
}

// De herstelcodes, met kopiëren en opslaan als tekstbestand. Je ziet ze maar één keer.
export function RecoveryCodes({ codes, account }: { codes: string[]; account: string }) {
  const copy = useCopy()
  const text = `Herstelcodes voor inloggen in twee stappen bij Potel (${account})\nElke code werkt één keer.\n\n${codes.join('\n')}\n`

  const save = () => {
    const href = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }))
    const a = Object.assign(document.createElement('a'), { href, download: 'potel-herstelcodes.txt' })
    a.click()
    setTimeout(() => URL.revokeObjectURL(href), 1000)
  }

  return (
    <div>
      <ol className="recovery-codes" aria-label="Herstelcodes">
        {codes.map(c => <li key={c}><code>{c}</code></li>)}
      </ol>
      <div className="row wrap" style={{ gap: 8, marginTop: 12 }}>
        <button type="button" className="btn btn-sm" onClick={() => copy(codes.join('\n'), 'Herstelcodes gekopieerd')}><Copy size={14} /> Kopiëren</button>
        <button type="button" className="btn btn-sm" onClick={save}><Download size={14} /> Opslaan als tekstbestand</button>
      </div>
    </div>
  )
}

// Instellen in drie stappen: wachtwoord, sleutel in je app zetten en een code invullen, herstelcodes bewaren.
function SetupFlow({ onEnabled, onFinish, onCancel }: {
  onEnabled?: (user: User) => void
  onFinish: (user: User) => void
  onCancel?: () => void
}) {
  const [password, setPassword] = useState('')
  const [info, setInfo] = useState<SetupInfo | null>(null)
  const [code, setCode] = useState('')
  const [done, setDone] = useState<{ user: User; codes: string[] } | null>(null)
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const copy = useCopy()

  const run = async (fn: () => Promise<void>) => {
    if (busy) return
    setBusy(true)
    setError(null)
    try {
      await fn()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const start = () => run(async () => {
    setInfo(await api.post<SetupInfo>('/auth/two-factor/setup', { password }))
    setPassword('')
  })

  const enable = () => run(async () => {
    const r = await api.post<{ recoveryCodes: string[]; user: User }>('/auth/two-factor/enable', { code })
    setDone({ user: r.user, codes: r.recoveryCodes })
    onEnabled?.(r.user)
  })

  if (done) return (
    <div>
      <div className="login-ok" style={{ marginTop: 0 }}><Check size={16} /> Inloggen in twee stappen staat aan.</div>
      <p>
        Bewaar deze herstelcodes op een veilige plek, bijvoorbeeld in je wachtwoordmanager of op papier. Ben je je telefoon kwijt,
        dan log je met een van deze codes in. Elke code werkt één keer, en je ziet ze hier maar één keer.
      </p>
      <RecoveryCodes codes={done.codes} account={done.user.email} />
      <label className="check-row check-row-top" style={{ marginTop: 16 }}>
        <input type="checkbox" checked={saved} onChange={e => setSaved(e.target.checked)} /> Ik heb mijn herstelcodes bewaard.
      </label>
      <div className="row" style={{ justifyContent: 'flex-end', marginTop: 16 }}>
        <button type="button" className="btn btn-primary" disabled={!saved} onClick={() => onFinish(done.user)}><Check size={15} /> Klaar</button>
      </div>
    </div>
  )

  if (!info) return (
    <div>
      <p style={{ marginTop: 0 }}>
        Na je wachtwoord vul je voortaan ook een code van 6 cijfers in uit een app op je telefoon. Zo komt niemand in je account met
        alleen je wachtwoord. Vul eerst je wachtwoord in om te beginnen.
      </p>
      <Field label="Wachtwoord">
        <input type="password" autoComplete="current-password" autoFocus value={password} onChange={e => setPassword(e.target.value)} onKeyDown={onEnter(start)} />
      </Field>
      {error && <div className="login-error">{error}</div>}
      <div className="row" style={{ justifyContent: 'flex-end', gap: 8, marginTop: 16 }}>
        {onCancel && <button type="button" className="btn" onClick={onCancel}>Annuleren</button>}
        <button type="button" className="btn btn-primary" disabled={busy || !password} onClick={start}>Verder</button>
      </div>
    </div>
  )

  return (
    <div>
      <ol className="steps two-factor-steps">
        <li><div>Open je app voor inlogcodes en voeg een account toe. Kies daarbij voor een sleutel invoeren of handmatig toevoegen.</div></li>
        <li>
          <div>
            Vul deze sleutel in, met als naam bijvoorbeeld {info.issuer} ({info.account}):
            <div className="setup-key-row">
              <code className="setup-key" aria-label="Sleutel voor je app">{info.key}</code>
              <button type="button" className="btn btn-sm" onClick={() => copy(info.key.replaceAll(' ', ''), 'Sleutel gekopieerd')}><Copy size={14} /> Kopiëren</button>
            </div>
          </div>
        </li>
        <li><div>Vul hieronder de code van 6 cijfers in die je app nu laat zien.</div></li>
      </ol>
      <p className="muted" style={{ fontSize: 13 }}>
        Er is geen QR-code om te scannen. Apps zoals Google Authenticator, Microsoft Authenticator, 1Password en Bitwarden accepteren
        deze sleutel ook als je hem intypt of plakt. Vraagt je app om het soort code, kies dan voor op tijd gebaseerd.
        Lees je dit op je telefoon? Dan kun je ook <a href={info.otpauthUrl}><Smartphone size={13} style={{ verticalAlign: -2 }} /> de sleutel in je app openen</a>.
      </p>
      <Field label="Code uit je app">
        <input inputMode="numeric" autoComplete="one-time-code" autoFocus maxLength={7} placeholder="123456" value={code}
          onChange={e => setCode(e.target.value)} onKeyDown={onEnter(enable)} />
      </Field>
      {error && <div className="login-error">{error}</div>}
      <div className="row" style={{ justifyContent: 'flex-end', gap: 8, marginTop: 16 }}>
        {onCancel && <button type="button" className="btn" onClick={onCancel}>Annuleren</button>}
        <button type="button" className="btn btn-primary" disabled={busy || code.replace(/\D/g, '').length !== 6} onClick={enable}><ShieldCheck size={15} /> Aanzetten</button>
      </div>
    </div>
  )
}

function SetupModal({ onClose }: { onClose: (user: User | null) => void }) {
  const [enabled, setEnabled] = useState<User | null>(null)
  return (
    <Modal title="Inloggen in twee stappen instellen" onClose={() => onClose(enabled)}>
      <SetupFlow onEnabled={setEnabled} onFinish={u => onClose(u)} onCancel={enabled ? undefined : () => onClose(null)} />
    </Modal>
  )
}

function DisableModal({ onClose }: { onClose: (user: User | null) => void }) {
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const submit = async () => {
    if (busy || !password || !code) return
    setBusy(true)
    setError(null)
    try {
      onClose(await api.post<User>('/auth/two-factor/disable', { password, code }))
    } catch (e) {
      setError((e as Error).message)
      setBusy(false)
    }
  }

  return (
    <Modal title="Inloggen in twee stappen uitzetten" onClose={() => onClose(null)} footer={<>
      <button type="button" className="btn" onClick={() => onClose(null)}>Annuleren</button>
      <button type="button" className="btn btn-danger" disabled={busy || !password || !code} onClick={submit}><ShieldOff size={15} /> Uitzetten</button>
    </>}>
      <p style={{ marginTop: 0 }}>
        Daarna is je wachtwoord weer genoeg om in te loggen. Je bent op je andere apparaten uitgelogd. Vul je wachtwoord in en een code
        uit je app, of een herstelcode als je je telefoon niet meer hebt.
      </p>
      <div className="form-grid">
        <Field label="Wachtwoord" full>
          <input type="password" autoComplete="current-password" autoFocus value={password} onChange={e => setPassword(e.target.value)} onKeyDown={onEnter(submit)} />
        </Field>
        <Field label="Code uit je app of herstelcode" full>
          <input autoComplete="one-time-code" value={code} onChange={e => setCode(e.target.value)} onKeyDown={onEnter(submit)} />
        </Field>
      </div>
      {error && <div className="login-error">{error}</div>}
    </Modal>
  )
}

function NewCodesModal({ onClose }: { onClose: (user: User | null) => void }) {
  const [code, setCode] = useState('')
  const [result, setResult] = useState<{ user: User; recoveryCodes: string[] } | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const submit = async () => {
    if (busy || !code) return
    setBusy(true)
    setError(null)
    try {
      setResult(await api.post<{ user: User; recoveryCodes: string[] }>('/auth/two-factor/recovery-codes', { code }))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal title="Nieuwe herstelcodes" onClose={() => onClose(result?.user ?? null)} footer={result
      ? <button type="button" className="btn btn-primary" onClick={() => onClose(result.user)}><Check size={15} /> Klaar</button>
      : <>
        <button type="button" className="btn" onClick={() => onClose(null)}>Annuleren</button>
        <button type="button" className="btn btn-primary" disabled={busy || code.replace(/\D/g, '').length !== 6} onClick={submit}><RefreshCw size={15} /> Nieuwe codes maken</button>
      </>}>
      {result ? <>
        <p style={{ marginTop: 0 }}>Dit zijn je nieuwe herstelcodes. Je oude codes werken niet meer. Bewaar ze op een veilige plek; je ziet ze maar één keer.</p>
        <RecoveryCodes codes={result.recoveryCodes} account={result.user.email} />
      </> : <>
        <p style={{ marginTop: 0 }}>Je krijgt tien nieuwe herstelcodes en de oude werken daarna niet meer. Vul ter bevestiging de code in die nu in je app staat.</p>
        <Field label="Code uit je app">
          <input inputMode="numeric" autoComplete="one-time-code" autoFocus maxLength={7} value={code} onChange={e => setCode(e.target.value)} onKeyDown={onEnter(submit)} />
        </Field>
        {error && <div className="login-error">{error}</div>}
      </>}
    </Modal>
  )
}

// Op Instellingen, Account: de stand van inloggen in twee stappen, voor iedereen.
export function TwoFactorCard() {
  const { user, update } = useAuth()
  const { data, reload } = useApi<TwoFactorStatus>('/auth/two-factor')
  const [modal, setModal] = useState<'setup' | 'disable' | 'codes' | null>(null)
  const toast = useToast()

  const closed = (message: string) => (u: User | null) => {
    setModal(null)
    if (!u) return
    update(u)
    toast(message)
    reload(true)
  }

  const few = !!data?.enabled && data.recoveryCodesLeft <= 3

  return (
    <div className="card">
      <div className="card-head">
        <h3><ShieldCheck size={16} /> Inloggen in twee stappen</h3>
        {data && <Badge tone={data.enabled ? 'green' : 'gray'}>{data.enabled ? 'Aan' : 'Uit'}</Badge>}
      </div>
      <div className="card-body">
        {!data ? <Loading /> : data.enabled ? <>
          <p className="muted" style={{ marginTop: 0 }}>
            Na je wachtwoord vul je een code uit je app in. Aangezet op {date(data.enabledAt)}. Je hebt nog {data.recoveryCodesLeft} van
            de 10 herstelcodes.
          </p>
          {few && <div className="notice">Je hebt nog maar weinig herstelcodes. Maak nieuwe aan, zodat je er altijd een paar hebt als je je telefoon kwijt bent.</div>}
          <div className="row wrap" style={{ gap: 8 }}>
            <button type="button" className="btn" onClick={() => setModal('codes')}><RefreshCw size={15} /> Nieuwe herstelcodes</button>
            <button type="button" className="btn" onClick={() => setModal('disable')}><ShieldOff size={15} /> Uitzetten</button>
          </div>
          {data.requiredForAdmins && (user.role === 'beheerder' || data.requiredForMe) && (
            <p className="muted" style={{ fontSize: 13, marginBottom: 0 }}>
              In je werkruimte is inloggen in twee stappen verplicht voor {user.role === 'beheerder' ? 'beheerders' : 'boekhouders'}. Zet je het uit, bijvoorbeeld voor een nieuwe
              telefoon, dan stel je het daarna meteen opnieuw in.
            </p>
          )}
        </> : <>
          <p className="muted" style={{ marginTop: 0 }}>
            Met inloggen in twee stappen vul je na je wachtwoord ook een code van 6 cijfers in uit een app op je telefoon, zoals
            Google Authenticator, Microsoft Authenticator, 1Password of Bitwarden. Zo komt niemand in je account met alleen je wachtwoord.
          </p>
          <button type="button" className="btn btn-primary" onClick={() => setModal('setup')}><KeyRound size={15} /> Instellen</button>
        </>}
      </div>
      {modal === 'setup' && <SetupModal onClose={closed('Inloggen in twee stappen staat aan')} />}
      {modal === 'disable' && <DisableModal onClose={closed('Inloggen in twee stappen staat uit')} />}
      {modal === 'codes' && <NewCodesModal onClose={closed('Je nieuwe herstelcodes zijn klaar')} />}
    </div>
  )
}

// De tweede stap van inloggen: een code uit de app of een herstelcode. Ook gebruikt na een nieuw wachtwoord via de mail.
export function TwoFactorLoginStep({ onDone, onExpired, onBack }: {
  onDone: (result: CodeLogin) => void
  onExpired: (message: string) => void
  onBack: () => void
}) {
  const [code, setCode] = useState('')
  const [recovery, setRecovery] = useState(false)
  const [remember, setRemember] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      onDone(await api.post<CodeLogin>('/auth/login/two-factor', { code, rememberDevice: remember }))
    } catch (err) {
      if (err instanceof ApiError && (err.data as { expired?: boolean } | null)?.expired) return onExpired(err.message)
      setError((err as Error).message)
      setBusy(false)
    }
  }

  const switchKind = () => {
    setRecovery(r => !r)
    setCode('')
    setError(null)
  }

  return (
    <form onSubmit={submit}>
      <h1>Code invullen</h1>
      <p className="muted" style={{ margin: '6px 0 22px' }}>
        {recovery
          ? 'Vul een van je herstelcodes in. Elke herstelcode werkt één keer.'
          : 'Je hebt inloggen in twee stappen aan. Vul de code van 6 cijfers in die nu in je app staat.'}
      </p>
      <label className="field">
        <span>{recovery ? 'Herstelcode' : 'Code uit je app'}</span>
        {recovery
          ? <input key="recovery" autoComplete="off" autoCapitalize="none" spellCheck={false} autoFocus required placeholder="xxxx-xxxx-xxxx" value={code} onChange={e => setCode(e.target.value)} />
          : <input key="app" inputMode="numeric" autoComplete="one-time-code" autoFocus required maxLength={7} placeholder="123456" value={code} onChange={e => setCode(e.target.value)} />}
      </label>
      <label className="check-row" style={{ marginTop: 14 }}>
        <input type="checkbox" checked={remember} onChange={e => setRemember(e.target.checked)} /> Dit apparaat 30 dagen onthouden
      </label>
      {error && <div className="login-error">{error}</div>}
      <button className="btn btn-primary login-btn" disabled={busy}><LogIn size={16} /> {busy ? 'Bezig…' : 'Inloggen'}</button>
      <div className="row between wrap" style={{ marginTop: 14, fontSize: 13 }}>
        <button type="button" className="link-btn" onClick={onBack}><ArrowLeft size={13} /> Terug</button>
        <button type="button" className="link-btn" onClick={switchKind}>{recovery ? 'Een code uit je app gebruiken' : 'Telefoon niet bij de hand? Gebruik een herstelcode'}</button>
      </div>
    </form>
  )
}

// Voor een beheerder (of boekhouder) in een werkruimte waar inloggen in twee stappen verplicht is: eerst instellen, dan verder.
// Een boekhouder kan ook eerst naar een andere administratie waar het niet verplicht is.
export function TwoFactorRequiredPage() {
  const { user, update, logout } = useAuth()
  const [enabled, setEnabled] = useState(false)
  const accountant = isAccountant(user)
  const { data: choices } = useApi<WorkspaceChoice[]>(accountant ? '/auth/workspaces' : null)
  const others = (choices ?? []).filter(c => !c.current)
  return (
    <div className="login login-single">
      <div className="login-card two-factor-required">
        <h1>Inloggen in twee stappen instellen</h1>
        <p className="muted" style={{ margin: '6px 0 18px' }}>
          {enabled
            ? 'Bijna klaar. Bewaar je herstelcodes, dan ga je verder naar je werkruimte.'
            : `Hoi ${user.name}. In deze werkruimte is inloggen in twee stappen verplicht voor ${accountant ? 'boekhouders' : 'beheerders'}. Stel het nu in; het duurt een paar minuten.${accountant ? ' Het geldt daarna ook in je andere administraties.' : ''}`}
        </p>
        <SetupFlow onEnabled={() => setEnabled(true)} onFinish={update} />
        {!enabled && others.length > 0 && (
          <div style={{ marginTop: 18 }}>
            <p className="muted" style={{ fontSize: 13, margin: '0 0 8px' }}>Of ga eerst naar een andere administratie:</p>
            <div className="row wrap" style={{ gap: 8 }}>
              {others.map(c => <button key={c.workspaceId} type="button" className="btn btn-sm" onClick={() => void switchWorkspace(c.workspaceId)}>{c.name}</button>)}
            </div>
          </div>
        )}
        {!enabled && (
          <button type="button" className="link-btn" style={{ marginTop: 18, fontSize: 13 }} onClick={() => logout()}><LogOut size={13} /> Uitloggen</button>
        )}
      </div>
    </div>
  )
}
