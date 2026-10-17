import { createContext, useCallback, useContext, useState, type ReactNode } from 'react'
import { CheckCircle2, AlertTriangle, Info } from 'lucide-react'

type Kind = 'ok' | 'error' | 'info'
// Een eigen icoon, een klik die ergens heen gaat (zoals bij een nieuwe melding) en hoe lang hij blijft staan.
export interface ToastOptions { icon?: ReactNode; onClick?: () => void; duration?: number }
interface Toast { id: number; text: string; kind: Kind; icon?: ReactNode; onClick?: () => void }
const ToastContext = createContext<(text: string, kind?: Kind, options?: ToastOptions) => void>(() => {})

const defaultIcon = (kind: Kind) => kind === 'ok' ? <CheckCircle2 size={16} /> : kind === 'error' ? <AlertTriangle size={16} /> : <Info size={16} />

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([])
  const dismiss = useCallback((id: number) => setToasts(t => t.filter(x => x.id !== id)), [])
  const push = useCallback((text: string, kind: Kind = 'ok', options?: ToastOptions) => {
    const id = Date.now() + Math.random()
    setToasts(t => [...t, { id, text, kind, icon: options?.icon, onClick: options?.onClick }])
    setTimeout(() => dismiss(id), options?.duration ?? 3500)
  }, [dismiss])
  return (
    <ToastContext.Provider value={push}>
      {children}
      <div className="toasts" role="status" aria-live="polite">
        {toasts.map(t => t.onClick ? (
          <button key={t.id} type="button" className={`toast toast-${t.kind} toast-link`} onClick={() => { dismiss(t.id); t.onClick?.() }}>
            {t.icon ?? defaultIcon(t.kind)}
            <span>{t.text}</span>
          </button>
        ) : (
          <div key={t.id} className={`toast toast-${t.kind}`}>
            {t.icon ?? defaultIcon(t.kind)}
            {t.text}
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  )
}

export const useToast = () => useContext(ToastContext)
