import { Route, Routes, useLocation } from 'react-router-dom'
import { ActiveTabContext } from './lib/tabs'
import { Layout } from './components/Layout'
import { TabsProvider } from './lib/tabs'
import { ToastProvider } from './lib/toast'
import { ModulesProvider } from './lib/modules'
import { AuthProvider } from './lib/auth'
import { TimerProvider } from './lib/timer'
import { WorkspaceProvider } from './lib/workspace'
import { Loading } from './components/ui'
import { PublicSite } from './pages/public/PublicSite'
import { ResetPasswordPage, VerifyEmailPage } from './pages/public/AccountLinks'
import { ProcessorAgreementPage, PrivacyPage, TermsPage } from './pages/public/Legal'
import { PublicInvoicePage } from './pages/public/PublicInvoice'
import { PublicQuotePage } from './pages/public/PublicQuote'
import { PortalPage } from './pages/public/Portal'
import { UsersPage } from './pages/Users'
import { DashboardPage } from './pages/Dashboard'
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
import { TwoFactorRequiredPage } from './components/TwoFactor'

// Pagina's die los van inloggen werken, zoals de links uit een mail en de juridische teksten.
const standalone: Record<string, () => React.ReactNode> = {
  '/wachtwoord-herstellen': ResetPasswordPage,
  '/email-bevestigen': VerifyEmailPage,
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
          <Layout renderTab={(path, active) => (
            <ActiveTabContext.Provider value={active}>
              <AppRoutes location={path} />
            </ActiveTabContext.Provider>
          )} />
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
  return (
    <Routes location={location}>
      <Route path="/" element={<DashboardPage />} />
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
      <Route path="*" element={<DashboardPage />} />
    </Routes>
  )
}
