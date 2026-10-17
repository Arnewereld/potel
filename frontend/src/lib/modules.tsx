import { createContext, useContext, type ReactNode } from 'react'
import { useApi } from './useApi'
import { useAuth } from './auth'
import { isAccountant } from './accountant'
import type { CustomModule, ModuleField } from './types'

interface ModulesValue { modules: CustomModule[]; reload: () => void }
const ModulesContext = createContext<ModulesValue>({ modules: [], reload: () => {} })

export function ModulesProvider({ children }: { children: ReactNode }) {
  // Eigen modules zijn er niet voor een boekhouder.
  const { user } = useAuth()
  const { data, reload } = useApi<CustomModule[]>(isAccountant(user) ? null : '/modules')
  return <ModulesContext.Provider value={{ modules: data ?? [], reload }}>{children}</ModulesContext.Provider>
}

export const useModules = () => useContext(ModulesContext)

export function parseFields(m: CustomModule): ModuleField[] {
  try { return JSON.parse(m.fieldsJson) } catch { return [] }
}
