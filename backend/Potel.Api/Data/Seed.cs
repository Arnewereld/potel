namespace Potel.Api.Data;

public static class Seed
{
    // Zorgt dat er altijd een beheerder is om mee in te loggen.
    public static void EnsureAdmin(AppDb db, IConfiguration config, ILogger logger)
    {
        if (db.Users.Any()) return;
        var admin = new User
        {
            Name = config["Admin:Name"] ?? "Beheerder",
            Email = (config["Admin:Email"] ?? "admin@potel.nl").ToLower(),
            Role = Roles.Admin,
        };
        var password = config["Admin:Password"] ?? "welkom123";
        admin.PasswordHash = Endpoints.AuthEndpoints.Hash(admin, password);
        db.Users.Add(admin);
        db.SaveChanges();
        logger.LogWarning("Beheerder aangemaakt: {Email}. Wijzig het wachtwoord na de eerste keer inloggen.", admin.Email);
    }

    public static void Run(AppDb db)
    {
        if (db.Customers.Any()) return;

        var settings = db.Settings.OrderBy(x => x.Id).FirstOrDefault() ?? db.Settings.Add(new Settings()).Entity;
        settings.CompanyName = "Pixelwerk Development";
        settings.OwnerName = "Jouw naam";
        settings.Address = "Keizersgracht 100";
        settings.City = "1015 AA Amsterdam";
        settings.Email = "hallo@pixelwerk.dev";
        settings.Website = "pixelwerk.dev";
        settings.Kvk = "12345678";
        settings.Btw = "NL001234567B01";
        settings.Iban = "NL00 BANK 0123 4567 89";

        var customers = new List<Customer>
        {
            new() { Name = "Sanne de Vries", Company = "Fietsplein B.V.", VatNumber = "NL812345678B01", Email = "sanne@fietsplein.nl", Phone = "06 12345678", Address = "Oudegracht 12", City = "Utrecht", Notes = "Webshop op Next.js. Voorraad en orders lopen via Exact Online." },
            new() { Name = "Mark Jansen", Company = "Jansen Logistiek", Email = "mark@jansenlogistiek.nl", Phone = "06 23456789", Address = "Industrieweg 4", City = "Rotterdam", Notes = "Chauffeurs gebruiken de planningsapp op Android." },
            new() { Name = "Fatima El Amrani", Company = "Studio Noord", VatNumber = "NL860011223B01", Email = "fatima@studionoord.nl", Phone = "06 34567890", Address = "Noordkade 88", City = "Amsterdam", Notes = "Designbureau; ik bouw hun ontwerpen onder hun naam." },
            new() { Name = "Peter Bakker", Company = "Zorgnet Oost", Email = "peter@zorgnetoost.nl", Phone = "06 45678901", Address = "Hengelosestraat 1", City = "Enschede", Notes = "Onderhoudscontract voor het cliëntportaal (.NET + Angular)." },
        };
        db.Customers.AddRange(customers);
        db.SaveChanges();

        var today = DateTime.Today;
        var projects = new List<Project>
        {
            new() { Name = "Exact Online-koppeling", CustomerId = customers[0].Id, Billing = Billing.Hourly, HourlyRate = 95, BudgetHours = 160, Color = "#4ea5ff", RepoUrl = "https://github.com/fietsplein/exact-sync", Description = "Orders, voorraad en klanten synchroniseren tussen de webshop en Exact Online." },
            new() { Name = "Planningsapp (React Native)", CustomerId = customers[1].Id, Billing = Billing.Fixed, FixedPrice = 14500, BudgetHours = 160, Color = "#3ecf8e", Deadline = today.AddDays(40), Description = "Rittenplanning voor chauffeurs met offline modus en pushmeldingen." },
            new() { Name = "White-label development", CustomerId = customers[2].Id, Billing = Billing.Hourly, HourlyRate = 85, Color = "#9b7bff", Description = "Frontend bouwen van Figma-ontwerpen voor klanten van Studio Noord." },
            new() { Name = "Onderhoud cliëntportaal", CustomerId = customers[3].Id, Billing = Billing.Hourly, HourlyRate = 90, BudgetHours = 400, Color = "#f5b83d", RepoUrl = "https://dev.azure.com/zorgnetoost/portaal", Description = "Updates, beveiligingspatches en kleine wijzigingen." },
            new() { Name = "Migratie naar Next.js", CustomerId = customers[0].Id, Status = "afgerond", Billing = Billing.Hourly, HourlyRate = 95, BudgetHours = 80, Color = "#ff5ca8", Description = "Oude WooCommerce-shop omgezet naar Next.js." },
        };
        db.Projects.AddRange(projects);
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
                    Billable = p.Billing == Billing.Hourly,
                });
            }
        }
        db.TimeEntries.AddRange(entries);
        db.SaveChanges();

        // Uurprojecten worden per maand gefactureerd; de lopende maand staat nog open.
        var invoices = new List<(Invoice Invoice, List<TimeEntry> Entries)>();
        var thisMonth = new DateTime(today.Year, today.Month, 1);
        foreach (var month in entries.Where(e => e.Billable && e.Date < thisMonth).GroupBy(e => (e.ProjectId, new DateTime(e.Date.Year, e.Date.Month, 1))))
        {
            var p = projects.First(x => x.Id == month.Key.ProjectId);
            var issue = month.Key.Item2.AddMonths(1);
            var minutes = month.Sum(e => e.Minutes);
            invoices.Add((new Invoice
            {
                CustomerId = p.CustomerId, IssueDate = issue, DueDate = issue.AddDays(14),
                Status = issue < thisMonth ? "betaald" : "verzonden", PaidAt = issue < thisMonth ? issue.AddDays(9) : null, Reference = p.Id == projects[3].Id ? "PO-2026-0412" : null, Notes = Endpoints.SettingsEndpoints.DefaultNote,
                Lines = [new InvoiceLine { Description = $"{p.Name}: werkzaamheden {issue.AddMonths(-1):MMMM yyyy}", Quantity = Math.Round(minutes / 60m, 2), Unit = "uur", UnitPrice = p.HourlyRate }],
            }, month.ToList()));
        }
        var start = today.AddMonths(-4);
        invoices.Add((new Invoice
        {
            CustomerId = customers[1].Id, IssueDate = start, DueDate = start.AddDays(14), Status = "betaald", PaidAt = start.AddDays(6), Notes = Endpoints.SettingsEndpoints.DefaultNote,
            Lines = [new InvoiceLine { Description = "Planningsapp: aanbetaling 50% bij start", Quantity = 1, UnitPrice = 7250 }],
        }, []));

        var n = 0;
        foreach (var (inv, linked) in invoices.OrderBy(x => x.Invoice.IssueDate))
        {
            inv.Number = $"{inv.IssueDate.Year}-{++n:0000}";
            db.Invoices.Add(inv);
            db.SaveChanges();
            foreach (var e in linked) e.InvoiceId = inv.Id;
        }

        db.Leads.AddRange(
            new Lead { Name = "Lisa Visser", Company = "Visser Media", Email = "lisa@vissermedia.nl", Value = 6500, Status = "nieuw", Source = "Website", Notes = "Nieuwe website met headless CMS" },
            new Lead { Name = "Tom Hendriks", Company = "Hendriks Installatie", Value = 18000, Status = "contact", Source = "LinkedIn", Notes = "Klantportaal waar klanten storingen melden" },
            new Lead { Name = "Eva Smit", Company = "Smit Advies", Value = 4200, Status = "offerte", Source = "Doorverwijzing", Notes = "API-koppeling met Moneybird" },
            new Lead { Name = "Daan Mulder", Company = "Mulder Retail", Value = 9800, Status = "offerte", Source = "Freelanceplatform", Notes = "Shopify-app op maat" },
            new Lead { Name = "Noah de Boer", Company = "De Boer Transport", Value = 22000, Status = "gewonnen", Source = "Netwerk", Notes = "MVP ritplanner, start volgende maand" },
            new Lead { Name = "Julia Meijer", Company = "Meijer Design", Value = 1800, Status = "verloren", Source = "Koude acquisitie", Notes = "WordPress-onderhoud; te klein" }
        );

        DateTime At(int day, int hour) => today.AddDays(day).AddHours(hour);
        db.Appointments.AddRange(
            new Appointment { Title = "Kennismaking Visser Media", Start = At(0, 10), End = At(0, 11), Kind = "afspraak", Location = "Google Meet" },
            new Appointment { Title = "Offerte Moneybird-koppeling afronden", Start = At(1, 9), End = At(1, 10), Kind = "taak" },
            new Appointment { Title = "Sprint review planningsapp", Start = At(2, 14), End = At(2, 15), Kind = "afspraak", CustomerId = customers[1].Id, Location = "Rotterdam" },
            new Appointment { Title = "Release 2.3 Exact-koppeling", Start = At(3, 9), End = At(3, 12), Kind = "project", CustomerId = customers[0].Id },
            new Appointment { Title = "Administratie en btw-aangifte", Start = At(4, 15), End = At(4, 17), Kind = "intern" }
        );

        db.CustomModules.Add(new CustomModule
        {
            Name = "Servers & domeinen",
            Icon = "server",
            Color = "#2fc6c6",
            FieldsJson = """[{"key":"naam","label":"Naam","type":"text"},{"key":"soort","label":"Soort","type":"text"},{"key":"provider","label":"Provider","type":"text"},{"key":"verloopt","label":"Verloopt op","type":"date"},{"key":"kosten","label":"Kosten per maand","type":"number"}]"""
        });
        db.CustomModules.Add(new CustomModule
        {
            Name = "Licenties",
            Icon = "key",
            Color = "#9b7bff",
            FieldsJson = """[{"key":"naam","label":"Naam","type":"text"},{"key":"verloopt","label":"Verlengt op","type":"date"},{"key":"kosten","label":"Kosten per jaar","type":"number"},{"key":"zakelijk","label":"Zakelijk aftrekbaar","type":"checkbox"}]"""
        });
        db.SaveChanges();
        var modules = db.CustomModules.OrderBy(m => m.Id).ToList();
        db.CustomRecords.AddRange(
            new CustomRecord { ModuleId = modules[0].Id, DataJson = """{"naam":"fietsplein.nl","soort":"Domein","provider":"TransIP","verloopt":"2027-03-01","kosten":1}""" },
            new CustomRecord { ModuleId = modules[0].Id, DataJson = """{"naam":"exact-sync productie","soort":"VPS","provider":"Hetzner","verloopt":"","kosten":18}""" },
            new CustomRecord { ModuleId = modules[0].Id, DataJson = """{"naam":"pixelwerk.dev","soort":"Domein","provider":"Cloudflare","verloopt":"2026-12-15","kosten":1}""" },
            new CustomRecord { ModuleId = modules[1].Id, DataJson = """{"naam":"JetBrains All Products","verloopt":"2027-02-01","kosten":289,"zakelijk":true}""" },
            new CustomRecord { ModuleId = modules[1].Id, DataJson = """{"naam":"GitHub Copilot","verloopt":"2026-11-20","kosten":100,"zakelijk":true}""" }
        );

        db.Workflows.AddRange(
            new Workflow
            {
                Name = "Nieuwe lead opvolgen",
                Active = true,
                GraphJson = """{"nodes":[{"id":"n1","type":"trigger.lead","label":"Nieuwe lead","x":80,"y":180},{"id":"n2","type":"logic.if","label":"Waarde boven 5.000?","x":340,"y":180,"config":{"field":"lead.value","operator":">","value":"5000"}},{"id":"n3","type":"action.task","label":"Taak: intake plannen","x":620,"y":80,"config":{"title":"Intakegesprek plannen met {{lead.name}} ({{lead.company}})","days":"0","kind":"taak"}},{"id":"n4","type":"action.status","label":"Status: contact","x":880,"y":80,"config":{"target":"lead","status":"contact"}},{"id":"n5","type":"action.email","label":"Bedankmail","x":620,"y":300,"config":{"to":"{{lead.email}}","subject":"Bedankt voor je aanvraag","body":"Hoi {{lead.name}},\n\nBedankt voor je aanvraag. Ik bekijk je project en kom binnen twee werkdagen bij je terug met een voorstel."}},{"id":"n6","type":"logic.wait","label":"Wacht 3 dagen","x":880,"y":300,"config":{"amount":"3","unit":"dagen"}},{"id":"n7","type":"action.task","label":"Taak: nabellen","x":1140,"y":300,"config":{"title":"Nabellen: {{lead.name}}","days":"0","kind":"taak"}}],"edges":[{"from":"n1","to":"n2"},{"from":"n2","to":"n3","branch":"ja"},{"from":"n3","to":"n4"},{"from":"n2","to":"n5","branch":"nee"},{"from":"n5","to":"n6"},{"from":"n6","to":"n7"}]}"""
            },
            new Workflow
            {
                Name = "Vrijdag: uren nalopen",
                Active = true,
                GraphJson = """{"nodes":[{"id":"n1","type":"trigger.schedule","label":"Elke vrijdag 16:00","x":80,"y":180,"config":{"interval":"week","weekday":"5","hour":"16"}},{"id":"n2","type":"action.task","label":"Taak: uren checken","x":360,"y":180,"config":{"title":"Uren van deze week nalopen en open uren factureren","days":"0","kind":"intern"}}],"edges":[{"from":"n1","to":"n2"}]}"""
            },
            new Workflow
            {
                Name = "Betaling ontvangen",
                Active = false,
                GraphJson = """{"nodes":[{"id":"n1","type":"trigger.paid","label":"Factuur betaald","x":80,"y":180},{"id":"n2","type":"action.email","label":"Bedankmail","x":360,"y":180,"config":{"to":"{{customer.email}}","subject":"Betaling ontvangen voor factuur {{invoice.number}}","body":"Hoi {{customer.name}},\n\nDank je wel, de betaling voor factuur {{invoice.number}} is binnen."}}],"edges":[{"from":"n1","to":"n2"}]}"""
            }
        );

        db.Log("systeem", "Portaal ingericht met voorbeelddata");
        db.SaveChanges();
    }
}
