import { useEffect, useState } from 'react'
import { Link, Route, Routes, useLocation } from 'react-router-dom'
import type { PublicAuth } from '../../lib/auth'
import { product } from '../../lib/plans'
import { standalone } from '../../lib/pwa'
import { LandingPage } from './Landing'
import { LoginPage } from '../Login'
import { SignupPage } from './Signup'
import { ForgotPasswordPage } from './ForgotPassword'

// Alles wat je ziet zonder in te loggen: de verkooppagina, inloggen en aanmelden. Open je Potel als app vanaf je
// beginscherm, dan kom je meteen bij inloggen in plaats van op de verkooppagina.
export function PublicSite({ auth }: { auth: PublicAuth }) {
  const { pathname } = useLocation()
  useEffect(() => { window.scrollTo(0, 0) }, [pathname])
  const [app] = useState(standalone)
  return (
    <Routes>
      <Route path="/" element={app ? <LoginPage auth={auth} /> : <LandingPage />} />
      <Route path="/aanmelden" element={<SignupPage onRegister={auth.register} />} />
      <Route path="/wachtwoord-vergeten" element={<ForgotPasswordPage />} />
      <Route path="*" element={<LoginPage auth={auth} />} />
    </Routes>
  )
}

export function Logo({ size = 22 }: { size?: number }) {
  return (
    <Link to="/" className="brand public-brand">
      <div className="brand-logo">
        <svg viewBox="0 0 32 32" width={size} height={size}><circle cx="9" cy="16" r="3.4" fill="currentColor" /><circle cx="23" cy="9" r="3.4" fill="currentColor" /><circle cx="23" cy="23" r="3.4" fill="currentColor" /><path d="M12 16h3c3 0 3-7 5-7M12 16h3c3 0 3 7 5 7" stroke="currentColor" strokeWidth="2.2" fill="none" /></svg>
      </div>
      <span className="brand-name">{product.name}</span>
    </Link>
  )
}
