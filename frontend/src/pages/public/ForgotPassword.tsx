import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Mail } from 'lucide-react'
import { api } from '../../lib/api'
import { Logo } from './PublicSite'

// Wachtwoord vergeten: je krijgt een link per mail. Het antwoord is altijd hetzelfde, of er nu een account bij het adres hoort of niet.
export function ForgotPasswordPage() {
  const [email, setEmail] = useState('')
  const [sent, setSent] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const res = await api.post<{ message: string }>('/auth/forgot-password', { email })
      setSent(res.message)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login login-single">
      <form className="login-card" onSubmit={submit}>
        <div style={{ marginBottom: 22 }}><Logo /></div>
        <h1>Wachtwoord vergeten</h1>
        <p className="muted" style={{ margin: '6px 0 22px' }}>Vul je e-mailadres in. Je krijgt een link waarmee je een nieuw wachtwoord kiest.</p>
        {sent ? <div className="login-ok">{sent}</div> : (<>
          <label className="field">
            <span>E-mailadres</span>
            <input type="email" autoComplete="username" autoFocus required value={email} onChange={e => setEmail(e.target.value)} />
          </label>
          {error && <div className="login-error">{error}</div>}
          <button className="btn btn-primary login-btn" disabled={busy}><Mail size={16} /> {busy ? 'Bezig…' : 'Stuur me een link'}</button>
        </>)}
        <p className="muted" style={{ marginTop: 18, fontSize: 14 }}><Link to="/inloggen">Terug naar inloggen</Link></p>
      </form>
    </div>
  )
}
