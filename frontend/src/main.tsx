import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import './index.css'
import App from './App.tsx'
import { setupPwa } from './lib/pwa'

// Thema meteen zetten, ook al op de inlogpagina. Standaard licht.
try { document.documentElement.dataset.theme = localStorage.getItem('potel.theme') ?? 'light' } catch { document.documentElement.dataset.theme = 'light' }

// Potel als app: service worker, nieuwe versies en installeren.
setupPwa()

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <App />
    </BrowserRouter>
  </StrictMode>,
)
