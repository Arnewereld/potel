import { useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { AlertTriangle, ArrowLeft, CheckCircle2, Download, FileUp, FolderKanban, History, Receipt, Timer, Undo2, Upload, Users } from 'lucide-react'
import { api, ApiError } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useAuth } from '../lib/auth'
import { useToast } from '../lib/toast'
import { utcDateTime } from '../lib/format'
import type { DuplicateMode, ImportBatch, ImportCheck, ImportKind, ImportPreview } from '../lib/types'
import { Empty, ErrorBox, Field, Loading, PageHeader } from '../components/ui'

const MaxBytes = 5 * 1024 * 1024

const kinds: { id: ImportKind; label: string; icon: typeof Users; text: string; page: string; sample: string }[] = [
  {
    id: 'klanten', label: 'Klanten', icon: Users, page: '/klanten',
    text: 'Naam, contactpersoon, e-mail, adres, KvK-nummer, btw-nummer en taal.',
    sample: 'Bedrijfsnaam;Contactpersoon;E-mail;Telefoon;Adres;Postcode;Plaats;Land;KvK-nummer;Btw-nummer;Taal\nFietsplein B.V.;Jan Jansen;jan@fietsplein.nl;020 1234567;Dorpsstraat 1;1234 AB;Utrecht;Nederland;12345678;NL123456789B01;nl\n',
  },
  {
    id: 'projecten', label: 'Projecten', icon: FolderKanban, page: '/projecten',
    text: 'Per project de klant en een uurtarief of een vaste prijs.',
    sample: 'Project;Klant;Uurtarief;Vaste prijs\nWebshop;Fietsplein B.V.;95;\nLogo;Studio Noord;;1250\n',
  },
  {
    id: 'uren', label: 'Uren', icon: Timer, page: '/uren',
    text: 'Een export uit Toggl Track, Clockify of Harvest, of een eigen lijst met datum, project en duur.',
    sample: 'Datum;Klant;Project;Omschrijving;Duur;Factureerbaar\n02-01-2026;Fietsplein B.V.;Webshop;Bugfix checkout;1:30;ja\n',
  },
  {
    id: 'facturen', label: 'Oude facturen', icon: Receipt, page: '/facturen?tab=geimporteerd',
    text: 'Je facturen uit een vorig pakket, alleen ter informatie. Ze tellen mee in je omzet en het jaaroverzicht.',
    sample: 'Factuurnummer;Factuurdatum;Klant;Bedrag excl. btw;Btw;Betaald op\n2025-0001;15-01-2025;Fietsplein B.V.;1000,00;210,00;29-01-2025\n',
  },
]

const steps = ['Wat', 'Bestand', 'Kolommen', 'Controleren', 'Klaar']

// Een label zonder hoofdletters aan het begin van elk woord, voor tellingen als "3 nieuwe klanten".
const Chip = ({ tone, children }: { tone: string; children: React.ReactNode }) => <span className={`badge badge-${tone} badge-plain`}>{children}</span>

const kindLabel = (k: ImportKind) => kinds.find(x => x.id === k)?.label ?? k
const plural = (n: number, one: string, more: string) => `${n} ${n === 1 ? one : more}`
const itemWord = (k: ImportKind, n: number) =>
  k === 'klanten' ? (n === 1 ? 'klant' : 'klanten') : k === 'projecten' ? (n === 1 ? 'project' : 'projecten') : k === 'uren' ? (n === 1 ? 'boeking' : 'boekingen') : (n === 1 ? 'factuur' : 'facturen')

const readFile = (file: File) => new Promise<string>((resolve, reject) => {
  const reader = new FileReader()
  reader.onload = () => resolve(String(reader.result))
  reader.onerror = () => reject(new Error('Dit bestand kunnen we niet lezen. Probeer het opnieuw.'))
  reader.readAsDataURL(file)
})

// Een voorbeeldbestand om te zien welke kolommen we verwachten.
function downloadSample(kind: (typeof kinds)[number]) {
  const href = URL.createObjectURL(new Blob(['﻿' + kind.sample], { type: 'text/csv;charset=utf-8' }))
  const a = Object.assign(document.createElement('a'), { href, download: `voorbeeld-${kind.id}.csv` })
  a.click()
  setTimeout(() => URL.revokeObjectURL(href), 1000)
}

// Importeren uit een ander pakket, in vijf stappen. Het bestand blijft in je browser tot je importeert; elke stap stuurt het opnieuw
// mee, zodat de server nergens een half bestand bewaart. Alles of niets: met één fout in het bestand wordt er niets geïmporteerd.
export function ImportPage() {
  const { user } = useAuth()
  const admin = user.role === 'beheerder'
  const { data: batches, reload: reloadBatches } = useApi<ImportBatch[]>(admin ? '/imports' : null)
  const [step, setStep] = useState(0)
  const [kind, setKind] = useState<ImportKind>('klanten')
  const [file, setFile] = useState<{ name: string; data: string } | null>(null)
  const [preview, setPreview] = useState<ImportPreview | null>(null)
  const [mapping, setMapping] = useState<Record<string, string | null>>({})
  const [check, setCheck] = useState<ImportCheck | null>(null)
  const [duplicates, setDuplicates] = useState<DuplicateMode>('overslaan')
  const [customers, setCustomers] = useState<Record<string, number>>({})
  const [projects, setProjects] = useState<Record<string, number>>({})
  const [result, setResult] = useState<ImportBatch | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  const toast = useToast()

  if (!admin) return <ErrorBox message="Alleen een beheerder kan gegevens importeren." />

  const reset = () => {
    setStep(0); setFile(null); setPreview(null); setMapping({}); setCheck(null); setDuplicates('overslaan')
    setCustomers({}); setProjects({}); setResult(null); setError(null)
  }

  const pick = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const f = e.target.files?.[0]
    e.target.value = ''
    if (!f) return
    setError(null)
    if (f.size > MaxBytes) { setError('Dit bestand is te groot. Een import mag hooguit 5 MB zijn; splits het bestand of kies een kortere periode.'); return }
    setBusy(true)
    try {
      const data = await readFile(f)
      const p = await api.post<ImportPreview>('/imports/preview', { kind, fileName: f.name, data })
      setFile({ name: f.name, data })
      setPreview(p)
      setMapping(p.mapping)
      setCheck(null); setCustomers({}); setProjects({})
      setStep(2)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  // Controleren met de keuzes van nu. Namen die nog geen keuze hebben, krijgen wat Potel vond of "nieuw aanmaken"; dan controleren
  // we nog één keer, zodat de tellingen kloppen met wat je ziet. Je bevestigt die keuzes met de knop Importeren.
  const runCheck = async (next?: { mapping?: Record<string, string | null>; duplicates?: DuplicateMode; customers?: Record<string, number>; projects?: Record<string, number> }) => {
    if (!file) return
    const body = {
      kind, fileName: file.name, data: file.data, mapping: next?.mapping ?? mapping, duplicates: next?.duplicates ?? duplicates,
      customers: next?.customers ?? customers, projects: next?.projects ?? projects,
    }
    setBusy(true)
    setError(null)
    try {
      let c = await api.post<ImportCheck>('/imports/check', body)
      const cust = { ...body.customers }
      const proj = { ...body.projects }
      let added = false
      for (const x of c.customers) if (!(x.key in cust)) { const v = x.choice ?? x.match ?? (x.canCreate ? 0 : -1); cust[x.key] = v; added ||= v >= 0 && x.choice == null }
      for (const x of c.projects) if (!(x.key in proj)) { const v = x.choice ?? x.match ?? (x.canCreate ? 0 : -1); proj[x.key] = v; added ||= v >= 0 && x.choice == null }
      if (added) c = await api.post<ImportCheck>('/imports/check', { ...body, customers: cust, projects: proj })
      // Ook een klantnaam die pas na de tweede controle opduikt (bij een nieuw project), krijgt een standaardkeuze in het scherm.
      for (const x of c.customers) if (!(x.key in cust)) cust[x.key] = x.choice ?? x.match ?? (x.canCreate ? 0 : -1)
      setCustomers(cust)
      setProjects(proj)
      setCheck(c)
      setStep(3)
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const run = async () => {
    if (!file) return
    setBusy(true)
    setError(null)
    try {
      const batch = await api.post<ImportBatch>('/imports', { kind, fileName: file.name, data: file.data, mapping, duplicates, customers, projects })
      setResult(batch)
      setStep(4)
      reloadBatches(true)
    } catch (err) {
      const refused = err instanceof ApiError ? (err.data as { check?: ImportCheck } | null)?.check : null
      if (refused) setCheck(refused)
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const undo = async (b: ImportBatch) => {
    if (!confirm(`De import van ${b.fileName} ongedaan maken? Wat deze import aanmaakte, gaat weg uit Potel, behalve wat je inmiddels gebruikt. Gegevens die de import bijwerkte, blijven zoals ze nu zijn.`)) return
    setBusy(true)
    try {
      const res = await api.post<{ removed: number; kept: number; keptReasons: string[]; batch: ImportBatch }>(`/imports/${b.id}/undo`)
      toast(res.kept > 0
        ? `Import ongedaan gemaakt: ${res.removed} weggehaald. Bleef staan: ${res.keptReasons.join('; ')}.`
        : `Import ongedaan gemaakt: ${res.removed} weggehaald.`, 'ok', { duration: res.kept > 0 ? 9000 : undefined })
      if (result?.id === b.id) setResult(res.batch)
      reloadBatches(true)
    } catch (err) {
      toast((err as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const fields = preview?.fields ?? []
  const missing = fields.filter(f => f.required && !mapping[f.key])
  const current = kinds.find(k => k.id === kind)!
  const choicesOpen = check ? check.customers.some(x => (customers[x.key] ?? -1) < 0) || check.projects.some(x => (projects[x.key] ?? -1) < 0) : false

  return (
    <>
      <PageHeader icon={<Upload size={20} />} title="Importeren" subtitle="Haal je klanten, projecten, uren en oude facturen uit een ander pakket naar Potel" />

      <ol className="import-steps" aria-label="Stappen">
        {steps.map((s, i) => (
          <li key={s} className={i === step ? 'active' : i < step ? 'done' : ''} aria-current={i === step ? 'step' : undefined}>
            <span>{i < step ? <CheckCircle2 size={14} /> : i + 1}</span>{s}
          </li>
        ))}
      </ol>

      {error && <div className="import-error" role="alert"><AlertTriangle size={16} /><span>{error}</span></div>}

      {step === 0 && (
        <div className="card">
          <div className="card-head"><h3>Wat wil je importeren?</h3></div>
          <div className="card-body">
            <div className="import-kinds">
              {kinds.map(k => (
                <button key={k.id} type="button" className={`import-kind ${kind === k.id ? 'selected' : ''}`} aria-pressed={kind === k.id} onClick={() => setKind(k.id)}>
                  <k.icon size={20} />
                  <span><strong>{k.label}</strong><span className="cell-sub">{k.text}</span></span>
                </button>
              ))}
            </div>
            <p className="muted" style={{ marginBottom: 0 }}>
              Importeer eerst je klanten, dan je projecten en daarna je uren of facturen. Zo vindt Potel bij elke regel de juiste klant.
            </p>
          </div>
          <div className="card-body import-actions">
            <button className="btn btn-primary" onClick={() => setStep(1)}>Volgende</button>
          </div>
        </div>
      )}

      {step === 1 && (
        <div className="card">
          <div className="card-head"><h3>Kies je bestand met {current.label.toLowerCase()}</h3></div>
          <div className="card-body">
            <p style={{ marginTop: 0 }}>
              Exporteer de gegevens in je vorige pakket als CSV-bestand. {kind === 'uren' && 'In Toggl Track, Clockify en Harvest is dat het gedetailleerde rapport, als CSV. '}
              Een bestand mag hooguit 5 MB en 10.000 regels hebben. Een Excel-bestand sla je in Excel eerst op als CSV.
            </p>
            <input ref={fileInput} type="file" accept=".csv,.txt,text/csv,text/plain" hidden onChange={pick} />
            <div className="import-drop">
              <FileUp size={26} />
              <button className="btn btn-primary" disabled={busy} onClick={() => fileInput.current?.click()}><Upload size={16} /> {busy ? 'Bezig met lezen…' : 'Bestand kiezen'}</button>
              <button className="btn btn-ghost" type="button" onClick={() => downloadSample(current)}><Download size={15} /> Voorbeeldbestand</button>
            </div>
          </div>
          <div className="card-body import-actions">
            <button className="btn" onClick={() => setStep(0)}><ArrowLeft size={15} /> Terug</button>
          </div>
        </div>
      )}

      {step === 2 && preview && (
        <div className="card">
          <div className="card-head"><h3>Welke kolom is wat?</h3></div>
          <div className="card-body">
            <p style={{ marginTop: 0 }}>
              <strong>{preview.fileName}</strong>: {plural(preview.rows, 'regel', 'regels')}
              {preview.source && <> uit <Chip tone="green">{preview.source}</Chip></>}
              {preview.headerLine > 1 && <> De kolomnamen staan op regel {preview.headerLine}; de regels erboven slaan we over.</>}
            </p>
            <p className="muted">We hebben de kolommen gekoppeld die we herkennen. Kijk ze na en kies zelf waar het niet klopt.</p>
            <div className="form-grid">
              {fields.map(f => (
                <Field key={f.key} label={`${f.label}${f.required ? ' *' : ''}`}>
                  <select value={mapping[f.key] ?? ''} aria-invalid={f.required && !mapping[f.key]} onChange={e => setMapping(m => ({ ...m, [f.key]: e.target.value || null }))}>
                    <option value="">{f.required ? 'Kies een kolom' : 'Niet gebruiken'}</option>
                    {preview.headers.map(h => <option key={h} value={h}>{h}</option>)}
                  </select>
                </Field>
              ))}
            </div>
            <h4 className="import-sub">De eerste regels uit het bestand</h4>
            <div className="table-wrap">
              <table>
                <thead><tr>{preview.headers.map(h => <th key={h} className="nowrap">{h}</th>)}</tr></thead>
                <tbody>{preview.sample.map((r, i) => <tr key={i}>{preview.headers.map((_, j) => <td key={j} className="nowrap">{r[j] ?? ''}</td>)}</tr>)}</tbody>
              </table>
            </div>
          </div>
          <div className="card-body import-actions">
            <button className="btn" onClick={() => setStep(1)}><ArrowLeft size={15} /> Ander bestand</button>
            <button className="btn btn-primary" disabled={busy || missing.length > 0} onClick={() => runCheck()}>
              {busy ? 'Bezig met controleren…' : 'Controleren'}
            </button>
          </div>
          {missing.length > 0 && <div className="card-body muted" style={{ paddingTop: 0 }}>Kies eerst een kolom voor {missing.map(f => f.label.toLowerCase()).join(' en ')}.</div>}
        </div>
      )}

      {step === 3 && check && (
        <div className="card">
          <div className="card-head"><h3>Controleren</h3></div>
          <div className="card-body">
            <div className="import-summary">
              <Chip tone="green">{plural(check.create, 'nieuw', 'nieuw')}</Chip>
              {check.update > 0 && <Chip tone="blue">{check.update} bijwerken</Chip>}
              {check.skip > 0 && <Chip tone="gray">{check.skip} overslaan</Chip>}
              {check.newCustomers > 0 && <Chip tone="purple">{plural(check.newCustomers, 'nieuwe klant', 'nieuwe klanten')}</Chip>}
              {check.newProjects > 0 && <Chip tone="purple">{plural(check.newProjects, 'nieuw project', 'nieuwe projecten')}</Chip>}
              {check.errorCount > 0 && <Chip tone="red">{plural(check.errorCount, 'fout', 'fouten')}</Chip>}
            </div>

            {check.errorCount > 0 && (
              <div className="import-block">
                <h4 className="import-sub text-red">Dit moet eerst beter</h4>
                <p className="muted">Zolang er fouten in het bestand staan, importeren we niets. Verbeter ze in het bestand en kies het opnieuw, of pas de kolommen aan.</p>
                <ul className="import-messages">
                  {check.errors.map((m, i) => <li key={i}>{m.line > 0 && <strong>Regel {m.line}: </strong>}{m.text}</li>)}
                  {check.errorCount > check.errors.length && <li className="muted">En nog {check.errorCount - check.errors.length} andere.</li>}
                </ul>
              </div>
            )}

            {(check.customers.length > 0 || check.projects.length > 0) && (
              <div className="import-block">
                <h4 className="import-sub">Bij welke klant en welk project hoort het?</h4>
                <p className="muted">Controleer bij elke naam uit het bestand of het een bestaande klant of een bestaand project is, of dat Potel een nieuwe aanmaakt.</p>
                {check.projects.length > 0 && (
                  <div className="import-choices">
                    {check.projects.map(x => (
                      <label key={x.key} className="import-choice">
                        <span><strong>{x.name || 'Zonder project'}</strong><span className="cell-sub">{x.customer || 'Zonder klant'} · {plural(x.rows, 'regel', 'regels')}</span></span>
                        <select value={projects[x.key] ?? -1} aria-invalid={(projects[x.key] ?? -1) < 0}
                          onChange={e => { const p = { ...projects, [x.key]: Number(e.target.value) }; runCheck({ projects: p }) }}>
                          {(projects[x.key] ?? -1) < 0 && <option value={-1}>Kies een project</option>}
                          {x.canCreate && <option value={0}>Nieuw project aanmaken</option>}
                          {check.projectOptions.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}
                        </select>
                      </label>
                    ))}
                  </div>
                )}
                {check.customers.length > 0 && (
                  <div className="import-choices">
                    {check.customers.map(x => (
                      <label key={x.key} className="import-choice">
                        {x.name
                          ? <span><strong>{x.name}</strong><span className="cell-sub">Klant · {plural(x.rows, 'regel', 'regels')}</span></span>
                          : <span><strong>Nieuwe projecten zonder klant</strong><span className="cell-sub">Kies bij welke klant ze horen · {plural(x.rows, 'regel', 'regels')}</span></span>}
                        <select value={customers[x.key] ?? -1} aria-invalid={(customers[x.key] ?? -1) < 0}
                          onChange={e => { const c = { ...customers, [x.key]: Number(e.target.value) }; runCheck({ customers: c }) }}>
                          {(customers[x.key] ?? -1) < 0 && <option value={-1}>Kies een klant</option>}
                          {x.canCreate && <option value={0}>Nieuwe klant aanmaken</option>}
                          {check.customerOptions.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}
                        </select>
                      </label>
                    ))}
                  </div>
                )}
              </div>
            )}

            <div className="import-block">
              <h4 className="import-sub">Wat staat er al in Potel?</h4>
              <div className="import-modes" role="radiogroup" aria-label="Dubbele regels">
                {(['overslaan', 'bijwerken'] as const).map(m => (
                  <label key={m} className={`import-mode ${duplicates === m ? 'selected' : ''}`}>
                    <input type="radio" name="duplicates" checked={duplicates === m} onChange={() => { setDuplicates(m); runCheck({ duplicates: m }) }} />
                    <span>
                      <strong>{m === 'overslaan' ? 'Overslaan' : 'Bijwerken'}</strong>
                      <span className="cell-sub">{m === 'overslaan'
                        ? 'Wat er al staat, blijft zoals het is.'
                        : kind === 'uren' ? 'Een boeking die er al staat, krijgt factureerbaar zoals in het bestand (niet als hij al gefactureerd is).'
                          : kind === 'facturen' ? 'Een eerder geïmporteerde factuur krijgt de gegevens uit het bestand. Je eigen facturen blijven altijd zoals ze zijn.'
                            : 'Wat er in het bestand staat, neemt Potel over. Een lege cel wist niets.'}</span>
                    </span>
                  </label>
                ))}
              </div>
              {check.duplicateCount > 0 && (
                <details className="import-details">
                  <summary>{plural(check.duplicateCount, 'regel staat', 'regels staan')} al in Potel of dubbel in het bestand</summary>
                  <ul className="import-messages">
                    {check.duplicates.map((m, i) => <li key={i}><strong>Regel {m.line}: </strong>{m.text}</li>)}
                    {check.duplicateCount > check.duplicates.length && <li className="muted">En nog {check.duplicateCount - check.duplicates.length} andere.</li>}
                  </ul>
                </details>
              )}
            </div>

            {check.preview.length > 0 && (
              <div className="import-block">
                <h4 className="import-sub">Zo leest Potel de eerste regels</h4>
                <div className="table-wrap">
                  <table>
                    <thead><tr>{check.previewFields.map(h => <th key={h} className="nowrap">{h}</th>)}</tr></thead>
                    <tbody>{check.preview.map((r, i) => <tr key={i}>{r.map((v, j) => <td key={j} className="nowrap">{v}</td>)}</tr>)}</tbody>
                  </table>
                </div>
              </div>
            )}
            {kind === 'facturen' && (
              <p className="muted">
                Geïmporteerde facturen tellen mee in je omzet, het jaaroverzicht en de inzichten. Potel verstuurt of wijzigt ze niet, ze krijgen geen nummer uit je
                eigen reeks en ze tellen niet mee in de btw-aangifte.
              </p>
            )}
          </div>
          <div className="card-body import-actions">
            <button className="btn" onClick={() => setStep(2)}><ArrowLeft size={15} /> Kolommen</button>
            <button className="btn btn-primary" disabled={busy || check.errorCount > 0 || choicesOpen || check.create + check.update === 0} onClick={run}>
              {busy ? 'Bezig…' : check.create + check.update === 0 ? 'Niets te importeren' : `Importeer ${plural(check.create + check.update, 'regel', 'regels')}`}
            </button>
          </div>
        </div>
      )}

      {step === 4 && result && (
        <div className="card">
          <div className="card-head"><h3><CheckCircle2 size={18} className="text-green" /> {result.undoneAt ? 'Import ongedaan gemaakt' : 'Klaar'}</h3></div>
          <div className="card-body">
            <p style={{ marginTop: 0 }}>{summary(result)}</p>
            {!result.undoneAt && <p className="muted">Klopt er iets niet? Tot {utcDateTime(result.undoUntil)} kun je deze import in één keer ongedaan maken.</p>}
          </div>
          <div className="card-body import-actions">
            {result.canUndo && <button className="btn" disabled={busy} onClick={() => undo(result)}><Undo2 size={15} /> Ongedaan maken</button>}
            <button className="btn" onClick={reset}>Nog een bestand</button>
            {!result.undoneAt && <Link className="btn btn-primary" to={kinds.find(k => k.id === result.kind)!.page}>Bekijk {kindLabel(result.kind).toLowerCase()}</Link>}
          </div>
        </div>
      )}

      <div className="card" style={{ marginTop: 18 }}>
        <div className="card-head"><h3><History size={16} /> Eerdere imports</h3></div>
        {!batches ? <div className="card-body"><Loading /></div> : batches.length === 0 ? (
          <Empty icon={<History size={22} />} title="Nog niets geïmporteerd" text="Hier zie je straks elke import, en tot 24 uur erna kun je hem ongedaan maken." />
        ) : (
          <div className="table-wrap">
            <table>
              <thead><tr><th>Bestand</th><th>Wat</th><th>Wanneer</th><th>Resultaat</th><th /></tr></thead>
              <tbody>
                {batches.map(b => (
                  <tr key={b.id}>
                    <td><strong>{b.fileName}</strong>{b.source && <div className="cell-sub">{b.source}</div>}</td>
                    <td>{kindLabel(b.kind)}</td>
                    <td className="muted nowrap">{utcDateTime(b.importedAt)}{b.importedBy && <div className="cell-sub">{b.importedBy}</div>}</td>
                    <td>
                      {summary(b)}
                      {b.undoneAt && <div className="cell-sub">Ongedaan gemaakt{b.undoneBy ? ` door ${b.undoneBy}` : ''}{b.kept > 0 ? `; ${b.kept} bleef staan omdat het gebruikt wordt` : ''}</div>}
                    </td>
                    <td className="row-actions">
                      {b.canUndo && <button className="btn btn-sm" disabled={busy} onClick={() => undo(b)}><Undo2 size={14} /> Ongedaan maken</button>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </>
  )
}

function summary(b: ImportBatch) {
  const parts = [`${b.created} ${itemWord(b.kind, b.created)} toegevoegd`]
  if (b.updated) parts.push(`${b.updated} bijgewerkt`)
  if (b.skipped) parts.push(`${b.skipped} overgeslagen`)
  if (b.kind !== 'klanten' && b.customersCreated) parts.push(plural(b.customersCreated, 'nieuwe klant', 'nieuwe klanten'))
  if (b.kind !== 'projecten' && b.projectsCreated) parts.push(plural(b.projectsCreated, 'nieuw project', 'nieuwe projecten'))
  return parts.join(', ') + '.'
}
