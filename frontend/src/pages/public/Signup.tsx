import { useState } from 'react'
import { Link } from 'react-router-dom'
import { ArrowRight, Check } from 'lucide-react'
import type { RegisterInput } from '../../lib/auth'
import { product } from '../../lib/plans'
import { Logo } from './PublicSite'

export function SignupPage({ onRegister }: { onRegister: (input: RegisterInput) => Promise<void> }) {
  const [form, setForm] = useState<RegisterInput>({
    company: '', name: '', email: '', password: '', demoData: false, kvk: '', businessUse: false, acceptTerms: false,
  })
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const set = (k: keyof RegisterInput) => (e: React.ChangeEvent<HTMLInputElement>) =>
    setForm(f => ({ ...f, [k]: e.target.type === 'checkbox' ? e.target.checked : e.target.value }))

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await onRegister(form)
    } catch (err) {
      setError((err as Error).message)
      setBusy(false)
    }
  }

  return (
    <div className="login">
      <div className="login-art signup-art">
        <Logo />
        <h2>Begin vandaag, betaal pas als het bevalt</h2>
        <ul className="signup-points">
          <li><Check size={16} /> 30 dagen alle functies, geen creditcard</li>
          <li><Check size={16} /> Je eigen afgeschermde werkruimte</li>
          <li><Check size={16} /> Facturen met je eigen logo en kleur</li>
          <li><Check size={16} /> Altijd al je gegevens te exporteren</li>
        </ul>
      </div>

      <form className="login-card" onSubmit={submit}>
        <h1>Account aanmaken</h1>
        <p className="muted" style={{ margin: '6px 0 22px' }}>Heb je al een account? <Link to="/inloggen">Log in</Link></p>
        <div className="form-grid">
          <label className="field"><span>Je naam</span><input required autoFocus autoComplete="name" value={form.name} onChange={set('name')} /></label>
          <label className="field"><span>Bedrijfsnaam</span><input required autoComplete="organization" value={form.company} onChange={set('company')} /></label>
          <label className="field field-full">
            <span>KvK-nummer</span>
            <input required inputMode="numeric" pattern="\s*(\d[\s.]*){8}" title="Een KvK-nummer heeft 8 cijfers." placeholder="8 cijfers" value={form.kvk} onChange={set('kvk')} />
          </label>
          <label className="field field-full"><span>E-mailadres</span><input type="email" required autoComplete="email" value={form.email} onChange={set('email')} /></label>
          <label className="field field-full"><span>Wachtwoord</span><input type="password" required minLength={8} autoComplete="new-password" placeholder="Minstens 8 tekens" value={form.password} onChange={set('password')} /></label>
        </div>
        <label className="check-row" style={{ marginTop: 14 }}>
          <input type="checkbox" checked={form.demoData} onChange={set('demoData')} />
          <span>Vul mijn werkruimte met voorbeelddata om rond te kijken</span>
        </label>
        <label className="check-row check-row-top">
          <input type="checkbox" required checked={form.businessUse} onChange={set('businessUse')} />
          <span>Ik gebruik {product.name} voor mijn bedrijf. Het is niet bedoeld voor privégebruik.</span>
        </label>
        <label className="check-row check-row-top">
          <input type="checkbox" required checked={form.acceptTerms} onChange={set('acceptTerms')} />
          <span>
            Ik ga akkoord met de <a href="/voorwaarden" target="_blank" rel="noopener">algemene voorwaarden</a> en
            de <a href="/verwerkersovereenkomst" target="_blank" rel="noopener">verwerkersovereenkomst</a>.
            Lees ook onze <a href="/privacy" target="_blank" rel="noopener">privacyverklaring</a>.
          </span>
        </label>
        {error && <div className="login-error">{error}</div>}
        <button className="btn btn-primary login-btn" disabled={busy}>{busy ? 'Bezig…' : <>Gratis beginnen <ArrowRight size={16} /></>}</button>
      </form>
    </div>
  )
}
