import { useState } from 'react'
import { MailWarning } from 'lucide-react'
import { api } from '../lib/api'
import { useToast } from '../lib/toast'

// Zolang je e-mailadres niet bevestigd is: een smalle balk bovenin, met een knop om de mail nog eens te sturen.
export function VerifyEmailBanner({ email }: { email: string }) {
  const [busy, setBusy] = useState(false)
  const toast = useToast()

  const resend = async () => {
    setBusy(true)
    try {
      const res = await api.post<{ message: string }>('/auth/verify-email/resend')
      toast(res.message)
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="verify-banner" role="status">
      <MailWarning size={16} />
      <span>Bevestig je e-mailadres <strong>{email}</strong> met de link in de mail die we je stuurden. Daarna kunnen je werkstromen ook e-mail versturen.</span>
      <button type="button" className="btn btn-sm" disabled={busy} onClick={resend}>{busy ? 'Bezig…' : 'Opnieuw versturen'}</button>
    </div>
  )
}
