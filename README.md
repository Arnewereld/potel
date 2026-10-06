# Potel · Portaal voor freelance developers

Een portaal voor zzp'ers in softwareontwikkeling: uren schrijven met een timer, projecten per klant, open uren met één klik factureren, en daarnaast planning, klanten, leads, eigen modules en werkstromen in de stijl van n8n.

Potel is gebouwd om te verkopen: elke klant maakt zelf een account aan en krijgt een eigen, volledig afgeschermde werkruimte met 30 dagen proefperiode. Jij beheert als eigenaar alle werkruimtes en abonnementen op de pagina *Platform*.

![stack](https://img.shields.io/badge/frontend-Vite%20%2B%20React%20%2B%20TypeScript-ff6d5a) ![stack](https://img.shields.io/badge/backend-C%23%20ASP.NET%20Core%20%2B%20SQLite-4ea5ff)

## Wat zit erin

| Onderdeel | Wat je ermee doet |
| --- | --- |
| **Dashboard** | Omzet, open uren klaar om te factureren, openstaande facturen, btw van dit kwartaal (met de aangiftedatum), uren deze week, je voortgang naar het urencriterium (1.225 uur), lopende projecten, leads en wat er gepland staat. |
| **Uren** | Start/stop-timer (loopt door als je de pagina ververst en staat altijd rechts in de tabbalk), weekoverzicht per project en dag, en uren handmatig boeken als `1:30` of `1,5`. Gefactureerde uren liggen vast. |
| **Projecten** | Per klant, per uur of met een vaste prijs, met budget in uren, deadline en repository. Je ziet de budgetbalk, open uren en bij vaste prijs je effectieve uurtarief. Met *Factureer* zet je de open uren op een conceptfactuur: één regel of één regel per boeking. |
| **Planning** | Weekkalender (klik op een leeg vak om iets in te plannen) en een takenlijst om af te vinken. |
| **Klanten** | Klantenlijst met zoeken. Elke klant opent in een eigen tabblad met de tabjes Overzicht, Projecten, Facturen, Planning en Notities. |
| **Leads** | Kanbanbord: sleep leads van Nieuw naar Contact, Offerte, Gewonnen of Verloren. Met één klik maak je van een lead een klant. |
| **Facturen** | Overzicht met wat openstaat, wat te laat is, concepten en wat je dit jaar ontving, plus per factuur hoeveel dagen hij nog loopt of te laat is. Regels met eenheid (uur, dag, stuk, maand), referentie van de klant, leverdatum of periode (vult zich vanzelf uit de uren), open uren van de klant met één klik erbij en dupliceren. Volgens de regels van de Belastingdienst: alleen een concept kun je nog aanpassen of verwijderen. *Versturen* controleert eerst of alle verplichte gegevens er zijn, geeft dan het volgende nummer per jaar zonder gaten en legt jouw gegevens en die van je klant vast; daarna gaat alleen de status nog vooruit en herstel je een fout met een creditnota die vast naar de oorspronkelijke factuur verwijst. Btw per factuur: normaal, verlegd (alleen voor zakelijke klanten in een ander EU-land, met beide btw-nummers), KOR of buiten de EU, met de juiste vermelding erbij. Server en factuur ronden op dezelfde manier af. Na versturen staat er een mail klaar; bij te late facturen een herinneringsmail. Strakke A4-layout met betaalblok, die je als PDF opslaat. |
| **Instellingen** | Je bedrijfsgegevens voor op de factuur, je standaard uurtarief, betaaltermijn en je doelen in uren per week en per jaar. |
| **Werkstromen** | Een canvas zoals in n8n: zet blokken neer, stel ze in en verbind ze door te slepen. Staat een werkstroom aan, dan start hij vanzelf bij zijn trigger en voert hij de acties echt uit. Zie hieronder. |
| **Gebruikers** | Iedereen logt in met e-mail en wachtwoord. Beheerders voegen gebruikers toe, kiezen hun rol en kunnen ze uitschakelen. |
| **Eigen modules** | Maak zelf een lijst met je eigen velden (tekst, getal, datum, ja/nee). Standaard staan er *Servers & domeinen* en *Licenties* in. Een nieuwe module verschijnt direct in het menu. |
| **Verkooppagina** | Op `/` voor bezoekers: uitleg, functies, prijzen en veelgestelde vragen. Prijzen en je contactadres pas je aan in `frontend/src/lib/plans.ts`. |
| **Aanmelden** | Op `/aanmelden` maakt een klant zelf een werkruimte aan, eventueel met voorbeelddata, en doorloopt een welkomstwizard (bedrijf, tarief, huisstijl, eerste klant). |
| **Huisstijl** | Elke klant uploadt een logo en kiest een accentkleur; die komen op de facturen. |
| **Abonnement** | Proef (30 dagen), ZZP of Team. Na de proef is de werkruimte alleen-lezen tot er een abonnement is gekozen. Overstappen gaat nu via een mail naar jou; jij zet het om op *Platform*. |
| **Account** | Beheerders exporteren alle gegevens als JSON of verwijderen hun hele werkruimte. |
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
| `DatabasePath`, `KeysPath` | Waar de SQLite-database en de cookiesleutels staan (standaard in `/data`). |
| `RateLimit__AuthPerMinute` | Hoe vaak per minuut één IP-adres mag inloggen of aanmelden (standaard 10). Daarnaast geldt een grens per e-mailadres, en op aanmelden een grens per netwerk en voor het hele platform. |
| `Smtp__*` | Mailserver voor de werkstroomblokken die e-mail sturen. |

Voordat je echt verkoopt, regel je nog:

- **Betalen**: online betalen (bijvoorbeeld Stripe of Mollie) zit er nog niet in. Klanten mailen nu om over te stappen en jij zet het abonnement om op *Platform*.
- **Juridisch**: algemene voorwaarden, een privacyverklaring en een verwerkersovereenkomst; je verwerkt immers gegevens van de klanten van je klanten.
- **Wachtwoord vergeten**: werkt nog niet via e-mail; een beheerder van de werkruimte kan een nieuw wachtwoord zetten bij *Gebruikers*.
- **Back-ups** van het volume `/data`.

## Werkstromen

Een werkstroom begint met een **trigger** en loopt via de lijnen langs de blokken.

| Blok | Wat het doet |
| --- | --- |
| Nieuwe lead / Nieuwe klant / Factuur betaald | Start vanzelf als dat in het portaal gebeurt (alleen als de werkstroom aan staat). |
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

**E-mail versturen:** vul in `appsettings.json` de sectie `Smtp` in (server, poort, gebruiker, wachtwoord, afzender). Zolang er geen mailserver is ingesteld, slaat het blok de mail over en staat er een melding in de uitvoering. Alle mail gaat van jouw afzenderadres (of de gebruiker als `From` leeg is), met de bedrijfsnaam van de werkruimte als naam; antwoorden gaan naar het e-mailadres onder *Instellingen*. Per werkruimte geldt een daglimiet: 20 tijdens de proef, 200 met een abonnement. Is die op, dan slaat het blok de mail over met een melding en loopt de werkstroom gewoon verder.

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
  Workflows/     de motor die werkstromen uitvoert, plus de planner op de achtergrond
backend/Potel.Api.Tests/   tests voor inloggen, werkruimtes (afscherming, proef, platform), werkstromen, uren en facturen
frontend/src/
  components/    menu, tabbladen, zoekvenster en losse UI-onderdelen
  pages/         één bestand per scherm
  lib/           API-client, tabbladen, opmaak van bedragen en datums
```

De API-documentatie (Swagger) staat op http://localhost:5080/swagger zolang de backend in ontwikkelmodus draait.


Een database uit de allereerste versie wordt bij het starten bewaard als `potel.db.<datum>.bak` en vervangen door een nieuwe met het juiste schema.
