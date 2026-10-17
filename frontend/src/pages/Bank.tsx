import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { ArrowLeftRight, Ban, House, Lightbulb, Link2, Plus, Repeat, Search, Undo2, Upload, Wallet } from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useAuth } from '../lib/auth'
import { isAccountant } from '../lib/accountant'
import { useToast } from '../lib/toast'
import { date, euro, utcDateTime } from '../lib/format'
import { categoryLabel, expenseCategories, expenseVat, expenseVatRates } from '../lib/expenses'
import type { BankCsvMapping, BankFilter, BankImportResult, BankMark, BankOverview, BankProposal, BankTransaction, ExpenseCategory } from '../lib/types'
import { Badge, Empty, ErrorBox, Field, Loading, Modal, PageHeader, SubTabs } from '../components/ui'
import { RecurringExpenseForm } from '../components/RecurringExpenses'

const MaxBytes = 10 * 1024 * 1024

// Een bestand als data-URL, zoals de server het aanneemt.
const readFile = (file: File) => new Promise<string>((resolve, reject) => {
  const reader = new FileReader()
  reader.onload = () => resolve(String(reader.result))
  reader.onerror = () => reject(new Error('Dit bestand kunnen we niet lezen. Probeer het opnieuw.'))
  reader.readAsDataURL(file)
})

const markLabel = (m: BankMark) => (m === 'prive' ? 'Privé' : 'Genegeerd')
const iban = (s?: string | null) => s?.replace(/(.{4})/g, '$1 ').trim()

// De pagina Bank: lees een afschrift in en koppel elke regel aan een factuur of uitgave. Potel doet voorstellen; jij beslist.
export function BankPage() {
  const { user } = useAuth()
  // Een boekhouder ziet de regels en waaraan ze gekoppeld zijn, maar leest niets in en koppelt niets.
  const view = isAccountant(user)
  const allowed = user.role === 'beheerder' || view
  const [filter, setFilter] = useState<BankFilter>('open')
  const [q, setQ] = useState('')
  const [search, setSearch] = useState('')
  const { data, error, loading, reload } = useApi<BankOverview>(allowed ? `/bank?filter=${filter}&q=${encodeURIComponent(search)}` : null)
  const [busy, setBusy] = useState(false)
  const [mapping, setMapping] = useState<{ file: string; fileName: string; result: Extract<BankImportResult, { needsMapping: true }> } | null>(null)
  const [making, setMaking] = useState<BankTransaction | null>(null)
  const [makingRecurring, setMakingRecurring] = useState<BankTransaction | null>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  const toast = useToast()

  // Zoeken pas na even typen, niet bij elke letter.
  useEffect(() => {
    const t = setTimeout(() => setSearch(q.trim()), 300)
    return () => clearTimeout(t)
  }, [q])

  if (!allowed) return <ErrorBox message="Alleen een beheerder kan bankafschriften inlezen en koppelen." />

  const upload = async (fileName: string, file: string, map?: BankCsvMapping) => {
    setBusy(true)
    try {
      const res = await api.post<BankImportResult>('/bank/import', { fileName, data: file, mapping: map })
      if (res.needsMapping) {
        setMapping({ file, fileName, result: res })
        return
      }
      setMapping(null)
      const extra = [
        res.skipped ? `${res.skipped} ${res.skipped === 1 ? 'regel stond' : 'regels stonden'} er al` : '',
        res.skippedOtherCurrency ? `${res.skippedOtherCurrency} in een andere valuta overgeslagen` : '',
        res.skippedNotBooked ? `${res.skippedNotBooked} nog niet geboekt` : '',
      ].filter(Boolean).join(', ')
      toast(`${res.added} nieuwe ${res.added === 1 ? 'regel' : 'regels'} ingelezen${extra ? `; ${extra}` : ''}.`)
      setFilter('open')
      await reload(true)
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const pick = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    e.target.value = ''
    if (!file) return
    if (file.size > MaxBytes) { toast('Dit bestand is te groot. Een afschrift mag hooguit 10 MB zijn; kies een kortere periode.', 'error'); return }
    try {
      await upload(file.name, await readFile(file))
    } catch (err) {
      toast((err as Error).message, 'error')
    }
  }

  // Elke actie op een regel: daarna de lijst opnieuw, zodat tellers en voorstellen kloppen.
  const act = async (url: string, body: unknown, done: string) => {
    setBusy(true)
    try {
      const res = await api.post<{ message?: string }>(url, body)
      toast(res?.message ?? done)
      await reload(true)
      return true
    } catch (e) {
      toast((e as Error).message, 'error')
      return false
    } finally {
      setBusy(false)
    }
  }

  const undoImport = async (id: number, fileName: string) => {
    if (!confirm(`De import van ${fileName} ongedaan maken? De regels uit dit bestand gaan weg uit Potel.`)) return
    setBusy(true)
    try {
      await api.del(`/bank/imports/${id}`)
      toast('Import ongedaan gemaakt')
      await reload(true)
    } catch (e) {
      toast((e as Error).message, 'error')
    } finally {
      setBusy(false)
    }
  }

  const counts = data?.counts
  const empty = counts?.all === 0

  return (
    <>
      <PageHeader
        icon={<ArrowLeftRight size={20} />}
        title="Bank"
        subtitle={counts ? (empty ? (view ? 'Er is nog geen bankafschrift ingelezen' : 'Lees je bankafschrift in en koppel betalingen aan je facturen en kosten') : `${counts.open} ${counts.open === 1 ? 'regel' : 'regels'} om te koppelen`) : undefined}
        actions={<>
          {!empty && <div className="search-input"><Search size={16} /><input placeholder="Zoek naam, omschrijving of bedrag…" value={q} onChange={e => setQ(e.target.value)} /></div>}
          {!view && <>
            <input ref={fileInput} type="file" accept=".xml,.csv,.txt,text/xml,text/csv" hidden onChange={pick} />
            <button className="btn btn-primary" disabled={busy} onClick={() => fileInput.current?.click()}><Upload size={16} /> Afschrift inlezen</button>
          </>}
        </>}
      />

      {error && <ErrorBox message={error} onRetry={() => reload()} />}
      {loading && !data && <Loading />}

      {data && empty && view && (
        <div className="card">
          <Empty icon={<ArrowLeftRight size={22} />} title="Nog geen bankafschrift ingelezen"
            text="Zodra je klant een bankafschrift inleest, zie je hier de regels en waaraan ze gekoppeld zijn." />
        </div>
      )}

      {data && empty && !view && (
        <div className="card">
          <Empty icon={<ArrowLeftRight size={22} />} title="Nog geen bankafschrift ingelezen"
            text="Download bij je bank een afschrift als CAMT.053 (XML) of als CSV en lees het hier in. Potel stelt dan voor bij welke factuur of uitgave elke regel hoort. Jij bevestigt elk voorstel zelf."
            action={<button className="btn btn-primary" disabled={busy} onClick={() => fileInput.current?.click()}><Upload size={16} /> Afschrift inlezen</button>} />
          <div className="card-body" style={{ paddingTop: 0 }}>
            <ul className="cell-sub" style={{ margin: '0 auto', maxWidth: 620, lineHeight: 1.6, paddingLeft: 18 }}>
              <li><strong>ING:</strong> Mijn ING, Af- en bijschrijvingen downloaden, kies CSV met puntkomma of CAMT.053.</li>
              <li><strong>Rabobank:</strong> Rabo Online Bankieren, Transacties downloaden, kies CSV of CAMT.053.</li>
              <li><strong>Andere banken:</strong> kies CAMT.053 als dat kan. Een andere CSV kan ook: dan wijs je één keer aan welke kolom wat is.</li>
            </ul>
            <p className="cell-sub" style={{ textAlign: 'center' }}>Lees je hetzelfde afschrift nog eens in, dan slaat Potel de regels over die er al staan.</p>
          </div>
        </div>
      )}

      {data && !empty && counts && (<>
        <SubTabs<BankFilter>
          value={filter}
          onChange={setFilter}
          tabs={[
            { id: 'open', label: 'Te koppelen', count: counts.open },
            { id: 'gekoppeld', label: 'Gekoppeld', count: counts.linked },
            { id: 'prive', label: 'Privé', count: counts.prive },
            { id: 'negeren', label: 'Genegeerd', count: counts.negeren },
            { id: 'alle', label: 'Alle', count: counts.all },
          ]}
        />
        <div className="card" style={{ marginTop: 12 }}>
          {data.transactions.length === 0 ? (
            <Empty icon={<Link2 size={22} />}
              title={search ? 'Niets gevonden' : filter === 'open' ? 'Alles is gekoppeld' : 'Hier staat nog niets'}
              text={search ? 'Probeer een andere naam, omschrijving of bedrag.' : filter === 'open' ? 'Elke regel uit je afschrift hoort ergens bij. Lees een nieuw afschrift in als er weer betalingen zijn.' : undefined} />
          ) : (
            <div className="bank-list">
              {data.transactions.map(t => (
                <BankRow key={t.id} t={t} data={data} busy={busy} view={view} act={act} onMakeExpense={() => setMaking(t)} onMakeRecurring={() => setMakingRecurring(t)} />
              ))}
            </div>
          )}
          {data.more && <p className="cell-sub" style={{ padding: '0 18px 14px' }}>Je ziet de nieuwste {data.transactions.length} van {data.total} regels. Zoek om oudere regels te vinden.</p>}
        </div>

        {data.imports.length > 0 && (
          <div className="card" style={{ marginTop: 16 }}>
            <div className="card-head"><h3><Upload size={16} /> Ingelezen afschriften</h3></div>
            <div className="card-body">
              <ul className="mail-log">
                {data.imports.map(i => (
                  <li key={i.id}>
                    <div className="row between wrap" style={{ gap: 8 }}>
                      <strong style={{ overflowWrap: 'anywhere' }}>{i.fileName}</strong>
                      <span className="row" style={{ gap: 8 }}>
                        <span className="muted">{utcDateTime(i.importedAt)}</span>
                        {!view && <button className="btn btn-sm btn-ghost" disabled={busy} onClick={() => undoImport(i.id, i.fileName)}><Undo2 size={14} /> Ongedaan maken</button>}
                      </span>
                    </div>
                    <div className="muted">
                      {i.format}: {i.added} nieuwe {i.added === 1 ? 'regel' : 'regels'}{i.skipped ? `, ${i.skipped} stonden er al` : ''}{i.importedBy ? `, door ${i.importedBy}` : ''}
                    </div>
                  </li>
                ))}
              </ul>
              {!view && <p className="cell-sub" style={{ marginBottom: 0 }}>
                Een import ongedaan maken kan zolang er nog niets uit gekoppeld is. Handig als je de kolommen van een CSV verkeerd had aangewezen.
                Lees je van dezelfde periode zowel een CAMT.053 als een CSV in, dan kunnen regels dubbel staan; kies één soort bestand.
              </p>}
            </div>
          </div>
        )}
      </>)}

      {mapping && (
        <MappingDialog fileName={mapping.fileName} result={mapping.result} busy={busy} onClose={() => setMapping(null)}
          onSubmit={m => upload(mapping.fileName, mapping.file, m)} />
      )}
      {making && making.newExpense && (
        <NewExpenseDialog t={making} busy={busy} onClose={() => setMaking(null)}
          onSubmit={async body => { if (await act(`/bank/transactions/${making.id}/create-expense`, body, 'Uitgave gemaakt en gekoppeld')) setMaking(null) }} />
      )}
      {makingRecurring && makingRecurring.newExpense && (
        <RecurringExpenseForm onClose={() => setMakingRecurring(null)} onSaved={() => setMakingRecurring(null)}
          fromBank={{ ...makingRecurring.newExpense, amount: -makingRecurring.amount, date: makingRecurring.date }}
          submit={body => act(`/bank/transactions/${makingRecurring.id}/create-subscription`, body, 'Vaste kost gemaakt en gekoppeld')} />
      )}
    </>
  )
}

const reasonText = (p: BankProposal) => `Gevonden op ${p.reason}`

function BankRow({ t, data, busy, view, act, onMakeExpense, onMakeRecurring }: {
  t: BankTransaction
  data: BankOverview
  busy: boolean
  // Alleen bekijken, zoals een boekhouder: geen voorstellen of knoppen.
  view: boolean
  act: (url: string, body: unknown, done: string) => Promise<boolean>
  onMakeExpense: () => void
  onMakeRecurring: () => void
}) {
  const incoming = t.amount > 0
  const [remember, setRemember] = useState(true)
  const [choice, setChoice] = useState('')
  const open = t.state === 'open'
  const base = `/bank/transactions/${t.id}`

  const linkInvoice = (invoiceId: number, paymentId?: number | null, rememberIban?: boolean) =>
    act(`${base}/invoice`, { invoiceId, paymentId: paymentId ?? null, rememberIban: !!rememberIban }, 'Gekoppeld aan de factuur')
  const linkExpense = (expenseId: number) => act(`${base}/expense`, { expenseId }, 'Gekoppeld aan de uitgave')
  const bookSubscription = (p: BankProposal) =>
    act(`${base}/subscription`, { recurringExpenseId: p.recurringExpenseId, periodStart: p.periodStart }, 'Geboekt als vaste kost en gekoppeld')
  const mark = (m: BankMark) => act(`${base}/mark`, { mark: m }, m === 'prive' ? 'Op privé gezet' : 'Genegeerd')
  const unlink = () => act(`${base}/unlink`, null, 'Ongedaan gemaakt')

  // Zelf kiezen: open facturen bij geld dat binnenkwam, uitgaven zonder bankregel bij geld dat eraf ging.
  const choices = incoming
    ? data.openInvoices.map(i => ({ id: i.id, label: `${i.number}, ${i.customer} (${euro(i.open)} open)` }))
    : data.expenses.map(e => ({ id: e.id, label: `${e.supplier}, ${date(e.date)} (${euro(e.amount)})` }))
  const chosenInvoice = incoming ? data.openInvoices.find(i => String(i.id) === choice) : undefined
  const customerIban = (invoiceId?: number | null) => data.openInvoices.find(i => i.id === invoiceId)?.customerIban
  // Het rekeningnummer uit de bank bij de klant bewaren, alleen als hij daar nog niet (of anders) staat. Je ziet het altijd.
  const offerIban = !!chosenInvoice && t.ibanValid && chosenInvoice.customerIban !== t.counterpartyIban
  const rememberLabel = (current?: string | null) =>
    `Onthoud ${iban(t.counterpartyIban)} bij deze klant${current ? `, in plaats van ${iban(current)}` : ''}`

  return (
    <div className="bank-row">
      <div className="bank-main">
        <div className="bank-when">
          <span className={`bank-dir ${incoming ? 'in' : 'out'}`} aria-hidden>{incoming ? '+' : '−'}</span>
          <div>
            <strong className={incoming ? 'text-green' : undefined}>{euro(t.amount)}</strong>
            <div className="cell-sub">{date(t.date)}</div>
          </div>
        </div>
        <div className="bank-what">
          <strong>{t.counterpartyName ?? (incoming ? 'Onbekende betaler' : 'Onbekende ontvanger')}</strong>
          {t.counterpartyIban && <span className="cell-sub" style={{ marginLeft: 8 }}>{iban(t.counterpartyIban)}</span>}
          {t.description && <div className="cell-sub bank-desc">{t.description}</div>}
        </div>
        <div className="bank-state">
          {t.link?.kind === 'invoice' && <Badge tone="green">Factuur {t.link.number}</Badge>}
          {t.link?.kind === 'expense' && <Badge tone="blue">Uitgave {t.link.supplier}</Badge>}
          {t.mark && <Badge tone="gray">{markLabel(t.mark)}</Badge>}
          {open && t.proposals.length > 0 && <Badge tone="yellow">{t.proposals.length === 1 ? '1 voorstel' : `${t.proposals.length} voorstellen`}</Badge>}
        </div>
      </div>

      {!open && (
        <div className="bank-actions">
          {t.link?.kind === 'invoice' && (
            <span className="cell-sub">
              <Link to={`/facturen/${t.link.invoiceId}`}>Factuur {t.link.number}</Link>{t.link.customer ? ` (${t.link.customer})` : ''}:{' '}
              {t.link.created ? 'de betaling op de factuur komt uit deze regel.' : 'hoort bij een betaling die je zelf had ingevuld.'}
            </span>
          )}
          {t.link?.kind === 'expense' && (
            <span className="cell-sub">
              Uitgave bij <Link to="/kosten">{t.link.supplier}</Link> van {date(t.link.date)}: {categoryLabel(t.link.category)}, {euro(t.link.amount)}
              {t.link.recurring && <>, van een <Link to="/kosten?tab=vast">vaste kost</Link></>}
            </span>
          )}
          {!view && <button className="btn btn-sm btn-ghost" disabled={busy} onClick={unlink}><Undo2 size={14} /> Ongedaan maken</button>}
        </div>
      )}

      {open && !view && (<>
        {t.hint && <div className="notice bank-hint"><Lightbulb size={14} /> {t.hint}</div>}
        {t.proposals.map(p => (
          <div key={`${p.kind}-${p.invoiceId}-${p.paymentId}-${p.expenseId}-${p.recurringExpenseId}`} className="bank-proposal">
            <div className="grow">
              {p.kind === 'subscription' ? (
                <div><strong><Repeat size={13} /> Vaste kost bij {p.supplier}</strong>, {p.period}</div>
              ) : p.kind === 'expense' ? (
                <div>
                  <strong>Uitgave bij {p.supplier}</strong> van {date(p.expenseDate)}
                  {p.period && <>, je vaste kost voor {p.period}</>}
                </div>
              ) : (
                <div>
                  <strong>Factuur {p.number}</strong>, {p.customer}
                  {p.kind === 'payment' && <>, de betaling van {date(p.paymentDate)} die je zelf had ingevuld</>}
                </div>
              )}
              <div className="cell-sub">
                {reasonText(p)}.
                {p.kind === 'subscription' && <> Er staat nog geen uitgave voor deze periode; die komt erbij met bedrag en datum uit je bank.</>}
                {p.expenseAmount != null && p.expenseAmount !== -t.amount && <> Verwacht was {euro(p.expenseAmount)}, je bank zegt {euro(-t.amount)}.</>}
                {p.kind === 'invoice' && p.partial && p.open != null && <> Deelbetaling: daarna staat er nog {euro(p.open - t.amount)} open.</>}
                {p.kind === 'invoice' && !p.partial && <> Daarmee is de factuur betaald.</>}
              </div>
              {p.rememberIban && (
                <label className="row cell-sub" style={{ gap: 6, marginTop: 4 }}>
                  <input type="checkbox" checked={remember} onChange={e => setRemember(e.target.checked)} />
                  {rememberLabel(customerIban(p.invoiceId))}
                </label>
              )}
            </div>
            <button className="btn btn-sm btn-primary" disabled={busy}
              onClick={() => p.kind === 'subscription' ? bookSubscription(p) : p.kind === 'expense' ? linkExpense(p.expenseId!) : linkInvoice(p.invoiceId!, p.paymentId, p.rememberIban ? remember : false)}>
              <Link2 size={14} /> {p.kind === 'subscription' ? 'Boek en koppel' : 'Koppel'}
            </button>
          </div>
        ))}
        <div className="bank-actions">
          {!incoming && t.newExpense && <button className="btn btn-sm" disabled={busy} onClick={onMakeExpense}><Plus size={14} /> Maak kost</button>}
          {!incoming && t.newExpense && <button className="btn btn-sm" disabled={busy} onClick={onMakeRecurring} title="Komt deze afschrijving elke maand, elk kwartaal of elk jaar terug?"><Repeat size={14} /> Maak vaste kost</button>}
          {choices.length > 0 && (
            <span className="row wrap" style={{ gap: 6 }}>
              <select value={choice} onChange={e => setChoice(e.target.value)} aria-label={incoming ? 'Kies een factuur' : 'Kies een uitgave'} className="bank-choice">
                <option value="">{incoming ? 'Andere factuur kiezen…' : 'Bestaande uitgave kiezen…'}</option>
                {choices.map(c => <option key={c.id} value={c.id}>{c.label}</option>)}
              </select>
              {choice && <button className="btn btn-sm" disabled={busy}
                onClick={() => incoming ? linkInvoice(Number(choice), null, offerIban && remember) : linkExpense(Number(choice))}>
                <Link2 size={14} /> Koppel
              </button>}
            </span>
          )}
          {offerIban && (
            <label className="row cell-sub" style={{ gap: 6 }}>
              <input type="checkbox" checked={remember} onChange={e => setRemember(e.target.checked)} />
              {rememberLabel(chosenInvoice?.customerIban)}
            </label>
          )}
          <button className="btn btn-sm btn-ghost" disabled={busy} onClick={() => mark('prive')} title="Privé: geen zakelijke inkomsten of kosten"><House size={14} /> Privé</button>
          <button className="btn btn-sm btn-ghost" disabled={busy} onClick={() => mark('negeren')} title="Negeren, bijvoorbeeld een overboeking naar je spaarrekening"><Ban size={14} /> Negeren</button>
        </div>
      </>)}
    </div>
  )
}

const fields: { key: keyof BankCsvMapping; label: string; required?: boolean; guess: RegExp }[] = [
  { key: 'date', label: 'Datum', required: true, guess: /datum|date|boek/i },
  { key: 'amount', label: 'Bedrag', required: true, guess: /bedrag|amount|transactiebedrag/i },
  { key: 'description', label: 'Omschrijving', guess: /omschrijving|mededeling|description|kenmerk/i },
  { key: 'name', label: 'Naam tegenpartij', guess: /naam|name|tegenpartij/i },
  { key: 'iban', label: 'Rekeningnummer tegenpartij', guess: /iban|tegenrekening|rekening tegen/i },
  { key: 'sign', label: 'Af of bij (als het teken niet in het bedrag staat)', guess: /af.?bij|debet|credit|richting|d\/c/i },
]

// Een CSV die we niet herkennen: jij wijst aan welke kolom wat is. Potel onthoudt het voor de volgende keer.
function MappingDialog({ fileName, result, busy, onClose, onSubmit }: {
  fileName: string
  result: Extract<BankImportResult, { needsMapping: true }>
  busy: boolean
  onClose: () => void
  onSubmit: (m: BankCsvMapping) => void
}) {
  const [form, setForm] = useState<BankCsvMapping>(() => {
    const has = (c?: string | null) => !!c && result.headers.some(h => h.toLowerCase() === c.toLowerCase())
    const saved = result.mapping
    const used = new Set<string>()
    const guess = (re: RegExp) => {
      const h = result.headers.find(x => re.test(x) && !used.has(x))
      if (h) used.add(h)
      return h ?? ''
    }
    const out = {} as BankCsvMapping
    for (const f of fields) {
      const fromSaved = saved?.[f.key]
      out[f.key] = (has(fromSaved) ? fromSaved : guess(f.guess)) as string
    }
    return out
  })
  const ok = !!form.date && !!form.amount

  return (
    <Modal wide title="Welke kolom is wat?" onClose={onClose}
      footer={<>
        <button className="btn" onClick={onClose}>Annuleren</button>
        <button className="btn btn-primary" disabled={!ok || busy}
          onClick={() => onSubmit(Object.fromEntries(Object.entries(form).map(([k, v]) => [k, v || null])) as unknown as BankCsvMapping)}>
          Inlezen
        </button>
      </>}>
      <p className="muted" style={{ marginTop: 0 }}>
        We herkennen de kolommen van <strong>{fileName}</strong> niet. Wijs aan in welke kolom de datum en het bedrag staan, en als het kan ook
        de omschrijving en de tegenpartij. Potel onthoudt deze keuze voor de volgende keer.
      </p>
      <div className="form-grid">
        {fields.map(f => (
          <Field key={f.key} label={`${f.label}${f.required ? '' : ' (optioneel)'}`}>
            <select value={form[f.key] ?? ''} onChange={e => setForm(m => ({ ...m, [f.key]: e.target.value }))}>
              <option value="">{f.required ? 'Kies een kolom' : 'Geen'}</option>
              {result.headers.map(h => <option key={h} value={h}>{h}</option>)}
            </select>
          </Field>
        ))}
      </div>
      <p className="cell-sub">Bedragen mogen met een komma (12,50) of een punt (12.50). Datums lezen we als dag, maand, jaar (31-12-2026) of als 2026-12-31.</p>
      <div className="table-wrap">
        <table>
          <thead><tr>{result.headers.map(h => <th key={h}>{h}</th>)}</tr></thead>
          <tbody>{result.sample.map((r, i) => <tr key={i}>{result.headers.map((_, j) => <td key={j} className="nowrap">{r[j] ?? ''}</td>)}</tr>)}</tbody>
        </table>
      </div>
    </Modal>
  )
}

// Van een afschrijving een uitgave maken. Bedrag en datum komen uit de bank; leverancier, categorie en btw kies je zelf.
function NewExpenseDialog({ t, busy, onClose, onSubmit }: {
  t: BankTransaction
  busy: boolean
  onClose: () => void
  onSubmit: (body: { supplier: string; description: string | null; category: ExpenseCategory | ''; vatRate: number | null }) => void
}) {
  const pre = t.newExpense!
  const [supplier, setSupplier] = useState(pre.supplier ?? '')
  const [description, setDescription] = useState(pre.description ?? '')
  const [category, setCategory] = useState<ExpenseCategory | ''>(pre.category ?? '')
  const [vatRate, setVatRate] = useState<number | null>(pre.vatRate === undefined ? 21 : pre.vatRate)
  const vat = expenseVat(pre.amount, vatRate, false)

  return (
    <Modal title="Maak kost" onClose={onClose}
      footer={<>
        <button className="btn" onClick={onClose}>Annuleren</button>
        <button className="btn btn-primary" disabled={busy || !supplier.trim() || !category}
          onClick={() => onSubmit({ supplier: supplier.trim(), description: description.trim() || null, category, vatRate })}>
          <Wallet size={15} /> Opslaan en koppelen
        </button>
      </>}>
      <p className="muted" style={{ marginTop: 0 }}>
        Je betaalde <strong>{euro(pre.amount)}</strong> op {date(pre.date)}. Bedrag en datum komen uit je bank.
        {pre.fromHistory && ' Categorie en btw komen van je vorige uitgave bij deze leverancier.'}
      </p>
      <div className="form-grid">
        <Field label="Leverancier"><input value={supplier} onChange={e => setSupplier(e.target.value)} /></Field>
        <Field label="Categorie">
          <select value={category} onChange={e => setCategory(e.target.value as ExpenseCategory)}>
            <option value="">Kies een categorie</option>
            {expenseCategories.map(c => <option key={c.id} value={c.id}>{c.label}</option>)}
          </select>
        </Field>
        <Field label="Btw-tarief">
          <select value={vatRate == null ? '' : String(vatRate)} onChange={e => setVatRate(e.target.value === '' ? null : Number(e.target.value))}>
            {expenseVatRates.map(r => <option key={r.label} value={r.value == null ? '' : String(r.value)}>{r.label}</option>)}
          </select>
        </Field>
        <Field label="Btw in het bedrag"><input value={euro(vat)} readOnly tabIndex={-1} /></Field>
        <Field label="Omschrijving" full><input value={description} onChange={e => setDescription(e.target.value)} /></Field>
      </div>
      <p className="cell-sub" style={{ marginBottom: 0 }}>
        Rekende een buitenlandse leverancier geen btw (btw verlegd)? Sla de uitgave dan op en pas hem daarna aan bij Kosten. Een bonnetje voeg je daar ook toe.
      </p>
    </Modal>
  )
}
