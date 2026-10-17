import { Route, Routes, useLocation } from 'react-router-dom'
import { ActiveTabContext } from './lib/tabs'
import { Layout } from './components/Layout'
import { TabsProvider } from './lib/tabs'
import { ToastProvider } from './lib/toast'
import { ModulesProvider } from './lib/modules'
import { AuthProvider, useAuth } from './lib/auth'
import { isAccountant } from './lib/accountant'
import { TimerProvider } from './lib/timer'
import { WorkspaceProvider } from './lib/workspace'
import { ErrorBox, Loading } from './components/ui'
import { PublicSite } from './pages/public/PublicSite'
import { AcceptInvitePage, ResetPasswordPage, VerifyEmailPage } from './pages/public/AccountLinks'
import { ProcessorAgreementPage, PrivacyPage, TermsPage } from './pages/public/Legal'
import { PublicInvoicePage } from './pages/public/PublicInvoice'
import { PublicQuotePage } from './pages/public/PublicQuote'
import { PortalPage } from './pages/public/Portal'
import { UsersPage } from './pages/Users'
import { DashboardPage } from './pages/Dashboard'
import { AccountantHomePage } from './pages/AccountantHome'
import { CustomersPage } from './pages/Customers'
import { CustomerDetailPage } from './pages/CustomerDetail'
import { LeadsPage } from './pages/Leads'
import { InvoicesPage } from './pages/Invoices'
import { InvoiceEditorPage } from './pages/InvoiceEditor'
import { QuotesPage } from './pages/Quotes'
import { QuoteEditorPage } from './pages/QuoteEditor'
import { RecurringEditorPage } from './pages/RecurringInvoices'
import { ExpensesPage } from './pages/Expenses'
import { TripsPage } from './pages/Trips'
import { BankPage } from './pages/Bank'
import { VatReturnPage } from './pages/VatReturn'
import { AnnualPage } from './pages/Annual'
import { InsightsPage } from './pages/Insights'
import { InvestmentsPage } from './pages/Investments'
import { PlanningPage } from './pages/Planning'
import { WorkflowsPage } from './pages/Workflows'
import { WorkflowEditorPage } from './pages/WorkflowEditor'
import { ModulesPage } from './pages/Modules'
import { ModuleRecordsPage } from './pages/ModuleRecords'
import { TimePage } from './pages/Time'
import { ProjectsPage } from './pages/Projects'
import { ProjectDetailPage } from './pages/ProjectDetail'
import { SettingsPage } from './pages/Settings'
import { PlatformPage } from './pages/Platform'
import { NotificationsPage } from './pages/Notifications'
import { NotificationsProvider } from './components/Notifications'
import { TwoFactorRequiredPage } from './components/TwoFactor'

// Pagina's die los van inloggen werken, zoals de links uit een mail en de juridische teksten.
const standalone: Record<string, () => React.ReactNode> = {
  '/wachtwoord-herstellen': ResetPasswordPage,
  '/email-bevestigen': VerifyEmailPage,
  // De uitnodiging als boekhouder.
  '/uitnodiging': AcceptInvitePage,
  '/voorwaarden': TermsPage,
  '/privacy': PrivacyPage,
  '/verwerkersovereenkomst': ProcessorAgreementPage,
  // De factuur achter de link in een mail aan een klant.
  '/factuur': PublicInvoicePage,
  // De offerte achter de link, waar de klant hem ook accepteert of afwijst.
  '/offerte': PublicQuotePage,
  // Het klantportaal achter de geheime link per klant.
  '/portaal': PortalPage,
}

export default function App() {
  const { pathname } = useLocation()
  const Page = standalone[pathname]
  if (Page) return <ToastProvider><Page /></ToastProvider>
  return (
    <ToastProvider>
      <AuthProvider fallback={<Loading />} publicSite={auth => <PublicSite auth={auth} />} twoFactorSetup={<TwoFactorRequiredPage />}>
      <WorkspaceProvider>
      <ModulesProvider>
      <TimerProvider>
        <TabsProvider>
        <NotificationsProvider>
          <Layout renderTab={(path, active) => (
            <ActiveTabContext.Provider value={active}>
              <AppRoutes location={path} />
            </ActiveTabContext.Provider>
          )} />
        </NotificationsProvider>
        </TabsProvider>
      </TimerProvider>
      </ModulesProvider>
      </WorkspaceProvider>
      </AuthProvider>
    </ToastProvider>
  )
}

// Elk tabblad houdt zijn eigen pagina vast, zodat je niets kwijtraakt als je wisselt.
function AppRoutes({ location }: { location: string }) {
  const { user } = useAuth()
  if (isAccountant(user)) return <AccountantRoutes location={location} />
  return (
    <Routes location={location}>
      <Route path="/" element={<HomePage />} />
      <Route path="/inzichten" element={<InsightsPage />} />
      <Route path="/uren" element={<TimePage />} />
      <Route path="/projecten" element={<ProjectsPage />} />
      <Route path="/projecten/:id" element={<ProjectDetailPage />} />
      <Route path="/instellingen" element={<SettingsPage />} />
      <Route path="/platform" element={<PlatformPage />} />
      <Route path="/planning" element={<PlanningPage />} />
      <Route path="/klanten" element={<CustomersPage />} />
      <Route path="/klanten/:id" element={<CustomerDetailPage />} />
      <Route path="/leads" element={<LeadsPage />} />
      <Route path="/offertes" element={<QuotesPage />} />
      <Route path="/offertes/:id" element={<QuoteEditorPage />} />
      <Route path="/facturen" element={<InvoicesPage />} />
      <Route path="/facturen/:id" element={<InvoiceEditorPage />} />
      <Route path="/facturen/terugkerend/:id" element={<RecurringEditorPage />} />
      <Route path="/kosten" element={<ExpensesPage />} />
      <Route path="/ritten" element={<TripsPage />} />
      <Route path="/bank" element={<BankPage />} />
      <Route path="/btw-aangifte" element={<VatReturnPage />} />
      <Route path="/jaaroverzicht" element={<AnnualPage />} />
      <Route path="/investeringen" element={<InvestmentsPage />} />
      <Route path="/werkstromen" element={<WorkflowsPage />} />
      <Route path="/werkstromen/:id" element={<WorkflowEditorPage />} />
      <Route path="/modules" element={<ModulesPage />} />
      <Route path="/modules/:id" element={<ModuleRecordsPage />} />
      <Route path="/gebruikers" element={<UsersPage />} />
      <Route path="/meldingen" element={<NotificationsPage />} />
      <Route path="*" element={<HomePage />} />
    </Routes>
  )
}

// Een boekhouder ziet alleen de administratie. Een andere pagina, bijvoorbeeld uit een oude link, laat uitleg zien in plaats van fouten.
function AccountantRoutes({ location }: { location: string }) {
  return (
    <Routes location={location}>
      <Route path="/" element={<AccountantHomePage />} />
      <Route path="/facturen" element={<InvoicesPage />} />
      <Route path="/facturen/:id" element={<InvoiceEditorPage />} />
      <Route path="/offertes" element={<QuotesPage />} />
      <Route path="/offertes/:id" element={<QuoteEditorPage />} />
      <Route path="/kosten" element={<ExpensesPage />} />
      <Route path="/ritten" element={<TripsPage />} />
      <Route path="/bank" element={<BankPage />} />
      <Route path="/btw-aangifte" element={<VatReturnPage />} />
      <Route path="/jaaroverzicht" element={<AnnualPage />} />
      <Route path="/investeringen" element={<InvestmentsPage />} />
      <Route path="/klanten" element={<CustomersPage />} />
      <Route path="/klanten/:id" element={<CustomerDetailPage />} />
      <Route path="/instellingen" element={<SettingsPage />} />
      <Route path="/meldingen" element={<NotificationsPage />} />
      <Route path="*" element={<ErrorBox message="Deze pagina is er niet voor boekhouders. Je vindt de administratie in het menu links." />} />
    </Routes>
  )
}

// Een boekhouder heeft geen dashboard maar een eigen overzicht van wat hij kan bekijken.
function HomePage() {
  const { user } = useAuth()
  return isAccountant(user) ? <AccountantHomePage /> : <DashboardPage />
}
