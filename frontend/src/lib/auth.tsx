import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { api } from './api'

export interface User {
  id: number
  name: string
  email: string
  role: 'beheerder' | 'medewerker'
  active: boolean
  createdAt: string
  lastLoginAt?: string | null
}

interface AuthValue {
  user: User
  login: (email: string, password: string) => Promise<void>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthValue | null>(null)

// api.ts meldt via dit event dat de sessie verlopen is.
export const UNAUTHORIZED_EVENT = 'potel:unauthorized'

export function AuthProvider({ children, fallback, loginPage }: {
  children: ReactNode
  fallback: ReactNode
  loginPage: (login: AuthValue['login']) => ReactNode
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
    setUser(await api.post<User>('/auth/login', { email, password }))
  }, [])

  const logout = useCallback(async () => {
    await api.post('/auth/logout').catch(() => {})
    setUser(null)
  }, [])

  if (!checked) return <>{fallback}</>
  if (!user) return <>{loginPage(login)}</>
  return <AuthContext.Provider value={{ user, login, logout }}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth buiten AuthProvider')
  return ctx
}
