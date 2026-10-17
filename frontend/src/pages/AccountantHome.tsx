import { useNavigate } from 'react-router-dom'
import { ArrowLeftRight, BookOpenCheck, Building2, CalendarRange, Car, Check, FileSignature, Landmark, Package, Receipt, Users, Wallet } from 'lucide-react'
import { useAuth } from '../lib/auth'
import { useWorkspace } from '../lib/workspace'
import { useApi } from '../lib/useApi'
import { useTabTitle } from '../lib/tabs'
import { useToast } from '../lib/toast'
import { switchWorkspace, type WorkspaceChoice } from '../lib/accountant'
import { PageHeader } from '../components/ui'

// Wat een boekhouder kan openen, met een korte uitleg.
const areas = [
  { to: '/facturen', label: 'Facturen', text: 'Facturen en creditnota\'s, met de pdf en de urenspecificatie.', icon: Receipt },
  { to: '/kosten', label: 'Kosten', text: 'Uitgaven met bonnetjes, en de vaste kosten.', icon: Wallet },
  { to: '/bank', label: 'Bank', text: 'Ingelezen banktransacties en waaraan ze gekoppeld zijn.', icon: ArrowLeftRight },
  { to: '/btw-aangifte', label: 'Btw-aangifte', text: 'De rubrieken per kwartaal, om over te nemen.', icon: Landmark },
  { to: '/jaaroverzicht', label: 'Jaaroverzicht', text: 'Omzet, kosten en winst per jaar, ook als CSV.', icon: CalendarRange },
  { to: '/investeringen', label: 'Investeringen', text: 'Afschrijvingen en de investeringsaftrek.', icon: Package },
  { to: '/ritten', label: 'Ritten', text: 'Zakelijke kilometers per jaar.', icon: Car },
  { to: '/offertes', label: 'Offertes', text: 'Verstuurde en geaccepteerde offertes.', icon: FileSignature },
  { to: '/klanten', label: 'Klanten', text: 'De factuurgegevens van klanten.', icon: Users },
]

// De startpagina van een boekhouder: bij welke administratie je nu bent, waar je heen kunt en wat je mag.
export function AccountantHomePage() {
  const { user } = useAuth()
  const { workspace } = useWorkspace()
  const { data: choices } = useApi<WorkspaceChoice[]>('/auth/workspaces')
  const navigate = useNavigate()
  const toast = useToast()
  useTabTitle('Overzicht')
  const extra = [user.canFileVat && 'de btw-aangifte als gedaan markeren', user.canCompleteExpenses && 'kosten aanvullen met een bonnetje of categorie'].filter(Boolean)

  return (
    <>
      <PageHeader icon={<BookOpenCheck size={20} />} title="Overzicht"
        subtitle={`Je kijkt als boekhouder mee in de administratie van ${workspace?.name ?? 'je klant'}. Bekijken en exporteren kan, wijzigen niet.`} />

      {choices && choices.length > 1 && (
        <div className="card" style={{ marginBottom: 16 }}>
          <div className="card-head"><h3><Building2 size={16} /> Je administraties</h3></div>
          <div className="card-body">
            <p className="muted" style={{ marginTop: 0 }}>Je bent boekhouder bij {choices.length} werkruimtes. Kies welke je wilt bekijken; je ziet steeds alleen die ene.</p>
            <div className="row wrap" style={{ gap: 8 }}>
              {choices.map(c => c.current
                ? <span key={c.workspaceId} className="btn btn-primary" aria-current="true"><Check size={15} /> {c.name}</span>
                : <button key={c.workspaceId} className="btn" onClick={() => switchWorkspace(c.workspaceId).catch(e => toast((e as Error).message, 'error'))}>
                    <Building2 size={15} /> {c.name}
                  </button>)}
            </div>
          </div>
        </div>
      )}

      <div className="grid grid-stats">
        {areas.map(a => (
          <div key={a.to} className="card stat clickable" style={{ '--accent': 'var(--primary)' } as React.CSSProperties} onClick={() => navigate(a.to)}
            role="link" tabIndex={0} onKeyDown={e => { if (e.key === 'Enter') navigate(a.to) }}>
            <div className="stat-icon"><a.icon size={18} /></div>
            <div>
              <div style={{ fontWeight: 600 }}>{a.label}</div>
              <div className="stat-sub">{a.text}</div>
            </div>
          </div>
        ))}
      </div>

      <p className="muted" style={{ fontSize: 13, marginTop: 16 }}>
        {extra.length ? `Je mag daarnaast ${extra.join(' en ')}. ` : ''}
        Elke export die je doet, zoals een CSV of een bonnetje, komt in het activiteitenlog van {workspace?.name ?? 'de werkruimte'}.
      </p>
    </>
  )
}
