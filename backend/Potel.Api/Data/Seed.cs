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

        var customers = new List<Customer>
        {
            new() { Name = "Sanne de Vries", Company = "De Vries Bouw B.V.", Email = "sanne@devriesbouw.nl", Phone = "06 12345678", Address = "Havenstraat 12", City = "Rotterdam" },
            new() { Name = "Mark Jansen", Company = "Jansen Logistiek", Email = "mark@jansenlogistiek.nl", Phone = "06 23456789", Address = "Industrieweg 4", City = "Utrecht" },
            new() { Name = "Fatima El Amrani", Company = "Studio Noord", Email = "fatima@studionoord.nl", Phone = "06 34567890", Address = "Noordkade 88", City = "Amsterdam" },
            new() { Name = "Peter Bakker", Company = "Bakker & Zn", Email = "peter@bakkerzn.nl", Phone = "06 45678901", Address = "Dorpsplein 1", City = "Eindhoven" },
        };
        db.Customers.AddRange(customers);
        db.SaveChanges();

        db.Leads.AddRange(
            new Lead { Name = "Lisa Visser", Company = "Visser Media", Email = "lisa@vissermedia.nl", Value = 4500, Status = "nieuw", Source = "Website" },
            new Lead { Name = "Tom Hendriks", Company = "Hendriks Installatie", Value = 12000, Status = "contact", Source = "Beurs" },
            new Lead { Name = "Eva Smit", Company = "Smit Advies", Value = 7800, Status = "offerte", Source = "Doorverwijzing" },
            new Lead { Name = "Daan Mulder", Company = "Mulder Retail", Value = 3200, Status = "offerte", Source = "LinkedIn" },
            new Lead { Name = "Noah de Boer", Company = "De Boer Transport", Value = 15000, Status = "gewonnen", Source = "Website" },
            new Lead { Name = "Julia Meijer", Company = "Meijer Design", Value = 2100, Status = "verloren", Source = "Koude acquisitie" }
        );

        var today = DateTime.UtcNow.Date;
        var year = today.Year;
        var invoices = new List<Invoice>();
        var rnd = new Random(7);
        for (var i = 0; i < 10; i++)
        {
            var issue = today.AddDays(-i * 17 - 3);
            var status = i == 0 ? "concept" : i is 2 or 4 ? "verzonden" : "betaald";
            invoices.Add(new Invoice
            {
                Number = $"{issue.Year}-{(i + 1):0000}",
                CustomerId = customers[i % customers.Count].Id,
                IssueDate = issue,
                DueDate = issue.AddDays(14),
                Status = status,
                Lines =
                [
                    new InvoiceLine { Description = "Advieswerk", Quantity = rnd.Next(4, 20), UnitPrice = 85 },
                    new InvoiceLine { Description = "Materiaal", Quantity = 1, UnitPrice = rnd.Next(150, 900) },
                ]
            });
        }
        db.Invoices.AddRange(invoices);

        DateTime At(int day, int hour) => today.AddDays(day).AddHours(hour);
        db.Appointments.AddRange(
            new Appointment { Title = "Kennismaking Visser Media", Start = At(0, 10), End = At(0, 11), Kind = "afspraak", Location = "Online" },
            new Appointment { Title = "Offerte Smit Advies afronden", Start = At(1, 9), End = At(1, 10), Kind = "taak" },
            new Appointment { Title = "Oplevering De Vries Bouw", Start = At(2, 13), End = At(2, 16), Kind = "project", CustomerId = customers[0].Id, Location = "Rotterdam" },
            new Appointment { Title = "Teamoverleg", Start = At(3, 9), End = At(3, 10), Kind = "intern" },
            new Appointment { Title = "Bellen Jansen Logistiek", Start = At(4, 14), End = At(4, 15), Kind = "taak", CustomerId = customers[1].Id }
        );

        db.CustomModules.Add(new CustomModule
        {
            Name = "Voertuigen",
            Icon = "truck",
            Color = "#7c5cff",
            FieldsJson = """[{"key":"kenteken","label":"Kenteken","type":"text"},{"key":"merk","label":"Merk","type":"text"},{"key":"apk","label":"APK tot","type":"date"},{"key":"km","label":"Kilometerstand","type":"number"}]"""
        });
        db.SaveChanges();
        var module = db.CustomModules.OrderBy(m => m.Id).First();
        db.CustomRecords.AddRange(
            new CustomRecord { ModuleId = module.Id, DataJson = """{"kenteken":"VX-123-B","merk":"Volkswagen Transporter","apk":"2027-03-01","km":84210}""" },
            new CustomRecord { ModuleId = module.Id, DataJson = """{"kenteken":"GH-882-K","merk":"Ford Transit","apk":"2026-12-15","km":120455}""" }
        );

        db.Workflows.Add(new Workflow
        {
            Name = "Nieuwe lead opvolgen",
            Active = true,
            GraphJson = """{"nodes":[{"id":"n1","type":"trigger.lead","label":"Nieuwe lead","x":80,"y":180},{"id":"n2","type":"logic.if","label":"Waarde boven 5.000?","x":340,"y":180,"config":{"field":"lead.value","operator":">","value":"5000"}},{"id":"n3","type":"action.task","label":"Taak: bel lead","x":620,"y":80,"config":{"title":"Bel {{lead.name}} ({{lead.company}})","days":"0","kind":"taak"}},{"id":"n4","type":"action.status","label":"Status: contact","x":880,"y":80,"config":{"target":"lead","status":"contact"}},{"id":"n5","type":"action.email","label":"Welkomstmail","x":620,"y":300,"config":{"to":"{{lead.email}}","subject":"Bedankt voor je interesse","body":"Beste {{lead.name}},\n\nBedankt voor je aanvraag. We nemen snel contact met je op."}},{"id":"n6","type":"logic.wait","label":"Wacht 3 dagen","x":880,"y":300,"config":{"amount":"3","unit":"dagen"}},{"id":"n7","type":"action.task","label":"Taak: nabellen","x":1140,"y":300,"config":{"title":"Nabellen: {{lead.name}}","days":"0","kind":"taak"}}],"edges":[{"from":"n1","to":"n2"},{"from":"n2","to":"n3","branch":"ja"},{"from":"n3","to":"n4"},{"from":"n2","to":"n5","branch":"nee"},{"from":"n5","to":"n6"},{"from":"n6","to":"n7"}]}"""
        });

        db.Log("systeem", "Portaal ingericht met voorbeelddata");
        db.SaveChanges();
    }
}
