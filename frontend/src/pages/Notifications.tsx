import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Bell, BellOff, CheckCheck, Settings } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import { useNotifications, type AppNotification, type NotificationPage, type NotificationSettings } from '../lib/notifications'
import { Empty, ErrorBox, Loading, PageHeader, SubTabs } from '../components/ui'
import { NotificationGroups } from '../components/Notifications'

type Filter = 'alle' | 'ongelezen'

// Alle meldingen, nieuwste eerst en per dag, met filters op ongelezen en op soort.
export function NotificationsPage() {
  const { unread, version, open, markAllRead } = useNotifications()
  const { data: settings } = useApi<NotificationSettings>('/notifications/settings')
  const [filter, setFilter] = useState<Filter>('alle')
  const [kind, setKind] = useState('')
  const [items, setItems] = useState<AppNotification[] | null>(null)
  const [more, setMore] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const navigate = useNavigate()
  const toast = useToast()

  const query = (before?: number) => {
    const p = new URLSearchParams({ limit: '30' })
    if (filter === 'ongelezen') p.set('unread', 'true')
    if (kind) p.set('kind', kind)
    if (before) p.set('before', String(before))
    return `/notifications?${p}`
  }

  useEffect(() => {
    let cancelled = false
    const p = new URLSearchParams({ limit: '30' })
    if (filter === 'ongelezen') p.set('unread', 'true')
    if (kind) p.set('kind', kind)
    api.get<NotificationPage>(`/notifications?${p}`)
      .then(page => { if (!cancelled) { setItems(page.items); setMore(page.more); setError(null) } })
      .catch(e => { if (!cancelled) setError((e as Error).message) })
    return () => { cancelled = true }
  }, [filter, kind, version])

  const loadMore = async () => {
    if (!items?.length) return
    setBusy(true)
    try {
      const page = await api.get<NotificationPage>(query(items[items.length - 1]!.id))
      setItems([...items, ...page.items])
      setMore(page.more)
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const allRead = async () => {
    try {
      await markAllRead(kind || undefined)
      toast(kind ? 'Deze meldingen staan op gelezen' : 'Alles staat op gelezen')
    } catch (e) {
      toast((e as Error).message, 'error')
    }
  }

  const kindLabel = settings?.kinds.find(k => k.id === kind)?.label.toLowerCase()

  return (
    <>
      <PageHeader
        icon={<Bell size={20} />}
        title="Meldingen"
        subtitle="Wat er gebeurde terwijl je met iets anders bezig was: betalingen, offertes, te late facturen en meer."
        actions={<>
          <button className="btn" onClick={() => navigate('/instellingen?tab=meldingen')}><Settings size={15} /> Instellingen</button>
          <button className="btn btn-primary" disabled={unread === 0} onClick={allRead}><CheckCheck size={15} /> Alles gelezen</button>
        </>}
      />
      <div className="row wrap notif-filters">
        <SubTabs<Filter> value={filter} onChange={setFilter} tabs={[
          { id: 'alle', label: 'Alle' },
          { id: 'ongelezen', label: 'Ongelezen', count: unread },
        ]} />
        {settings && settings.kinds.length > 1 && (
          <select value={kind} onChange={e => setKind(e.target.value)} aria-label="Soort melding">
            <option value="">Alle soorten</option>
            {settings.kinds.map(k => <option key={k.id} value={k.id}>{k.label}</option>)}
          </select>
        )}
      </div>
      {error && <ErrorBox message={error} />}
      {!error && items === null && <Loading />}
      {!error && items?.length === 0 && (
        <div className="card">
          <Empty
            icon={<BellOff size={22} />}
            title={filter === 'ongelezen' ? 'Je bent helemaal bij' : 'Nog geen meldingen'}
            text={filter === 'ongelezen'
              ? `Je hebt geen ongelezen meldingen${kindLabel ? ` over ${kindLabel}` : ''}.`
              : kindLabel
                ? `Er zijn nog geen meldingen over ${kindLabel}.`
                : 'Hier zie je straks bijvoorbeeld een betaling die binnenkomt of een offerte waar je klant op reageert. Gelezen meldingen blijven 90 dagen staan.'}
          />
        </div>
      )}
      {!error && items && items.length > 0 && (
        <div className="card notif-page">
          <NotificationGroups items={items} onOpen={open} />
          {more && (
            <div className="notif-more">
              <button className="btn" disabled={busy} onClick={loadMore}>Meer laden</button>
            </div>
          )}
        </div>
      )}
    </>
  )
}
