import { Route, Routes } from 'react-router-dom'
import { ActiveTabContext } from './lib/tabs'
import { Layout } from './components/Layout'
import { TabsProvider } from './lib/tabs'
import { ToastProvider } from './lib/toast'
import { ModulesProvider } from './lib/modules'
import { DashboardPage } from './pages/Dashboard'
import { CustomersPage } from './pages/Customers'
import { CustomerDetailPage } from './pages/CustomerDetail'
import { LeadsPage } from './pages/Leads'
import { InvoicesPage } from './pages/Invoices'
import { InvoiceEditorPage } from './pages/InvoiceEditor'
import { PlanningPage } from './pages/Planning'
import { WorkflowsPage } from './pages/Workflows'
import { WorkflowEditorPage } from './pages/WorkflowEditor'
import { ModulesPage } from './pages/Modules'
import { ModuleRecordsPage } from './pages/ModuleRecords'

export default function App() {
  return (
    <ToastProvider>
      <ModulesProvider>
        <TabsProvider>
          <Layout renderTab={(path, active) => (
            <ActiveTabContext.Provider value={active}>
              <AppRoutes location={path} />
            </ActiveTabContext.Provider>
          )} />
        </TabsProvider>
      </ModulesProvider>
    </ToastProvider>
  )
}

// Elk tabblad houdt zijn eigen pagina vast, zodat je niets kwijtraakt als je wisselt.
function AppRoutes({ location }: { location: string }) {
  return (
    <Routes location={location}>
      <Route path="/" element={<DashboardPage />} />
      <Route path="/planning" element={<PlanningPage />} />
      <Route path="/klanten" element={<CustomersPage />} />
      <Route path="/klanten/:id" element={<CustomerDetailPage />} />
      <Route path="/leads" element={<LeadsPage />} />
      <Route path="/facturen" element={<InvoicesPage />} />
      <Route path="/facturen/:id" element={<InvoiceEditorPage />} />
      <Route path="/werkstromen" element={<WorkflowsPage />} />
      <Route path="/werkstromen/:id" element={<WorkflowEditorPage />} />
      <Route path="/modules" element={<ModulesPage />} />
      <Route path="/modules/:id" element={<ModuleRecordsPage />} />
      <Route path="*" element={<DashboardPage />} />
    </Routes>
  )
}
