import { useState } from 'react'
import { Check, ExternalLink, GitCommitHorizontal, KeyRound, Mail, RefreshCw, SkipForward, Trash2 } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useToast } from '../lib/toast'
import { notifyTimeChanged } from '../lib/timer'
import { date, hm, parseDuration, toDateInput, utcDateTime, weekStart } from '../lib/format'
import type { GitHubRules, GitHubSettings, GitHubSuggestion, GitHubSuggestions, Project } from '../lib/types'
import { Badge, Empty, ErrorBox, Field, Loading, Modal } from './ui'

// Hoe we de tijd schatten, in gewone woorden. Dezelfde regels als op de server (CommitDuration).
function RulesText({ rules }: { rules: GitHubRules }) {
  return (
    <p className="cell-sub" style={{ margin: 0 }}>
      We tellen de tijd tussen je commits op dezelfde dag bij elkaar op. Zit er meer dan {rules.gapMinutes / 60} uur tussen twee commits, dan
      begint een nieuw blok. Voor de eerste commit van elk blok rekenen we {rules.firstCommitMinutes} minuten, want daarvoor was je ook al bezig.
      We ronden af naar boven op {rules.roundToMinutes} minuten, rekenen minstens {rules.firstCommitMinutes} minuten en nooit meer
      dan {rules.maxMinutesPerDay / 60} uur per dag. Het is een schatting: pas de tijd gerust aan voor je boekt.
    </p>
  )
}

// Het tabblad GitHub bij Instellingen: het token van de werkruimte (alleen een beheerder) en je eigen e-mailadressen.
export function GitHubSettingsTab({ admin }: { admin: boolean }) {
  const { data, setData, error } = useApi<GitHubSettings>('/github/settings')
  const [token, setToken] = useState('')
  const [emails, setEmails] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const toast = useToast()

  if (error) return <ErrorBox message={error} />
  if (!data) return <Loading />
  const emailText = emails ?? data.emails.join('\n')

  const run = async (action: () => Promise<GitHubSettings>, done: string) => {
    setBusy(true)
    try {
      setData(await action())
      toast(done)
      return true
    } catch (e) {
      toast((e as Error).message, 'error')
      return false
    } finally {
      setBusy(false)
    }
  }

  const saveToken = async () => {
    if (!token.trim()) { toast('Plak eerst je token in het veld.', 'error'); return }
    if (await run(() => api.put<GitHubSettings>('/github/token', { token }), 'Token opgeslagen')) setToken('')
  }

  const removeToken = async () => {
    if (!confirm('Het GitHub-token verwijderen? Daarna kan niemand in de werkruimte nog voorstellen uit GitHub ophalen.')) return
    await run(async () => { await api.del('/github/token'); return api.get<GitHubSettings>('/github/settings') }, 'Token verwijderd')
  }

  const saveEmails = async () => {
    const list = emailText.split(/[\n,;]+/).map(e => e.trim()).filter(Boolean)
    if (await run(() => api.put<GitHubSettings>('/github/emails', { emails: list }), 'E-mailadressen opgeslagen')) setEmails(null)
  }

  // Enter in een veld hoort bij dit veld, niet bij het formulier van de instellingen eromheen.
  const onEnter = (action: () => void) => (e: React.KeyboardEvent) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); action() } }

  return (
    <div className="grid grid-1-1">
      <div className="card">
        <div className="card-head"><h3><KeyRound size={16} /> GitHub-token</h3></div>
        <div className="card-body">
          <p className="muted" style={{ marginTop: 0 }}>
            Met een token haalt Potel je commits op uit de repositories van je projecten en stelt het uren voor. Potel boekt nooit zelf iets.
          </p>
          {data.token
            ? <p style={{ margin: '0 0 12px' }}><Badge tone="green">Ingesteld</Badge> <span className="muted">Eindigt op <code>{data.token.last4}</code>, opgeslagen op {utcDateTime(data.token.updatedAt)}{data.token.updatedBy ? ` door ${data.token.updatedBy}` : ''}.</span></p>
            : <p style={{ margin: '0 0 12px' }}><Badge tone="gray">Niet ingesteld</Badge></p>}
          {admin ? (<>
            <Field label={data.token ? 'Nieuw token' : 'Token'}>
              <input type="password" autoComplete="off" spellCheck={false} value={token} placeholder="github_pat_…"
                onChange={e => setToken(e.target.value)} onKeyDown={onEnter(saveToken)} />
            </Field>
            <div className="row wrap mt" style={{ gap: 8 }}>
              <button type="button" className="btn btn-primary" disabled={busy || !token.trim()} onClick={saveToken}><Check size={15} /> Opslaan</button>
              {data.token && <button type="button" className="btn btn-danger" disabled={busy} onClick={removeToken}><Trash2 size={15} /> Verwijderen</button>}
            </div>
            <p className="cell-sub" style={{ marginBottom: 0 }}>
              Maak op GitHub een <a href="https://github.com/settings/personal-access-tokens/new" target="_blank" rel="noreferrer">fine-grained token <ExternalLink size={11} /></a> met
              alleen leesrechten op <em>Contents</em> voor de repositories van je klanten. Potel bewaart het versleuteld en laat het daarna nooit meer zien,
              ook niet aan jou. Het geldt voor de hele werkruimte.
            </p>
          </>) : (
            <p className="cell-sub" style={{ marginBottom: 0 }}>Alleen een beheerder kan het token instellen of verwijderen.</p>
          )}
        </div>
      </div>

      <div className="grid">
        <div className="card">
          <div className="card-head"><h3><Mail size={16} /> Jouw e-mailadressen in commits</h3></div>
          <div className="card-body">
            <Field label="Eén adres per regel (hooguit 5)">
              <textarea rows={3} spellCheck={false} value={emailText} placeholder={data.accountEmail} onChange={e => setEmails(e.target.value)} />
            </Field>
            <p className="cell-sub">
              Potel stelt alleen uren voor bij commits met deze adressen als auteur. Laat je het leeg, dan gebruiken we het adres van je
              account ({data.accountEmail}). Commit je met het noreply-adres van GitHub, zet dat er dan ook bij.
            </p>
            <button type="button" className="btn btn-primary" disabled={busy || emails === null} onClick={saveEmails}><Check size={15} /> Opslaan</button>
          </div>
        </div>
        <div className="card">
          <div className="card-head"><h3><GitCommitHorizontal size={16} /> Hoe de voorstellen werken</h3></div>
          <div className="card-body">
            <p className="cell-sub" style={{ marginTop: 0 }}>
              Zet bij een project de link naar de repository, zoals https://github.com/eigenaar/repository. Kies daarna op de pagina Uren of bij
              het project <em>Voorstellen uit GitHub</em>. Per project per dag krijg je een voorstel met je commitberichten als omschrijving.
              Een commit die je boekt of overslaat, komt nooit meer terug.
            </p>
            <RulesText rules={data.rules} />
          </div>
        </div>
      </div>
    </div>
  )
}

type Draft = GitHubSuggestion & { duration: string; billable: boolean; busy?: boolean }

// Het moment van een commit (UTC van GitHub) in de tijd van de gebruiker.
const commitTime = (s: string) => new Date(s.endsWith('Z') ? s : `${s}Z`).toLocaleTimeString('nl-NL', { hour: '2-digit', minute: '2-digit' })

// Zoals 'Maandag 12 oktober': alleen de eerste letter een hoofdletter.
const weekday = (s: string) => {
  const text = new Date(`${s.slice(0, 10)}T00:00:00`).toLocaleDateString('nl-NL', { weekday: 'long', day: 'numeric', month: 'long' })
  return text.charAt(0).toUpperCase() + text.slice(1)
}

// Voorstellen uit GitHub voor een dag of een week, eventueel voor één project. Elk voorstel boek je, pas je aan of sla je over.
export function GitHubSuggestionsModal({ projectId, day, onClose, onBooked }: {
  projectId?: number
  day?: string
  onClose: () => void
  onBooked?: () => void
}) {
  const { data: projects } = useApi<Project[]>('/projects')
  const [period, setPeriod] = useState<'dag' | 'week'>('dag')
  const [today] = useState(() => toDateInput(new Date()))
  const [when, setWhen] = useState(() => day ?? today)
  const [result, setResult] = useState<GitHubSuggestions | null>(null)
  const [drafts, setDrafts] = useState<Draft[]>([])
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const toast = useToast()

  const range = () => {
    if (period === 'dag') return { from: when, to: when }
    const monday = weekStart(new Date(`${when}T00:00:00`))
    const sunday = new Date(monday)
    sunday.setDate(sunday.getDate() + 6)
    return { from: toDateInput(monday), to: toDateInput(sunday) }
  }

  const billableFor = (id: number) => projects?.find(p => p.id === id)?.billing !== 'vast'

  const load = async () => {
    const { from, to } = range()
    setLoading(true)
    setError(null)
    try {
      const res = await api.get<GitHubSuggestions>(`/github/suggestions?from=${from}&to=${to}${projectId ? `&projectId=${projectId}` : ''}`)
      setResult(res)
      setDrafts(res.suggestions.map(s => ({ ...s, duration: hm(s.minutes), billable: billableFor(s.projectId) })))
    } catch (e) {
      setResult(null)
      setError((e as Error).message)
    } finally {
      setLoading(false)
    }
  }

  const update = (key: string, change: Partial<Draft>) => setDrafts(ds => ds.map(d => d.key === key ? { ...d, ...change } : d))
  const drop = (key: string) => setDrafts(ds => ds.filter(d => d.key !== key))

  const book = async (d: Draft) => {
    const minutes = parseDuration(d.duration)
    if (!minutes) { toast('Vul de tijd in, bijvoorbeeld 1:30 of 1,5.', 'error'); return }
    update(d.key, { busy: true })
    try {
      await api.post('/github/accept', {
        projectId: d.projectId, date: d.date.slice(0, 10), minutes, description: d.description, billable: d.billable, shas: d.commits.map(c => c.sha),
      })
      drop(d.key)
      toast(`${hm(minutes)} geboekt op ${projects?.find(p => p.id === d.projectId)?.name ?? d.projectName}`)
      notifyTimeChanged()
      onBooked?.()
    } catch (e) {
      update(d.key, { busy: false })
      toast((e as Error).message, 'error')
    }
  }

  const skip = async (d: Draft) => {
    update(d.key, { busy: true })
    try {
      await api.post('/github/skip', { projectId: d.projectId, shas: d.commits.map(c => c.sha) })
      drop(d.key)
      toast('Overgeslagen. Deze commits komen niet meer terug.')
    } catch (e) {
      update(d.key, { busy: false })
      toast((e as Error).message, 'error')
    }
  }

  const selectable = (projects ?? []).filter(p => p.status !== 'afgerond')

  return (
    <Modal title="Voorstellen uit GitHub" wide onClose={onClose}>
      <div className="row wrap" style={{ gap: 10, alignItems: 'flex-end' }}>
        <Field label="Periode">
          <select value={period} onChange={e => setPeriod(e.target.value as 'dag' | 'week')}>
            <option value="dag">Eén dag</option>
            <option value="week">De hele week</option>
          </select>
        </Field>
        <Field label={period === 'dag' ? 'Dag' : 'Een dag in de week'}>
          <input type="date" value={when} max={today} onChange={e => e.target.value && setWhen(e.target.value)} />
        </Field>
        <button type="button" className="btn btn-primary" disabled={loading} onClick={load}><RefreshCw size={15} /> {result ? 'Opnieuw ophalen' : 'Ophalen'}</button>
      </div>

      {error && <div className="notice mt" role="alert">{error}</div>}
      {loading && <Loading />}

      {result && !loading && (
        <div className="mt">
          {result.problems.map(p => <div key={p.repo} className="notice"><strong>{p.project}</strong>: {p.message}</div>)}
          {result.truncated.length > 0 && (
            <div className="notice">Bij {result.truncated.join(', ')} waren er meer commits dan we in één keer ophalen. Kies een kortere periode om niets te missen.</div>
          )}
          {result.skippedRepos.length > 0 && (
            <div className="notice">We kijken naar hooguit 10 repositories tegelijk. Niet bekeken: {result.skippedRepos.join(', ')}. Haal ze op vanaf de pagina van het project.</div>
          )}
          {drafts.length === 0 ? (
            <Empty icon={<GitCommitHorizontal size={22} />} title="Geen nieuwe commits"
              text={`Er zijn ${result.from === result.to ? `op ${date(result.from)}` : `van ${date(result.from)} tot en met ${date(result.to)}`} geen commits van ${result.emails.join(' of ')} die nog niet geboekt of overgeslagen zijn.`} />
          ) : (
            <div className="gh-list">
              {drafts.map(d => (
                <div key={d.key} className="card gh-suggestion">
                  <div className="row between wrap" style={{ gap: 8 }}>
                    <strong>{weekday(d.date)}</strong>
                    <code className="cell-sub">{d.repo}</code>
                  </div>
                  <div className="gh-fields">
                    <Field label="Project">
                      <select value={d.projectId} onChange={e => update(d.key, { projectId: Number(e.target.value), billable: billableFor(Number(e.target.value)) })}>
                        {selectable.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
                        {!selectable.some(p => p.id === d.projectId) && <option value={d.projectId}>{d.projectName}</option>}
                      </select>
                    </Field>
                    <Field label="Tijd">
                      <input value={d.duration} inputMode="decimal" onChange={e => update(d.key, { duration: e.target.value })} />
                    </Field>
                    <label className="check-row"><input type="checkbox" checked={d.billable} onChange={e => update(d.key, { billable: e.target.checked })} /> Factureerbaar</label>
                  </div>
                  <Field label="Omschrijving" full>
                    <textarea rows={2} maxLength={1000} value={d.description} onChange={e => update(d.key, { description: e.target.value })} />
                  </Field>
                  <details className="gh-commits">
                    <summary>{d.commits.length === 1 ? '1 commit' : `${d.commits.length} commits`}</summary>
                    <ul>
                      {d.commits.map(c => (
                        <li key={c.sha}>
                          <span className="muted">{commitTime(c.time)}</span>{' '}
                          {c.url ? <a href={c.url} target="_blank" rel="noreferrer"><code>{c.sha.slice(0, 7)}</code></a> : <code>{c.sha.slice(0, 7)}</code>}{' '}
                          {c.message}
                        </li>
                      ))}
                    </ul>
                  </details>
                  <div className="row" style={{ gap: 8 }}>
                    <button type="button" className="btn btn-primary btn-sm" disabled={d.busy} onClick={() => book(d)}><Check size={14} /> Boeken</button>
                    <button type="button" className="btn btn-sm" disabled={d.busy} onClick={() => skip(d)}><SkipForward size={14} /> Overslaan</button>
                  </div>
                </div>
              ))}
            </div>
          )}
          <details className="mt">
            <summary className="cell-sub">Hoe we de tijd schatten</summary>
            <div style={{ marginTop: 6 }}><RulesText rules={result.rules} /></div>
          </details>
        </div>
      )}
      {!result && !loading && !error && (
        <p className="cell-sub mt" style={{ marginBottom: 0 }}>
          Kies een dag of een week en haal je commits op. We kijken naar {projectId ? 'de repository van dit project' : 'de repositories van je lopende projecten'}.
        </p>
      )}
    </Modal>
  )
}
