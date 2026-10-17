import { useEffect, useRef } from 'react'
import { useLocation } from 'react-router-dom'
import { useTabActive } from './tabs'

// Kleine letters en zonder accenten, zodat "cafe" ook "Café" vindt.
export const fold = (s: string) => s.normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase()

const words = (s: string) => fold(s).split(/[^a-z0-9]+/).filter(Boolean)

// Kleine woordjes die je typt zonder dat ze hoeven te kloppen: "start timer op webshop" vindt ook "Wissel timer naar Webshop".
const filler = new Set(['op', 'van', 'de', 'het', 'een', 'naar', 'en', 'voor', 'mijn', 'aan', 'met', 'in', 'ik', 'wil'])

// Of de letters van wat je typt in dezelfde volgorde in het woord staan, voor een tikfout of een afkorting.
function inOrder(typed: string, word: string) {
  let i = 0
  for (const ch of word) if (ch === typed[i]) i++
  return i === typed.length
}

// Hoe goed één getypt woord bij één woord van een actie past. Nederlandse vormen tellen mee: "facturen" en
// "factureer" vinden "factuur", "klanten" vindt "klant" en "afspraken" vindt "afspraak".
function wordScore(typed: string, word: string) {
  if (word === typed) return 10
  if (word.startsWith(typed)) return 8
  if (typed.startsWith(word) && word.length >= 3) return 6
  let same = 0
  while (same < typed.length && same < word.length && typed[same] === word[same]) same++
  if (same >= 4 && same >= Math.min(typed.length, word.length) - 2) return 6
  if (typed.length >= 3 && word.includes(typed)) return 4
  if (typed.length >= 3 && typed[0] === word[0] && inOrder(typed, word)) return 2
  return 0
}

// De score van een actie voor wat je typt; 0 als hij niet past. Elk woord dat je typt moet ergens passen,
// behalve de kleine woordjes. Woorden uit de naam tellen zwaarder dan de zoekwoorden erachter.
export function matchScore(query: string, label: string, keywords: readonly string[] = []) {
  const typed = words(query)
  if (typed.length === 0) return 1
  const main = words(label)
  const extra = keywords.flatMap(words)
  const allFiller = typed.every(t => filler.has(t))
  let total = 0
  for (const t of typed) {
    let best = 0
    for (const w of main) best = Math.max(best, wordScore(t, w))
    for (const w of extra) best = Math.max(best, wordScore(t, w) * 0.8)
    if (best === 0 && (allFiller || !filler.has(t))) return 0
    total += best
  }
  const whole = fold(query).trim()
  const name = fold(label)
  if (name.startsWith(whole)) total += 6
  else if (name.includes(whole)) total += 3
  return total
}

// Wat je het laatst deed in het zoekvenster, per gebruiker en alleen in deze browser.
const recentKey = (userId: number) => `potel.palette.recent.${userId}`
const MAX_RECENT = 8

export function loadRecent(userId: number): string[] {
  try {
    const list: unknown = JSON.parse(localStorage.getItem(recentKey(userId)) ?? '[]')
    return Array.isArray(list) ? list.filter((x): x is string => typeof x === 'string').slice(0, MAX_RECENT) : []
  } catch {
    return []
  }
}

export function rememberRecent(userId: number, key: string) {
  try {
    const list = [key, ...loadRecent(userId).filter(k => k !== key)].slice(0, MAX_RECENT)
    localStorage.setItem(recentKey(userId), JSON.stringify(list))
  } catch { /* opslag niet beschikbaar: dan zonder recent */ }
}

// Iets nieuws maken op een andere pagina: het zoekvenster zet het verzoek klaar en gaat naar die pagina,
// en de pagina opent zelf zijn venster zodra zijn tabblad in beeld is. Alleen het tabblad met precies dat adres
// pakt het op (Kosten en Vaste kosten zijn twee tabbladen met dezelfde pagina). Een verzoek dat niemand oppakt, vervalt.
export type NewKind = 'uitgave' | 'rit' | 'klant' | 'lead' | 'afspraak'
const NEW_EVENT = 'potel:nieuw'
let pending: { kind: NewKind; path: string; at: number } | null = null

export function requestNew(kind: NewKind, path: string) {
  pending = { kind, path, at: Date.now() }
  window.dispatchEvent(new Event(NEW_EVENT))
}

export function useNewRequest(kind: NewKind, open: () => void) {
  const active = useTabActive()
  // Binnen een tabblad is dit het adres van dat tabblad.
  const { pathname, search } = useLocation()
  const path = pathname + search
  const openRef = useRef(open)
  useEffect(() => { openRef.current = open })
  useEffect(() => {
    if (!active) return
    const take = () => {
      if (pending?.kind !== kind || pending.path !== path) return
      const fresh = Date.now() - pending.at < 10_000
      pending = null
      if (fresh) openRef.current()
    }
    take()
    window.addEventListener(NEW_EVENT, take)
    return () => window.removeEventListener(NEW_EVENT, take)
  }, [active, kind, path])
}
