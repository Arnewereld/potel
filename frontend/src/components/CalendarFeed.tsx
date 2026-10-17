import { useRef, useState } from 'react'
import { CalendarCheck, CalendarPlus, Copy, Info, Link2, Link2Off, Save } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import { relative, utcDateTime } from '../lib/format'
import type { CalendarFeedSettings } from '../lib/types'
import { ErrorBox, Field, Loading } from './ui'

type Option = 'planning' | 'deadlines' | 'invoices' | 'quotes' | 'vat' | 'showAmounts'

// Het tabblad Agenda bij Instellingen: je eigen geheime link voor Apple, Google of Outlook, en wat erin staat.
export function CalendarFeedTab() {
  const { data, setData, error } = useApi<CalendarFeedSettings>('/calendar-feed')
  const [edit, setEdit] = useState<CalendarFeedSettings | null>(null)
  const [created, setCreated] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  const toast = useToast()

  if (error) return <ErrorBox message={error} />
  if (!data) return <Loading />
  const form = edit ?? data
  const dirty = JSON.stringify(form) !== JSON.stringify(data)
  // Een agenda-app opent een webcal-link meteen met de vraag of je je wilt abonneren.
  const webcal = created?.replace(/^https?:/, 'webcal:')

  const run = async (work: () => Promise<void>) => {
    setBusy(true)
    try {
      await work()
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const makeLink = () => {
    if (data.active && !confirm('Je huidige link werkt daarna niet meer, ook niet in de agenda\'s waar je hem al gebruikt. Een nieuwe link maken?')) return
    return run(async () => {
      const res = await api.post<{ path: string; url: string | null; feed: CalendarFeedSettings }>('/calendar-feed/link')
      setData(res.feed)
      setEdit(null)
      setCreated(res.url ?? `${window.location.origin}${res.path}`)
      toast('Link gemaakt. Kopieer hem nu en zet hem in je agenda.')
    })
  }

  const revoke = () => {
    if (!confirm('De link intrekken? Je agenda haalt daarna niets meer op uit Potel. Wat er al in staat, haalt je agenda bij de volgende keer kijken weg.')) return
    return run(async () => {
      setData(await api.post<CalendarFeedSettings>('/calendar-feed/revoke'))
      setCreated(null)
      toast('Link ingetrokken')
    })
  }

  const save = () => run(async () => {
    setData(await api.put<CalendarFeedSettings>('/calendar-feed', {
      planning: form.planning, deadlines: form.deadlines, invoices: form.invoices, quotes: form.quotes, vat: form.vat, showAmounts: form.showAmounts,
    }))
    setEdit(null)
    toast('Opgeslagen. Je agenda ziet het de volgende keer dat hij kijkt.')
  })

  const copy = async () => {
    if (!created) return
    try {
      await navigator.clipboard.writeText(created)
      toast('Link gekopieerd')
    } catch {
      input.current?.select()
      toast('Kopiëren lukte niet vanzelf. De link is geselecteerd; kopieer hem met Ctrl+C.', 'error')
    }
  }

  const check = (key: Option, label: string, help: string) => (
    <label className="check-row field-full" style={{ alignItems: 'flex-start' }}>
      <input type="checkbox" checked={form[key]} onChange={e => setEdit({ ...form, [key]: e.target.checked })} />
      <span><span>{label}</span><span className="cell-sub" style={{ display: 'block' }}>{help}</span></span>
    </label>
  )

  return (
    <div className="grid grid-1-1">
      <div className="card">
        <div className="card-head"><h3><CalendarCheck size={16} /> Je agendalink</h3></div>
        <div className="card-body">
          <p className="muted" style={{ marginTop: 0 }}>
            Met deze link zie je je planning uit Potel in de agenda op je telefoon of computer: Apple Agenda, Google Agenda of Outlook. De link is
            alleen voor jou. Je agenda kijkt zelf af en toe of er iets veranderd is.
          </p>
          {data.active ? (
            <dl className="dl">
              <dt>Status</dt><dd>Actief</dd>
              {data.createdAt && <><dt>Gemaakt</dt><dd>{utcDateTime(data.createdAt)}</dd></>}
              <dt>Opgehaald</dt>
              <dd>
                {data.lastFetchedAt
                  ? <span title={utcDateTime(data.lastFetchedAt)}>{relative(data.lastFetchedAt)}{data.client ? ` door ${data.client}` : ''}, {data.fetches === 1 ? '1 keer' : `${data.fetches} keer`} in totaal</span>
                  : 'Nog niet. Zet de link in je agenda; daarna zie je hier wanneer hij voor het laatst keek.'}
              </dd>
            </dl>
          ) : (
            <p style={{ margin: 0 }}>Er is op dit moment geen werkende link.</p>
          )}

          {created && (
            <div className="portal-created">
              <Field label="Je nieuwe link" full>
                {/* Enter doet hier niets: Instellingen staat in een formulier dat anders zou opslaan. */}
                <input ref={input} readOnly value={created} onFocus={e => e.target.select()} onKeyDown={e => { if (e.key === 'Enter') e.preventDefault() }} />
              </Field>
              <div className="row wrap" style={{ gap: 8 }}>
                <button type="button" className="btn btn-primary btn-sm" onClick={copy}><Copy size={14} /> Kopiëren</button>
                {webcal && <a className="btn btn-sm" href={webcal}><CalendarPlus size={14} /> Openen in je agenda</a>}
              </div>
              <p className="cell-sub" style={{ marginBottom: 0 }}>
                Kopieer de link nu. We bewaren hem niet, dus later kun je hem niet meer opvragen. Ben je hem kwijt? Maak dan een nieuwe link; de oude werkt dan niet meer.
              </p>
            </div>
          )}

          <div className="row wrap mt" style={{ gap: 8 }}>
            <button type="button" className="btn btn-primary" disabled={busy} onClick={makeLink}><Link2 size={15} /> {data.active ? 'Nieuwe link maken' : 'Link maken'}</button>
            {data.active && <button type="button" className="btn btn-danger" disabled={busy} onClick={revoke}><Link2Off size={15} /> Intrekken</button>}
          </div>
        </div>
      </div>

      <div className="card">
        <div className="card-head"><h3><CalendarPlus size={16} /> Wat erin staat</h3></div>
        <div className="card-body form-grid">
          {check('planning', 'Afspraken en taken', 'Wat in Planning voor jou of voor iedereen staat. Afgevinkte taken niet.')}
          {check('deadlines', 'Deadlines van projecten', 'Als een hele dag, voor projecten die nog lopen.')}
          {data.canSeeMoney && (<>
            {check('invoices', 'Vervaldatum van facturen', 'Verstuurde facturen die nog niet (helemaal) betaald zijn.')}
            {check('quotes', 'Offertes die verlopen', 'Verstuurde offertes waar je klant nog niet op heeft gereageerd.')}
            {check('vat', 'Btw-aangifte', 'De laatste dag om je aangifte te doen en te betalen.')}
          </>)}
          {check('showAmounts', 'Bedragen en notities tonen', 'Zonder dit vinkje staan er geen bedragen en geen notities in je agenda. Zo blijft het netjes als iemand anders je agenda ziet.')}
          {!data.canSeeMoney && (
            <p className="cell-sub field-full" style={{ margin: 0 }}>Facturen, offertes en de btw-aangifte zijn er alleen voor een beheerder.</p>
          )}
          <div className="field-full row" style={{ justifyContent: 'flex-end' }}>
            <button type="button" className="btn btn-primary" disabled={busy || !dirty} onClick={save}><Save size={15} /> Opslaan</button>
          </div>
        </div>
      </div>

      <div className="card">
        <div className="card-head"><h3>Apple Agenda op de Mac</h3></div>
        <div className="card-body">
          <ol className="steps">
            <li><div>Open de app <strong>Agenda</strong>.</div></li>
            <li><div>Kies in de menubalk <strong>Archief</strong> en dan <strong>Nieuw agenda-abonnement</strong>.</div></li>
            <li><div>Plak je link en klik op <strong>Abonneer</strong>.</div></li>
            <li><div>Kies bij automatisch vernieuwen bijvoorbeeld <strong>Elk uur</strong> en klik op <strong>OK</strong>.</div></li>
          </ol>
        </div>
      </div>

      <div className="card">
        <div className="card-head"><h3>Apple Agenda op je iPhone of iPad</h3></div>
        <div className="card-body">
          <p className="muted" style={{ marginTop: 0 }}>
            Het snelst: maak de link op je iPhone, tik op <strong>Openen in je agenda</strong> en kies <strong>Abonneer</strong>. Met een link die je al hebt gaat het zo:
          </p>
          <ol className="steps">
            <li><div>Open <strong>Instellingen</strong> en ga naar <strong>Agenda</strong> (op nieuwere iPhones via <strong>Apps</strong>).</div></li>
            <li><div>Tik op <strong>Accounts</strong>, <strong>Voeg account toe</strong>, <strong>Anders</strong> en <strong>Voeg agenda-abonnement toe</strong>.</div></li>
            <li><div>Plak je link, tik op <strong>Volgende</strong> en daarna op <strong>Bewaar</strong>.</div></li>
          </ol>
        </div>
      </div>

      <div className="card">
        <div className="card-head"><h3>Google Agenda</h3></div>
        <div className="card-body">
          <ol className="steps">
            <li><div>Open <strong>calendar.google.com</strong> op een computer. In de app op je telefoon kan dit niet.</div></li>
            <li><div>Klik links naast <strong>Andere agenda's</strong> op het plusje en kies <strong>Via URL</strong>.</div></li>
            <li><div>Plak je link en klik op <strong>Agenda toevoegen</strong>.</div></li>
            <li><div>Op je telefoon verschijnt hij daarna vanzelf in Google Agenda.</div></li>
          </ol>
        </div>
      </div>

      <div className="card">
        <div className="card-head"><h3>Outlook</h3></div>
        <div className="card-body">
          <ol className="steps">
            <li><div>Open je agenda in Outlook op het web of in de nieuwe Outlook.</div></li>
            <li><div>Kies <strong>Agenda toevoegen</strong> en dan de optie om je via internet te abonneren.</div></li>
            <li><div>Plak je link, geef de agenda een naam, bijvoorbeeld Potel, en klik op <strong>Importeren</strong>.</div></li>
          </ol>
        </div>
      </div>

      <div className="card">
        <div className="card-head"><h3><Info size={16} /> Goed om te weten</h3></div>
        <div className="card-body">
          <ul className="tax-reserve-list">
            <li>Je agenda bepaalt zelf hoe vaak hij kijkt. Apple doet het zo vaak als je kiest; Google en Outlook een paar keer per dag. Een wijziging staat er dus niet altijd meteen in.</li>
            <li>Het werkt maar één kant op: wat je in je agenda verandert, komt niet terug in Potel.</li>
            <li>Iedereen met de link ziet wat erin staat. Deel hem dus niet, en maak een nieuwe link als hij toch bij iemand anders terecht is gekomen.</li>
            <li>We tonen afspraken van drie maanden terug tot ruim een jaar vooruit.</li>
          </ul>
        </div>
      </div>
    </div>
  )
}
