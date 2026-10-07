import { useState } from 'react'
import { Link } from 'react-router-dom'
import { LogIn, Receipt, Target, Workflow, CalendarDays } from 'lucide-react'
import { Logo } from './public/PublicSite'

export function LoginPage({ onLogin }: { onLogin: (email: string, password: string) => Promise<void> }) {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await onLogin(email, password)
    } catch (err) {
      setError((err as Error).message)
      setBusy(false)
    }
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
    </div>
  )
}
