import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from './api'
import { useTabActive } from './tabs'

// Laadt data van de API en geeft een reload-functie terug.
export function useApi<T>(url: string | null) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const active = useTabActive()
  const wasActive = useRef(active)

  const reload = useCallback(async (silent = false) => {
    if (!url) return
    if (!silent) setLoading(true)
    try {
      setData(await api.get<T>(url))
      setError(null)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setLoading(false)
    }
  }, [url])

  useEffect(() => { reload() }, [reload])

  // Ververs stilletjes wanneer je terugkomt op dit tabblad.
  useEffect(() => {
    if (active && !wasActive.current) reload(true)
    wasActive.current = active
  }, [active, reload])

  return { data, setData, error, loading, reload }
}
