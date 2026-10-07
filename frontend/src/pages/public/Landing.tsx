import { useState } from 'react'
import { Link } from 'react-router-dom'
import {
  Timer, FolderKanban, Receipt, Award, Workflow, Blocks, ArrowRight, Check, Play, Square, ChevronDown, ShieldCheck, Download, Sparkles,
} from 'lucide-react'
import { plans, product } from '../../lib/plans'
import { Logo } from './PublicSite'
import { CompanyDetails } from './Legal'
import { legalLinks, usePlatformInfo } from '../../lib/platform'

const features = [
  { icon: Timer, color: '#ff6d5a', title: 'Uren met één klik', text: 'Start de timer als je begint, stop als je klaar bent. Hij loopt door als je je laptop dichtklapt en staat altijd in beeld.' },
  { icon: FolderKanban, color: '#4ea5ff', title: 'Projecten en budget', text: 'Per uur of voor een vaste prijs. Je ziet direct hoeveel budget er nog is en wat je effectief per uur verdient.' },
  { icon: Receipt, color: '#3ecf8e', title: 'Factureren in een minuut', text: 'Open uren op een strakke factuur, btw verlegd voor zakelijke klanten in de EU, de KOR, creditnota\'s en herinneringen als iemand te laat is.' },
  { icon: Award, color: '#f5b83d', title: 'Urencriterium en btw', text: 'Haal je de 1.225 uur voor de zelfstandigenaftrek? En hoeveel btw moet je dit kwartaal afdragen? Het staat op je dashboard.' },
  { icon: Workflow, color: '#9b7bff', title: 'Werkstromen zoals n8n', text: 'Sleep blokken op een canvas: nieuwe lead, wacht drie dagen, stuur een mail, maak een taak. Automatisch, zonder code.' },
  { icon: Blocks, color: '#2fc6c6', title: 'Je eigen modules', text: 'Houd servers, domeinen en licenties bij in lijsten met je eigen velden. Zie je wat er binnenkort verloopt.' },
]

const faq = [
  { q: 'Moet ik een creditcard opgeven om te proberen?', a: 'Nee. Je maakt een account aan en kunt 30 dagen alles gebruiken. Daarna kies je zelf of je doorgaat.' },
  { q: 'Kan ik mijn gegevens meenemen als ik stop?', a: 'Ja. Onder Instellingen download je met één klik al je klanten, projecten, uren en facturen. En je kunt je werkruimte zelf helemaal verwijderen.' },
  { q: 'Werkt het ook met buitenlandse klanten?', a: 'Ja. Vul het land en het btw-nummer van je klant in. Zit je klant in een ander EU-land, dan wordt de btw verlegd en komen beide btw-nummers en de juiste vermelding vanzelf op de factuur. Voor een klant buiten de EU rekent Potel geen Nederlandse btw.' },
  { q: 'Zien collega\'s of andere klanten mijn gegevens?', a: 'Nee. Elke werkruimte is volledig afgeschermd. In het Team-abonnement nodig je zelf collega\'s uit en bepaal je hun rol.' },
]

export function LandingPage() {
  const [open, setOpen] = useState<number | null>(0)
  const info = usePlatformInfo()
  return (
    <div className="public">
      <header className="public-nav">
        <Logo />
        <nav>
          <a href="#functies">Functies</a>
          <a href="#prijzen">Prijzen</a>
          <a href="#vragen">Vragen</a>
        </nav>
        <div className="public-nav-actions">
          <Link to="/inloggen" className="btn btn-ghost">Inloggen</Link>
          <Link to="/aanmelden" className="btn btn-primary">Gratis proberen</Link>
        </div>
      </header>

      <section className="hero">
        <div className="hero-copy">
          <span className="eyebrow"><Sparkles size={14} /> {product.tagline}</span>
          <h1>Minder administratie.<br /><span className="gradient-text">Meer bouwen.</span></h1>
          <p>
            Uren schrijven, projecten bewaken en facturen versturen, in één portaal dat gemaakt is voor developers.
            Zodat je vrijdagmiddag niet meer kwijt bent aan Excel.
          </p>
          <div className="hero-actions">
            <Link to="/aanmelden" className="btn btn-primary btn-lg">30 dagen gratis proberen <ArrowRight size={18} /></Link>
            <a href="#functies" className="btn btn-lg">Bekijk wat het kan</a>
          </div>
          <div className="hero-trust">
            <span><Check size={14} /> Geen creditcard nodig</span>
            <span><Check size={14} /> Altijd opzegbaar</span>
            <span><Check size={14} /> Je data altijd te exporteren</span>
          </div>
        </div>
        <AppMock />
      </section>

      <section className="public-section" id="functies">
        <div className="section-head">
          <h2>Alles wat je als freelancer nodig hebt</h2>
          <p>En niets wat je niet nodig hebt. Geen boekhoudpakket, wel alles van uur tot betaalde factuur.</p>
        </div>
        <div className="feature-grid">
          {features.map(f => (
            <div key={f.title} className="feature" style={{ '--c': f.color } as React.CSSProperties}>
              <div className="feature-icon"><f.icon size={22} /></div>
              <h3>{f.title}</h3>
              <p>{f.text}</p>
            </div>
          ))}
        </div>
      </section>

      <section className="public-section steps-section">
        <div className="section-head"><h2>Binnen vijf minuten aan de slag</h2></div>
        <div className="how">
          {[
            ['Maak je account', 'Vul je bedrijfsgegevens, IBAN en uurtarief in. Je logo en kleur komen op je facturen.'],
            ['Schrijf je uren', 'Met de timer of achteraf, per project. Je ziet je week en je jaar in één oogopslag.'],
            ['Factureer en ontvang', 'Open uren gaan met één klik op een factuur. Te laat? Er staat een herinnering klaar.'],
          ].map(([t, d], i) => (
            <div key={t} className="how-step">
              <span className="how-num">{i + 1}</span>
              <h3>{t}</h3>
              <p>{d}</p>
            </div>
          ))}
        </div>
      </section>

      <section className="public-section" id="prijzen">
        <div className="section-head">
          <h2>Eerlijke prijzen</h2>
          <p>Eén tarief per maand, geen kosten per factuur of per klant.</p>
        </div>
        <div className="pricing">
          {plans.map(p => (
            <div key={p.id} className={`price-card ${p.highlight ? 'highlight' : ''}`}>
              {p.highlight && <span className="price-badge">Meest gekozen</span>}
              <h3>{p.name}</h3>
              <p className="muted">{p.description}</p>
              <div className="price"><strong>{p.price}</strong><span>{p.period}</span></div>
              <ul>{p.features.map(f => <li key={f}><Check size={15} /> {f}</li>)}</ul>
              <Link to="/aanmelden" className={`btn ${p.highlight ? 'btn-primary' : ''}`}>Begin gratis</Link>
            </div>
          ))}
        </div>
        <div className="assurances">
          <span><ShieldCheck size={16} /> Elke werkruimte volledig afgeschermd</span>
          <span><Download size={16} /> Exporteer al je gegevens wanneer je wilt</span>
        </div>
      </section>

      <section className="public-section" id="vragen">
        <div className="section-head"><h2>Veelgestelde vragen</h2></div>
        <div className="faq">
          {faq.map((f, i) => (
            <div key={f.q} className={`faq-item ${open === i ? 'open' : ''}`}>
              <button onClick={() => setOpen(open === i ? null : i)}>{f.q}<ChevronDown size={18} /></button>
              {open === i && <p>{f.a}</p>}
            </div>
          ))}
        </div>
      </section>

      <section className="cta-band">
        <h2>Klaar met uren in Excel?</h2>
        <p>Probeer {product.name} 30 dagen gratis, met je eigen klanten of met voorbeelddata.</p>
        <Link to="/aanmelden" className="btn btn-lg btn-white">Account aanmaken <ArrowRight size={18} /></Link>
      </section>

      <footer className="public-foot">
        <Logo size={18} />
        <CompanyDetails info={info} />
        <nav>{legalLinks.map(l => <Link key={l.to} to={l.to}>{l.label}</Link>)}</nav>
      </footer>
    </div>
  )
}

// Een nagebouwd stukje van de app als blikvanger.
function AppMock() {
  const bars = [62, 80, 45, 90, 70, 20, 8]
  return (
    <div className="mock" aria-hidden>
      <div className="mock-window">
        <div className="mock-top"><span /><span /><span /></div>
        <div className="mock-body">
          <div className="mock-side">
            {['#ff6d5a', '#4ea5ff', '#3ecf8e', '#f5b83d', '#9b7bff'].map(c => <i key={c} style={{ background: c }} />)}
          </div>
          <div className="mock-main">
            <div className="mock-timer">
              <span className="timer-pulse" style={{ '--accent': '#4ea5ff' } as React.CSSProperties} />
              <div><small>Exact Online-koppeling</small><b>Webhook voor voorraad</b></div>
              <strong>1:42:07</strong>
              <em><Square size={10} fill="currentColor" /></em>
            </div>
            <div className="mock-stats">
              <div><small>Klaar om te factureren</small><b>€ 3.420</b></div>
              <div><small>Urencriterium</small><b>1.012 / 1.225</b><i><u style={{ width: '83%' }} /></i></div>
            </div>
            <div className="mock-chart">
              {bars.map((h, i) => <span key={i} style={{ height: `${h}%` }} className={i === 3 ? 'today' : ''} />)}
            </div>
          </div>
        </div>
      </div>
      <div className="mock-invoice">
        <div className="mock-inv-head"><span className="paper-logo" style={{ width: 26, height: 26, fontSize: 13, borderRadius: 8 }}>P</span><b>Factuur</b></div>
        <div className="mock-line" /><div className="mock-line short" /><div className="mock-line" />
        <div className="mock-total"><span>Te betalen</span><b>€ 1.439,90</b></div>
        <div className="mock-stamp">Betaald</div>
      </div>
      <div className="mock-toast"><Play size={12} fill="currentColor" /> 2:15 geboekt op Planningsapp</div>
    </div>
  )
}
