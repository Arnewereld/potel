import { useState } from 'react'
import { Link } from 'react-router-dom'
import { ArrowRight, KeyRound, LogIn, Receipt, Target, Workflow, CalendarDays } from 'lucide-react'
import { Logo } from './public/PublicSite'
import type { CodeLogin, PublicAuth } from '../lib/auth'
import { TwoFactorLoginStep } from '../components/TwoFactor'
import { resetSession } from '../lib/storage'

export function LoginPage({ auth }: { auth: PublicAuth }) {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  // Na het wachtwoord: de code van inloggen in twee stappen, en daarna eventueel een melding over je herstelcodes.
  const [step, setStep] = useState<'password' | 'code'>('password')
  const [recovered, setRecovered] = useState<CodeLogin | null>(null)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (await auth.login(email, password) === 'code') {
        setPassword('')
        setStep('code')
        setBusy(false)
      }
    } catch (err) {
      setError((err as Error).message)
      setBusy(false)
    }
  }

  const codeDone = (r: CodeLogin) => {
    // Met een herstelcode eerst laten zien hoeveel er nog over zijn.
    if (r.recoveryCodesLeft !== null) setRecovered(r)
    else auth.enter(r.user)
  }

  // Opnieuw laden op Instellingen, Account: je bent al ingelogd, en zo begin je met alleen dat tabblad.
  const toSettings = () => {
    resetSession()
    window.location.assign('/instellingen?tab=account')
  }

  const back = (message: string | null) => {
    setStep('password')
    setError(message)
  }

  return (
    <div className="login">
      <div className="login-art" aria-hidden>
        <div className="login-flow">
          <span className="login-node" style={{ '--c': '#ff6d5a' } as React.CSSProperties}><Target size={26} /></span>
          <span className="login-line" />
          <span className="login-node" style={{ '--c': '#4ea5ff' } as React.CSSProperties}><CalendarDays size={26} /></span>
          <span className="login-line" />
          <span className="login-node" style={{ '--c': '#f5b83d' } as React.CSSProperties}><Receipt size={26} /></span>
          <span className="login-line" />
          <span className="login-node" style={{ '--c': '#3ecf8e' } as React.CSSProperties}><Workflow size={26} /></span>
        </div>
        <h2>Je freelancepraktijk op één plek</h2>
        <p>Uren, projecten, facturen, klanten en leads, plus je eigen werkstromen.</p>
      </div>

      {recovered ? (
        <div className="login-card">
          <div style={{ marginBottom: 22 }}><Logo /></div>
          <h1>Ingelogd met een herstelcode</h1>
          <p className="muted" style={{ margin: '6px 0 0' }}>
            {recovered.recoveryCodesLeft === 0
              ? 'Dit was je laatste herstelcode. Maak nu nieuwe aan bij Instellingen, Account, zodat je niet buitengesloten raakt als je je telefoon kwijt bent.'
              : `Die code werkt nu niet meer. Je hebt er nog ${recovered.recoveryCodesLeft} over. Ben je je telefoon kwijt? Zet inloggen in twee stappen dan opnieuw op met je nieuwe telefoon, bij Instellingen, Account.`}
          </p>
          <button className="btn btn-primary login-btn" onClick={toSettings}>
            <KeyRound size={16} /> Naar inloggen in twee stappen
          </button>
          <button className="btn login-btn" style={{ marginTop: 10 }} onClick={() => auth.enter(recovered.user)}>Naar je werkruimte <ArrowRight size={16} /></button>
        </div>
      ) : step === 'code' ? (
        <div className="login-card">
          <div style={{ marginBottom: 22 }}><Logo /></div>
          <TwoFactorLoginStep onDone={codeDone} onExpired={back} onBack={() => back(null)} />
        </div>
      ) : (
      <form className="login-card" onSubmit={submit}>
        <div style={{ marginBottom: 22 }}><Logo /></div>
        <h1>Inloggen</h1>
        <p className="muted" style={{ margin: '6px 0 22px' }}>Welkom terug. Nog geen account? <Link to="/aanmelden">Probeer 30 dagen gratis</Link></p>
        <label className="field">
          <span>E-mailadres</span>
          <input type="email" autoComplete="username" autoFocus required value={email} onChange={e => setEmail(e.target.value)} />
        </label>
        <label className="field" style={{ marginTop: 14 }}>
          <span className="field-label-row">Wachtwoord <Link to="/wachtwoord-vergeten">Wachtwoord vergeten?</Link></span>
          <input type="password" autoComplete="current-password" required value={password} onChange={e => setPassword(e.target.value)} />
        </label>
        {error && <div className="login-error">{error}</div>}
        <button className="btn btn-primary login-btn" disabled={busy}><LogIn size={16} /> {busy ? 'Bezig…' : 'Inloggen'}</button>
      </form>
      )}
    </div>
  )
}
