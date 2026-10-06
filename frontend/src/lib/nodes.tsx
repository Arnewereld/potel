import { Zap, Mail, ListTodo, Tag, Clock, GitBranch, Receipt, Globe, CalendarClock, MousePointerClick, UserPlus, BadgeEuro, Users, type LucideIcon } from 'lucide-react'

export interface NodeField {
  key: string
  label: string
  type: 'text' | 'textarea' | 'number' | 'select'
  options?: { value: string; label: string }[]
  placeholder?: string
  default?: string
  help?: string
}

export interface NodeType {
  type: string
  label: string
  group: 'Triggers' | 'Acties' | 'Logica'
  color: string
  icon: LucideIcon
  description: string
  fields?: NodeField[]
}

const leadStatusOptions = ['nieuw', 'contact', 'offerte', 'gewonnen', 'verloren'].map(v => ({ value: v, label: v }))
const invoiceStatusOptions = ['concept', 'verzonden', 'betaald', 'verlopen'].map(v => ({ value: v, label: v }))

// De bouwblokken die je op het werkstroom-canvas kunt zetten. De backend voert ze echt uit.
export const nodeTypes: NodeType[] = [
  { type: 'trigger.manual', label: 'Handmatig starten', group: 'Triggers', color: '#ff6d5a', icon: MousePointerClick, description: 'Start met de knop Uitvoeren' },
  { type: 'trigger.lead', label: 'Nieuwe lead', group: 'Triggers', color: '#ff6d5a', icon: Zap, description: 'Als er een lead wordt toegevoegd' },
  { type: 'trigger.customer', label: 'Nieuwe klant', group: 'Triggers', color: '#ff6d5a', icon: Users, description: 'Als er een klant wordt toegevoegd' },
  { type: 'trigger.paid', label: 'Factuur betaald', group: 'Triggers', color: '#ff6d5a', icon: BadgeEuro, description: 'Als een factuur op betaald gaat' },
  {
    type: 'trigger.schedule', label: 'Schema', group: 'Triggers', color: '#ff6d5a', icon: CalendarClock, description: 'Elk uur, elke dag of elke week',
    fields: [
      { key: 'interval', label: 'Hoe vaak', type: 'select', default: 'dag', options: [{ value: 'uur', label: 'Elk uur' }, { value: 'dag', label: 'Elke dag' }, { value: 'week', label: 'Elke week' }] },
      { key: 'hour', label: 'Vanaf hoe laat (uur)', type: 'number', default: '9' },
      { key: 'weekday', label: 'Dag (bij elke week)', type: 'select', default: '1', options: ['zondag', 'maandag', 'dinsdag', 'woensdag', 'donderdag', 'vrijdag', 'zaterdag'].map((d, i) => ({ value: String(i), label: d })) },
    ],
  },
  {
    type: 'action.email', label: 'E-mail sturen', group: 'Acties', color: '#4ea5ff', icon: Mail, description: 'Stuur een e-mail',
    fields: [
      { key: 'to', label: 'Aan', type: 'text', default: '{{lead.email}}', help: 'Eén e-mailadres' },
      { key: 'subject', label: 'Onderwerp', type: 'text', default: 'Bedankt voor je interesse' },
      { key: 'body', label: 'Bericht', type: 'textarea', default: 'Beste {{lead.name}},\n\n' },
    ],
  },
  {
    type: 'action.task', label: 'Taak maken', group: 'Acties', color: '#3ecf8e', icon: ListTodo, description: 'Zet een taak in de planning',
    fields: [
      { key: 'title', label: 'Titel', type: 'text', default: 'Opvolgen: {{lead.name}}' },
      { key: 'days', label: 'Over hoeveel dagen', type: 'number', default: '1', help: '0 is vandaag, om 09:00' },
      { key: 'kind', label: 'Soort', type: 'select', default: 'taak', options: [{ value: 'taak', label: 'Taak' }, { value: 'afspraak', label: 'Afspraak' }, { value: 'project', label: 'Project' }, { value: 'intern', label: 'Intern' }] },
    ],
  },
  {
    type: 'action.status', label: 'Status wijzigen', group: 'Acties', color: '#9b7bff', icon: Tag, description: 'Verplaats een lead of factuur',
    fields: [
      { key: 'target', label: 'Wat', type: 'select', default: 'lead', options: [{ value: 'lead', label: 'Lead' }, { value: 'invoice', label: 'Factuur' }] },
      { key: 'status', label: 'Nieuwe status', type: 'select', default: 'contact', options: [...leadStatusOptions, ...invoiceStatusOptions.filter(o => !leadStatusOptions.some(l => l.value === o.value))] },
    ],
  },
  {
    type: 'action.customer', label: 'Klant maken', group: 'Acties', color: '#2fc6c6', icon: UserPlus, description: 'Zet de lead om naar een klant',
  },
  {
    type: 'action.invoice', label: 'Factuur maken', group: 'Acties', color: '#f5b83d', icon: Receipt, description: 'Maak een conceptfactuur voor de klant',
    fields: [
      { key: 'description', label: 'Omschrijving', type: 'text', default: 'Diensten' },
      { key: 'amount', label: 'Bedrag excl. btw', type: 'text', default: '{{lead.value}}' },
    ],
  },
  {
    type: 'action.webhook', label: 'Webhook', group: 'Acties', color: '#ff5ca8', icon: Globe, description: 'Stuur de gegevens naar een andere app',
    fields: [{ key: 'url', label: 'URL', type: 'text', placeholder: 'https://…', help: 'Krijgt alle gegevens van de run als JSON (POST). Alleen openbare adressen op poort 80 of 443.' }],
  },
  {
    type: 'logic.wait', label: 'Wachten', group: 'Logica', color: '#8a8aa0', icon: Clock, description: 'Wacht een tijdje en ga dan verder',
    fields: [
      { key: 'amount', label: 'Hoe lang', type: 'number', default: '1', help: 'Minstens 1' },
      { key: 'unit', label: 'Eenheid', type: 'select', default: 'dagen', options: [{ value: 'minuten', label: 'Minuten' }, { value: 'uren', label: 'Uren' }, { value: 'dagen', label: 'Dagen' }] },
    ],
  },
  {
    type: 'logic.if', label: 'Als / dan', group: 'Logica', color: '#8a8aa0', icon: GitBranch, description: 'Ga verder via Ja of Nee',
    fields: [
      { key: 'field', label: 'Veld', type: 'select', default: 'lead.value', options: [
        'lead.value', 'lead.status', 'lead.source', 'lead.email', 'lead.company', 'customer.city', 'customer.email', 'invoice.total', 'invoice.status',
      ].map(v => ({ value: v, label: v })) },
      { key: 'operator', label: 'Is', type: 'select', default: '>', options: [
        { value: '>', label: 'groter dan' }, { value: '>=', label: 'groter of gelijk' }, { value: '<', label: 'kleiner dan' }, { value: '<=', label: 'kleiner of gelijk' },
        { value: '=', label: 'gelijk aan' }, { value: '!=', label: 'niet gelijk aan' }, { value: 'bevat', label: 'bevat' }, { value: 'leeg', label: 'is leeg' }, { value: 'niet leeg', label: 'is niet leeg' },
      ] },
      { key: 'value', label: 'Waarde', type: 'text', default: '5000' },
    ],
  },
]

export const nodeType = (type: string) => nodeTypes.find(n => n.type === type) ?? nodeTypes[5]!

// Blokken die gegevens naar buiten sturen. Een werkstroom met zo'n blok maakt of wijzigt alleen een beheerder
// (de server controleert dit ook, zie WorkflowEndpoints.AdminOnlyBlocks).
export const adminOnlyTypes = ['action.email', 'action.webhook']

export function needsAdmin(graph: string | { type: string }[]) {
  let nodes: { type: string }[] = []
  if (typeof graph === 'string') { try { nodes = JSON.parse(graph).nodes ?? [] } catch { /* lege grafiek */ } }
  else nodes = graph
  return nodes.some(n => adminOnlyTypes.includes(n.type))
}

export const defaultConfig = (type: string) =>
  Object.fromEntries((nodeType(type).fields ?? []).filter(f => f.default !== undefined).map(f => [f.key, f.default!]))

export const variables = [
  'lead.name', 'lead.company', 'lead.email', 'lead.phone', 'lead.value', 'lead.status', 'lead.source',
  'customer.name', 'customer.company', 'customer.email', 'customer.city',
  'invoice.number', 'invoice.total', 'invoice.status', 'invoice.dueDate',
]
