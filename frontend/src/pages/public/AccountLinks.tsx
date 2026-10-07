import { useEffect, useRef, useState } from 'react'
import { ArrowRight, CheckCircle2, KeyRound, MailWarning } from 'lucide-react'
import { api } from '../../lib/api'
import { Logo } from './PublicSite'

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
export function ResetPasswordPage() {
  const token = useTokenFromLink()
  const [password, setPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [error, setError] = useState<string | null>(token ? null : 'Deze link is niet compleet. Vraag hieronder een nieuwe aan.')
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (password !== repeat) return setError('De twee wachtwoorden zijn niet gelijk.')
    setBusy(true)
    setError(null)
    try {
      await api.post('/auth/reset-password', { token, password })
      window.location.assign('/')
    } catch (err) {
      setError((err as Error).message)
      setBusy(false)
    }
  }

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
