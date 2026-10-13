# Potel · Portaal voor freelance developers

Een portaal voor zzp'ers in softwareontwikkeling: uren schrijven met een timer, projecten per klant, open uren met één klik factureren, en daarnaast planning, klanten, leads, eigen modules en werkstromen in de stijl van n8n.

Potel is gebouwd om te verkopen: elke klant maakt zelf een account aan en krijgt een eigen, volledig afgeschermde werkruimte met 30 dagen proefperiode. Jij beheert als eigenaar alle werkruimtes en abonnementen op de pagina *Platform*.

![stack](https://img.shields.io/badge/frontend-Vite%20%2B%20React%20%2B%20TypeScript-ff6d5a) ![stack](https://img.shields.io/badge/backend-C%23%20ASP.NET%20Core%20%2B%20SQLite-4ea5ff)

## Wat zit erin

| Onderdeel | Wat je ermee doet |
| --- | --- |
| **Dashboard** | Omzet, open uren klaar om te factureren, openstaande facturen, btw van dit kwartaal (te betalen of terug te vragen, met dezelfde som als de pagina Btw-aangifte en de uiterste aangiftedatum), uren deze week, je voortgang naar het urencriterium (1.225 uur), lopende projecten, leads en wat er gepland staat. |
| **Uren** | Start/stop-timer (loopt door als je de pagina ververst en staat altijd rechts in de tabbalk), weekoverzicht per project en dag, en uren handmatig boeken als `1:30` of `1,5`. Met *Voorstellen uit GitHub* maak je van je commits uren (zie GitHub). Gefactureerde uren liggen vast. |
| **Projecten** | Per klant, per uur of met een vaste prijs, met budget in uren, deadline en repository. De repository is een gewone link die begint met `https://`; een GitHub-link moet naar één repository wijzen, zoals `https://github.com/eigenaar/repository`. Je ziet de budgetbalk, open uren en bij vaste prijs je effectieve uurtarief. Met *Factureer* zet je de open uren op een conceptfactuur: één regel of één regel per boeking. |
| **GitHub** | Urenvoorstellen uit je commits. Een beheerder zet bij *Instellingen > GitHub* een token voor de hele werkruimte (een fine-grained token met alleen leesrechten op *Contents* is genoeg). Potel bewaart het versleuteld met de sleutels uit `KeysPath` en laat het daarna nooit meer zien, ook niet in de export: je ziet alleen dat het is ingesteld en de laatste 4 tekens. Verwijderen kan altijd, ook als de werkruimte alleen-lezen is. Iedereen vult daar ook zijn eigen e-mailadressen in commits in (hooguit 5); zonder eigen lijst gebruikt Potel het adres van je account. Op de pagina *Uren* of bij een project kies je *Voorstellen uit GitHub* voor een dag of een week. Potel vraagt dan aan api.github.com (en nergens anders) de commits met jouw adressen op uit de repositories van je lopende projecten, of van dat ene project, en maakt per project per dag één voorstel, met de eerste regel van elke commit als omschrijving. De tijd is een schatting: de tijd tussen je commits telt mee, na meer dan 2 uur zonder commit begint een nieuw blok, voor de eerste commit van elk blok rekent Potel 30 minuten, en het geheel wordt naar boven afgerond op een kwartier, met minstens 30 minuten en hooguit 10 uur per dag. Je boekt elk voorstel, past eerst project, tijd, omschrijving of factureerbaar aan, of slaat het over; Potel boekt nooit zelf iets. Een geboekte of overgeslagen commit komt nooit meer terug, ook niet als je de boeking later verwijdert. Merge-commits tellen niet mee. Potel kijkt naar hooguit 10 repositories tegelijk en 100 commits per repository en adres, alleen op de standaardbranch. Geeft GitHub een fout (token verlopen, limiet bereikt, repository weg), dan zie je dat in gewone woorden. |
| **Planning** | Weekkalender (klik op een leeg vak om iets in te plannen) en een takenlijst om af te vinken. |
| **Klanten** | Klantenlijst met zoeken. Elke klant opent in een eigen tabblad met de tabjes Overzicht, Projecten, Facturen, Planning, Notities en Klantportaal. |
| **Klantportaal** | Per klant een geheime link naar `/portaal`, waarmee je klant zonder inloggen zijn lopende projecten ziet: gewerkte uren tegen het budget, de uren per week van de laatste 8 weken, de recente factureerbare boekingen met datum, project, uren en omschrijving, en zijn verstuurde facturen met status, wat er nog open staat en een link naar de factuurpagina. Een beheerder maakt de link onder het tabje *Klantportaal* (zonder einddatum, of 30 dagen, 90 dagen of een jaar geldig), kopieert hem en trekt hem in; een nieuwe link maakt de oude ongeldig. Per klant kies je of hij uren, budget, facturen en bedragen ziet (bedragen staan standaard uit), en je kunt er een bericht bij zetten. Je klant ziet nooit andere klanten, je notities, niet-factureerbare uren, kosten of iets anders uit je werkruimte dan je bedrijfsnaam, logo en kleur. Net als bij de factuurlink bevat de link 32 willekeurige bytes en staat alleen de SHA-256-hash in de database; een foute, ingetrokken of verlopen link geeft steeds hetzelfde antwoord, de pagina komt niet in zoekmachines, geeft het adres niet door en is per IP-adres begrensd. De factuurlinks in het portaal werken alleen zolang het portaal werkt. Bij de klant zie je wanneer het portaal voor het laatst geopend is. Intrekken kan ook als de werkruimte alleen-lezen is. |
| **Leads** | Kanbanbord: sleep leads van Nieuw naar Contact, Offerte, Gewonnen of Verloren. Met één klik maak je van een lead een klant. Met *Offerte maken* maak je meteen een offerte voor de lead. |
| **Offertes** | Onder *Klanten en sales*: een offerte voor een klant of een lead, met dezelfde regels, eenheden, btw-regelingen en A4-opmaak als een factuur. Per offerte een onderwerp, een inleiding, voorwaarden, een geldigheidsdatum (standaard 30 dagen, aan te passen bij *Instellingen*) en eventueel een aanbetaling als percentage. Een concept pas je nog aan; iedereen in de werkruimte kan een concept maken, alleen een beheerder verstuurt het. Bij versturen krijgt de offerte het volgende nummer in een eigen reeks per jaar (`OFF-2026-001`), wordt de offertedatum vandaag (de geldigheid schuift evenveel dagen mee) en liggen jouw gegevens en die van je klant of lead vast. Daarna verandert er niets meer aan: met *Nieuwe versie* maak je een kopie als concept (versie 2, met een eigen nummer), en zodra je die verstuurt, wordt de vorige versie ingetrokken. De stand is concept, verstuurd, geaccepteerd, afgewezen, ingetrokken of verlopen; verlopen volgt vanzelf uit de geldigheidsdatum. Versturen kan als PDF (printen), met een mail die klaarstaat in je eigen mailprogramma, met *Mailen* vanuit Potel (als er een mailserver is, telt mee voor de daglimiet) en met een geheime link naar `/offerte`. Een beheerder maakt de link en trekt een offerte in; links dichtzetten mag iedereen, ook als de werkruimte alleen-lezen is. Op die pagina ziet je klant zonder inloggen alleen deze ene offerte en geeft hij akkoord met zijn volledige naam en een vinkje, of wijst hij af met eventueel een toelichting. Dat kan één keer, tot en met de geldigheidsdatum en niet als je de offerte hebt ingetrokken; als twee mensen tegelijk klikken, telt er één. Bij akkoord bewaren we de naam, het tijdstip en het IP-adres. De link werkt net als de factuurlink (32 willekeurige bytes, alleen de SHA-256-hash in de database, steeds hetzelfde antwoord bij een foute, ingetrokken of verlopen link, geen zoekmachines, per IP-adres begrensd) en werkt tot een jaar na de geldigheidsdatum, zodat je klant kan terugzien wat hij accepteerde. Na een besluit krijgen de beheerders met een bevestigd e-mailadres een mail (als er een mailserver is), en het dashboard toont het in de tegel *Offertes*, met wat er openstaat, wat binnenkort verloopt en welk deel van de offertes van de afgelopen 12 maanden geaccepteerd is. Was de offerte voor een lead, dan wordt die lead bij akkoord klant. Daarna kies je zelf, Potel doet niets vanzelf: *Maak project* (per uur of vaste prijs, met het totaal zonder btw als budget) of *Maak factuur* (een conceptfactuur met de regels van de offerte, of alleen de aanbetaling; komt de hele factuur na de aanbetaling, dan gaat die er met een min-regel weer af). Een akkoord start ook werkstromen met de trigger *Offerte geaccepteerd*. Een klant met offertes kun je niet verwijderen. |
| **Facturen** | Overzicht met wat openstaat, wat te laat is, concepten en wat je dit jaar ontving, plus per factuur hoeveel dagen hij nog loopt of te laat is. Regels met eenheid (uur, dag, stuk, maand), referentie van de klant, leverdatum of periode (vult zich vanzelf uit de uren), open uren van de klant met één klik erbij en dupliceren. Volgens de regels van de Belastingdienst: alleen een concept kun je nog aanpassen of verwijderen. *Versturen* controleert eerst of alle verplichte gegevens er zijn, geeft dan het volgende nummer per jaar zonder gaten en legt jouw gegevens en die van je klant vast; daarna gaat alleen de status nog vooruit en herstel je een fout met een creditnota die vast naar de oorspronkelijke factuur verwijst. Btw per factuur: normaal, verlegd (alleen voor zakelijke klanten in een ander EU-land, met beide btw-nummers), KOR of buiten de EU, met de juiste vermelding erbij. Server en factuur ronden op dezelfde manier af. Na versturen mail je de factuur vanuit Potel (zie hieronder); zonder mailserver staat er een mail klaar in je eigen mailprogramma, en bij te late facturen een herinneringsmail. Strakke A4-layout met betaalblok, die je als PDF opslaat. |
| **Betalingen** | Bij een verstuurde factuur zie je de betalingen: het bedrag, de datum, hoe het binnenkwam (overboeking, iDEAL of anders) en eventueel een notitie. Met *Betaling toevoegen* leg je ook een deelbetaling vast; wat er nog openstaat, zakt mee en staat in de lijst, op het dashboard, op de factuurpagina van je klant, in het klantportaal en in herinneringen. Is alles binnen, dan wordt de factuur betaald, met de datum van de laatste betaling, en starten werkstromen met de trigger *Factuur betaald* één keer. *Markeer als betaald* blijft werken en legt één betaling vast voor wat er nog openstond. Meer betalen dan er openstaat kan niet; je ziet dan wat er nog open staat. Iedereen in de werkruimte mag een betaling vastleggen, alleen een beheerder haalt er een weg; de factuur staat dan weer op verzonden of verlopen. Een creditnota die de rest dekt, maakt een deels betaalde factuur ook betaald. De btw-aangifte verandert niet door betalingen: die volgt de factuurdatum. Facturen die al betaald waren voor deze versie kregen bij het bijwerken één betaling voor hun hele bedrag, op hun betaaldatum. |
| **Urenspecificatie** | Staan er uren op een factuur (via *Factureer* of de open uren van de klant), dan kan er een urenspecificatie mee: per project elke boeking met datum, omschrijving, in een team wie hem boekte, en de uren, met een subtotaal per project en een totaal dat precies gelijk is aan de urenregels op de factuur. Staan meerdere boekingen op één regel, dan verdeelt Potel het aantal van die regel zo over de boekingen dat het optelt. *Urenspecificatie meesturen* staat standaard aan; met *Toon bedragen per regel* komt er per boeking ook een bedrag zonder btw bij, alleen bij regels per uur. Niet-factureerbare uren staan er nooit op. Heb je het aantal op een urenregel aangepast, dan klopt de specificatie niet meer: Potel zegt welke regel het is en verstuurt de factuur pas als je het aantal herstelt of de specificatie uitzet. Bij versturen ligt de specificatie vast, net als de factuur zelf: latere wijzigingen aan uren of projecten veranderen hem niet. Hij staat als extra A4-pagina na de factuur in de PDF, ingeklapt op de factuurpagina van je klant (en wel in zijn PDF), en is daar en bij de factuur als CSV te downloaden. Al verstuurde facturen van voor deze versie houden geen specificatie. |
| **Terugkerende facturen** | Onder *Facturen > Terugkerend* of vanaf de klantpagina: een factuur die elke maand, elk kwartaal of elk jaar terugkomt, zoals hosting, onderhoud of een retainer. Je kiest de klant, de regels, de btw, of hij vooraf (op de eerste dag van de periode) of achteraf (de dag erna) komt, een startdatum en eventueel een einddatum. In omschrijvingen en de opmerking vult Potel `{maand}`, `{jaar}`, `{kwartaal}` en `{periode}` in het Nederlands in, bijvoorbeeld *Hosting oktober 2026*; met een hoofdletter (`{Maand}`) krijgt het woord er ook een. De leverperiode komt vanzelf op de factuur. De planner zet elke periode precies één conceptfactuur klaar, ook na een herstart of als twee rondes tegelijk lopen (daar waakt een unieke index over). Lag de server stil, dan haalt hij gemiste perioden in, tot 12 stuks; de oudere slaat hij over en dat staat in het logboek. Pauzeren, hervatten (perioden uit de pauze worden overgeslagen) en *Nu aanmaken* kan ook. Na de eerste factuur liggen klant, startdatum en interval vast. Met *Direct versturen* (kan alleen als Potel zelf kan mailen) krijgt de factuur meteen een nummer en gaat hij per e-mail naar de klant. Kan dat niet, bijvoorbeeld omdat de klant geen e-mailadres heeft, de daglimiet op is of er factuurgegevens ontbreken, dan blijft hij als concept staan en staat de reden in het logboek. Iedereen kan ze bekijken, alleen een beheerder maakt of wijzigt ze. Werkruimtes die alleen-lezen zijn en voorbeelddata krijgen nooit facturen. |
| **Facturen mailen** | Is er een mailserver ingesteld, dan mail je een verstuurde factuur of creditnota met *Per e-mail versturen* (direct na versturen) of *Mailen*. Je ziet de mail eerst en kunt de tekst aanpassen; hij gaat altijd naar het e-mailadres van de klant (precies één geldig adres), van het adres van het platform met jouw bedrijfsnaam als afzender, en antwoorden komen bij jou. In de mail staat een persoonlijke link naar `/factuur`: daar ziet je klant zonder inloggen alleen die ene factuur, met wat er nog open staat en een knop om hem te printen of als PDF op te slaan. **Waarom een link en geen PDF-bijlage:** een goede PDF maken op de server kan niet zonder een extra pakket, en de link toont altijd de actuele stand (betaald, gecrediteerd). De link bevat 32 willekeurige bytes; in de database staat alleen de SHA-256-hash. Een link kun je intrekken bij de factuur, de pagina komt niet in zoekmachines (`noindex`), geeft het adres niet door aan andere sites en is per IP-adres begrensd. Elke mail telt mee voor de daglimiet van de werkruimte en gaat alleen weg als een beheerder zijn e-mailadres bevestigde. Bij de factuur zie je wat er gemaild is, wanneer, door wie en aan wie, en wanneer de link voor het laatst geopend is. |
| **Betalingsherinneringen** | Bij *Instellingen > E-mail en herinneringen* zet een beheerder automatische herinneringen aan, in drie stappen: een vriendelijke herinnering, een tweede herinnering en een aanmaning, standaard 7, 14 en 28 dagen na de vervaldatum. De teksten zijn aan te passen, met woorden als `{nummer}`, `{bedrag}`, `{vervaldatum}`, `{betaalgegevens}` en `{link}`. Volgens de wet: een particulier (een klant zonder bedrijfsnaam en btw-nummer) krijgt in de aanmaning 14 dagen na ontvangst om te betalen en hoort welke incassokosten er daarna komen; bij een bedrijf noemt de aanmaning de wettelijke handelsrente en de vergoeding voor incassokosten (minimaal € 40, volgens de wettelijke staffel). Kosten en rente staan alleen in de aanmaning. Elke stap gaat één keer en op volgorde, ook als twee rondes tegelijk lopen (daar waakt een unieke index over). Is een deel gecrediteerd of al betaald, dan noemt de mail wat nog open staat; een betaalde, helemaal gecrediteerde, concept- of voorbeeldfactuur krijgt nooit een herinnering. Per klant en per factuur zet je ze uit, en met *Herinnering sturen* stuur je er zelf een. Kan er niets weg (geen mailserver, daglimiet op, geen geldig e-mailadres), dan staat dat in het logboek, zonder dat het elke ronde herhaald wordt. Werkruimtes die alleen-lezen zijn krijgen geen herinneringen. |
| **Kosten** | Je zakelijke uitgaven per maand, met zoeken, een filter per categorie en totalen: wat je betaalde, je kosten zonder btw, de btw die je terugvraagt en wat nog geen bonnetje heeft. Per uitgave de datum, leverancier, categorie (hardware, software en abonnementen, hosting en domeinen, opleiding, reiskosten, telefoon en internet, kantoor, overig), het bedrag met btw, het tarief (21%, 9%, 0% of geen btw) en het btw-bedrag, dat je kunt aanpassen als de bon anders is afgerond. Bij een buitenlandse leverancier zonder btw vink je *btw verlegd naar mij* aan, met binnen of buiten de EU. Koppel een uitgave eventueel aan een project of klant en zet het bonnetje erbij (PNG, JPG, WebP of PDF, tot 5 MB). Iedereen in de werkruimte mag kosten toevoegen en aanpassen; alleen een beheerder verwijdert een uitgave of vervangt een bonnetje. |
| **Btw-aangifte** | Per kwartaal de rubrieken 1a, 1b, 1e, 3b, 4a, 4b, 5a en 5b, wat je moet betalen of terugkrijgt en de uiterste datum (de laatste dag van de maand na het kwartaal). Facturen tellen op hun factuurdatum, zonder concepten en voorbeeldfacturen; creditnota's trekken af. Diensten aan ondernemers in een ander EU-land tellen in het kwartaal waarin je ze leverde, met de opgaaf ICP per btw-nummer erbij. Kosten met verlegde btw komen in 4a of 4b en als voorbelasting in 5b. Met de KOR zie je dat je geen aangifte doet; vrijgestelde omzet en omzet buiten de EU staan apart. Elke rubriek verwijst naar de bron op belastingdienst.nl. Het is hulp bij het invullen, geen belastingadvies. |
| **Jaaroverzicht** | Per jaar je omzet zonder btw (op factuurdatum, creditnota's trekken af), je kosten per categorie (zonder btw, of met btw als je die niet aftrekt), je resultaat voor belasting en je uren tegen het urencriterium van 1.225 uur, factureerbaar en niet factureerbaar apart. Daaronder een indicatie van de zelfstandigenaftrek, startersaftrek en MKB-winstvrijstelling, met de bedragen per jaar en hun bron in `backend/Potel.Api/Data/TaxYears.cs`. Met *Exporteer voor je boekhouder* download je een CSV (UTF-8, puntkomma's, Nederlandse bedragen) met de samenvatting, alle facturen en alle kosten van dat jaar. |
| **Instellingen** | Je bedrijfsgegevens voor op de factuur, je standaard uurtarief, betaaltermijn, hoe lang een offerte geldig is en je doelen in uren per week en per jaar, je huisstijl, onder *E-mail en herinneringen* de automatische betalingsherinneringen en de teksten van de mails over facturen, en onder *GitHub* het token en je e-mailadressen in commits. |
| **Werkstromen** | Een canvas zoals in n8n: zet blokken neer, stel ze in en verbind ze door te slepen. Staat een werkstroom aan, dan start hij vanzelf bij zijn trigger en voert hij de acties echt uit. Zie hieronder. |
| **Gebruikers** | Iedereen logt in met e-mail en wachtwoord. Beheerders voegen gebruikers toe, kiezen hun rol en kunnen ze uitschakelen. Wachtwoord vergeten? Dan krijg je een link per mail die één uur en één keer werkt. Na het aanmelden bevestig je je e-mailadres via een link; tot die tijd staat er een balk bovenin met *Opnieuw versturen*. |
| **Eigen modules** | Maak zelf een lijst met je eigen velden (tekst, getal, datum, ja/nee). Standaard staan er *Servers & domeinen* en *Licenties* in. Een nieuwe module verschijnt direct in het menu. |
| **Verkooppagina** | Op `/` voor bezoekers: uitleg, functies, prijzen en veelgestelde vragen, met onderaan je bedrijfsgegevens. Prijzen pas je aan in `frontend/src/lib/plans.ts`, je bedrijfsgegevens en contactadres in de sectie `Platform` van de instellingen (zie hieronder). |
| **Aanmelden** | Op `/aanmelden` maakt een klant zelf een werkruimte aan, eventueel met voorbeelddata, en doorloopt een welkomstwizard (bedrijf, tarief, huisstijl, eerste klant). Alleen voor bedrijven: een KvK-nummer van 8 cijfers, een vinkje voor zakelijk gebruik en akkoord op de voorwaarden en de verwerkersovereenkomst zijn verplicht, ook op de server. Bij de werkruimte staat welke versie van de voorwaarden is geaccepteerd, wanneer en door wie. |
| **Juridische teksten** | Op `/voorwaarden`, `/privacy` en `/verwerkersovereenkomst` staan concepten van de algemene voorwaarden, de privacyverklaring en de verwerkersovereenkomst, met je bedrijfsgegevens erin. Ze staan in `frontend/src/pages/public/Legal.tsx`. Pas je ze aan, verhoog dan `Terms.Version` in `backend/Potel.Api/Data/Models.cs`. |
| **Huisstijl** | Elke klant uploadt een logo en kiest een accentkleur; die komen op de facturen. |
| **Abonnement** | Proef (30 dagen), ZZP of Team. Na de proef is de werkruimte alleen-lezen tot er een abonnement is gekozen. Met een sleutel van Mollie betalen klanten zelf: de eerste maand met iDEAL, daarna elke maand automatisch, en opzeggen kan onder *Instellingen*. Zonder Mollie mailen klanten jou en zet jij het abonnement om op *Platform*. |
| **Account** | Beheerders exporteren alle gegevens als JSON, met de bonnetjes erin, of verwijderen hun hele werkruimte. |
| **Platform** | Alleen voor platformbeheerders (jij): alle werkruimtes, gebruik, proefperiodes, omzet per maand, abonnementen omzetten en andere platformbeheerders aanwijzen. |

Verder: alles opent als **tabblad** bovenin (dubbelklik op een tabblad sluit de andere), **Ctrl K** om overal te zoeken, en een licht (standaard) en donker thema.

## Starten

Je hebt nodig: [.NET 10 SDK](https://dotnet.microsoft.com/download) en [Node.js 20+](https://nodejs.org).

**1. Backend** (API op http://localhost:5080, maakt `potel.db` aan met voorbeelddata):

```bash
cd backend/Potel.Api
dotnet run
```

**2. Frontend** (in een tweede terminal, opent op http://localhost:5173):

```bash
cd frontend
npm install
npm run dev
```

### Alles in één (productie)

```bash
cd frontend && npm install && npm run build   # zet de site in backend/Potel.Api/wwwroot
cd ../backend/Potel.Api && dotnet run          # portaal + API op http://localhost:5080
```

### Inloggen

Lokaal (ontwikkelmodus) wordt bij de eerste start een werkruimte met voorbeelddata en een beheerder aangemaakt:

- E-mail: `admin@potel.nl`
- Wachtwoord: `welkom123`

Dit account is ook platformeigenaar. **Wijzig het wachtwoord meteen** (klik linksonder op je naam). Deze instellingen staan in `backend/Potel.Api/appsettings.Development.json`. In productie wordt er geen standaardaccount gemaakt: klanten melden zich aan via `/aanmelden`.

Wil je opnieuw beginnen met de voorbeelddata? Stop de backend en verwijder `backend/Potel.Api/potel.db`.

## Verkopen en hosten

Met Docker draait alles in één container. De database en de sleutels van de inlogcookies staan in het volume `/data`.

```bash
docker compose up -d
```

Meld je na de eerste start zelf aan via `/aanmelden` en maak jezelf daarna platformbeheerder vanaf de server:

```bash
docker compose exec potel dotnet Potel.Api.dll platform-admin jij@jouwdomein.nl
```

Wie platformbeheerder is, staat in de database. Een e-mailadres in de instellingen geeft dat recht niet meer, zodat niemand het kan overnemen door zich met jouw adres aan te melden. Zet er een reverse proxy met https voor (bijvoorbeeld Caddy of het https van je hostingplatform); inlogcookies werken in productie alleen via https.

Belangrijke instellingen (als omgevingsvariabele, met `__` voor een punt):

| Instelling | Betekenis |
| --- | --- |
| `Admin__Email`, `Admin__Password`, `PlatformAdmins__0` | Alleen voor de allereerste start: maakt je account meteen aan, en met hetzelfde adres bij `PlatformAdmins__0` ben je ook platformbeheerder. |
| `BehindProxy` | `true` als er een reverse proxy voor staat, zodat https en het echte IP-adres herkend worden. |
| `KnownProxies__0` | Het IP-adres van je proxy. Alleen van dat adres wordt het doorgestuurde IP-adres van bezoekers geloofd. |
| `DatabasePath`, `KeysPath` | Waar de SQLite-database en de sleutels staan (standaard in `/data`). Met die sleutels zijn de cookies en het GitHub-token versleuteld: bewaar ze samen met de database. Raak je ze kwijt, dan logt iedereen opnieuw in en stelt een beheerder het GitHub-token opnieuw in. |
| `App__BaseUrl` | Het openbare adres van Potel, bijvoorbeeld `https://potel.jouwdomein.nl`. Daarmee maakt Potel de links in mails, ook de links naar facturen; zonder dit adres mailt Potel geen facturen en herinneringen. In productie is dit verplicht: Potel neemt het adres nooit over uit het verzoek, want dat kan iedereen verzinnen. |
| `RateLimit__AuthPerMinute` | Hoe vaak per minuut één IP-adres mag inloggen, aanmelden of een nieuw wachtwoord mag aanvragen (standaard 10). Daarnaast geldt een grens per e-mailadres, en op aanmelden een grens per netwerk en voor het hele platform. |
| `RateLimit__AccountMailsPerHour` | Hoeveel mails over het account (wachtwoord vergeten, e-mailadres bevestigen) één adres per uur krijgt (standaard 3). |
| `Smtp__*` | Mailserver voor wachtwoord vergeten, het bevestigen van e-mailadressen, facturen, offertes en betalingsherinneringen, de melding als een klant over een offerte besloot, en de werkstroomblokken die e-mail sturen. |
| `RateLimit__PublicInvoicePerMinute` | Hoe vaak per minuut één IP-adres de openbare factuur- en offertepagina mag opvragen (standaard 30), zodat niemand links kan blijven raden. |
| `RateLimit__PortalPerMinute` | Hoe vaak per minuut één IP-adres het klantportaal mag opvragen (standaard 30), om dezelfde reden. |
| `Platform__Company`, `Platform__Address`, `Platform__City`, `Platform__Email`, `Platform__Phone`, `Platform__Kvk`, `Platform__VatId` | Je eigen bedrijfsgegevens. Ze staan onderaan de verkooppagina en in de juridische teksten, en `Platform__Email` is het adres waar klanten naartoe mailen om over te stappen. Zolang hier nog `[Vul in: …]` staat, zie je dat geel gemarkeerd. |
| `Mollie__ApiKey` | Zet online betalen aan, zie hieronder. Leeg (standaard) betekent: klanten mailen om over te stappen. |
| `Mollie__WebhookUrl` | Alleen nodig als Mollie je server niet via `App__BaseUrl` kan bereiken, bijvoorbeeld bij het testen via een tunnel. |
| `RateLimit__MollieWebhookPerMinute` | Hoe vaak per minuut één IP-adres de webhook van Mollie mag aanroepen (standaard 120). |
| `Invoicing__StartHour`, `Invoicing__CheckEveryMinutes`, `Invoicing__MaxCatchUpPeriods`, `Invoicing__MaxRecurringPerRun` | Terugkerende facturen: vanaf welk uur (Nederlandse tijd) de planner facturen klaarzet (standaard 8), hoe vaak hij per werkruimte kijkt in minuten (standaard 5), hoeveel gemiste perioden hij na een storing nog inhaalt (standaard 12) en hoeveel facturen hij per ronde per werkruimte hooguit maakt (standaard 25). |
| `Invoicing__MaxRemindersPerRun` | Hoeveel betalingsherinneringen de planner per ronde per werkruimte hooguit verstuurt (standaard 20); de rest volgt in de volgende ronde. Herinneringen gaan ook pas vanaf `Invoicing__StartHour`. |
| `Receipts__MaxMegabytesTrial`, `Receipts__MaxMegabytesPaid` | Hoeveel ruimte een werkruimte in totaal heeft voor bonnetjes, in MB: tijdens de proef (standaard 100) en met een abonnement (standaard 2048). Bonnetjes staan in de database, dus ze gaan mee in je back-up. |
| `Platform__SubProcessors__0__Name`, `__Purpose`, `__Location` | De subverwerkers voor de privacyverklaring en de verwerkersovereenkomst, zoals je hostingpartij en je mailprovider. Begin bij `0` en tel op. |

Voordat je echt verkoopt, regel je nog:

- **Betalen**: zet Mollie aan (zie hieronder) en test eerst met een testsleutel. Controleer je prijzen in `Plans` (`backend/Potel.Api/Data/Models.cs`) en `frontend/src/lib/plans.ts`, en vul je eigen bedrijfsgegevens volledig in bij *Instellingen* in je eigen werkruimte, anders blijven de facturen voor je klanten als concept staan.
- **Juridisch**: vul de sectie `Platform` in met je bedrijfsgegevens en subverwerkers, en laat de concepten op `/voorwaarden`, `/privacy` en `/verwerkersovereenkomst` nakijken door een jurist. In `Legal.tsx` staan nog een paar keuzes geel gemarkeerd, zoals de bewaartermijn na opzeggen, het maximum van je aansprakelijkheid en hoe snel je een datalek meldt. Haal daarna de regel *Concept* bovenaan weg en verhoog `Terms.Version`. Werkruimtes van vóór deze versie hebben nog geen voorwaarden geaccepteerd; dat zie je op *Platform*.
- **Mail**: stel `Smtp__*` en `App__BaseUrl` in, anders werken wachtwoord vergeten en het bevestigen van e-mailadressen niet. Neem een mailprovider in de EU en zet SPF, DKIM en DMARC aan voor je domein, zodat je mail niet in de spam belandt.
- **Back-ups** van het volume `/data`.

### Online betalen met Mollie

Zonder sleutel blijft alles zoals het was: klanten mailen je en jij zet het abonnement om op *Platform*. Met een sleutel krijgt elk betaald abonnement onder *Instellingen, Abonnement* een knop *Betalen met iDEAL*.

1. Maak een account bij [Mollie](https://www.mollie.com) en zet iDEAL en SEPA-incasso aan. Voor de maandelijkse incasso moet Mollie je account ook voor terugkerende betalingen goedkeuren.
2. Zet `App__BaseUrl` op je openbare https-adres. Mollie meldt elke betaling op `https://jouwdomein/api/mollie/webhook`; dat adres moet vanaf internet bereikbaar zijn en heeft geen inlog nodig.
3. Begin met de testsleutel: `Mollie__ApiKey=test_...`. Op de testbetaalpagina van Mollie kies je zelf of een betaling lukt of mislukt. Controleer dat het abonnement omgaat, dat er in je eigen werkruimte een betaalde factuur verschijnt en dat opzeggen werkt.
4. Werkt alles, vervang de sleutel door je live-sleutel (`live_...`).

Zo werkt het:

- De eerste betaling is met iDEAL, voor de prijs uit `Plans` plus 21% btw. Is die betaald, dan maakt Potel bij Mollie een abonnement dat elke maand hetzelfde bedrag afschrijft, zet het abonnement om en stopt de proefperiode.
- Wat er met een betaling is gebeurd, vraagt Potel altijd zelf op bij Mollie; de webhook zegt alleen welke betaling het is. Een verzonnen id doet dus niets, en een betaling die Mollie twee keer meldt, telt één keer.
- Elke betaling wordt een factuur in de werkruimte van de eerste platformbeheerder (jij), bij een klant met de gegevens van de betalende werkruimte. De factuur wordt verstuurd en meteen op betaald gezet, en start je werkstromen met de trigger *Factuur betaald*. Potel mailt de factuur niet zelf; stuur hem door of mail hem met een werkstroom. Mist er iets om te mogen versturen, zoals je btw-id, dan blijft hij als concept staan met een melding in je logboek.
- Lukt een incasso niet, dan krijgt de klant een melding en nog 10 dagen de tijd om opnieuw met iDEAL te betalen. Daarna wordt de werkruimte alleen-lezen.
- Opzeggen stopt het abonnement bij Mollie. De klant werkt door tot het einde van de betaalde maand; daarna is de werkruimte alleen-lezen tot hij opnieuw betaalt. Een werkruimte verwijderen zegt het abonnement eerst op.
- Overstappen van ZZP naar Team (of andersom) is een nieuwe betaling met iDEAL; het oude abonnement stopt dan. Wat er nog over was van de oude maand, wordt niet verrekend.
- Zet je een abonnement zelf om op *Platform*, dan loopt het door tot je het weer omzet. Een lopend abonnement bij Mollie zeg je dan zelf op in Mollie.

## Nog niet ingebouwd

- **Terugbetalen en stornering**: een terugbetaling of een gestorneerde incasso regel je in Mollie, en de creditnota maak je zelf in Potel.
- **Jaarbetaling en verrekenen bij overstappen**: alleen maandabonnementen, zonder korting voor een jaar vooruit.
- **Btw verlegd voor klanten in een ander EU-land**: Potel kent alleen Nederlandse klanten, dus er staat altijd 21% btw op de betaling.
- **Opnieuw akkoord vragen**: verhoog je `Terms.Version`, dan vraagt Potel bestaande klanten nog niet om de nieuwe versie te accepteren. Meld de wijziging zelf per mail, zoals de voorwaarden beloven.
- **Tweestapsverificatie** voor beheerders.
- **Afschrijvingen en investeringsaftrek**: het jaaroverzicht telt kosten helemaal mee in het jaar van aankoop. Hardware van € 450 of meer wordt wel gemeld, want die schrijf je af.
- **Nieuwe belastingjaren**: de bedragen voor de indicatie staan in `TaxYears.cs`. Vul elk jaar het nieuwe jaar aan; voor 2027 staan de startersaftrek en de MKB-winstvrijstelling er nog niet in.
- **Opzeggen met bewaartermijn**: *Werkruimte verwijderen* wist nu meteen alles.
- **Andere branches en andere git-diensten**: de urenvoorstellen kijken alleen naar de standaardbranch van repositories op github.com. GitLab, Bitbucket en GitHub Enterprise Server werken (nog) niet.

## Werkstromen

Een werkstroom begint met een **trigger** en loopt via de lijnen langs de blokken.

| Blok | Wat het doet |
| --- | --- |
| Nieuwe lead / Nieuwe klant / Factuur betaald | Start vanzelf als dat in het portaal gebeurt (alleen als de werkstroom aan staat). |
| Offerte geaccepteerd | Start als een klant een offerte accepteert via de link. Je kunt `{{quote.number}}`, `{{quote.subject}}`, `{{quote.subtotal}}`, `{{quote.total}}`, `{{quote.deposit}}`, `{{quote.acceptedBy}}` en `{{quote.validUntil}}` gebruiken, plus de gegevens van de klant (en de lead, als de offerte voor een lead was). Niet als de werkruimte alleen-lezen is. |
| Factuur te laat | Start één keer per factuur, op de dag dat een verstuurde factuur over de vervaldatum gaat en op *verlopen* komt te staan. Naast de gewone factuurgegevens kun je `{{invoice.open}}` gebruiken: wat er na creditnota's nog open staat. Niet voor voorbeeldfacturen. |
| Schema | Start elk uur, elke dag of elke week vanaf een gekozen uur. |
| Handmatig starten | Start alleen met de knop *Uitvoeren*. |
| Taak maken | Zet een taak in de planning. |
| Status wijzigen | Zet een lead of factuur op een andere status. |
| Klant maken | Zet de lead om naar een klant. |
| Factuur maken | Maakt een conceptfactuur voor de klant. |
| E-mail sturen | Stuurt een e-mail naar één adres (zie hieronder). |
| Webhook | Stuurt alle gegevens als JSON naar een andere app. Alleen naar openbare adressen op poort 80 of 443; doorverwijzingen volgt hij niet. |
| Wachten | Wacht minstens een minuut, of uren of dagen, en gaat dan verder, ook als de server tussendoor herstart. |
| Als / dan | Kiest de Ja- of Nee-uitgang op basis van een veld, bijvoorbeeld `lead.value` groter dan 5000. |

In tekstvelden kun je gegevens invoegen met dubbele accolades, zoals `{{lead.name}}` of `{{invoice.number}}`. Onder *Uitvoeringen* zie je per run welke blokken liepen en wat er gebeurde.

**E-mail versturen:** vul in `appsettings.json` de sectie `Smtp` in (server, poort, gebruiker, wachtwoord, afzender). Zolang er geen mailserver is ingesteld, slaat het blok de mail over en staat er een melding in de uitvoering. Hetzelfde geldt zolang geen enkele beheerder van de werkruimte zijn e-mailadres heeft bevestigd. Alle mail gaat van jouw afzenderadres (of de gebruiker als `From` leeg is), met de bedrijfsnaam van de werkruimte als naam; antwoorden gaan naar het e-mailadres onder *Instellingen*. Per werkruimte geldt een daglimiet: 20 tijdens de proef, 200 met een abonnement. Gemailde facturen en herinneringen tellen daarin mee. Is die op, dan slaat het blok de mail over met een melding en loopt de werkstroom gewoon verder.

**Grenzen:** zodat één werkruimte de server niet kan volzetten, stopt een run na 200 blokken, 30 seconden, 10 e-mails en webhooks samen of 20 keer wachten. De planner op de achtergrond doet elke 30 seconden per werkruimte hooguit 10 runs en werkt een paar werkruimtes tegelijk af; werkruimtes met een verlopen proef slaat hij over. Handmatig uitvoeren kan 30 keer per minuut per werkruimte (`RateLimit__WorkflowRunsPerMinute`). De rest pas je aan in `appsettings.json` onder `Workflows` (als omgevingsvariabele bijvoorbeeld `Workflows__EmailsPerDayTrial`). Draai je Potel alleen voor jezelf en wil je webhooks naar je eigen netwerk, zet dan `Workflows__AllowPrivateWebhooks` op `true`.

## Testen

```bash
cd backend
dotnet test
```

## Opbouw

```
backend/Potel.Api/
  Data/          modellen, database (EF Core + SQLite, met migraties) en voorbeelddata
  Endpoints/     API per onderdeel: inloggen, gebruikers, klanten, projecten, uren, leads, facturen, planning, instellingen, modules, werkstromen
  Integrations/  koppelingen met andere diensten, zoals de GitHub-API voor urenvoorstellen
  Invoicing/     terugkerende facturen: perioden uitrekenen en facturen klaarzetten
  Workflows/     de motor die werkstromen uitvoert, plus de planner op de achtergrond
backend/Potel.Api.Tests/   tests voor inloggen, werkruimtes (afscherming, proef, platform), werkstromen, uren en facturen
frontend/src/
  components/    menu, tabbladen, zoekvenster en losse UI-onderdelen
  pages/         één bestand per scherm
  lib/           API-client, tabbladen, opmaak van bedragen en datums
```

De API-documentatie (Swagger) staat op http://localhost:5080/swagger zolang de backend in ontwikkelmodus draait.


Een database uit de allereerste versie wordt bij het starten bewaard als `potel.db.<datum>.bak` en vervangen door een nieuwe met het juiste schema.
