using Microsoft.EntityFrameworkCore;

namespace Potel.Api.Data;

public static class Seed
{
    static void SampleCompany(Settings settings)
    {
        settings.CompanyName = "Pixelwerk Development";
        settings.OwnerName = "Jouw naam";
        settings.Address = "Keizersgracht 100";
        settings.City = "1015 AA Amsterdam";
        settings.Email = "hallo@pixelwerk.dev";
        settings.Website = "pixelwerk.dev";
        settings.Kvk = "12345678";
        settings.Btw = "NL001234567B01";
        settings.Iban = "NL00 BANK 0123 4567 89";
    }

    // Eerste start: maakt een werkruimte met een beheerder om mee in te loggen, plus voorbeelddata als dat aan staat.
    public static void Bootstrap(AppDb db, IConfiguration config, ILogger logger)
    {
        // Zonder ingestelde beheerder (zoals in productie) maken klanten zelf hun account aan.
        if (db.Users.IgnoreQueryFilters().Any() || string.IsNullOrWhiteSpace(config["Admin:Email"]) || string.IsNullOrWhiteSpace(config["Admin:Password"])) return;
        var ws = new Workspace { Name = "Pixelwerk Development", Plan = Plans.Team, OnboardedAt = DateTime.UtcNow };
        db.Workspaces.Add(ws);
        db.SaveChanges();
        db.Tenant.WorkspaceId = ws.Id;

        var email = config["Admin:Email"]!.Trim().ToLower();
        var admin = new User
        {
            Name = config["Admin:Name"] ?? "Beheerder",
            Email = email,
            Role = Roles.Admin,
            // Het account uit de instellingen is van de eigenaar zelf, dus dat mag het platform beheren en telt als bevestigd.
            IsPlatformAdmin = ConfiguredPlatformAdmins(config).Contains(email),
            EmailVerifiedAt = DateTime.UtcNow,
        };
        var password = config["Admin:Password"]!;
        admin.PasswordHash = Endpoints.AuthEndpoints.Hash(admin, password);
        db.Users.Add(admin);
        db.SaveChanges();
        logger.LogWarning("Beheerder aangemaakt: {Email}. Wijzig het wachtwoord na de eerste keer inloggen.", admin.Email);
        if (config.GetValue("SeedDemoData", true)) Run(db, sampleCompany: true);
    }

    static string[] ConfiguredPlatformAdmins(IConfiguration config) =>
        (config.GetSection("PlatformAdmins").Get<string[]>() ?? []).Select(a => a.Trim().ToLower()).Where(a => a != "").ToArray();

    // Wie het platform beheert staat in de database, niet in een e-mailadres dat iedereen kan aanmaken. Bij het opstarten
    // krijgt niemand dit recht vanzelf: wie zich als eerste aanmeldt met het adres van de eigenaar, is daarmee nog niet de eigenaar.
    // Alleen het account uit Admin:Email (met het wachtwoord uit de instellingen) of de opdracht platform-admin op de server geeft het.
    public const string PlatformAdminCommand = "platform-admin";

    public static void PlatformAdmins(AppDb db, ILogger logger)
    {
        if (db.Users.IgnoreQueryFilters().Any(u => u.IsPlatformAdmin)) return;
        logger.LogWarning("Er is nog geen platformbeheerder. Meld je aan en maak jezelf daarna platformbeheerder op de server met: " +
                          "dotnet Potel.Api.dll {Command} <e-mailadres> (in Docker: docker compose exec potel dotnet Potel.Api.dll {Command} <e-mailadres>)",
            PlatformAdminCommand, PlatformAdminCommand);
    }

    // De opdracht "platform-admin <e-mailadres>": alleen wie bij de server kan, maakt zo een account platformbeheerder.
    public static bool MakePlatformAdmin(AppDb db, string email, ILogger logger)
    {
        email = email.Trim().ToLower();
        var user = db.Users.IgnoreQueryFilters().FirstOrDefault(u => u.Email == email);
        if (user is null)
        {
            logger.LogError("Er is geen account met e-mailadres {Email}. Meld je eerst aan via /aanmelden.", email);
            return false;
        }
        user.IsPlatformAdmin = true;
        db.SaveChanges();
        logger.LogWarning("{Email} is nu platformbeheerder. Andere platformbeheerders wijs je aan op de pagina Platform.", user.Email);
        return true;
    }

    public const string ResetTwoFactorCommand = "reset-two-factor";

    // De opdracht "reset-two-factor <e-mailadres>": voor wie zijn telefoon en herstelcodes kwijt is, ook de laatste beheerder of de
    // platformbeheerder. Alleen wie bij de server kan, doet dit. Alle sessies van dat account stoppen.
    public static bool ResetTwoFactor(AppDb db, string email, ILogger logger)
    {
        email = email.Trim().ToLower();
        var user = db.Users.IgnoreQueryFilters().FirstOrDefault(u => u.Email == email);
        if (user is null)
        {
            logger.LogError("Er is geen account met e-mailadres {Email}.", email);
            return false;
        }
        db.Tenant.WorkspaceId = user.WorkspaceId;
        user.TwoFactorSecret = null;
        user.TwoFactorPendingSecret = null;
        user.TwoFactorEnabledAt = null;
        user.TwoFactorLastStep = null;
        user.NewSecurityStamp();
        db.RecoveryCodes.RemoveRange(db.RecoveryCodes.Where(r => r.UserId == user.Id));
        db.Log("gebruiker", $"Inloggen in twee stappen van {user.Name} uitgezet op de server");
        db.SaveChanges();
        logger.LogWarning("Inloggen in twee stappen staat nu uit voor {Email}. Log in met alleen je wachtwoord en zet het daarna opnieuw aan.", user.Email);
        return true;
    }

    public const string DemoNumberPrefix = "VOORBEELD-";

    // Voorbeeldoffertes in elke stand: een concept, twee openstaande, een geaccepteerde (met project en aanbetaling), een
    // afgewezen en een verlopen offerte. Ze hebben een eigen reeks (VOORBEELD-OFF-001), zodat je echte offertes bij 001 beginnen.
    static void DemoQuotes(AppDb db, Settings settings, DateTime today, List<Customer> customers, Lead lisa, Lead eva, Lead daan, Lead julia,
        Project app, Invoice depositInvoice)
    {
        static QuoteLine Line(int position, string description, decimal quantity, string unit, decimal price) =>
            new() { Position = position, Description = description, Quantity = quantity, Unit = unit, UnitPrice = price, VatRate = 21 };
        const string terms = "Prijzen zijn exclusief btw. Uren boven de schatting meld ik vooraf. Facturen betaal je binnen 14 dagen. "
                             + "De broncode wordt jouw eigendom na betaling van de laatste factuur.";
        DateTime Utc(DateTime day, int hour) => DateTime.SpecifyKind(day.AddHours(hour - 2), DateTimeKind.Utc);
        var accepted = depositInvoice.IssueDate.AddDays(-5);

        var quotes = new List<Quote>
        {
            new()
            {
                LeadId = lisa.Id, IssueDate = today, ValidUntil = today.AddDays(30), Subject = "Nieuwe website met headless CMS", DepositPercent = 30, Terms = terms,
                Intro = "Naar aanleiding van ons gesprek stuur ik je graag een voorstel voor de nieuwe website van Visser Media.",
                Lines = [Line(0, "Ontwerp omzetten naar componenten", 16, "uur", 95), Line(1, "Website bouwen in Next.js met headless CMS", 40, "uur", 95), Line(2, "Hosting en onderhoud, eerste jaar", 12, "maand", 25)],
            },
            new()
            {
                LeadId = julia.Id, IssueDate = today.AddDays(-48), ValidUntil = today.AddDays(-18), Status = QuoteStatus.Rejected,
                Subject = "Onderhoud WordPress-website", Terms = terms, DecidedAt = Utc(today.AddDays(-41), 15),
                RejectReason = "Dank je wel voor de offerte. Voor nu is het te veel voor ons budget; we doen het onderhoud voorlopig zelf.",
                Lines = [Line(0, "Updates en beveiligingspatches", 12, "maand", 75), Line(1, "Kleine wijzigingen, strippenkaart", 10, "uur", 85)],
            },
            new()
            {
                LeadId = eva.Id, IssueDate = today.AddDays(-45), ValidUntil = today.AddDays(-15), Status = QuoteStatus.Sent, Subject = "API-koppeling met Moneybird", Terms = terms,
                Intro = "Hierbij mijn voorstel voor de koppeling tussen jullie urenregistratie en Moneybird.",
                Lines = [Line(0, "Analyse en technisch ontwerp", 6, "uur", 95), Line(1, "Koppeling bouwen en testen", 32, "uur", 95), Line(2, "Overdracht en documentatie", 4, "uur", 95)],
            },
            new()
            {
                CustomerId = customers[1].Id, IssueDate = accepted.AddDays(-9), ValidUntil = accepted.AddDays(21), Status = QuoteStatus.Accepted, Subject = "Planningsapp (React Native)",
                DepositPercent = 50, Terms = terms, DecidedAt = Utc(accepted, 11), DecidedName = "Mark Jansen", DecidedIp = "192.0.2.24",
                Intro = "Zoals besproken bouw ik de planningsapp voor jullie chauffeurs voor een vaste prijs, met offline modus en pushmeldingen.",
                Lines = [Line(0, "Planningsapp voor Android en iOS, vaste prijs", 1, "stuk", 12500), Line(1, "Beheeromgeving voor de planners", 1, "stuk", 2000)],
                ProjectId = app.Id, DepositInvoiceId = depositInvoice.Id,
            },
            new()
            {
                LeadId = daan.Id, IssueDate = today.AddDays(-12), ValidUntil = today.AddDays(2), Status = QuoteStatus.Sent, Subject = "Shopify-app op maat", DepositPercent = 25, Terms = terms,
                Intro = "Hierbij mijn offerte voor de Shopify-app waarmee jullie winkels de voorraad onderling zien.",
                Lines = [Line(0, "App bouwen met Shopify Admin API", 60, "uur", 95), Line(1, "Testen met jullie winkels en livegang", 12, "uur", 95), Line(2, "Licentie Shopify Partner-app, eerste jaar", 1, "jaar", 240)],
            },
            new()
            {
                CustomerId = customers[0].Id, IssueDate = today.AddDays(-5), ValidUntil = today.AddDays(25), Status = QuoteStatus.Sent, Subject = "Retourportaal voor de webshop", Terms = terms,
                Intro = "Hierbij de offerte voor het retourportaal, gekoppeld aan Exact Online, zoals we vorige week bespraken.",
                Lines = [Line(0, "Retourportaal bouwen", 28, "uur", 95), Line(1, "Koppeling met Exact Online uitbreiden", 8, "uur", 95)],
            },
            // In het Engels voor de Ierse klant, met btw verlegd: daarom zonder btw op de regels.
            new()
            {
                CustomerId = customers[4].Id, IssueDate = today.AddDays(-3), ValidUntil = today.AddDays(27), Status = QuoteStatus.Sent, Subject = "Webhooks and reporting module",
                VatRegime = VatRegimes.ReverseCharge, Language = Languages.English, DepositPercent = 30,
                Terms = "Prices exclude VAT; VAT is reverse charged to you. Hours above the estimate are discussed in advance. Invoices are payable within 14 days.",
                Intro = "Following our call last week, here is my proposal for the webhooks and reporting module of the payments platform.",
                Lines =
                [
                    Line(0, "Webhook delivery with retries and signing", 24, "uur", 110),
                    Line(1, "Reporting dashboard in React", 32, "uur", 110),
                    Line(2, "Hosting and monitoring, first year", 12, "maand", 40),
                ],
            },
        };
        foreach (var line in quotes.Where(q => VatRegimes.ZeroVat(q.VatRegime)).SelectMany(q => q.Lines)) line.VatRate = 0;
        var n = 0;
        foreach (var q in quotes.OrderBy(q => q.IssueDate))
        {
            q.IsDemo = true;
            q.CreatedAt = DateTime.SpecifyKind(q.IssueDate, DateTimeKind.Utc);
            if (q.Status == QuoteStatus.Draft) continue;
            q.Number = $"{Endpoints.QuoteEndpoints.DemoNumberPrefix}{++n:000}";
            q.SentAt = Utc(q.IssueDate, 10);
            q.Seller = InvoiceParty.Seller(settings);
            q.Buyer = q.CustomerId is { } customerId ? InvoiceParty.Buyer(customers.First(c => c.Id == customerId))
                : Endpoints.QuoteEndpoints.BuyerOf(new[] { eva, daan, julia }.First(l => l.Id == q.LeadId));
        }
        db.Quotes.AddRange(quotes);
        db.SaveChanges();
    }

    // Vult de huidige werkruimte met voorbeelddata. Met sampleCompany ook voorbeeld-bedrijfsgegevens.
    // Alles krijgt IsDemo, zodat je het later in één keer weghaalt (WorkspaceEndpoints, voorbeelddata verwijderen).
    public static void Run(AppDb db, bool sampleCompany)
    {
        if (db.Customers.Any()) return;
        if (db.Workspaces.Find(db.TenantId) is { } workspace) workspace.DemoDataAt = DateTime.UtcNow;

        var settings = db.Settings.OrderBy(x => x.Id).FirstOrDefault() ?? db.Settings.Add(new Settings()).Entity;
        if (sampleCompany) SampleCompany(settings);

        var customers = new List<Customer>
        {
            new() { Name = "Sanne de Vries", Company = "Fietsplein B.V.", VatNumber = "NL812345678B01", Email = "sanne@fietsplein.nl", Phone = "06 12345678", Address = "Oudegracht 12", City = "Utrecht", Notes = "Webshop op Next.js. Voorraad en orders lopen via Exact Online." },
            new() { Name = "Mark Jansen", Company = "Jansen Logistiek", Email = "mark@jansenlogistiek.nl", Phone = "06 23456789", Address = "Industrieweg 4", City = "Rotterdam", Notes = "Chauffeurs gebruiken de planningsapp op Android." },
            new() { Name = "Fatima El Amrani", Company = "Studio Noord", VatNumber = "NL860011223B01", Email = "fatima@studionoord.nl", Phone = "06 34567890", Address = "Noordkade 88", City = "Amsterdam", Notes = "Designbureau; ik bouw hun ontwerpen onder hun naam." },
            new() { Name = "Peter Bakker", Company = "Zorgnet Oost", Email = "peter@zorgnetoost.nl", Phone = "06 45678901", Address = "Hengelosestraat 1", City = "Enschede", Notes = "Onderhoudscontract voor het cliëntportaal (.NET + Angular)." },
            // Een Engelstalige klant in Ierland: facturen, offertes en mails in het Engels, met btw verlegd.
            new()
            {
                Name = "Aoife Murphy", Company = "Northwind Labs Ltd", VatNumber = "IE6388047V", Email = "aoife@northwindlabs.ie", Phone = "+353 1 555 0142",
                Address = "12 Grand Canal Quay", City = "Dublin 2, D02 X576", Country = "Ierland", Language = Languages.English,
                Notes = "SaaS-bedrijf in Dublin. Alles in het Engels; btw verlegd want ze hebben een Iers btw-nummer.",
            },
        };
        customers.ForEach(c => c.IsDemo = true);
        // Ritten naar Fietsplein worden doorbelast.
        customers[0].KilometreRate = 0.30m;
        db.Customers.AddRange(customers);
        db.SaveChanges();

        var today = new BusinessClock().Today;
        var projects = new List<Project>
        {
            new() { Name = "Exact Online-koppeling", CustomerId = customers[0].Id, Billing = Billing.Hourly, HourlyRate = 95, BudgetHours = 160, Color = "#4ea5ff", RepoUrl = "https://github.com/fietsplein/exact-sync", Description = "Orders, voorraad en klanten synchroniseren tussen de webshop en Exact Online." },
            new() { Name = "Planningsapp (React Native)", CustomerId = customers[1].Id, Billing = Billing.Fixed, FixedPrice = 14500, BudgetHours = 160, Color = "#3ecf8e", Deadline = today.AddDays(40), Description = "Rittenplanning voor chauffeurs met offline modus en pushmeldingen." },
            new() { Name = "White-label development", CustomerId = customers[2].Id, Billing = Billing.Hourly, HourlyRate = 85, Color = "#9b7bff", Description = "Frontend bouwen van Figma-ontwerpen voor klanten van Studio Noord." },
            new() { Name = "Onderhoud cliëntportaal", CustomerId = customers[3].Id, Billing = Billing.Hourly, HourlyRate = 90, BudgetHours = 400, Color = "#f5b83d", RepoUrl = "https://dev.azure.com/zorgnetoost/portaal", Description = "Updates, beveiligingspatches en kleine wijzigingen." },
            new() { Name = "Migratie naar Next.js", CustomerId = customers[0].Id, Status = "afgerond", Billing = Billing.Hourly, HourlyRate = 95, BudgetHours = 80, Color = "#ff5ca8", Description = "Oude WooCommerce-shop omgezet naar Next.js." },
        };
        // Het project voor de Ierse klant loopt sinds een paar maanden; zijn uren komen hieronder apart.
        var payments = new Project
        {
            Name = "Payments API", CustomerId = customers[4].Id, Billing = Billing.Hourly, HourlyRate = 110, BudgetHours = 200, Color = "#2fc6c6",
            Description = "REST-API in Go voor betalingen in het SaaS-platform van Northwind Labs.",
        };
        projects.ForEach(p => p.IsDemo = true);
        payments.IsDemo = true;
        db.Projects.AddRange(projects);
        db.Projects.Add(payments);
        db.SaveChanges();

        // Uren vanaf januari: elke werkdag een paar blokken verdeeld over de projecten die toen liepen.
        var work = new Dictionary<int, string[]>
        {
            [projects[0].Id] = ["Orders-sync bouwen", "Webhook-handler voor voorraad", "Bugfix dubbele klanten", "Tests voor de sync-jobs", "Overleg met Sanne", "Logging en monitoring"],
            [projects[1].Id] = ["Schermen ritoverzicht", "Offline opslag met SQLite", "Pushmeldingen", "Sprint review", "Kaartweergave", "Build voor Play Store"],
            [projects[2].Id] = ["Landingspagina bouwen", "Componenten uit Figma", "Responsive fixes", "Animaties", "Review met designer"],
            [projects[3].Id] = ["Beveiligingsupdates .NET", "Ticket: exportfunctie", "Angular-upgrade", "Storing onderzoeken", "Releasevoorbereiding"],
            [projects[4].Id] = ["Productpagina's migreren", "Checkout bouwen", "SEO-redirects", "Livegang begeleiden"],
        };
        var rnd = new Random(11);
        var entries = new List<TimeEntry>();
        // De voorbeelduren zijn geboekt door de eerste gebruiker van de werkruimte.
        var booker = db.Users.OrderBy(u => u.Id).Select(u => (int?)u.Id).FirstOrDefault();
        for (var d = new DateTime(today.Year, 1, 2); d < today; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || rnd.Next(12) == 0) continue;
            var running = projects.Where(p => p.Status == "actief" ? d >= today.AddMonths(-5) || p == projects[3] : d < today.AddMonths(-5)).ToList();
            if (running.Count == 0) running = [projects[3]];
            var blocks = rnd.Next(2, 4);
            for (var b = 0; b < blocks; b++)
            {
                var p = running[rnd.Next(running.Count)];
                var texts = work[p.Id];
                entries.Add(new TimeEntry
                {
                    ProjectId = p.Id, Date = d, Minutes = rnd.Next(4, 11) * 15, Description = texts[rnd.Next(texts.Length)],
                    Billable = p.Billing == Billing.Hourly, IsDemo = true, UserId = booker,
                });
            }
        }
        // De Ierse klant: de laatste drie maanden op een deel van de werkdagen een halve of hele dag.
        var irish = new Random(7);
        string[] apiWork = ["Payment intents endpoint", "Webhook signing", "Idempotency keys", "Load tests", "Call with Aoife", "OpenAPI docs"];
        for (var d = today.AddMonths(-3); d < today; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || irish.Next(3) != 0) continue;
            entries.Add(new TimeEntry
            {
                ProjectId = payments.Id, Date = d, Minutes = irish.Next(4, 9) * 60, Description = apiWork[irish.Next(apiWork.Length)],
                Billable = true, IsDemo = true, UserId = booker,
            });
        }
        projects.Add(payments);
        db.TimeEntries.AddRange(entries);
        db.SaveChanges();

        // Ritten: elke week een keer naar Fietsplein in Utrecht voor de Exact-koppeling (doorbelast), elke maand een sprint review
        // bij Jansen Logistiek in Rotterdam (vaste prijs, dus niet doorbelast) en een paar privéritten. De doorbelaste ritten van
        // een afgelopen maand staan op de maandfactuur; die van deze maand staan nog open.
        const string office = "Keizersgracht 100, Amsterdam";
        const string utrecht = "Oudegracht 12, Utrecht";
        const string rotterdam = "Industrieweg 4, Rotterdam";
        var trips = new List<Trip>();
        void Drive(DateTime date, string from, string to, decimal km, string? purpose, string kind, Project? project = null, bool billable = false, bool back = true)
        {
            Trip Leg(string a, string b) => new()
            {
                IsDemo = true, Date = date, From = a, To = b, Kilometres = km, Purpose = purpose, Kind = kind, ProjectId = project?.Id,
                CustomerId = project?.CustomerId, Billable = billable, UserId = booker,
            };
            trips.Add(Leg(from, to));
            if (back) trips.Add(Leg(to, from));
        }
        static int Week(DateTime d) => System.Globalization.ISOWeek.GetYear(d) * 100 + System.Globalization.ISOWeek.GetWeekOfYear(d);
        foreach (var week in entries.Where(e => e.ProjectId == projects[0].Id).Select(e => e.Date).Distinct().GroupBy(Week))
            Drive(week.Min(), office, utrecht, 45, "Overleg en werken op locatie bij Fietsplein", TripKinds.Business, projects[0], billable: true);
        foreach (var month in entries.Where(e => e.ProjectId == projects[1].Id).Select(e => e.Date).Distinct().GroupBy(d => d.Month))
            Drive(month.Min(), office, rotterdam, 78, "Sprint review planningsapp", TripKinds.Business, projects[1]);
        foreach (var (m, d) in new[] { (3, 8), (6, 14), (8, 2) })
            if (new DateTime(today.Year, m, d) < today) Drive(new DateTime(today.Year, m, d), office, "Boulevard Paulus Loot, Zandvoort", 29, "Dagje strand", TripKinds.Private);
        db.Trips.AddRange(trips);
        db.TripRoutes.AddRange(
            new TripRoute { IsDemo = true, Name = "Fietsplein Utrecht", From = office, To = utrecht, Kilometres = 45, Purpose = "Overleg en werken op locatie bij Fietsplein", ProjectId = projects[0].Id, CustomerId = customers[0].Id, Billable = true },
            new TripRoute { IsDemo = true, Name = "Jansen Logistiek Rotterdam", From = office, To = rotterdam, Kilometres = 78, Purpose = "Sprint review planningsapp", ProjectId = projects[1].Id, CustomerId = customers[1].Id });
        db.SaveChanges();

        // Uurprojecten worden per maand gefactureerd; de lopende maand staat nog open. Verstuurde facturen hebben een nummer
        // in een eigen reeks (VOORBEELD-0001), zodat je echte facturen gewoon bij 0001 beginnen, de gegevens van beide partijen
        // zoals ze toen waren en een leverperiode; de uren hangen aan hun factuurregel.
        var invoices = new List<(Invoice Invoice, List<TimeEntry> Entries, List<Trip> Trips)>();
        var thisMonth = new DateTime(today.Year, today.Month, 1);
        foreach (var month in entries.Where(e => e.Billable && e.Date < thisMonth).GroupBy(e => (e.ProjectId, new DateTime(e.Date.Year, e.Date.Month, 1))))
        {
            var p = projects.First(x => x.Id == month.Key.ProjectId);
            var issue = month.Key.Item2.AddMonths(1);
            var minutes = month.Sum(e => e.Minutes);
            var driven = trips.Where(t => t.Billable && t.ProjectId == p.Id && t.Date >= month.Key.Item2 && t.Date < issue).ToList();
            var km = driven.Sum(t => t.Kilometres);
            var customer = customers.First(c => c.Id == p.CustomerId);
            var rate = customer.KilometreRate ?? 0;
            // De Ierse klant krijgt Engelse facturen met btw verlegd.
            var lang = customer.Language;
            var regime = Countries.IsNetherlands(customer.Country) ? VatRegimes.Normal : VatRegimes.ReverseCharge;
            var vat = VatRegimes.ZeroVat(regime) ? 0 : 21;
            var worked = $"{Invoicing.ClientTexts.Month(lang, issue.AddMonths(-1).Month)} {issue.AddMonths(-1).Year}";
            var hoursText = Invoicing.ClientTexts.IsEnglish(lang) ? $"{p.Name}: work in {worked}" : $"{p.Name}: werkzaamheden {worked}";
            invoices.Add((new Invoice
            {
                CustomerId = p.CustomerId, IssueDate = issue, DueDate = issue.AddDays(14), VatRegime = regime, Language = lang,
                Status = issue < thisMonth ? InvoiceStatus.Paid : InvoiceStatus.Sent, PaidAt = issue < thisMonth ? issue.AddDays(9) : null, Reference = p.Id == projects[3].Id ? "PO-2026-0412" : null, Notes = Endpoints.SettingsEndpoints.Note(lang),
                DeliveryFrom = month.Min(e => e.Date), DeliveryTo = month.Max(e => e.Date),
                Lines = driven.Count == 0
                    ? [new InvoiceLine { Description = hoursText, Quantity = Endpoints.ProjectEndpoints.Hours(minutes), Unit = "uur", UnitPrice = p.HourlyRate, VatRate = vat }]
                    : [
                        new InvoiceLine { Description = hoursText, Quantity = Endpoints.ProjectEndpoints.Hours(minutes), Unit = "uur", UnitPrice = p.HourlyRate, VatRate = vat, Position = 0 },
                        new InvoiceLine { Description = Endpoints.TripEndpoints.LineText(km, rate, lang), Quantity = km, Unit = "km", UnitPrice = rate, VatRate = vat, Position = 1 },
                    ],
            }, month.ToList(), driven));
        }
        var start = today.AddMonths(-4);
        var depositInvoice = new Invoice
        {
            CustomerId = customers[1].Id, IssueDate = start, DueDate = start.AddDays(14), Status = InvoiceStatus.Paid, PaidAt = start.AddDays(6), Notes = Endpoints.SettingsEndpoints.DefaultNote,
            DeliveryFrom = start,
            Lines = [new InvoiceLine { Description = "Planningsapp: aanbetaling 50% bij start", Quantity = 1, UnitPrice = 7250 }],
        };
        invoices.Add((depositInvoice, [], []));

        var n = 0;
        foreach (var (inv, linked, driven) in invoices.OrderBy(x => x.Invoice.IssueDate))
        {
            inv.Number = $"{DemoNumberPrefix}{++n:0000}";
            inv.IsDemo = true;
            inv.SentAt = inv.IssueDate;
            inv.Seller = InvoiceParty.Seller(settings);
            inv.Buyer = InvoiceParty.Buyer(customers.First(c => c.Id == inv.CustomerId));
            // Een betaalde voorbeeldfactuur heeft zijn betaling erbij, net als een echte.
            if (inv.Status == InvoiceStatus.Paid)
                inv.Payments.Add(new InvoicePayment { Amount = inv.Totals.Total, Date = inv.PaidAt!.Value, Method = PaymentMethods.Bank });
            db.Invoices.Add(inv);
            db.SaveChanges();
            foreach (var e in linked) { e.InvoiceId = inv.Id; e.InvoiceLineId = inv.Lines[0].Id; }
            foreach (var t in driven) { t.InvoiceId = inv.Id; t.InvoiceLineId = inv.Lines[1].Id; }
            // Met de urenspecificatie zoals hij bij versturen is vastgelegd.
            if (linked.Count > 0)
                inv.HoursSnapshot = Invoicing.HourSpecs.Build(inv.Lines, linked, projects.ToDictionary(p => p.Id, p => p.Name), new Dictionary<int, string>(), team: false, showAmounts: false)
                    .Spec is { } spec ? Invoicing.HourSpecs.Serialize(spec) : null;
        }

        // Een terugkerende factuur om te laten zien hoe dat werkt. Als voorbeeld maakt hij nooit echte facturen.
        var hosting = new RecurringInvoice
        {
            IsDemo = true, Name = "Hosting en onderhoud webshop", CustomerId = customers[0].Id, Interval = RecurringIntervals.Monthly,
            PeriodRule = RecurringPeriodRules.InAdvance, StartDate = thisMonth, Notes = Endpoints.SettingsEndpoints.DefaultNote,
            Lines =
            [
                new RecurringInvoiceLine { Description = "Hosting webshop {maand} {jaar}", Quantity = 1, Unit = "maand", UnitPrice = 49, Position = 0 },
                new RecurringInvoiceLine { Description = "Onderhoud en beveiligingsupdates {periode}", Quantity = 1, Unit = "maand", UnitPrice = 100, Position = 1 },
            ],
        };
        Invoicing.RecurringPeriods.Schedule(hosting, hosting.StartDate);
        db.RecurringInvoices.Add(hosting);

        // Kosten van een freelance developer: elke maand dezelfde abonnementen en een paar losse aankopen, met de btw zoals op de bon.
        // Buitenlandse leveranciers rekenen geen btw; die is naar jou verlegd (GitHub en Frontend Masters van buiten de EU, Hetzner en JetBrains uit de EU).
        var expenses = new List<Expense>();
        Expense? Spend(DateTime date, string supplier, string description, string category, decimal amount, decimal? rate,
            string? reverseFrom = null, Project? project = null)
        {
            if (date > today) return null;
            var e = new Expense
            {
                IsDemo = true, Date = date, Supplier = supplier, Description = description, Category = category, Amount = amount, VatRate = rate,
                VatAmount = Expense.VatFor(amount, rate, reverseFrom is not null), ReverseCharge = reverseFrom is not null, SupplierRegion = reverseFrom,
                ProjectId = project?.Id, CustomerId = project?.CustomerId,
            };
            expenses.Add(e);
            return e;
        }
        for (var m = 1; m <= today.Month; m++)
        {
            var month = new DateTime(today.Year, m, 1);
            Spend(month, "Spaces Utrecht", "Flexplek coworking", ExpenseCategories.Office, 249m, 21);
            Spend(month.AddDays(2), "GitHub, Inc.", "GitHub Team en Copilot", ExpenseCategories.Software, 23m, 21, SupplierRegions.OutsideEu);
            Spend(month.AddDays(4), "Hetzner Online GmbH", "VPS voor de Exact-koppeling", ExpenseCategories.Hosting, 18.10m, 21, SupplierRegions.Eu, projects[0]);
            Spend(month.AddDays(19), "Odido Zakelijk", "Mobiel abonnement", ExpenseCategories.Phone, 30.25m, 21);
        }
        Spend(new DateTime(today.Year, 1, 8), "JetBrains s.r.o.", "All Products Pack, jaarlicentie", ExpenseCategories.Software, 289m, 21, SupplierRegions.Eu);
        // Investeringen: de nieuwe MacBook en een bureau van vorig jaar schrijf je af over 5 jaar. De oude ThinkPad uit
        // 2023 is verkocht toen de MacBook kwam.
        var macBook = Spend(new DateTime(today.Year, 2, 12), "Coolblue", "MacBook Pro 14 inch", ExpenseCategories.Hardware, 2899m, 21);
        if (macBook is not null) { macBook.Investment = true; macBook.UsefulLifeYears = 5; macBook.ResidualValue = 300; }
        var desk = Spend(new DateTime(today.Year - 1, 9, 15), "Ahrend", "Zit-sta-bureau en bureaustoel", ExpenseCategories.Office, 1149m, 21);
        if (desk is not null) { desk.Investment = true; desk.UsefulLifeYears = 5; }
        var thinkPad = Spend(new DateTime(today.Year - 3, 3, 6), "Lenovo", "ThinkPad X1 Carbon", ExpenseCategories.Hardware, 1815m, 21);
        if (thinkPad is not null)
        {
            thinkPad.Investment = true; thinkPad.UsefulLifeYears = 5; thinkPad.ResidualValue = 150;
            if (macBook is not null) { thinkPad.DisposedOn = macBook.Date; thinkPad.DisposalPrice = 450; }
        }
        Spend(new DateTime(today.Year, 3, 3), "TransIP", "Domeinnaam pixelwerk.dev, 1 jaar", ExpenseCategories.Hosting, 18.15m, 21);
        Spend(new DateTime(today.Year, 4, 16), "Frontend Masters", "Jaarabonnement cursussen", ExpenseCategories.Training, 390m, 21, SupplierRegions.OutsideEu);
        Spend(new DateTime(today.Year, 6, 9), "NS", "Treinkaartjes sprint review in Rotterdam", ExpenseCategories.Travel, 31.60m, 9, project: projects[1]);
        Spend(new DateTime(today.Year, 9, 24), "Techorama NL", "Conferentieticket", ExpenseCategories.Training, 605m, 21);

        // De abonnementen hierboven staan ook als vaste kost, met de uitgaven die er al bij horen. Odido herinnert alleen, de rest
        // zet zelf een concept klaar. Als voorbeeld boeken ze nooit echt iets.
        RecurringExpense Fixed(string supplier, string description, string category, decimal amount, string interval, DateTime start,
            string? reverseFrom = null, Project? project = null, string action = RecurringExpenseActions.Draft)
        {
            var t = new RecurringExpense
            {
                IsDemo = true, Supplier = supplier, Description = description, Category = category, Amount = amount, VatRate = 21,
                ReverseCharge = reverseFrom is not null, SupplierRegion = reverseFrom, ProjectId = project?.Id, CustomerId = project?.CustomerId,
                Interval = interval, Action = action, StartDate = start,
            };
            var next = start;
            for (var i = 1; next <= today; i++) next = Invoicing.RecurringPeriods.Start(start, interval, i);
            Invoicing.RecurringPeriods.Schedule(t, next);
            return t;
        }
        var fixedCosts = new List<RecurringExpense>
        {
            Fixed("Spaces Utrecht", "Flexplek coworking", ExpenseCategories.Office, 249m, RecurringIntervals.Monthly, new DateTime(today.Year, 1, 1)),
            Fixed("GitHub, Inc.", "GitHub Team en Copilot", ExpenseCategories.Software, 23m, RecurringIntervals.Monthly, new DateTime(today.Year, 1, 3), SupplierRegions.OutsideEu),
            Fixed("Hetzner Online GmbH", "VPS voor de Exact-koppeling", ExpenseCategories.Hosting, 18.10m, RecurringIntervals.Monthly, new DateTime(today.Year, 1, 5),
                SupplierRegions.Eu, projects[0]),
            Fixed("Odido Zakelijk", "Mobiel abonnement", ExpenseCategories.Phone, 30.25m, RecurringIntervals.Monthly, new DateTime(today.Year, 1, 20),
                action: RecurringExpenseActions.Remind),
            Fixed("JetBrains s.r.o.", "All Products Pack, jaarlicentie", ExpenseCategories.Software, 289m, RecurringIntervals.Yearly, new DateTime(today.Year, 1, 8), SupplierRegions.Eu),
        };
        db.RecurringExpenses.AddRange(fixedCosts);
        db.SaveChanges();
        foreach (var e in expenses)
            if (fixedCosts.FirstOrDefault(t => t.Supplier == e.Supplier && Invoicing.RecurringPeriods.Period(t, e.Date).From == e.Date) is { } t)
            {
                e.RecurringExpenseId = t.Id;
                e.RecurringPeriodStart = e.Date;
            }
        db.Expenses.AddRange(expenses);

        var lisa = new Lead { IsDemo = true, Name = "Lisa Visser", Company = "Visser Media", Email = "lisa@vissermedia.nl", Value = 6500, Status = "nieuw", Source = "Website", Notes = "Nieuwe website met headless CMS" };
        var eva = new Lead { IsDemo = true, Name = "Eva Smit", Company = "Smit Advies", Value = 4200, Status = "offerte", Source = "Doorverwijzing", Notes = "API-koppeling met Moneybird" };
        var daan = new Lead { IsDemo = true, Name = "Daan Mulder", Company = "Mulder Retail", Email = "daan@mulderretail.nl", Value = 9800, Status = "offerte", Source = "Freelanceplatform", Notes = "Shopify-app op maat" };
        var julia = new Lead { IsDemo = true, Name = "Julia Meijer", Company = "Meijer Design", Value = 1800, Status = "verloren", Source = "Koude acquisitie", Notes = "WordPress-onderhoud; te klein" };
        db.Leads.AddRange(
            lisa,
            new Lead { IsDemo = true, Name = "Tom Hendriks", Company = "Hendriks Installatie", Value = 18000, Status = "contact", Source = "LinkedIn", Notes = "Klantportaal waar klanten storingen melden" },
            eva,
            daan,
            new Lead { IsDemo = true, Name = "Noah de Boer", Company = "De Boer Transport", Value = 22000, Status = "gewonnen", Source = "Netwerk", Notes = "MVP ritplanner, start volgende maand" },
            julia
        );
        db.SaveChanges();
        DemoQuotes(db, settings, today, customers, lisa, eva, daan, julia, projects[1], depositInvoice);

        DateTime At(int day, int hour) => today.AddDays(day).AddHours(hour);
        db.Appointments.AddRange(
            new Appointment { IsDemo = true, Title = "Kennismaking Visser Media", Start = At(0, 10), End = At(0, 11), Kind = "afspraak", Location = "Google Meet" },
            new Appointment { IsDemo = true, Title = "Offerte Moneybird-koppeling afronden", Start = At(1, 9), End = At(1, 10), Kind = "taak" },
            new Appointment { IsDemo = true, Title = "Sprint review planningsapp", Start = At(2, 14), End = At(2, 15), Kind = "afspraak", CustomerId = customers[1].Id, Location = "Rotterdam" },
            new Appointment { IsDemo = true, Title = "Release 2.3 Exact-koppeling", Start = At(3, 9), End = At(3, 12), Kind = "project", CustomerId = customers[0].Id },
            new Appointment { IsDemo = true, Title = "Administratie en btw-aangifte", Start = At(4, 15), End = At(4, 17), Kind = "intern" }
        );

        db.CustomModules.Add(new CustomModule
        {
            IsDemo = true,
            Name = "Servers & domeinen",
            Icon = "server",
            Color = "#2fc6c6",
            FieldsJson = """[{"key":"naam","label":"Naam","type":"text"},{"key":"soort","label":"Soort","type":"text"},{"key":"provider","label":"Provider","type":"text"},{"key":"verloopt","label":"Verloopt op","type":"date"},{"key":"kosten","label":"Kosten per maand","type":"number"}]"""
        });
        db.CustomModules.Add(new CustomModule
        {
            IsDemo = true,
            Name = "Licenties",
            Icon = "key",
            Color = "#9b7bff",
            FieldsJson = """[{"key":"naam","label":"Naam","type":"text"},{"key":"verloopt","label":"Verlengt op","type":"date"},{"key":"kosten","label":"Kosten per jaar","type":"number"},{"key":"zakelijk","label":"Zakelijk aftrekbaar","type":"checkbox"}]"""
        });
        db.SaveChanges();
        var modules = db.CustomModules.OrderBy(m => m.Id).ToList();
        db.CustomRecords.AddRange(
            new CustomRecord { IsDemo = true, ModuleId = modules[0].Id, DataJson = """{"naam":"fietsplein.nl","soort":"Domein","provider":"TransIP","verloopt":"2027-03-01","kosten":1}""" },
            new CustomRecord { IsDemo = true, ModuleId = modules[0].Id, DataJson = """{"naam":"exact-sync productie","soort":"VPS","provider":"Hetzner","verloopt":"","kosten":18}""" },
            new CustomRecord { IsDemo = true, ModuleId = modules[0].Id, DataJson = """{"naam":"pixelwerk.dev","soort":"Domein","provider":"Cloudflare","verloopt":"2026-12-15","kosten":1}""" },
            new CustomRecord { IsDemo = true, ModuleId = modules[1].Id, DataJson = """{"naam":"JetBrains All Products","verloopt":"2027-02-01","kosten":289,"zakelijk":true}""" },
            new CustomRecord { IsDemo = true, ModuleId = modules[1].Id, DataJson = """{"naam":"GitHub Copilot","verloopt":"2026-11-20","kosten":100,"zakelijk":true}""" }
        );

        db.Workflows.AddRange(
            new Workflow
            {
                IsDemo = true,
                Name = "Nieuwe lead opvolgen",
                Active = true,
                GraphJson = """{"nodes":[{"id":"n1","type":"trigger.lead","label":"Nieuwe lead","x":80,"y":180},{"id":"n2","type":"logic.if","label":"Waarde boven 5.000?","x":340,"y":180,"config":{"field":"lead.value","operator":">","value":"5000"}},{"id":"n3","type":"action.task","label":"Taak: intake plannen","x":620,"y":80,"config":{"title":"Intakegesprek plannen met {{lead.name}} ({{lead.company}})","days":"0","kind":"taak"}},{"id":"n4","type":"action.status","label":"Status: contact","x":880,"y":80,"config":{"target":"lead","status":"contact"}},{"id":"n5","type":"action.email","label":"Bedankmail","x":620,"y":300,"config":{"to":"{{lead.email}}","subject":"Bedankt voor je aanvraag","body":"Hoi {{lead.name}},\n\nBedankt voor je aanvraag. Ik bekijk je project en kom binnen twee werkdagen bij je terug met een voorstel."}},{"id":"n6","type":"logic.wait","label":"Wacht 3 dagen","x":880,"y":300,"config":{"amount":"3","unit":"dagen"}},{"id":"n7","type":"action.task","label":"Taak: nabellen","x":1140,"y":300,"config":{"title":"Nabellen: {{lead.name}}","days":"0","kind":"taak"}}],"edges":[{"from":"n1","to":"n2"},{"from":"n2","to":"n3","branch":"ja"},{"from":"n3","to":"n4"},{"from":"n2","to":"n5","branch":"nee"},{"from":"n5","to":"n6"},{"from":"n6","to":"n7"}]}"""
            },
            new Workflow
            {
                IsDemo = true,
                Name = "Vrijdag: uren nalopen",
                Active = true,
                GraphJson = """{"nodes":[{"id":"n1","type":"trigger.schedule","label":"Elke vrijdag 16:00","x":80,"y":180,"config":{"interval":"week","weekday":"5","hour":"16"}},{"id":"n2","type":"action.task","label":"Taak: uren checken","x":360,"y":180,"config":{"title":"Uren van deze week nalopen en open uren factureren","days":"0","kind":"intern"}}],"edges":[{"from":"n1","to":"n2"}]}"""
            },
            new Workflow
            {
                IsDemo = true,
                Name = "Betaling ontvangen",
                Active = false,
                GraphJson = """{"nodes":[{"id":"n1","type":"trigger.paid","label":"Factuur betaald","x":80,"y":180},{"id":"n2","type":"action.email","label":"Bedankmail","x":360,"y":180,"config":{"to":"{{customer.email}}","subject":"Betaling ontvangen voor factuur {{invoice.number}}","body":"Hoi {{customer.name}},\n\nDank je wel, de betaling voor factuur {{invoice.number}} is binnen."}}],"edges":[{"from":"n1","to":"n2"}]}"""
            }
        );

        db.Log("systeem", "Portaal ingericht met voorbeelddata");
        db.SaveChanges();
    }
}
