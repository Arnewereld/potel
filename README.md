# Potel · Bedrijvenportaal

Een portaal waarin een bedrijf zijn planning, facturen, klanten en leads bijhoudt, met eigen modules en werkstromen in de stijl van n8n.

![stack](https://img.shields.io/badge/frontend-Vite%20%2B%20React%20%2B%20TypeScript-ff6d5a) ![stack](https://img.shields.io/badge/backend-C%23%20ASP.NET%20Core%20%2B%20SQLite-4ea5ff)

## Wat zit erin

| Onderdeel | Wat je ermee doet |
| --- | --- |
| **Dashboard** | Omzet per maand, openstaande facturen, pipeline-waarde, wat er binnenkort gepland staat en recente activiteit. |
| **Planning** | Weekkalender (klik op een leeg vak om iets in te plannen) en een takenlijst om af te vinken. |
| **Klanten** | Klantenlijst met zoeken. Elke klant opent in een eigen tabblad met de tabjes Overzicht, Facturen, Planning en Notities. |
| **Leads** | Kanbanbord: sleep leads van Nieuw naar Contact, Offerte, Gewonnen of Verloren. Met één klik maak je van een lead een klant. |
| **Facturen** | Facturen met regels, btw (21%, 9% of 0%), automatische nummering, een live voorbeeld en afdrukken/opslaan als PDF. Verzonden facturen voorbij de vervaldatum worden vanzelf "verlopen". |
| **Werkstromen** | Een canvas zoals in n8n: zet blokken neer (triggers, acties, logica), verbind ze door te slepen en klik op *Testen* om de stroom te zien lopen. |
| **Eigen modules** | Maak zelf een lijst met je eigen velden (tekst, getal, datum, ja/nee), bijvoorbeeld voertuigen, voorraad of contracten. Hij verschijnt direct in het menu. |

Verder: alles opent als **tabblad** bovenin (dubbelklik op een tabblad sluit de andere), **Ctrl K** om overal te zoeken, en een licht en donker thema.

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

Wil je opnieuw beginnen met de voorbeelddata? Stop de backend en verwijder `backend/Potel.Api/potel.db`.

## Opbouw

```
backend/Potel.Api/
  Data/          modellen, database (EF Core + SQLite) en voorbeelddata
  Endpoints/     API per onderdeel: klanten, leads, facturen, planning, modules, werkstromen
frontend/src/
  components/    menu, tabbladen, zoekvenster en losse UI-onderdelen
  pages/         één bestand per scherm
  lib/           API-client, tabbladen, opmaak van bedragen en datums
```

De API-documentatie (Swagger) staat op http://localhost:5080/swagger zolang de backend in ontwikkelmodus draait.

## Nog niet ingebouwd

- Inloggen en meerdere gebruikers.
- Werkstromen worden ontworpen en getest op het canvas, maar voeren nog geen echte acties uit (zoals e-mail sturen).
- Je bedrijfsgegevens op de factuur (via *Mijn gegevens*) worden in de browser bewaard, niet in de database.
