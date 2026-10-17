import { Fragment, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  Search, Users, Target, Receipt, FileSignature, CornerDownLeft, FolderKanban, Wallet, Car, UserPlus, CalendarPlus,
  Play, Square, Pause, Shuffle, Moon, Sun, Lock, ReceiptText, CreditCard,
} from 'lucide-react'
import { api } from '../lib/api'
import { useApi } from '../lib/useApi'
import { useAuth } from '../lib/auth'
import { isAccountant } from '../lib/accountant'
import { useWorkspace } from '../lib/workspace'
import { useTimer } from '../lib/timer'
import { useToast } from '../lib/toast'
import { euro } from '../lib/format'
import { loadRecent, matchScore, rememberRecent, requestNew, type NewKind } from '../lib/palette'
import type { Project } from '../lib/types'

interface Hit { type: string; id: number; title: string; subtitle?: string | null }

// Een pagina om naartoe te gaan. De lijst komt uit het menu, dus je ziet alleen wat bij je rol hoort.
export interface PaletteLink { to: string; label: string; icon: ReactNode; hint?: string; keywords?: string[] }

type Group = 'Recent gebruikt' | 'Timer' | 'Nieuw' | 'Factureren' | 'Ga naar' | 'Weergave' | 'Gevonden'
const groupOrder: Group[] = ['Timer', 'Nieuw', 'Factureren', 'Ga naar', 'Weergave']

interface Command {
  id: string
  // Dezelfde handeling onder een andere naam telt als hetzelfde: starten op of wisselen naar een project.
  recent?: string
  label: string
  hint?: string | null
  keywords?: readonly string[]
  icon: ReactNode
  group: Group
  // Alleen als je iets typt, zoals een actie per project; anders wordt de lijst te lang.
  whenTyping?: boolean
  // Vult de zoektekst aan in plaats van iets te doen, zoals "Start timer op …". Alleen zonder zoektekst.
  fill?: string
  run?: () => void
}

const typeInfo: Record<string, { label: string; icon: ReactNode; path: (id: number) => string }> = {
  project: { label: 'Project', icon: <FolderKanban size={16} />, path: id => `/projecten/${id}` },
  klant: { label: 'Klant', icon: <Users size={16} />, path: id => `/klanten/${id}` },
  lead: { label: 'Lead', icon: <Target size={16} />, path: () => '/leads' },
  factuur: { label: 'Factuur', icon: <Receipt size={16} />, path: id => `/facturen/${id}` },
  offerte: { label: 'Offerte', icon: <FileSignature size={16} />, path: id => `/offertes/${id}` },
}

const timerWords = ['timer', 'uren', 'uur', 'tijd', 'klok', 'tijdregistratie']
const focusWords = ['focus', 'focusmodus', 'pomodoro', 'concentratie', 'blok']
const makeWords = ['nieuw', 'nieuwe', 'maak', 'maken', 'aanmaken', 'toevoegen', 'invoeren']
const billWords = ['factuur', 'facturen', 'factureren', 'uren', 'open', 'rekening']
const uur = (minutes: number) => `${(minutes / 60).toLocaleString('nl-NL', { maximumFractionDigits: 2 })} uur`
const MAX_RECENT_SHOWN = 5

export function CommandPalette({ onClose, theme, onToggleTheme, pages }: {
  onClose: () => void
  theme: string
  onToggleTheme: () => void
  pages: PaletteLink[]
}) {
  const [q, setQ] = useState('')
  const [hits, setHits] = useState<Hit[]>([])
  const [index, setIndex] = useState(0)
  const [opener] = useState(() => document.activeElement as HTMLElement | null)
  const list = useRef<HTMLDivElement>(null)
  const navigate = useNavigate()
  const toast = useToast()
  const { user } = useAuth()
  const { workspace } = useWorkspace()
  const { running, paused, focus, focusMinutes, start, stop, pause, resume, switchTo, startFocus, openFocus } = useTimer()
  const readOnly = workspace?.readOnly ?? null
  const admin = user.role === 'beheerder'
  // Een boekhouder kijkt alleen mee: geen acties en niet zoeken in de gegevens, alleen de pagina's die hij mag zien.
  const accountant = isAccountant(user)
  const noActions = !!readOnly || accountant
  // Projecten voor de timer en de open uren; in een alleen-lezen werkruimte zijn die acties er niet.
  const { data: projects } = useApi<Project[]>(noActions ? null : '/projects')
  const [recent] = useState(() => loadRecent(user.id))

  // Zoeken in je gegevens vanaf twee letters; daaronder tellen oude treffers niet meer.
  const searching = !accountant && q.trim().length >= 2
  useEffect(() => {
    if (!searching) return
    const t = setTimeout(() => {
      api.get<Hit[]>(`/search?q=${encodeURIComponent(q)}`).then(setHits).catch(() => setHits([]))
    }, 150)
    return () => clearTimeout(t)
  }, [q, searching])

  const commands = useMemo<Command[]>(() => {
    const list: Command[] = []
    const go = (to: string) => () => navigate(to)
    const newOn = (kind: NewKind, to: string) => () => { requestNew(kind, to); navigate(to) }

    if (!noActions) {
      const active = (projects ?? []).filter(p => p.status === 'actief')
      if (running) {
        const since = new Date(running.startedAt).toLocaleTimeString('nl-NL', { hour: '2-digit', minute: '2-digit' })
        list.push({
          id: 'timer-stop', label: `Stop timer op ${running.projectName} en boek de tijd`, hint: `Loopt sinds ${since}`,
          keywords: [...timerWords, 'stoppen', 'boeken', 'klaar'], icon: <Square size={16} />, group: 'Timer', run: () => void stop(),
        })
        if (focus) {
          list.push({ id: 'focus-open', label: 'Open de focusweergave', keywords: focusWords, icon: <Target size={16} />, group: 'Timer', run: openFocus })
        } else {
          list.push(paused
            ? { id: 'timer-resume', label: 'Hervat de timer', keywords: [...timerWords, 'verder', 'doorgaan', 'pauze'], icon: <Play size={16} />, group: 'Timer', run: () => void resume() }
            : { id: 'timer-pause', label: 'Pauzeer de timer', keywords: [...timerWords, 'pauze', 'pauzeren'], icon: <Pause size={16} />, group: 'Timer', run: () => void pause() })
          list.push({
            id: 'focus-running', recent: `focus:${running.projectId}`, label: `Start focusmodus op ${running.projectName}`, hint: `Blokken van ${focusMinutes} minuten`,
            keywords: [...focusWords, 'start', 'starten'], icon: <Target size={16} />, group: 'Timer', run: () => void startFocus(focusMinutes),
          })
        }
        const others = active.filter(p => p.id !== running.projectId)
        if (others.length) list.push({ id: 'timer-switch', label: 'Wissel timer naar …', fill: 'wissel timer naar ', icon: <Shuffle size={16} />, group: 'Timer' })
        for (const p of others) {
          list.push({
            id: `timer-switch-${p.id}`, recent: `timer:${p.id}`, label: `Wissel timer naar ${p.name}`, hint: p.customerName,
            keywords: [...timerWords, 'start', 'starten', 'wisselen', p.customerName ?? ''], icon: <Shuffle size={16} />, group: 'Timer', whenTyping: true,
            run: () => void switchTo({ projectId: p.id, description: '', projectName: p.name }),
          })
        }
      } else if (active.length) {
        list.push({ id: 'timer-start', label: 'Start timer op …', fill: 'start timer op ', icon: <Play size={16} />, group: 'Timer' })
        list.push({ id: 'focus-start', label: 'Start focusmodus op …', fill: 'start focusmodus op ', icon: <Target size={16} />, group: 'Timer' })
        for (const p of active) {
          list.push({
            id: `timer-start-${p.id}`, recent: `timer:${p.id}`, label: `Start timer op ${p.name}`, hint: p.customerName,
            keywords: [...timerWords, 'starten', 'begin', p.customerName ?? ''], icon: <Play size={16} />, group: 'Timer', whenTyping: true,
            run: () => void start({ projectId: p.id, description: '' }),
          })
          list.push({
            id: `focus-start-${p.id}`, recent: `focus:${p.id}`, label: `Start focusmodus op ${p.name}`, hint: `Blokken van ${focusMinutes} minuten`,
            keywords: [...focusWords, 'starten', 'timer', p.customerName ?? ''], icon: <Target size={16} />, group: 'Timer', whenTyping: true,
            run: () => void startFocus(focusMinutes, { projectId: p.id, description: '' }),
          })
        }
      }

      // Iets nieuws maken. Facturen, offertes en uitgaven gaan over geld en zijn er alleen voor beheerders.
      const make = (id: string, label: string, icon: ReactNode, keywords: string[], run: () => void): Command =>
        ({ id, label, icon, keywords: [...makeWords, ...keywords], group: 'Nieuw', run })
      if (admin) {
        list.push(make('new-invoice', 'Nieuwe factuur', <Receipt size={16} />, ['facturen', 'factureren', 'rekening'], go('/facturen/nieuw')))
        list.push(make('new-quote', 'Nieuwe offerte', <FileSignature size={16} />, ['offertes', 'voorstel', 'prijsopgave'], go('/offertes/nieuw')))
        list.push(make('new-expense', 'Nieuwe uitgave', <Wallet size={16} />, ['kosten', 'kost', 'bon', 'bonnetje', 'aankoop', 'inkoop'], newOn('uitgave', '/kosten')))
      }
      list.push(make('new-trip', 'Nieuwe rit', <Car size={16} />, ['ritten', 'kilometers', 'km', 'auto', 'reis'], newOn('rit', '/ritten')))
      list.push(make('new-customer', 'Nieuwe klant', <UserPlus size={16} />, ['klanten', 'relatie', 'opdrachtgever'], newOn('klant', '/klanten')))
      list.push(make('new-lead', 'Nieuwe lead', <Target size={16} />, ['leads', 'kans', 'sales', 'prospect'], newOn('lead', '/leads')))
      list.push(make('new-appointment', 'Nieuwe afspraak', <CalendarPlus size={16} />, ['afspraken', 'planning', 'inplannen', 'agenda', 'taak', 'meeting'], newOn('afspraak', '/planning')))

      // Open uren per klant, van projecten op uurbasis. Alleen voor beheerders, net als op de server.
      if (admin) {
        const open = new Map<number, { id: number; name: string; minutes: number; value: number }>()
        for (const p of projects ?? []) {
          if (p.billing !== 'uur' || p.minutesUnbilled <= 0) continue
          const c = open.get(p.customerId) ?? { id: p.customerId, name: p.customerName ?? 'Klant', minutes: 0, value: 0 }
          c.minutes += p.minutesUnbilled
          c.value += p.unbilledValue
          open.set(p.customerId, c)
        }
        const customers = [...open.values()].sort((a, b) => a.name.localeCompare(b.name, 'nl'))
        if (customers.length) list.push({ id: 'bill', label: 'Factureer open uren van …', fill: 'factureer open uren van ', icon: <ReceiptText size={16} />, group: 'Factureren' })
        for (const c of customers) {
          list.push({
            id: `bill-${c.id}`, recent: `bill:${c.id}`, label: `Factureer open uren van ${c.name}`, hint: `${uur(c.minutes)} · ${euro(c.value)}`,
            keywords: billWords, icon: <ReceiptText size={16} />, group: 'Factureren', whenTyping: true,
            run: async () => {
              try {
                const res = await api.post<{ id: number; minutes: number }>(`/customers/${c.id}/invoice-hours`)
                toast(`Conceptfactuur voor ${c.name} gemaakt met ${uur(res.minutes)}. Kijk hem na en verstuur hem.`)
                navigate(`/facturen/${res.id}`)
              } catch (e) {
                toast((e as Error).message, 'error')
              }
            },
          })
        }
      }
    }

    if (readOnly && admin) {
      list.push({ id: 'plan', label: 'Kies of betaal je abonnement', keywords: ['abonnement', 'betalen', 'alleen-lezen', 'proef'], icon: <CreditCard size={16} />, group: 'Ga naar', run: go('/instellingen?tab=abonnement') })
    }
    for (const p of pages) {
      list.push({ id: `page:${p.to}`, label: p.label, hint: p.hint, keywords: ['ga', 'naar', 'open', 'pagina', ...(p.keywords ?? [])], icon: p.icon, group: 'Ga naar', run: go(p.to) })
    }

    list.push({
      id: 'theme', label: theme === 'dark' ? 'Wissel naar licht thema' : 'Wissel naar donker thema',
      keywords: ['thema', 'donker', 'licht', 'dark', 'light', 'modus', 'kleur', 'nacht', 'dag', 'weergave'],
      icon: theme === 'dark' ? <Sun size={16} /> : <Moon size={16} />, group: 'Weergave', run: onToggleTheme,
    })
    return list
  }, [readOnly, noActions, admin, projects, running, paused, focus, focusMinutes, pages, theme, onToggleTheme, navigate, toast, start, stop, pause, resume, switchTo, startFocus, openFocus])

  // Zonder zoektekst: eerst wat je laatst deed, dan alles per groep. Met zoektekst: de beste treffers, wat je laatst deed iets hoger.
  const shown = useMemo<Command[]>(() => {
    const key = (c: Command) => c.recent ?? c.id
    if (!q.trim()) {
      const recentShown: Command[] = []
      for (const r of recent) {
        const c = commands.find(x => !x.fill && key(x) === r)
        if (c && recentShown.length < MAX_RECENT_SHOWN) recentShown.push({ ...c, group: 'Recent gebruikt' })
      }
      const done = new Set(recentShown.map(c => c.id))
      const rest = commands.filter(c => !c.whenTyping && !done.has(c.id))
      return [...recentShown, ...groupOrder.flatMap(g => rest.filter(c => c.group === g))]
    }
    const found = commands
      .filter(c => !c.fill)
      .map((c, order) => {
        const at = recent.indexOf(key(c))
        const score = matchScore(q, c.label, c.keywords)
        return { c, order, score: score > 0 && at >= 0 ? score + (recent.length - at) : score }
      })
      .filter(x => x.score > 0)
      .sort((a, b) => b.score - a.score || a.order - b.order)
      .slice(0, 30)
      .map(x => x.c)
    const searched = (searching ? hits : []).map<Command>(h => {
      const info = typeInfo[h.type]
      return {
        id: `hit:${h.type}-${h.id}`, label: h.title, hint: [info?.label ?? h.type, h.subtitle].filter(Boolean).join(' · '),
        icon: info?.icon, group: 'Gevonden', run: () => navigate(info?.path(h.id) ?? '/'),
      }
    })
    return [...found, ...searched]
  }, [q, commands, hits, searching, recent, navigate])

  const active = Math.min(index, shown.length - 1)

  useEffect(() => {
    list.current?.querySelector<HTMLElement>('[data-active="true"]')?.scrollIntoView({ block: 'nearest' })
  }, [active, shown])

  const search = (value: string) => { setQ(value); setIndex(0) }

  // Sluiten zonder iets te doen: terug naar waar je was.
  const dismiss = () => { onClose(); opener?.focus?.() }

  const execute = (c?: Command) => {
    if (!c) return
    if (c.fill) { search(c.fill); return }
    if (!c.id.startsWith('hit:')) rememberRecent(user.id, c.recent ?? c.id)
    onClose()
    c.run?.()
  }

  const move = (step: number) => {
    if (!shown.length) return
    setIndex((active + step + shown.length) % shown.length)
  }

  const typing = q.trim().length > 0
  return (
    <div className="modal-backdrop palette-backdrop" onMouseDown={dismiss}>
      <div className="palette" role="dialog" aria-label="Zoeken en snelle acties" onMouseDown={e => e.stopPropagation()}>
        <div className="palette-input">
          <Search size={18} />
          <input
            autoFocus
            role="combobox"
            aria-expanded="true"
            aria-controls="palette-list"
            aria-activedescendant={shown[active] ? `palette-${shown[active].id}` : undefined}
            aria-autocomplete="list"
            placeholder={accountant ? 'Zoek een pagina…' : 'Zoek een pagina of typ een actie…'}
            value={q}
            onChange={e => search(e.target.value)}
            onKeyDown={e => {
              if (e.nativeEvent.isComposing) return
              if (e.key === 'Escape') { e.preventDefault(); dismiss() }
              else if (e.key === 'ArrowDown' || (e.key === 'Tab' && !e.shiftKey)) { e.preventDefault(); move(1) }
              else if (e.key === 'ArrowUp' || (e.key === 'Tab' && e.shiftKey)) { e.preventDefault(); move(-1) }
              else if (e.key === 'Enter') { e.preventDefault(); execute(shown[active]) }
            }}
          />
        </div>
        <div className="palette-list" id="palette-list" role="listbox" aria-label="Acties en resultaten" ref={list}>
          {readOnly && !accountant && (
            <div className="palette-note" role="note" title={readOnly}>
              <Lock size={14} />
              <span>
                Je werkruimte is alleen-lezen, dus acties die iets veranderen staan hier niet.{' '}
                {admin ? 'Kies of betaal je abonnement om weer alles te kunnen doen.' : 'Vraag je beheerder om het abonnement te regelen.'}
              </span>
            </div>
          )}
          {shown.length === 0 && <div className="palette-empty">Niets gevonden voor "{q.trim()}". Probeer een ander woord.</div>}
          {shown.map((c, i) => {
            const header = typing
              ? (i === 0 || (c.group === 'Gevonden') !== (shown[i - 1]!.group === 'Gevonden')) && (c.group === 'Gevonden' ? 'Gevonden' : 'Acties')
              : (i === 0 || c.group !== shown[i - 1]!.group) && c.group
            return (
              <Fragment key={`${c.group}-${c.id}`}>
                {header && <div className="palette-group" role="presentation">{header}</div>}
                <button
                  type="button"
                  tabIndex={-1}
                  id={`palette-${c.id}`}
                  role="option"
                  aria-selected={i === active}
                  data-active={i === active}
                  className={`palette-item ${i === active ? 'active' : ''}`}
                  onMouseMove={() => { if (i !== active) setIndex(i) }}
                  onMouseDown={e => e.preventDefault()}
                  onClick={() => execute(c)}
                >
                  <span className="palette-icon">{c.icon}</span>
                  <span className="palette-label">{c.label}</span>
                  {c.hint && <span className="palette-hint">{c.hint}</span>}
                  {i === active && <CornerDownLeft size={14} className="palette-enter" />}
                </button>
              </Fragment>
            )
          })}
        </div>
        <div className="palette-foot" aria-hidden="true">
          <span><kbd>↑</kbd> <kbd>↓</kbd> kiezen</span>
          <span><kbd>Enter</kbd> uitvoeren</span>
          <span><kbd>Esc</kbd> sluiten</span>
          <span className="palette-foot-tip">Open dit venster ook met <kbd>/</kbd></span>
        </div>
      </div>
    </div>
  )
}
