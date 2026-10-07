import type { InvoiceStatus, LeadStatus, ProjectStatus } from './types'

export const projectStatuses: { id: ProjectStatus; label: string; tone: string }[] = [
  { id: 'actief', label: 'Actief', tone: 'green' },
  { id: 'gepauzeerd', label: 'Gepauzeerd', tone: 'yellow' },
  { id: 'afgerond', label: 'Afgerond', tone: 'gray' },
]

export const leadStatuses: { id: LeadStatus; label: string; tone: string; color: string }[] = [
  { id: 'nieuw', label: 'Nieuw', tone: 'blue', color: '#4ea5ff' },
  { id: 'contact', label: 'Contact', tone: 'purple', color: '#9b7bff' },
  { id: 'offerte', label: 'Offerte', tone: 'yellow', color: '#f5b83d' },
  { id: 'gewonnen', label: 'Gewonnen', tone: 'green', color: '#3ecf8e' },
  { id: 'verloren', label: 'Verloren', tone: 'red', color: '#ff5c6c' },
]

export const invoiceStatuses: { id: InvoiceStatus; label: string; tone: string }[] = [
  { id: 'concept', label: 'Concept', tone: 'gray' },
  { id: 'verzonden', label: 'Verzonden', tone: 'blue' },
  { id: 'betaald', label: 'Betaald', tone: 'green' },
  { id: 'verlopen', label: 'Verlopen', tone: 'red' },
]

export const appointmentKinds: { id: string; label: string; color: string }[] = [
  { id: 'afspraak', label: 'Afspraak', color: '#ff6d5a' },
  { id: 'taak', label: 'Taak', color: '#4ea5ff' },
  { id: 'project', label: 'Project', color: '#3ecf8e' },
  { id: 'intern', label: 'Intern', color: '#9b7bff' },
]

export const kindColor = (kind: string) => appointmentKinds.find(k => k.id === kind)?.color ?? '#8a8aa0'

const palette = ['#ff6d5a', '#4ea5ff', '#3ecf8e', '#9b7bff', '#f5b83d', '#ff5ca8', '#2fc6c6']
export const colorFor = (s: string) => palette[[...s].reduce((a, c) => a + c.charCodeAt(0), 0) % palette.length]!
