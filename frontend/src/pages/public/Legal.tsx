import { useEffect, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { AlertTriangle, Printer } from 'lucide-react'
import { legalLinks, usePlatformInfo, type PlatformInfo } from '../../lib/platform'
import { product } from '../../lib/plans'
import { Logo } from './PublicSite'

// De algemene voorwaarden, de privacyverklaring en de verwerkersovereenkomst. Dit zijn concepten: laat ze nakijken
// door een jurist. Pas je de voorwaarden of de verwerkersovereenkomst aan, verhoog dan Terms.Version in
// backend/Potel.Api/Data/Models.cs, zodat bij elke werkruimte klopt welke versie is geaccepteerd.

const placeholder = (s?: string | null) => !s || s.trim().startsWith('[')

// Een gegeven van de eigenaar, opvallend gemarkeerd zolang het nog niet is ingevuld.
function V({ value }: { value?: string | null }) {
  if (placeholder(value)) return <mark className="legal-fill">{value || '[Vul in]'}</mark>
  return <>{value}</>
}

// Iets wat je zelf nog moet kiezen, zoals een termijn of een bedrag.
const Fill = ({ children }: { children: ReactNode }) => <mark className="legal-fill">[Vul in: {children}]</mark>

function Article({ n, title, children }: { n: number; title: string; children: ReactNode }) {
  return <section className="legal-article"><h2>{n}. {title}</h2>{children}</section>
}

// Wie wij zijn, zoals in de instellingen van de server staat.
export function CompanyDetails({ info }: { info: PlatformInfo | null }) {
  if (!info) return null
  return (
    <p className="legal-company">
      <strong><V value={info.company} /></strong>, <V value={info.address} />, <V value={info.city} /><br />
      KvK <V value={info.kvk} /> · btw-id <V value={info.vatId} /> · <V value={info.email} />{info.phone && <> · {info.phone}</>}
    </p>
  )
}

function LegalPage({ title, intro, children }: { title: string; intro: (info: PlatformInfo) => ReactNode; children: (info: PlatformInfo) => ReactNode }) {
  const info = usePlatformInfo()
  // De titel wordt ook de bestandsnaam als je de pagina als PDF opslaat.
  useEffect(() => {
    const before = document.title
    window.scrollTo(0, 0)
    document.title = `${title} ${product.name}`
    return () => { document.title = before }
  }, [title])
  return (
    <div className="public legal">
      <header className="public-nav no-print">
        <Logo />
        <nav>{legalLinks.map(l => <Link key={l.to} to={l.to}>{l.label}</Link>)}</nav>
        <div className="public-nav-actions">
          <button type="button" className="btn" onClick={() => window.print()}><Printer size={15} /> Opslaan als PDF</button>
        </div>
      </header>
      <main className="legal-body">
        <div className="legal-draft"><AlertTriangle size={18} /> Concept: laat deze tekst nakijken door een jurist voordat je verkoopt.</div>
        <h1>{title}</h1>
        {info && <>
          <p className="legal-meta">Versie {info.termsVersion}</p>
          <CompanyDetails info={info} />
          <div className="legal-intro">{intro(info)}</div>
          {children(info)}
        </>}
      </main>
      <footer className="public-foot no-print">
        <Logo size={18} />
        <span>{legalLinks.map((l, i) => <span key={l.to}>{i > 0 && ' · '}<Link to={l.to}>{l.label}</Link></span>)}</span>
      </footer>
    </div>
  )
}

export function TermsPage() {
  return (
    <LegalPage
      title="Algemene voorwaarden"
      intro={i => <p>
        Deze voorwaarden gelden voor het gebruik van {i.name}, het online portaal voor freelance developers van <V value={i.company} />.
        In deze tekst noemen we <V value={i.company} /> ook "wij" of "ons", en het bedrijf dat {i.name} gebruikt "de klant" of "jij".
      </p>}
    >{i => <>
      <Article n={1} title="Wat we bedoelen">
        <ul>
          <li><strong>Dienst</strong>: het online portaal {i.name}, met alles wat daarbij hoort, zoals updates en hulp per e-mail.</li>
          <li><strong>Werkruimte</strong>: de afgeschermde omgeving van de klant in {i.name}, met de gegevens en gebruikers van de klant.</li>
          <li><strong>Gebruiker</strong>: iedereen die met een eigen account namens de klant inlogt.</li>
          <li><strong>Overeenkomst</strong>: de afspraak tussen ons en de klant over het gebruik van {i.name}, met deze voorwaarden en de verwerkersovereenkomst.</li>
        </ul>
      </Article>
      <Article n={2} title="Wanneer deze voorwaarden gelden">
        <p>Deze voorwaarden gelden voor elke overeenkomst over {i.name}, ook tijdens de gratis proefperiode.</p>
        <p>De <Link to="/verwerkersovereenkomst">verwerkersovereenkomst</Link> hoort bij deze voorwaarden. Zeggen ze iets anders over persoonsgegevens, dan gaat de verwerkersovereenkomst voor.</p>
        <p>Algemene voorwaarden of inkoopvoorwaarden van de klant gelden niet, tenzij we dat schriftelijk met elkaar afspreken.</p>
      </Article>
      <Article n={3} title="Alleen voor zakelijk gebruik">
        <p>{i.name} is alleen bedoeld voor ondernemers die het gebruiken voor hun bedrijf of beroep. Bij het aanmelden geef je je KvK-nummer op en bevestig je dat je {i.name} zakelijk gebruikt.</p>
        <p>Omdat je geen consument bent, gelden de regels voor consumenten niet, zoals de bedenktijd van 14 dagen. Onze prijzen zijn exclusief btw.</p>
        <p>Gebruik je {i.name} toch als consument, of klopt je KvK-nummer niet, dan mogen we je account stopzetten.</p>
      </Article>
      <Article n={4} title="Account en proefperiode">
        <p>Je maakt zelf een account aan. Je zorgt dat je gegevens kloppen en je houdt je wachtwoord geheim. Wat er met de accounts in jouw werkruimte gebeurt, valt onder jouw verantwoordelijkheid.</p>
        <p>Een nieuwe werkruimte krijgt een gratis proefperiode van 30 dagen. Daarvoor hoef je niet te betalen en geen betaalgegevens op te geven.</p>
        <p>Kies je na de proefperiode geen abonnement, dan wordt je werkruimte alleen-lezen. Je kunt dan nog alles bekijken en exporteren, maar niets meer wijzigen.</p>
      </Article>
      <Article n={5} title="Abonnement, prijs en betalen">
        <p>De prijzen staan op de verkooppagina en in {i.name} onder Instellingen. Ze gelden per maand en zijn exclusief btw.</p>
        <p>Je betaalt per maand vooraf: de eerste maand met iDEAL en daarna via automatische incasso, of op een andere manier die we met je afspreken. Van elke betaling krijg je een factuur.</p>
        <p>Lukt een betaling niet, dan proberen we het opnieuw of vragen we je om zelf te betalen. Blijft de betaling uit, dan mogen we je werkruimte op alleen-lezen zetten tot er betaald is.</p>
      </Article>
      <Article n={6} title="Prijswijzigingen">
        <p>We mogen onze prijzen aanpassen. Een verhoging melden we minstens 30 dagen van tevoren per e-mail.</p>
        <p>Ben je het niet eens met een verhoging, dan kun je opzeggen tegen de datum waarop de nieuwe prijs ingaat.</p>
      </Article>
      <Article n={7} title="Looptijd en opzeggen">
        <p>Een abonnement loopt per maand en wordt steeds met een maand verlengd.</p>
        <p>Je kunt op elk moment opzeggen in {i.name} onder Instellingen. Het abonnement stopt dan aan het einde van de periode die je al betaald hebt. Daarna wordt je werkruimte alleen-lezen.</p>
        <p>Wij kunnen de overeenkomst opzeggen met een termijn van twee maanden. Bij ernstig misbruik of als je niet betaalt, mogen we meteen stoppen.</p>
        <p>Wat je al betaald hebt voor een lopende periode, krijg je bij opzeggen niet terug.</p>
      </Article>
      <Article n={8} title="Je gegevens exporteren en verwijderen">
        <p>Je gegevens blijven van jou. Een beheerder van je werkruimte kan op elk moment alle gegevens exporteren als JSON-bestand.</p>
        <p>Na het einde van je abonnement kun je je werkruimte nog <Fill>aantal</Fill> dagen bekijken en exporteren. Daarna mogen we de werkruimte met alle gegevens verwijderen.</p>
        <p>Je kunt je werkruimte ook zelf verwijderen. Dan zijn de gegevens meteen weg en kunnen we ze niet meer terughalen, behalve uit back-ups. Die worden binnen <Fill>aantal</Fill> dagen overschreven.</p>
      </Article>
      <Article n={9} title="Jij bewaart je administratie">
        <p>Als ondernemer moet je je administratie, waaronder je facturen, 7 jaar bewaren. Dat blijft jouw verantwoordelijkheid, ook als je {i.name} gebruikt.</p>
        <p>{i.name} is een hulpmiddel en geen archief. Maak daarom regelmatig een export en bewaar die zelf, zeker voordat je opzegt of je werkruimte verwijdert.</p>
        <p>{i.name} helpt je om facturen volgens de regels te maken, maar jij blijft verantwoordelijk voor wat je verstuurt, voor je btw-aangifte en voor je boekhouding. Twijfel je, vraag het dan aan je boekhouder.</p>
      </Article>
      <Article n={10} title="Goed gebruik">
        <p>Je gebruikt {i.name} niet voor iets wat volgens de wet niet mag, zoals spam of berichten aan mensen die daar geen toestemming voor gaven.</p>
        <p>Werkstromen die e-mail of webhooks versturen, hebben grenzen per dag. Bij misbruik mogen we werkstromen stopzetten of je account blokkeren.</p>
        <p>Je probeert niet bij gegevens van andere klanten te komen of de beveiliging van {i.name} te omzeilen. Vind je een zwakke plek, meld die dan bij ons.</p>
      </Article>
      <Article n={11} title="Beschikbaarheid en onderhoud">
        <p>We doen ons best om {i.name} altijd bereikbaar te houden en maken elke dag back-ups. Een vaste beschikbaarheid beloven we niet.</p>
        <p>Voor onderhoud kan {i.name} even niet bereikbaar zijn. Groter onderhoud melden we als het kan van tevoren.</p>
        <p>We mogen {i.name} verbeteren en veranderen. Halen we een belangrijke functie weg, dan melden we dat van tevoren.</p>
      </Article>
      <Article n={12} title="Aansprakelijkheid">
        <p>Zijn we aansprakelijk voor schade, dan alleen voor directe schade. Onze aansprakelijkheid is altijd beperkt tot het bedrag dat je in de 12 maanden voor de schade voor {i.name} hebt betaald, met een maximum van <Fill>bedrag</Fill> euro.</p>
        <p>We zijn niet aansprakelijk voor indirecte schade, zoals gemiste omzet, gemiste besparingen, verlies van gegevens die je niet zelf hebt bewaard, of boetes en naheffingen van de Belastingdienst.</p>
        <p>Deze beperkingen gelden niet als de schade komt door opzet of bewuste roekeloosheid van onze directie.</p>
        <p>Meld schade zo snel mogelijk en uiterlijk binnen 12 maanden nadat je hem ontdekte. Daarna vervalt je recht op vergoeding.</p>
      </Article>
      <Article n={13} title="Overmacht">
        <p>Kunnen we ons door overmacht niet aan onze afspraken houden, bijvoorbeeld door een storing bij onze hosting, een storing van het internet of stroomuitval, dan hoeft dat zolang de overmacht duurt. Duurt het langer dan 60 dagen, dan mogen we allebei de overeenkomst beëindigen.</p>
      </Article>
      <Article n={14} title="Persoonsgegevens en geheimhouding">
        <p>In {i.name} zet je gegevens van je eigen klanten en leads. Daarvoor ben jij verantwoordelijk, en wij verwerken ze alleen voor jou. De afspraken daarover staan in de <Link to="/verwerkersovereenkomst">verwerkersovereenkomst</Link>.</p>
        <p>Wat we over jouw bedrijf en je gegevens te weten komen, houden we geheim.</p>
        <p>Hoe we omgaan met de gegevens van jou als klant, lees je in onze <Link to="/privacy">privacyverklaring</Link>.</p>
      </Article>
      <Article n={15} title="Intellectueel eigendom">
        <p>De software, het ontwerp en de teksten van {i.name} zijn van ons. Je mag {i.name} gebruiken zolang de overeenkomst loopt.</p>
        <p>De gegevens die je in {i.name} zet, blijven van jou. Wij gebruiken ze alleen om de dienst te leveren.</p>
      </Article>
      <Article n={16} title="Wijzigingen van deze voorwaarden">
        <p>We mogen deze voorwaarden aanpassen. Een wijziging melden we minstens 30 dagen voordat hij ingaat. Elke versie heeft een eigen versienummer, en we bewaren welke versie je hebt geaccepteerd.</p>
        <p>Ben je het niet eens met de nieuwe voorwaarden, dan kun je opzeggen tegen de datum waarop ze ingaan.</p>
      </Article>
      <Article n={17} title="Toepasselijk recht">
        <p>Op deze voorwaarden en de overeenkomst is Nederlands recht van toepassing.</p>
        <p>Hebben we een geschil, dan proberen we dat eerst samen op te lossen. Lukt dat niet, dan beslist de bevoegde rechter in het arrondissement waar wij gevestigd zijn.</p>
      </Article>
    </>}</LegalPage>
  )
}

export function PrivacyPage() {
  return (
    <LegalPage
      title="Privacyverklaring"
      intro={i => <p>
        In deze privacyverklaring lees je welke persoonsgegevens <V value={i.company} /> verwerkt als je {i.name} gebruikt of bezoekt,
        waarom we dat doen, hoe lang we ze bewaren en welke rechten je hebt.
      </p>}
    >{i => <>
      <Article n={1} title="Wie we zijn">
        <p>{i.name} is een dienst van <V value={i.company} />, gevestigd op <V value={i.address} /> in <V value={i.city} />, ingeschreven bij de KvK onder nummer <V value={i.kvk} />.
          Heb je een vraag over privacy, mail dan naar <V value={i.email} />.</p>
      </Article>
      <Article n={2} title="Twee rollen">
        <p>Bij {i.name} hebben we twee rollen:</p>
        <ul>
          <li>Voor de gegevens van jou als klant, zoals je account en je abonnement, bepalen wij waarom en hoe we ze verwerken. Daarvoor zijn wij verantwoordelijk, en daar gaat deze privacyverklaring over.</li>
          <li>Voor de gegevens die jij in {i.name} zet over je eigen klanten, leads en contactpersonen, ben jij verantwoordelijk. Wij verwerken die alleen in jouw opdracht. De afspraken daarover staan in de <Link to="/verwerkersovereenkomst">verwerkersovereenkomst</Link>.</li>
        </ul>
      </Article>
      <Article n={3} title="Welke gegevens, waarom en hoe lang">
        <div className="legal-table">
          <table>
            <thead><tr><th>Gegevens</th><th>Waarvoor</th><th>Grondslag</th><th>Hoe lang</th></tr></thead>
            <tbody>
              <tr><td>Je account: naam, e-mailadres, wachtwoord (alleen als versleutelde hash), rol en laatste keer ingelogd</td><td>Inloggen en je werkruimte gebruiken</td><td>Uitvoering van de overeenkomst</td><td>Zolang je account bestaat</td></tr>
              <tr><td>Je bedrijf: bedrijfsnaam, KvK-nummer, adres en btw-id</td><td>Controleren dat je zakelijk klant bent en je factureren</td><td>Uitvoering van de overeenkomst</td><td>Zolang de overeenkomst loopt, daarna 7 jaar in onze administratie</td></tr>
              <tr><td>Je akkoord op de voorwaarden: welke versie, wanneer en door wie</td><td>Kunnen laten zien welke afspraken gelden</td><td>Gerechtvaardigd belang</td><td>Zolang de overeenkomst loopt en 2 jaar daarna</td></tr>
              <tr><td>Betaalgegevens: naam van de rekeninghouder, IBAN en of een betaling gelukt is</td><td>Je abonnement innen</td><td>Uitvoering van de overeenkomst</td><td>7 jaar, want dat is de wettelijke bewaarplicht</td></tr>
              <tr><td>Onze facturen aan jou</td><td>Onze administratie</td><td>Wettelijke plicht</td><td>7 jaar</td></tr>
              <tr><td>Mail tussen jou en ons</td><td>Je vragen beantwoorden</td><td>Uitvoering van de overeenkomst</td><td>Tot 2 jaar nadat je vraag is afgehandeld</td></tr>
              <tr><td>Technische gegevens: IP-adres, tijdstip en mislukte inlogpogingen</td><td>Beveiliging: het raden van wachtwoorden en misbruik tegengaan</td><td>Gerechtvaardigd belang</td><td>Hooguit <Fill>aantal</Fill> dagen in de logboeken van de server. Tellers van inlogpogingen staan alleen in het geheugen</td></tr>
              <tr><td>Eén inlogcookie</td><td>Ingelogd blijven</td><td>Nodig om de dienst te leveren</td><td>Tot 7 dagen na je laatste bezoek</td></tr>
            </tbody>
          </table>
        </div>
      </Article>
      <Article n={4} title="Ons gerechtvaardigd belang">
        <p>Waar we ons op een gerechtvaardigd belang beroepen, is dat belang: {i.name} veilig en bruikbaar houden voor alle klanten, bijvoorbeeld door inlogpogingen te begrenzen en misbruik van mail tegen te gaan, en kunnen laten zien welke voorwaarden je hebt geaccepteerd. Je mag daartegen bezwaar maken; zie hieronder bij je rechten.</p>
      </Article>
      <Article n={5} title="Wie je gegevens krijgt">
        <p>We verkopen je gegevens niet. We delen ze alleen met partijen die we nodig hebben om {i.name} te leveren:</p>
        <ul>
          {i.subProcessors.map((p, n) => <li key={n}><V value={p.name} /> (<V value={p.location} />): {p.purpose.toLowerCase()}</li>)}
          <li>Onze betaalprovider, als je online betaalt: voor het innen van je abonnement</li>
          <li>Onze boekhouder: voor onze administratie</li>
        </ul>
        <p>Met elk van deze partijen hebben we afspraken gemaakt, zodat ze je gegevens alleen voor ons gebruiken. Aan de overheid geven we gegevens alleen als de wet ons daartoe verplicht.</p>
      </Article>
      <Article n={6} title="Buiten de Europese Unie">
        <p>We bewaren je gegevens in de Europese Economische Ruimte (EER). Gebruiken we toch een partij buiten de EER, dan alleen met passende waarborgen, zoals de standaardcontractbepalingen van de Europese Commissie.</p>
        <p>Onze pagina's laden het lettertype Inter via Google Fonts. Daarbij krijgt Google, ook in de Verenigde Staten, je IP-adres te zien. <Fill>host het lettertype zelf, dan kan deze alinea weg</Fill></p>
      </Article>
      <Article n={7} title="Beveiliging">
        <p>We beveiligen je gegevens zo goed als redelijkerwijs kan. De verbinding loopt altijd via https, wachtwoorden bewaren we alleen als versleutelde hash, elke werkruimte is afgeschermd van de andere, inlogpogingen zijn begrensd en we maken elke dag back-ups in de EU.</p>
      </Article>
      <Article n={8} title="Je rechten">
        <p>Je mag je gegevens inzien, laten verbeteren of laten verwijderen. Je mag ook vragen om minder te verwerken, bezwaar maken tegen verwerking op basis van ons gerechtvaardigd belang, en je gegevens meenemen naar een andere dienst.</p>
        <p>Veel kun je zelf in {i.name}: je gegevens aanpassen onder Instellingen en al je gegevens exporteren onder Instellingen, Account. Voor de rest mail je naar <V value={i.email} />. We reageren binnen een maand. Soms vragen we je eerst te laten zien dat het om jouw gegevens gaat.</p>
      </Article>
      <Article n={9} title="Een klacht">
        <p>Ben je niet tevreden over hoe we met je gegevens omgaan, laat het ons dan weten. Je hebt ook het recht om een klacht in te dienen bij de Autoriteit Persoonsgegevens, via autoriteitpersoonsgegevens.nl.</p>
      </Article>
      <Article n={10} title="Geen tracking en geen automatische besluiten">
        <p>We gebruiken geen trackingcookies en werken niet met advertentienetwerken. We nemen geen besluiten over je die alleen door een computer worden genomen.</p>
      </Article>
      <Article n={11} title="Wijzigingen">
        <p>We kunnen deze privacyverklaring aanpassen. Bovenaan staat de versie. Bij grote wijzigingen sturen we je een mail.</p>
      </Article>
    </>}</LegalPage>
  )
}

export function ProcessorAgreementPage() {
  return (
    <LegalPage
      title="Verwerkersovereenkomst"
      intro={i => <p>
        Deze verwerkersovereenkomst hoort bij de <Link to="/voorwaarden">algemene voorwaarden</Link> van {i.name}. Hij geldt tussen <V value={i.company} /> als verwerker ("wij")
        en het bedrijf dat {i.name} gebruikt als verwerkingsverantwoordelijke ("de klant"). Hiermee maken we de afspraken die artikel 28 van de Algemene verordening gegevensbescherming (AVG) vraagt.
      </p>}
    >{i => <>
      <Article n={1} title="Onderwerp en duur">
        <p>De klant gebruikt {i.name} om zijn praktijk te beheren. Daarbij zet de klant persoonsgegevens in {i.name}, en die verwerken wij voor de klant.</p>
        <p>Deze overeenkomst loopt zolang wij persoonsgegevens voor de klant verwerken: zolang de klant {i.name} gebruikt, en daarna tot alle gegevens zijn verwijderd.</p>
      </Article>
      <Article n={2} title="Welke gegevens, van wie en waarvoor">
        <ul>
          <li><strong>Wat we doen</strong>: gegevens opslaan, tonen, doorzoeken, versturen en verwijderen, zodat de klant zijn uren, projecten, facturen, klanten, leads, planning en eigen lijsten kan bijhouden. Versturen gebeurt alleen via werkstromen die de klant zelf instelt, zoals een e-mail of een webhook.</li>
          <li><strong>Soorten gegevens</strong>: namen, contactgegevens (e-mailadres, telefoonnummer, adres), bedrijfsgegevens en btw-nummers, gegevens op facturen, notities, afspraken, gegevens in eigen modules en de accounts van gebruikers van de klant.</li>
          <li><strong>Van wie</strong>: klanten en contactpersonen van de klant, leads, medewerkers en andere gebruikers van de klant, en anderen over wie de klant iets vastlegt.</li>
        </ul>
        <p>{i.name} is niet bedoeld voor bijzondere persoonsgegevens, zoals gegevens over gezondheid, en niet voor burgerservicenummers. De klant zet die er niet in.</p>
      </Article>
      <Article n={3} title="Alleen in opdracht van de klant">
        <p>We verwerken de gegevens alleen in opdracht van de klant. Die opdracht bestaat uit deze overeenkomst, de algemene voorwaarden en wat de klant zelf in {i.name} instelt en doet. We gebruiken de gegevens niet voor onszelf.</p>
        <p>Verplicht de wet ons om gegevens op een andere manier te verwerken, dan melden we dat vooraf aan de klant, tenzij de wet dat verbiedt.</p>
        <p>Vinden we dat een opdracht van de klant in strijd is met de AVG, dan laten we dat meteen weten.</p>
      </Article>
      <Article n={4} title="Geheimhouding">
        <p>Iedereen die bij ons bij de gegevens kan, moet ze geheimhouden. We kijken alleen in gegevens van de klant als dat nodig is, bijvoorbeeld om een storing op te lossen of omdat de klant om hulp vraagt.</p>
      </Article>
      <Article n={5} title="Beveiliging">
        <p>We nemen passende technische en organisatorische maatregelen om de gegevens te beveiligen, zoals bedoeld in artikel 32 van de AVG. Daaronder vallen in elk geval:</p>
        <ul>
          <li>een verbinding die altijd via https loopt;</li>
          <li>wachtwoorden die we alleen als versleutelde hash bewaren, en sessies die stoppen zodra iemand een nieuw wachtwoord kiest;</li>
          <li>werkruimtes die van elkaar zijn afgeschermd;</li>
          <li>grenzen aan inlogpogingen en aan e-mail en webhooks uit werkstromen;</li>
          <li>dagelijkse back-ups in de EU, en regelmatig testen of terugzetten werkt;</li>
          <li>toegang tot de server alleen voor wie die nodig heeft, met sterke wachtwoorden of sleutels;</li>
          <li>updates van de software op tijd.</li>
        </ul>
        <p>We mogen deze maatregelen aanpassen, zolang de beveiliging daardoor niet minder wordt.</p>
      </Article>
      <Article n={6} title="Subverwerkers">
        <p>De klant geeft ons algemene toestemming om subverwerkers in te schakelen. De subverwerkers van nu staan in de lijst onderaan.</p>
        <p>Willen we een subverwerker toevoegen of vervangen, dan melden we dat minstens 30 dagen van tevoren per e-mail. De klant kan in die tijd met redenen bezwaar maken. Komen we er samen niet uit, dan kan de klant opzeggen voordat de wijziging ingaat.</p>
        <p>We leggen elke subverwerker dezelfde plichten op als die in deze overeenkomst staan. Tegenover de klant blijven wij verantwoordelijk voor wat zij doen.</p>
        <p>Gegevens gaan alleen naar een land buiten de Europese Economische Ruimte als daar passende waarborgen voor zijn, zoals de standaardcontractbepalingen van de Europese Commissie. Dat staat dan in de lijst.</p>
      </Article>
      <Article n={7} title="Hulp bij verzoeken van betrokkenen">
        <p>Veel kan de klant zelf in {i.name}: gegevens inzien, aanpassen, exporteren en verwijderen. Lukt dat niet, dan helpen we de klant om een verzoek van een betrokkene op tijd te beantwoorden. Krijgen wij zelf zo'n verzoek, dan sturen we het door naar de klant.</p>
      </Article>
      <Article n={8} title="Datalekken">
        <p>Ontdekken we een datalek dat gegevens van de klant raakt, dan melden we dat zonder onnodige vertraging aan de beheerder van de werkruimte, en uiterlijk binnen <Fill>aantal</Fill> uur nadat we het ontdekten.</p>
        <p>We geven de klant de informatie die nodig is om het lek te melden bij de Autoriteit Persoonsgegevens en aan de betrokkenen: wat er is gebeurd, om welke gegevens en hoeveel mensen het gaat, wat de gevolgen kunnen zijn en wat we eraan doen. De klant beslist zelf of hij het lek meldt; een melding bij de Autoriteit Persoonsgegevens moet binnen 72 uur.</p>
        <p>We houden van elk datalek een logboek bij, ook van kleine lekken.</p>
      </Article>
      <Article n={9} title="Andere hulp">
        <p>We helpen de klant met een gegevensbeschermingseffectbeoordeling (DPIA) en met een voorafgaande raadpleging van de Autoriteit Persoonsgegevens, voor zover het om {i.name} gaat. Kost dat veel werk, dan mogen we daarvoor redelijke kosten rekenen.</p>
      </Article>
      <Article n={10} title="Controle">
        <p>We geven de klant de informatie die nodig is om te laten zien dat we ons aan deze overeenkomst houden.</p>
        <p>De klant mag eens per jaar, of na een datalek, een controle laten doen door een onafhankelijke deskundige die geheimhouding heeft. De klant meldt dat minstens 30 dagen van tevoren en betaalt de kosten zelf. We krijgen een kopie van het rapport.</p>
      </Article>
      <Article n={11} title="Einde van de overeenkomst">
        <p>Aan het einde van de overeenkomst kan de klant alle gegevens exporteren. Na de termijn uit de algemene voorwaarden verwijderen we de werkruimte met alle gegevens, tenzij de wet ons verplicht iets te bewaren.</p>
        <p>Back-ups worden binnen <Fill>aantal</Fill> dagen overschreven. Tot die tijd blijven ze beveiligd en gebruiken we ze alleen om na een storing gegevens terug te zetten.</p>
      </Article>
      <Article n={12} title="Aansprakelijkheid en recht">
        <p>Voor aansprakelijkheid gelden de afspraken uit de algemene voorwaarden, voor zover de wet dat toestaat.</p>
        <p>Op deze overeenkomst is Nederlands recht van toepassing. Een geschil gaat naar de rechter die in de algemene voorwaarden staat.</p>
      </Article>
      <section className="legal-article">
        <h2>Bijlage: subverwerkers</h2>
        <div className="legal-table">
          <table>
            <thead><tr><th>Subverwerker</th><th>Wat ze voor ons doen</th><th>Waar de gegevens staan</th></tr></thead>
            <tbody>
              {i.subProcessors.map((p, n) => <tr key={n}><td><V value={p.name} /></td><td>{p.purpose}</td><td><V value={p.location} /></td></tr>)}
            </tbody>
          </table>
        </div>
      </section>
    </>}</LegalPage>
  )
}
