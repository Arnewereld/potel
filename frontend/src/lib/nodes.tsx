import { Zap, Mail, ListTodo, Tag, Clock, GitBranch, Receipt, Globe, CalendarClock, MousePointerClick, UserPlus, BadgeEuro, type LucideIcon } from 'lucide-react'

export interface NodeType {
  type: string
  label: string
  group: 'Triggers' | 'Acties' | 'Logica'
  color: string
  icon: LucideIcon
  description: string
}

// De bouwblokken die je op het werkstroom-canvas kunt zetten.
export const nodeTypes: NodeType[] = [
  { type: 'trigger.manual', label: 'Handmatig starten', group: 'Triggers', color: '#ff6d5a', icon: MousePointerClick, description: 'Start met een klik op Testen' },
  { type: 'trigger.lead', label: 'Nieuwe lead', group: 'Triggers', color: '#ff6d5a', icon: Zap, description: 'Als er een lead binnenkomt' },
  { type: 'trigger.paid', label: 'Factuur betaald', group: 'Triggers', color: '#ff6d5a', icon: BadgeEuro, description: 'Als een factuur betaald is' },
  { type: 'trigger.schedule', label: 'Schema', group: 'Triggers', color: '#ff6d5a', icon: CalendarClock, description: 'Elke dag, week of maand' },
  { type: 'action.email', label: 'E-mail sturen', group: 'Acties', color: '#4ea5ff', icon: Mail, description: 'Stuur een e-mail' },
  { type: 'action.task', label: 'Taak maken', group: 'Acties', color: '#3ecf8e', icon: ListTodo, description: 'Zet een taak in de planning' },
  { type: 'action.status', label: 'Status wijzigen', group: 'Acties', color: '#9b7bff', icon: Tag, description: 'Verplaats een lead of factuur' },
  { type: 'action.invoice', label: 'Factuur maken', group: 'Acties', color: '#f5b83d', icon: Receipt, description: 'Maak een conceptfactuur' },
  { type: 'action.customer', label: 'Klant maken', group: 'Acties', color: '#2fc6c6', icon: UserPlus, description: 'Zet een lead om naar klant' },
  { type: 'action.webhook', label: 'Webhook', group: 'Acties', color: '#ff5ca8', icon: Globe, description: 'Roep een andere app aan' },
  { type: 'logic.wait', label: 'Wachten', group: 'Logica', color: '#8a8aa0', icon: Clock, description: 'Wacht een tijdje' },
  { type: 'logic.if', label: 'Als / dan', group: 'Logica', color: '#8a8aa0', icon: GitBranch, description: 'Kies een pad op voorwaarde' },
]

export const nodeType = (type: string) => nodeTypes.find(n => n.type === type) ?? nodeTypes[4]!
