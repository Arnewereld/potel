import { useEffect, useRef, useState } from 'react'
import { ArrowRight, CheckCircle2, KeyRound, MailWarning } from 'lucide-react'
import { api } from '../../lib/api'
import { Logo } from './PublicSite'
import { TwoFactorLoginStep } from '../../components/TwoFactor'

// Het token staat achter een # in de link uit de mail, zodat het niet in logboeken van servers belandt.
function useTokenFromLink() {
  const [token] = useState(() => new URLSearchParams(window.location.hash.slice(1)).get('token') ?? '')
  useEffect(() => {
    // Haal het token uit de adresbalk en de geschiedenis.
    if (window.location.hash) window.history.replaceState(null, '', window.location.pathname)
  }, [])
  return token
}

function LinkCard({ children }: { children: React.ReactNode }) {
  return (
    <div className="login login-single">
      <div className="login-card">
        <div style={{ marginBottom: 22 }}><Logo /></div>
        {children}
      </div>
    </div>
  )
}

// Een nieuw wachtwoord kiezen met de link uit de mail. Daarna ben je meteen ingelogd en overal anders uitgelogd.
// Met inloggen in twee stappen vraagt de server daarna nog een code: een nieuw wachtwoord slaat die stap nooit over.
export function ResetPasswordPage() {
  const token = useTokenFromLink()
  const [password, setPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [error, setError] = useState<string | null>(token ? null : 'Deze link is niet compleet. Vraag hieronder een nieuwe aan.')
  const [busy, setBusy] = useState(false)
  const [code, setCode] = useState<'nodig' | 'verlopen' | null>(null)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (password !== repeat) return setError('De twee wachtwoorden zijn niet gelijk.')
    setBusy(true)
    setError(null)
    try {
      const r = await api.post<{ twoFactorRequired?: boolean }>('/auth/reset-password', { token, password })
      if (r?.twoFactorRequired) {
        setCode('nodig')
        setBusy(false)
      } else window.location.assign('/')
    } catch (err) {
      setError((err as Error).message)
      setBusy(false)
    }
  }

  if (code === 'nodig') return (
    <LinkCard>
      <div className="login-ok" style={{ margin: '0 0 18px' }}><CheckCircle2 size={16} /> Je nieuwe wachtwoord is opgeslagen.</div>
      <TwoFactorLoginStep onDone={() => window.location.assign('/')} onExpired={() => setCode('verlopen')} onBack={() => window.location.assign('/')} />
    </LinkCard>
  )

  if (code === 'verlopen') return (
    <LinkCard>
      <h1>Nieuw wachtwoord</h1>
      <div className="login-ok"><CheckCircle2 size={16} /> Je nieuwe wachtwoord is opgeslagen. Je inlogpoging is wel verlopen; log opnieuw in met je e-mailadres en je nieuwe wachtwoord.</div>
      <a className="btn btn-primary login-btn" href="/">Naar inloggen <ArrowRight size={16} /></a>
    </LinkCard>
  )

  return (
    <LinkCard>
      <form onSubmit={submit}>
        <h1>Nieuw wachtwoord</h1>
        <p className="muted" style={{ margin: '6px 0 22px' }}>Kies een nieuw wachtwoord van minstens 8 tekens. Daarna ben je op al je andere apparaten uitgelogd.</p>
        <label className="field">
          <span>Nieuw wachtwoord</span>
          <input type="password" autoComplete="new-password" autoFocus required minLength={8} value={password} onChange={e => setPassword(e.target.value)} />
        </label>
        <label className="field" style={{ marginTop: 14 }}>
          <span>Herhaal je nieuwe wachtwoord</span>
          <input type="password" autoComplete="new-password" required minLength={8} value={repeat} onChange={e => setRepeat(e.target.value)} />
        </label>
        {error && <div className="login-error">{error} <a href="/wachtwoord-vergeten">Nieuwe link aanvragen</a></div>}
        <button className="btn btn-primary login-btn" disabled={busy || !token}><KeyRound size={16} /> {busy ? 'Bezig…' : 'Wachtwoord opslaan'}</button>
      </form>
    </LinkCard>
  )
}

// De link uit de bevestigingsmail. Werkt ook in een browser waar je niet bent ingelogd.
export function VerifyEmailPage() {
  const token = useTokenFromLink()
  const [state, setState] = useState<{ ok: boolean; text: string } | null>(token ? null : { ok: false, text: 'Deze link is niet compleet. Log in en vraag een nieuwe bevestigingsmail aan.' })
  const started = useRef(false)

  useEffect(() => {
    if (!token || started.current) return
    started.current = true
    api.post<{ email: string }>('/auth/verify-email', { token })
      .then(r => setState({ ok: true, text: `Je e-mailadres ${r.email} is bevestigd. Je werkstromen kunnen nu ook e-mail versturen.` }))
      .catch(e => setState({ ok: false, text: (e as Error).message }))
  }, [token])

  return (
    <LinkCard>
      <h1>E-mailadres bevestigen</h1>
      {!state && <p className="muted">Even geduld…</p>}
      {state && (
        <div className={state.ok ? 'login-ok' : 'login-error'}>
          {state.ok ? <CheckCircle2 size={16} /> : <MailWarning size={16} />} {state.text}
        </div>
      )}
      <a className="btn btn-primary login-btn" href="/">Naar je werkruimte <ArrowRight size={16} /></a>
    </LinkCard>
  )
}

interface InviteInfo { workspace: string; name: string; email: string; existingAccount: boolean }

// De link uit de uitnodiging als boekhouder. Nieuw: je kiest hier je wachtwoord en bent meteen binnen. Ben je al boekhouder
// in een andere werkruimte, dan komt deze erbij en log je in met je bestaande wachtwoord.
export function AcceptInvitePage() {
  const token = useTokenFromLink()
  const [info, setInfo] = useState<InviteInfo | null>(null)
  const [error, setError] = useState<string | null>(token ? null : 'Deze link is niet compleet. Vraag je klant om je opnieuw uit te nodigen.')
  const [password, setPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [busy, setBusy] = useState(false)
  const [joined, setJoined] = useState(false)
  const started = useRef(false)

  useEffect(() => {
    if (!token || started.current) return
    started.current = true
    api.post<InviteInfo>('/auth/invite', { token }).then(setInfo).catch(e => setError((e as Error).message))
  }, [token])

  const accept = async (e: React.FormEvent) => {
    e.preventDefault()
    if (info && !info.existingAccount && password !== repeat) return setError('De twee wachtwoorden zijn niet gelijk.')
    setBusy(true)
    setError(null)
    try {
      const r = await api.post<{ signedIn: boolean }>('/auth/invite/accept', { token, password: info?.existingAccount ? null : password })
      if (r.signedIn) window.location.assign('/')
      else { setJoined(true); setBusy(false) }
    } catch (err) {
      setError((err as Error).message)
      setBusy(false)
    }
  }

  if (joined && info) return (
    <LinkCard>
      <h1>Uitnodiging aangenomen</h1>
      <div className="login-ok"><CheckCircle2 size={16} /> Je hebt nu ook toegang tot de administratie van {info.workspace}.</div>
      <p className="muted" style={{ margin: '14px 0 0' }}>
        Log in met {info.email} en je bestaande wachtwoord. Daarna wissel je tussen je klanten via het menu onder je naam.
      </p>
      <a className="btn btn-primary login-btn" href="/">Naar inloggen <ArrowRight size={16} /></a>
    </LinkCard>
  )

  if (!info) return (
    <LinkCard>
      <h1>Uitnodiging als boekhouder</h1>
      {error ? <div className="login-error"><MailWarning size={16} /> {error}</div> : <p className="muted">Even geduld…</p>}
      {error && <a className="btn btn-primary login-btn" href="/">Naar inloggen <ArrowRight size={16} /></a>}
    </LinkCard>
  )

  return (
    <LinkCard>
      <form onSubmit={accept}>
        <h1>Welkom, {info.name}</h1>
        <p className="muted" style={{ margin: '6px 0 22px' }}>
          {info.workspace} geeft je toegang als boekhouder. Je kunt de facturen, kosten, bank, btw-aangifte en het jaaroverzicht bekijken en
          exporteren. Wijzigen kun je niets.
        </p>
        {info.existingAccount ? (
          <p className="muted" style={{ margin: '0 0 6px' }}>
            Je bent al boekhouder in een andere werkruimte met {info.email}. Neem de uitnodiging aan en log daarna in met je bestaande wachtwoord.
          </p>
        ) : (
          <>
            <label className="field">
              <span>Kies een wachtwoord</span>
              <input type="password" autoComplete="new-password" autoFocus required minLength={8} value={password} onChange={e => setPassword(e.target.value)} />
            </label>
            <label className="field" style={{ marginTop: 14 }}>
              <span>Herhaal je wachtwoord</span>
              <input type="password" autoComplete="new-password" required minLength={8} value={repeat} onChange={e => setRepeat(e.target.value)} />
            </label>
            <p className="muted" style={{ margin: '8px 0 0', fontSize: 13 }}>Minstens 8 tekens. Je logt in met {info.email}.</p>
          </>
        )}
        {error && <div className="login-error">{error}</div>}
        <button className="btn btn-primary login-btn" disabled={busy}><CheckCircle2 size={16} /> {busy ? 'Bezig…' : 'Uitnodiging aannemen'}</button>
      </form>
    </LinkCard>
  )
}
