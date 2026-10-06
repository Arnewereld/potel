export interface Customer {
  id: number
  name: string
  company?: string | null
  email?: string | null
  phone?: string | null
  address?: string | null
  city?: string | null
  // Land van vestiging; leeg of Nederland is een Nederlandse klant.
  country?: string | null
  vatNumber?: string | null
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
  unit: string
  unitPrice: number
  vatRate: number
}

// normaal: btw op de regels. verlegd: zakelijke klant in een ander EU-land. kor: kleineondernemersregeling.
// vrijgesteld: diensten die vrijgesteld zijn van btw. buiten-eu: klant buiten de EU.
export type VatRegime = 'normaal' | 'verlegd' | 'kor' | 'vrijgesteld' | 'buiten-eu'

// Jij of je klant zoals ze bij versturen op de factuur kwamen.
export interface InvoiceParty {
  name: string
  contact?: string | null
  address?: string | null
  city?: string | null
  country?: string | null
  email?: string | null
  phone?: string | null
  website?: string | null
  kvk?: string | null
  vatNumber?: string | null
  iban?: string | null
}

export interface VatGroup { rate: number; base: number; vat: number }
export interface InvoiceTotals { subtotal: number; vat: number; total: number; vatGroups: VatGroup[] }

// Een verstuurde creditnota op een factuur.
export interface CreditRef { id: number; number?: string | null; issueDate: string; total: number }

export interface Invoice {
  id: number
  // Een concept heeft nog geen nummer; dat komt bij versturen.
  number: string | null
  customerId: number
  customer?: Customer | null
  issueDate: string
  dueDate: string
  status: InvoiceStatus
  reference?: string | null
  vatRegime: VatRegime
  deliveryFrom?: string | null
  deliveryTo?: string | null
  sentAt?: string | null
  paidAt?: string | null
  notes?: string | null
  creditForInvoiceId?: number | null
  creditForNumber?: string | null
  creditForIssueDate?: string | null
  isCredit?: boolean
  // Verstuurde creditnota's op deze factuur, wat ze samen aftrekken en wat de klant dus nog moet betalen.
  creditNotes?: CreditRef[]
  creditedTotal?: number
  openAmount?: number
  fullyCredited?: boolean
  // Voorbeeldfactuur uit de welkomstwizard: altijd te verwijderen.
  demo?: boolean
  seller?: InvoiceParty | null
  buyer?: InvoiceParty | null
  totals?: InvoiceTotals
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
  config?: Record<string, string>
}

export interface WorkflowEdge {
  from: string
  to: string
  branch?: 'ja' | 'nee'
}

export interface WorkflowRunLog {
  nodeId: string
  label: string
  type: string
  status: 'ok' | 'fout' | 'wacht' | 'let op'
  message: string
  at: string
}

export interface WorkflowRun {
  id: number
  workflowId: number
  trigger: string
  status: 'bezig' | 'wachtend' | 'klaar' | 'fout'
  startedAt: string
  finishedAt?: string | null
  resumeAt?: string | null
  logJson: string
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

export type ProjectStatus = 'actief' | 'gepauzeerd' | 'afgerond'
export type BillingType = 'uur' | 'vast'

export interface Project {
  id: number
  name: string
  customerId: number
  customerName?: string | null
  status: ProjectStatus
  billing: BillingType
  hourlyRate: number
  fixedPrice: number
  budgetHours?: number | null
  color: string
  repoUrl?: string | null
  description?: string | null
  deadline?: string | null
  createdAt?: string
  minutesTotal: number
  minutesUnbilled: number
  unbilledValue: number
  // Wat er van dit project op facturen staat (alleen de eigen uren, ook op een factuur met meer projecten).
  invoicedValue: number
  lastEntry?: string | null
}

export interface TimeEntry {
  id: number
  projectId: number
  projectName: string
  projectColor: string
  customerName?: string | null
  date: string
  minutes: number
  description: string
  billable: boolean
  invoiceId?: number | null
  invoiceNumber?: string | null
}

export interface Settings {
  companyName: string
  ownerName?: string | null
  address?: string | null
  city?: string | null
  email?: string | null
  phone?: string | null
  website?: string | null
  kvk?: string | null
  btw?: string | null
  iban?: string | null
  defaultHourlyRate: number
  paymentTermDays: number
  weeklyHoursTarget: number
  yearlyHoursTarget: number
  logoDataUrl?: string | null
  brandColor?: string
  // Standaard btw-regeling voor nieuwe facturen: normaal, KOR of vrijgesteld.
  vatRegime?: VatRegime
}

export interface Workspace {
  id: number
  name: string
  plan: 'proef' | 'zzp' | 'team'
  trialEndsAt?: string | null
  createdAt: string
  onboarded: boolean
  trialDaysLeft?: number | null
  platformAdmin: boolean
  // Hoeveel gebruikers het abonnement toestaat (ook uitgeschakelde tellen mee).
  maxUsers: number
  // Er staat nog voorbeelddata uit de welkomstwizard in.
  demoData: boolean
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
  hours: {
    week: number
    weekBillable: number
    weekTarget: number
    year: number
    yearTarget: number
    weeksLeft: number
    byDay: { date: string; minutes: number }[]
  }
  unbilled: { minutes: number; value: number }
  // Met de KOR doe je geen btw-aangifte; dan is er geen datum.
  vat: { label: string; amount: number; revenue: number; dueDate: string | null; kor: boolean }
  projects: Project[]
}
