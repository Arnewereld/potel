export interface Customer {
  id: number
  name: string
  company?: string | null
  email?: string | null
  phone?: string | null
  address?: string | null
  city?: string | null
  notes?: string | null
  createdAt?: string
}

export type LeadStatus = 'nieuw' | 'contact' | 'offerte' | 'gewonnen' | 'verloren'

export interface Lead {
  id: number
  name: string
  company?: string | null
  email?: string | null
  phone?: string | null
  value: number
  status: LeadStatus
  source?: string | null
  notes?: string | null
  customerId?: number | null
  createdAt?: string
}

export type InvoiceStatus = 'concept' | 'verzonden' | 'betaald' | 'verlopen'

export interface InvoiceLine {
  id?: number
  description: string
  quantity: number
  unitPrice: number
  vatRate: number
}

export interface Invoice {
  id: number
  number: string
  customerId: number
  customer?: Customer | null
  issueDate: string
  dueDate: string
  status: InvoiceStatus
  notes?: string | null
  lines: InvoiceLine[]
}

export interface Appointment {
  id: number
  title: string
  start: string
  end: string
  kind: string
  customerId?: number | null
  location?: string | null
  notes?: string | null
  done: boolean
}

export type FieldType = 'text' | 'number' | 'date' | 'textarea' | 'checkbox'

export interface ModuleField {
  key: string
  label: string
  type: FieldType
}

export interface CustomModule {
  id: number
  name: string
  icon: string
  color: string
  fieldsJson: string
}

export interface CustomRecord {
  id: number
  moduleId: number
  dataJson: string
  createdAt: string
}

export interface WorkflowNode {
  id: string
  type: string
  label: string
  x: number
  y: number
}

export interface WorkflowEdge {
  from: string
  to: string
}

export interface Workflow {
  id: number
  name: string
  active: boolean
  graphJson: string
  updatedAt: string
}

export interface ActivityItem {
  id: number
  kind: string
  text: string
  at: string
}

export interface Dashboard {
  customers: number
  openLeads: number
  pipelineValue: number
  revenueYear: number
  outstanding: number
  overdue: number
  revenueByMonth: { month: string; total: number }[]
  leadsByStatus: { status: LeadStatus; count: number; value: number }[]
  upcoming: Appointment[]
  activity: ActivityItem[]
}
