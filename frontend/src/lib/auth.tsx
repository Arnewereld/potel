import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { api } from './api'
import { clearSession, resetSession } from './storage'

export interface User {
  id: number
  workspaceId: number
  name: string
  email: string
  role: 'beheerder' | 'medewerker' | 'boekhouder'
  active: boolean
  createdAt: string
  lastLoginAt?: string | null
  // Bevestigd via de link in de mail. Zolang dat niet zo is, staat er een balk bovenin.
  emailVerified: boolean
  // Inloggen in twee stappen staat aan.
  twoFactor: boolean
  // Alleen bij jezelf: je werkruimte verplicht inloggen in twee stappen voor beheerders en je moet het eerst instellen.
  twoFactorSetupRequired?: boolean
  // Alleen bij een boekhouder: de uitnodiging is nog niet aangenomen, en wat hij naast meekijken mag.
  invitePending?: boolean
  canFileVat?: boolean
  canCompleteExpenses?: boolean
}

// Het antwoord van de tweede stap: wie je bent, en hoeveel herstelcodes je nog hebt als je er net een gebruikte.
export interface CodeLogin { user: User; recoveryCodesLeft: number | null }

export interface RegisterInput {
  company: string; name: string; email: string; password: string; demoData: boolean
  // Alleen zakelijk: KvK-nummer, bevestiging van zakelijk gebruik en akkoord op de voorwaarden. De server controleert dit ook.
  kvk: string; businessUse: boolean; acceptTerms: boolean
}

interface AuthValue {
  user: User
  // 'code' als er nog een code van inloggen in twee stappen nodig is (zie enter).
  login: (email: string, password: string) => Promise<'ok' | 'code'>
  // Na een goede code: nu ben je echt binnen.
  enter: (user: User) => void
  register: (input: RegisterInput) => Promise<void>
  logout: () => Promise<void>
  // Na een wijziging aan jezelf, zoals inloggen in twee stappen aan- of uitzetten.
  update: (user: User) => void
}

export type PublicAuth = Pick<AuthValue, 'login' | 'enter' | 'register'>

const AuthContext = createContext<AuthValue | null>(null)

// api.ts meldt via dit event dat de sessie verlopen is.
export const UNAUTHORIZED_EVENT = 'potel:unauthorized'
// En via dit event dat je eerst inloggen in twee stappen moet instellen.
export const TWO_FACTOR_SETUP_EVENT = 'potel:two-factor-setup'

export function AuthProvider({ children, fallback, publicSite, twoFactorSetup }: {
  children: ReactNode
  fallback: ReactNode
  publicSite: (auth: PublicAuth) => ReactNode
  // Wat je ziet zolang je inloggen in twee stappen nog moet instellen.
  twoFactorSetup: ReactNode
}) {
  const [user, setUser] = useState<User | null>(null)
  const [checked, setChecked] = useState(false)

  useEffect(() => {
    api.get<User>('/auth/me').then(setUser).catch(() => setUser(null)).finally(() => setChecked(true))
    const onUnauthorized = () => setUser(null)
    const onSetup = () => setUser(u => u && { ...u, twoFactorSetupRequired: true })
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
    window.addEventListener(TWO_FACTOR_SETUP_EVENT, onSetup)
    return () => {
      window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
      window.removeEventListener(TWO_FACTOR_SETUP_EVENT, onSetup)
    }
  }, [])

  const enter = useCallback((u: User) => {
    resetSession()
    setUser(u)
  }, [])

  const login = useCallback(async (email: string, password: string) => {
    const r = await api.post<User | { twoFactorRequired: true }>('/auth/login', { email, password })
    if ('twoFactorRequired' in r) return 'code' as const
    enter(r)
    return 'ok' as const
  }, [enter])

  const register = useCallback(async (input: RegisterInput) => {
    const u = await api.post<User>('/auth/register', input)
    resetSession()
    setUser(u)
  }, [])

  const logout = useCallback(async () => {
    await api.post('/auth/logout').catch(() => {})
    clearSession()
    setUser(null)
  }, [])

  if (!checked) return <>{fallback}</>
  if (!user) return <>{publicSite({ login, enter, register })}</>
  return (
    <AuthContext.Provider value={{ user, login, enter, register, logout, update: setUser }}>
      {user.twoFactorSetupRequired ? twoFactorSetup : children}
    </AuthContext.Provider>
  )
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth buiten AuthProvider')
  return ctx
}
