# Potel · Portaal voor freelance developers

Een portaal voor een zzp'er in softwareontwikkeling: uren schrijven met een timer, projecten per klant, open uren met één klik factureren, en daarnaast planning, klanten, leads, eigen modules en werkstromen in de stijl van n8n.

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
| **Facturen** | Overzicht met wat openstaat, wat te laat is, concepten en wat je dit jaar ontving, plus per factuur hoeveel dagen hij nog loopt of te laat is. Regels met eenheid (uur, dag, stuk, maand), referentie van de klant, btw verlegd, open uren van de klant met één klik erbij, dupliceren en creditnota's. *Versturen* zet een mail klaar en markeert de factuur als verzonden; bij te late facturen staat er een herinneringsmail klaar. Strakke A4-layout met betaalblok, die je als PDF opslaat. |
| **Instellingen** | Je bedrijfsgegevens voor op de factuur, je standaard uurtarief, betaaltermijn en je doelen in uren per week en per jaar. |
| **Werkstromen** | Een canvas zoals in n8n: zet blokken neer, stel ze in en verbind ze door te slepen. Staat een werkstroom aan, dan start hij vanzelf bij zijn trigger en voert hij de acties echt uit. Zie hieronder. |
| **Gebruikers** | Iedereen logt in met e-mail en wachtwoord. Beheerders voegen gebruikers toe, kiezen hun rol en kunnen ze uitschakelen. |
| **Eigen modules** | Maak zelf een lijst met je eigen velden (tekst, getal, datum, ja/nee). Standaard staan er *Servers & domeinen* en *Licenties* in. Een nieuwe module verschijnt direct in het menu. |

Verder: alles opent als **tabblad** bovenin (dubbelklik op een tabblad sluit de andere), **Ctrl K** om overal te zoeken, en een licht (standaard) en donker thema.

## Starten

Je hebt nodig: [.NET 8 SDK](https://dotnet.microsoft.com/download) en [Node.js 20+](https://nodejs.org).

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

Bij de eerste start wordt een beheerder aangemaakt:

- E-mail: `admin@potel.nl`
- Wachtwoord: `welkom123`

**Wijzig dit wachtwoord meteen** (klik linksonder op je naam). Je kunt de eerste beheerder ook vooraf instellen in `backend/Potel.Api/appsettings.json` onder `Admin`. Medewerkers kunnen alles behalve gebruikers beheren.

Wil je opnieuw beginnen met de voorbeelddata? Stop de backend en verwijder `backend/Potel.Api/potel.db`.

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
| E-mail sturen | Stuurt een e-mail (zie hieronder). |
| Webhook | Stuurt alle gegevens als JSON naar een andere app. |
| Wachten | Wacht minuten, uren of dagen en gaat dan verder, ook als de server tussendoor herstart. |
| Als / dan | Kiest de Ja- of Nee-uitgang op basis van een veld, bijvoorbeeld `lead.value` groter dan 5000. |

In tekstvelden kun je gegevens invoegen met dubbele accolades, zoals `{{lead.name}}` of `{{invoice.number}}`. Onder *Uitvoeringen* zie je per run welke blokken liepen en wat er gebeurde.

**E-mail versturen:** vul in `appsettings.json` de sectie `Smtp` in (server, poort, gebruiker, wachtwoord, afzender). Zolang er geen mailserver is ingesteld, slaat het blok de mail over en staat er een melding in de uitvoering.

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
backend/Potel.Api.Tests/   tests voor inloggen, werkstromen en uren factureren
frontend/src/
  components/    menu, tabbladen, zoekvenster en losse UI-onderdelen
  pages/         één bestand per scherm
  lib/           API-client, tabbladen, opmaak van bedragen en datums
```

De API-documentatie (Swagger) staat op http://localhost:5080/swagger zolang de backend in ontwikkelmodus draait.

## Nog niet ingebouwd

- Wachtwoord vergeten via e-mail: een beheerder zet een nieuw wachtwoord bij *Gebruikers*.

Een database uit de allereerste versie wordt bij het starten bewaard als `potel.db.<datum>.bak` en vervangen door een nieuwe met het juiste schema.
