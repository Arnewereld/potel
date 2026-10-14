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
  // Rekeningnummer waarvan de klant betaalt; daarmee herkent de pagina Bank zijn betalingen.
  iban?: string | null
  notes?: string | null
  // Geen automatische betalingsherinneringen voor deze klant.
  remindersOff?: boolean
  // Wat je deze klant per kilometer rekent voor doorbelaste ritten; leeg is niet doorbelasten.
  kilometreRate?: number | null
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

// Een (deel)betaling op een factuur: zelf ingevoerd, via iDEAL binnengekomen of gekoppeld aan een regel van het bankafschrift.
export type PaymentMethod = 'handmatig' | 'ideal' | 'bank'
export interface InvoicePayment {
  id: number
  amount: number
  date: string
  method: PaymentMethod
  note?: string | null
  bankTransactionId?: number | null
  molliePaymentId?: string | null
  createdBy?: string | null
}

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
  // De betalingen op deze factuur en wat ze samen zijn.
  payments?: InvoicePayment[]
  paidTotal?: number
  // Voorbeeldfactuur uit de welkomstwizard: altijd te verwijderen.
  demo?: boolean
  // Gemaakt door een terugkerende factuur, voor de periode die op recurringPeriodStart begint.
  recurringInvoiceId?: number | null
  recurringPeriodStart?: string | null
  seller?: InvoiceParty | null
  buyer?: InvoiceParty | null
  totals?: InvoiceTotals
  // De urenspecificatie gaat mee (standaard aan), eventueel met een bedrag per boeking.
  hourSpecification?: boolean
  hourAmounts?: boolean
  lines: InvoiceLine[]
}

// De urenspecificatie bij een factuur: per project de boekingen, met subtotalen en een totaal dat gelijk is aan de urenregels.
export interface HourSpecEntry { date: string; description: string; who?: string | null; hours: number; amount?: number | null }
export interface HourSpecProject { name: string; entries: HourSpecEntry[]; hours: number; amount?: number | null }
export interface HourSpec { projects: HourSpecProject[]; hours: number; amount?: number | null; showWho: boolean; showAmounts: boolean }
// Wat de editor krijgt: de specificatie, wat er niet klopt (bij een concept) en de schakelaars.
export interface InvoiceHours { spec: HourSpec | null; problem: string | null; enabled: boolean; amounts: boolean }

export type RecurringInterval = 'maand' | 'kwartaal' | 'jaar'
export type RecurringPeriodRule = 'vooraf' | 'achteraf'
export type RecurringAction = 'concept' | 'versturen'

export interface RecurringLine { id?: number; description: string; quantity: number; unit: string; unitPrice: number; vatRate: number }

// Een periode van een terugkerende factuur: wat hij factureert en op welke dag de factuur komt.
export interface RecurringPeriod { from: string; to: string; runDate: string; label: string }

export interface RecurringInvoiceRef { id: number; number: string | null; status: InvoiceStatus; periodStart?: string | null; period?: string | null; total: number }

export interface RecurringInvoice {
  id: number
  name: string
  customerId: number
  customerName?: string | null
  vatRegime: VatRegime
  interval: RecurringInterval
  periodRule: RecurringPeriodRule
  action: RecurringAction
  startDate: string
  endDate?: string | null
  nextPeriodStart: string
  // Leeg als de einddatum voorbij is.
  nextRunDate?: string | null
  nextPeriod?: RecurringPeriod | null
  active: boolean
  state: 'actief' | 'gepauzeerd' | 'afgelopen'
  reference?: string | null
  notes?: string | null
  lines: RecurringLine[]
  totals: InvoiceTotals
  invoiceCount: number
  // Na de eerste factuur liggen klant, startdatum en interval vast.
  locked: boolean
  demo: boolean
  createdAt: string
  invoices?: RecurringInvoiceRef[] | null
}

// Mail over een factuur: de factuur zelf (stap 0) of een herinnering (1 t/m 3).
export type MailStep = 0 | 1 | 2 | 3
export type MailStatus = 'bezig' | 'verstuurd' | 'mislukt' | 'overgeslagen'

export interface InvoiceMail {
  id: number
  step: MailStep
  label: string
  to: string
  subject?: string | null
  status: MailStatus
  note?: string | null
  automatic: boolean
  sentBy?: string | null
  createdAt: string
  sentAt?: string | null
}

export interface InvoiceMailInfo {
  mails: InvoiceMail[]
  activeLinks: number
  lastOpenedAt?: string | null
  remindersEnabled: boolean
  remindersOff: boolean
  customerRemindersOff: boolean
  nextReminder?: { step: MailStep; label: string; date: string } | null
  nextStep: MailStep
}

export interface MailPreview { step: MailStep; label: string; to?: string | null; subject: string; body: string; problem?: string | null; consumer: boolean }

// Kan Potel zelf mailen? Zo niet, dan opent een mail in je eigen mailprogramma.
export interface MailServerStatus { canSend: boolean; problem?: string | null; remindersEnabled: boolean }

export interface MailText { subject: string; body: string }

export interface ReminderSettings {
  enabled: boolean
  firstDays: number
  secondDays: number
  finalDays: number
  texts: { invoice: MailText; first: MailText; second: MailText; final: MailText }
  defaults: { invoice: MailText; first: MailText; second: MailText; final: MailText }
  placeholders: string[]
  canSend: boolean
  problem?: string | null
}

// Wat de openbare factuurpagina krijgt: alleen wat op de factuur staat, plus logo en kleur.
export interface PublicInvoice {
  invoice: Omit<Invoice, 'id' | 'customerId'> & { isCredit: boolean; openAmount: number; fullyCredited: boolean; paidTotal: number }
  brand: { logoDataUrl?: string | null; brandColor: string }
  hours?: HourSpec | null
  // Kan de klant hier met iDEAL betalen, via de eigen Mollie van de freelancer?
  canPay?: boolean
}

// Terug van de betaalpagina van Mollie: wat Potel over die ene betaling weet.
export interface PublicPaymentStatus {
  status: 'open' | 'pending' | 'authorized' | 'paid' | 'canceled' | 'expired' | 'failed' | string
  paid: boolean
  amount: number
  test: boolean
  number?: string | null
  seller?: string | null
  invoicePaid: boolean
  brand: { logoDataUrl?: string | null; brandColor: string }
}

// De eigen Mollie van de werkruimte voor iDEAL op facturen. De sleutel zelf komt nooit terug.
export interface IdealSettings {
  key: { mode: 'test' | 'live'; last4: string; updatedAt: string; updatedBy?: string | null } | null
  available: boolean
  problem?: string | null
}

// Een offerte: dezelfde regels als een factuur, voor een klant of een lead. Verlopen is geen opgeslagen status maar volgt
// uit de geldigheidsdatum (state). Ingetrokken zet je zelf, of komt door een nieuwe versie.
export type QuoteStatus = 'concept' | 'verstuurd' | 'geaccepteerd' | 'afgewezen' | 'ingetrokken'
export type QuoteState = QuoteStatus | 'verlopen'

export interface Quote {
  id: number
  number: string | null
  customerId: number | null
  customer?: Customer | null
  leadId: number | null
  lead?: Lead | null
  version: number
  previousVersionId?: number | null
  previousNumber?: string | null
  newerVersionId?: number | null
  issueDate: string
  validUntil: string
  status: QuoteStatus
  state?: QuoteState
  vatRegime: VatRegime
  subject?: string | null
  intro?: string | null
  terms?: string | null
  depositPercent?: number | null
  sentAt?: string | null
  decidedAt?: string | null
  decidedName?: string | null
  decidedIp?: string | null
  rejectReason?: string | null
  withdrawnAt?: string | null
  mailedAt?: string | null
  mailedTo?: string | null
  seller?: InvoiceParty | null
  buyer?: InvoiceParty | null
  lines: InvoiceLine[]
  totals?: InvoiceTotals
  projectId?: number | null
  depositInvoiceId?: number | null
  invoiceId?: number | null
  demo?: boolean
  createdAt?: string
}

export interface QuoteLinks { active: number; lastOpenedAt?: string | null; opens: number; mailedAt?: string | null; mailedTo?: string | null }
export interface QuoteMailPreview { to?: string | null; subject: string; body: string; problem?: string | null }

// Wat de openbare offertepagina krijgt: alleen wat op de offerte staat, of de klant nog kan besluiten, plus logo en kleur.
export interface PublicQuote {
  quote: Omit<Quote, 'id' | 'customerId' | 'leadId' | 'status'> & { state: QuoteState; newerNumber?: string | null; newerVersion?: number | null }
  canDecide: boolean
  problem?: string | null
  brand: { logoDataUrl?: string | null; brandColor: string }
}

export interface RecurringPreview { periods: RecurringPeriod[]; dueNow: number; lines: string[]; error?: string | null }

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
  // Kilometertarief voor doorbelaste ritten; leeg is het tarief van de klant.
  kilometreRate?: number | null
}

// Soorten ritten, zoals de server ze kent (TripKinds in backend/Potel.Api/Data/Models.cs).
export type TripKind = 'zakelijk' | 'auto-van-de-zaak' | 'prive'

export interface Trip {
  id: number
  date: string
  from: string
  to: string
  kilometres: number
  purpose?: string | null
  kind: TripKind
  kindLabel: string
  customerId?: number | null
  customerName?: string | null
  projectId?: number | null
  projectName?: string | null
  projectColor?: string | null
  odometerStart?: number | null
  odometerEnd?: number | null
  billable: boolean
  // Het kilometertarief waartegen hij doorbelast wordt; leeg als er nog geen tarief is.
  rate?: number | null
  invoiceId?: number | null
  invoiceNumber?: string | null
  userId?: number | null
  userName?: string | null
  demo: boolean
  canEdit: boolean
}

export interface TripRoute {
  id: number
  name: string
  from: string
  to: string
  kilometres: number
  purpose?: string | null
  kind: TripKind
  customerId?: number | null
  projectId?: number | null
  billable: boolean
}

export interface TripYear {
  year: number
  trips: Trip[]
  months: { month: string; trips: number; businessKm: number; companyCarKm: number; privateKm: number }[]
  count: number
  businessKm: number
  companyCarKm: number
  privateKm: number
  // Aftrek per zakelijke kilometer met je eigen auto; null als het bedrag voor dat jaar nog niet vaststaat.
  rate: number | null
  deduction: number
  rateSource: string
  note?: string | null
  kilometreSource: string
}

// Wat er van een klant of project nog door te belasten is, per kilometertarief.
export interface BillableTrips {
  trips: number
  kilometres: number
  amount: number
  lines: { rate: number; kilometres: number; amount: number; trips: number }[]
  withoutRate: number
  withoutRateKilometres: number
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
  // Zoveel dagen is een nieuwe offerte geldig.
  quoteValidDays?: number
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
  // Online betalen via Mollie staat aan op deze server, en hoe het abonnement ervoor staat.
  onlinePayment?: boolean
  subscriptionActive?: boolean
  paidUntil?: string | null
  subscriptionCanceledAt?: string | null
  // Waarom de werkruimte alleen-lezen is, of null als alles kan.
  readOnly?: string | null
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
  // Openstaande offertes (bedrag zonder btw) en hoeveel van de offertes van het afgelopen jaar geaccepteerd zijn.
  quotes: {
    open: number
    openValue: number
    expiringSoon: number
    accepted: number
    decided: number
    acceptanceRate: number | null
    recent: { id: number; number: string | null; status: QuoteStatus; decidedAt: string; decidedName?: string | null; buyer?: string | null; subtotal: number }[]
  }
  // De btw-aangifte van dit kwartaal: positief is te betalen, negatief terug te vragen. Met de KOR is er geen datum.
  vat: { label: string; year: number; quarter: number; amount: number; revenue: number; dueDate: string | null; kor: boolean }
  // De belastingpot van dit jaar; alleen voor beheerders, voor medewerkers null.
  taxReserve: {
    year: number
    available: boolean
    missing?: string | null
    mode: ReserveMode
    kor: boolean
    vat: number
    total: number
    provisional: number
    remaining: number
    perMonth: number | null
    monthsLeft: number
  } | null
  projects: Project[]
}

export type ExpenseCategory = 'hardware' | 'software' | 'hosting' | 'opleiding' | 'reiskosten' | 'telefoon' | 'kantoor' | 'overig'

export interface ReceiptInfo {
  fileName: string
  contentType: string
  size: number
  uploadedAt: string
}

export interface Expense {
  id: number
  date: string
  supplier: string
  description?: string | null
  category: ExpenseCategory
  // Wat je betaalde, inclusief btw. Bij verlegde btw is dit het bedrag zonder btw.
  amount: number
  // null is "geen btw".
  vatRate: number | null
  vatAmount: number
  amountExclVat: number
  reverseCharge: boolean
  supplierRegion?: 'eu' | 'buiten-eu' | null
  projectId?: number | null
  projectName?: string | null
  customerId?: number | null
  customerName?: string | null
  demo: boolean
  receipt?: ReceiptInfo | null
  createdAt: string
  // Een investering schrijf je af over de gebruiksduur (standaard 5 jaar) in plaats van in één keer.
  investment: boolean
  usefulLifeYears?: number | null
  residualValue?: number | null
  passengerCar: boolean
  disposedOn?: string | null
  disposalPrice?: number | null
}

export interface DepreciationYear {
  year: number
  depreciation: number
  bookValue: number
}

export interface Investment {
  id: number
  date: string
  supplier: string
  description?: string | null
  category: ExpenseCategory
  // De aanschafprijs voor je winst: zonder btw, of met btw als je die niet aftrekt.
  cost: number
  residual: number
  years: number
  passengerCar: boolean
  countsForKia: boolean
  perYear: number
  // Meer dan 20% van de aanschafprijs per jaar.
  aboveMaximum: boolean
  // In het gekozen jaar.
  depreciation: number
  bookValue: number
  disposedOn?: string | null
  disposalPrice?: number | null
  bookValueAtDisposal?: number | null
  // Positief is boekwinst, negatief is afgeboekte boekwaarde.
  disposalResult?: number | null
  demo: boolean
  schedule: DepreciationYear[]
}

// Zonder voorbeelddata, zoals op het jaaroverzicht.
export interface InvestmentSummary {
  count: number
  depreciation: number
  bookValue: number
  disposalLoss: number
  disposalGain: number
  invested: number
  qualifying: number
  kia: number
  kiaKnown: boolean
  kiaSource: string
  assumptions: string[]
  kiaNotes: string[]
  warnings: string[]
}

export interface InvestmentYear {
  year: number
  deduction: boolean
  investments: Investment[]
  summary: InvestmentSummary
  // Of de totalen nu ook de voorbeelddata bevatten.
  includesDemo: boolean
  depreciationSource: string
  calculationSource: string
  kiaExclusionsSource: string
}

export interface VatRubric {
  code: string
  title: string
  base: number
  // null bij een rubriek zonder btw-kolom (1e en 3b).
  vat: number | null
}

export interface VatSource {
  kind: 'factuur' | 'kosten'
  id: number
  date: string
  name: string
  number?: string | null
  rubric: string
  base: number
  vat: number
}

export interface VatReturn {
  year: number
  quarter: number
  label: string
  from: string
  to: string
  dueDate: string
  regime: VatRegime
  kor: boolean
  // Of je de btw op je kosten mag aftrekken (niet met de KOR of vrijgestelde omzet).
  deduction: boolean
  rubrics: VatRubric[]
  due: number
  deductible: number
  // Positief is te betalen, negatief terug te vragen.
  total: number
  revenue: number
  outsideEu: number
  exempt: number
  korRevenue: number
  icp: { vatNumber: string; customer: string; country: string; amount: number }[]
  sources: VatSource[]
  demoData: boolean
  includesDemo: boolean
  // Het vorige kwartaal zolang die aangifte nog op tijd kan; filed als je hem als gedaan hebt gemarkeerd.
  pending?: { year: number; quarter: number; label: string; dueDate: string; filed: boolean } | null
  // Wanneer en door wie de aangifte over dit kwartaal als gedaan is gemarkeerd.
  filedAt?: string | null
  filedBy?: string | null
}

export interface TaxIndication {
  available: boolean
  profit: number
  hoursMet: boolean
  starter: boolean
  selfEmployedDeduction: number
  starterDeduction: number
  profitAfterDeductions: number
  // null als het percentage voor dat jaar nog niet vaststaat.
  smePercentage: number | null
  smeExemption: number
  taxableProfit: number
  notes: string[]
  sources: string[]
  // De kleinschaligheidsinvesteringsaftrek; die gaat van de winst af voor de ondernemersaftrek.
  investmentDeduction: number
}

export interface AnnualOverview {
  year: number
  revenue: number
  costs: number
  result: number
  // Of je btw aftrekt; zo niet (KOR of vrijgesteld), dan zijn de kosten met btw.
  deduction: boolean
  costsByCategory: { category: ExpenseCategory; label: string; amount: number; count: number }[]
  months: { month: string; revenue: number; costs: number }[]
  hours: { minutes: number; billableMinutes: number; nonBillableMinutes: number; criterion: number; met: boolean }
  indication: TaxIndication
  invoices: number
  expenses: number
  // Hardware van € 450 of meer die nog geen investering is: die schrijf je af in plaats van in één keer.
  possibleInvestments: number
  demoData: boolean
  hoursSource: string
  depreciationSource: string
  // Ritten van het jaar: alleen zakelijke kilometers met je eigen auto geven aftrek.
  mileage: { trips: number; businessKm: number; companyCarKm: number; privateKm: number; rate: number | null; deduction: number; source: string; note?: string | null }
  // Afschrijving, verkopen en de kleinschaligheidsinvesteringsaftrek van dit jaar.
  investments: InvestmentSummary
}

// De belastingpot: wat je opzij zet voor de btw, de inkomstenbelasting en de bijdrage Zvw. Een indicatie, geen belastingadvies.
export type ReserveMode = 'berekend' | 'percentage'
export type ProvisionalKind = 'geen' | 'totaal' | 'per-maand'

export interface TaxReserveSettings {
  year: number
  // false zolang je voor dit jaar nog niets hebt opgeslagen.
  saved: boolean
  wage: number
  wageTaxWithheld: number
  starter: boolean
  provisional: ProvisionalKind
  provisionalAmount: number
  provisionalMonths: number
  provisionalTotal: number
  mode: ReserveMode
  percentage: number
  updatedAt?: string | null
  updatedBy?: string | null
}

export interface BracketTax { from: number; upTo: number | null; rate: number; income: number; tax: number }

// De som stap voor stap, zoals de server hem maakt (IncomeTax.Estimate). Due is na de ingehouden loonheffing; negatief is terug.
export interface IncomeTaxEstimate {
  result: number
  investmentDeduction: number
  profit: number
  hoursMet: boolean
  starter: boolean
  selfEmployedDeduction: number
  starterDeduction: number
  smeExemption: number
  taxableProfit: number
  wage: number
  taxableIncome: number
  brackets: BracketTax[]
  rateAdjustment: number
  taxBeforeCredits: number
  labourIncome: number
  generalCredit: number
  labourCredit: number
  credits: number
  incomeTax: number
  zvwBase: number
  zvw: number
  total: number
  wageTaxWithheld: number
  due: number
}

export interface TaxReserve {
  year: number
  today: string
  current: boolean
  started: boolean
  elapsed: number
  monthsLeft: number
  settings: TaxReserveSettings
  kor: boolean
  // Btw die nog betaald moet worden: het vorige kwartaal (als die aangifte nog niet gedaan is) en het lopende.
  vat: { year: number; quarter: number; label: string; dueDate: string; amount: number; running: boolean }[]
  vatTotal: number
  mode: ReserveMode
  available: boolean
  missing?: string | null
  resultSoFar: number
  resultFullYear: number
  hours: AnnualOverview['hours']
  expectedHours: number
  hoursMet: boolean
  soFar: IncomeTaxEstimate | null
  fullYear: IncomeTaxEstimate | null
  paidSoFar: number
  paidFullYear: number
  soFarAmount: number
  total: number
  provisional: number
  remaining: number
  perMonth: number | null
  explanation: string[]
  leftOut: string[]
  sources: { label: string; url: string }[]
}

// Het klantportaal: wat de klant ziet en de stand van de link. Het token zelf bewaart de server niet.
export interface PortalLinkInfo {
  createdAt: string
  createdBy?: string | null
  expiresAt?: string | null
  lastOpenedAt?: string | null
  opens: number
  expired: boolean
}

export interface CustomerPortalSettings {
  showHours: boolean
  showBudget: boolean
  showInvoices: boolean
  showAmounts: boolean
  note?: string | null
  link?: PortalLinkInfo | null
  lastOpenedAt?: string | null
}

export interface PortalProject {
  name: string
  billing: BillingType
  deadline?: string | null
  budgetHours?: number | null
  minutes?: number | null
  lastEntry?: string | null
  weeks?: { start: string; minutes: number }[] | null
  hourlyRate?: number | null
  value?: number | null
  fixedPrice?: number | null
}

export interface PortalEntry {
  date: string
  project: string
  minutes: number
  description: string
  amount?: number | null
}

export interface PortalInvoice {
  number?: string | null
  issueDate: string
  dueDate: string
  status: InvoiceStatus
  isCredit: boolean
  total: number
  openAmount: number
  fullyCredited: boolean
  paidAt?: string | null
  link?: string | null
  canPay?: boolean
}

export interface PublicPortal {
  company: { name: string; logoDataUrl?: string | null; brandColor: string }
  customer: { name: string }
  note?: string | null
  visible: { hours: boolean; budget: boolean; invoices: boolean; amounts: boolean }
  projects: PortalProject[]
  entries?: PortalEntry[] | null
  invoices?: PortalInvoice[] | null
  expiresAt?: string | null
}

// Urenvoorstellen uit GitHub. Het token zelf komt nooit naar de browser, alleen de laatste 4 tekens.
export interface GitHubRules {
  gapMinutes: number
  firstCommitMinutes: number
  roundToMinutes: number
  maxMinutesPerDay: number
}

export interface GitHubSettings {
  token: { last4: string; updatedAt: string; updatedBy?: string | null } | null
  emails: string[]
  accountEmail: string
  rules: GitHubRules
}

export interface GitHubCommitSuggestion {
  sha: string
  message: string
  time: string
  url?: string | null
}

export interface GitHubSuggestion {
  key: string
  projectId: number
  projectName: string
  repo: string
  date: string
  minutes: number
  description: string
  commits: GitHubCommitSuggestion[]
}

export interface GitHubSuggestions {
  from: string
  to: string
  emails: string[]
  suggestions: GitHubSuggestion[]
  problems: { project: string; repo: string; message: string }[]
  truncated: string[]
  skippedRepos: string[]
  rules: GitHubRules
}

// De pagina Bank: regels uit je bankafschrift, met voorstellen om te koppelen.
export type BankFilter = 'open' | 'gekoppeld' | 'prive' | 'negeren' | 'alle'
export type BankMark = 'prive' | 'negeren'

export interface BankProposal {
  kind: 'invoice' | 'payment' | 'expense'
  reason: string
  invoiceId?: number | null
  number?: string | null
  customer?: string | null
  open?: number | null
  partial: boolean
  paymentId?: number | null
  paymentDate?: string | null
  expenseId?: number | null
  supplier?: string | null
  expenseDate?: string | null
  rememberIban?: string | null
}

export interface BankNewExpense {
  supplier?: string | null
  date: string
  amount: number
  description?: string | null
  category?: ExpenseCategory | null
  vatRate?: number | null
  fromHistory: boolean
}

export type BankLink =
  | { kind: 'invoice'; invoiceId: number; number?: string | null; customer?: string | null; paymentId: number; created: boolean }
  | { kind: 'expense'; expenseId: number; supplier: string; date: string; amount: number; category: string }

export interface BankTransaction {
  id: number
  date: string
  amount: number
  counterpartyName?: string | null
  counterpartyIban?: string | null
  description?: string | null
  account?: string | null
  mark?: BankMark | null
  state: 'open' | 'factuur' | 'kosten' | BankMark
  link?: BankLink | null
  proposals: BankProposal[]
  newExpense?: BankNewExpense | null
  hint?: string | null
  ibanValid: boolean
}

export interface BankImport {
  id: number
  fileName: string
  format: string
  added: number
  skipped: number
  importedAt: string
  importedBy?: string | null
}

export interface BankCsvMapping {
  date: string
  amount: string
  description?: string | null
  name?: string | null
  iban?: string | null
  sign?: string | null
}

export interface BankOverview {
  filter: BankFilter
  counts: { open: number; linked: number; prive: number; negeren: number; all: number }
  transactions: BankTransaction[]
  total: number
  more: boolean
  imports: BankImport[]
  mapping: BankCsvMapping | null
  openInvoices: { id: number; number: string; customer: string; open: number; customerIban?: string | null }[]
  expenses: { id: number; date: string; supplier: string; amount: number }[]
}

export type BankImportResult =
  | { needsMapping: true; headers: string[]; sample: string[][]; mapping: BankCsvMapping | null }
  | { needsMapping: false; importId: number | null; format: string; added: number; skipped: number; skippedOtherCurrency: number; skippedNotBooked: number }
