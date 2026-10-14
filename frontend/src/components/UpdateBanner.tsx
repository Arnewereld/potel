import { RefreshCw } from 'lucide-react'
import { applyUpdate, usePwa } from '../lib/pwa'

// Na een nieuwe versie van Potel: een smalle balk bovenin. Klik je niet, dan krijg je de nieuwe versie bij je volgende bezoek.
export function UpdateBanner() {
  const { update } = usePwa()
  if (!update) return null
  return (
    <div className="verify-banner update-banner" role="status">
      <RefreshCw size={16} />
      <span>Er staat een nieuwe versie van Potel klaar. Sla eerst op waar je mee bezig bent en vernieuw dan.</span>
      <button type="button" className="btn btn-sm btn-primary" onClick={applyUpdate}>Vernieuwen</button>
    </div>
  )
}
