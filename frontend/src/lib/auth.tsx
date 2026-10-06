import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { api } from './api'
import { clearSession, resetSession } from './storage'

export interface User {
  id: number
  workspaceId: number
  name: string
  email: string
  role: 'beheerder' | 'medewerker'
  active: boolean
  createdAt: string
  lastLoginAt?: string | null
}

export interface RegisterInput { company: string; name: string; email: string; password: string; demoData: boolean }

interface AuthValue {
  user: User
  login: (email: string, password: string) => Promise<void>
  register: (input: RegisterInput) => Promise<void>
  logout: () => Promise<void>
}

export type PublicAuth = Pick<AuthValue, 'login' | 'register'>

const AuthContext = createContext<AuthValue | null>(null)

// api.ts meldt via dit event dat de sessie verlopen is.
export const UNAUTHORIZED_EVENT = 'potel:unauthorized'

export function AuthProvider({ children, fallback, publicSite }: {
  children: ReactNode
  fallback: ReactNode
  publicSite: (auth: PublicAuth) => ReactNode
}) {
  const [user, setUser] = useState<User | null>(null)
  const [checked, setChecked] = useState(false)

  useEffect(() => {
    api.get<User>('/auth/me').then(setUser).catch(() => setUser(null)).finally(() => setChecked(true))
    const onUnauthorized = () => setUser(null)
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
  }, [])

  const login = useCallback(async (email: string, password: string) => {
    const u = await api.post<User>('/auth/login', { email, password })
    resetSession()
    setUser(u)
  }, [])

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
  if (!user) return <>{publicSite({ login, register })}</>
  return <AuthContext.Provider value={{ user, login, register, logout }}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth buiten AuthProvider')
  return ctx
}
