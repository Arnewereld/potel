import { createContext, useContext, type ReactNode } from 'react'
import { useApi } from './useApi'
import type { Workspace } from './types'

interface WorkspaceValue {
  workspace: Workspace | null
  reload: () => Promise<void>
}

const WorkspaceContext = createContext<WorkspaceValue>({ workspace: null, reload: async () => {} })

// De werkruimte van de ingelogde gebruiker: naam, abonnement en of de welkomstwizard al gedaan is.
export function WorkspaceProvider({ children }: { children: ReactNode }) {
  const { data, reload } = useApi<Workspace>('/workspace')
  return <WorkspaceContext.Provider value={{ workspace: data, reload: () => reload(true) }}>{children}</WorkspaceContext.Provider>
}

export const useWorkspace = () => useContext(WorkspaceContext)
