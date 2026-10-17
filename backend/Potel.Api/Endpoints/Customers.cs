using Microsoft.EntityFrameworkCore;
using Potel.Api.Banking;
using Potel.Api.Data;
using Potel.Api.Workflows;

namespace Potel.Api.Endpoints;

public static class CustomerEndpoints
{
    public const string IbanError = "Dit rekeningnummer klopt niet. Vul een IBAN in, zoals NL91 ABNA 0417 1643 00, of laat het veld leeg.";
    public const string KvkError = "Dit KvK-nummer klopt niet. Een Nederlands KvK-nummer is 8 cijfers; laat het veld anders leeg.";

    // Het KvK-nummer van een klant: in Nederland 8 cijfers, elders zoals het is (hooguit 20 letters en cijfers). Leeg mag.
    public static bool TryKvk(string? input, string country, out string? kvk)
    {
        kvk = null;
        if (string.IsNullOrWhiteSpace(input)) return true;
        if (Countries.IsNetherlands(country))
        {
            kvk = WorkspaceEndpoints.NormalizeKvk(input);
            return kvk is not null;
        }
        kvk = new string(input.Where(char.IsLetterOrDigit).Take(20).ToArray());
        if (kvk == "") kvk = null;
        return true;
    }

    // Het rekeningnummer zonder spaties en in hoofdletters; leeg mag, een IBAN dat niet klopt niet.
    static bool TryIban(string? input, out string? iban)
    {
        iban = Ibans.Normalize(input);
        if (string.IsNullOrWhiteSpace(input)) return true;
        return Ibans.IsValid(iban);
    }

    public static void MapCustomers(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/customers");

        g.MapGet("/", async (AppDb db) => await db.Customers.OrderBy(c => c.Name).ToListAsync()).ForAccountant();

        g.MapGet("/{id:int}", async (AppDb db, int id) =>
            await db.Customers.FindAsync(id) is { } c ? Results.Ok(c) : Results.NotFound()).ForAccountant();

        g.MapPost("/", async (AppDb db, WorkflowEngine engine, Customer input) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Naam is verplicht" });
            if (!TryIban(input.Iban, out var iban)) return Results.BadRequest(new { error = IbanError });
            if (TripEndpoints.RateProblem(input.KilometreRate) is { } rateProblem) return Results.BadRequest(new { error = rateProblem });
            if (!Languages.IsKnown(input.Language)) return Results.BadRequest(new { error = Languages.Error });
            input.Country = Countries.Normalize(input.Country);
            if (VatNumbers.Problem(input.VatNumber, input.Country) is { } vatProblem) return Results.BadRequest(new { error = vatProblem });
            if (!TryKvk(input.Kvk, input.Country, out var kvk)) return Results.BadRequest(new { error = KvkError });
            input.Kvk = kvk;
            input.ImportBatchId = null;
            input.VatNumber = VatNumbers.Clean(input.VatNumber, input.Country);
            input.Language = Languages.Of(input.Language);
            input.Iban = iban;
            input.KilometreRate = TripEndpoints.CleanRate(input.KilometreRate);
            input.Id = 0;
            input.CreatedAt = DateTime.UtcNow;
            db.Customers.Add(input);
            db.Log("klant", $"Klant {input.Name} toegevoegd");
            await db.SaveChangesAsync();
            var ctx = new Dictionary<string, string>();
            WorkflowContext.AddCustomer(ctx, input);
            await engine.TriggerAsync("trigger.customer", ctx, $"Nieuwe klant: {input.Name}");
            return Results.Created($"/api/customers/{input.Id}", input);
        });

        g.MapPut("/{id:int}", async (AppDb db, int id, Customer input) =>
        {
            var c = await db.Customers.FindAsync(id);
            if (c is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Naam is verplicht" });
            if (!TryIban(input.Iban, out var iban)) return Results.BadRequest(new { error = IbanError });
            if (TripEndpoints.RateProblem(input.KilometreRate) is { } rateProblem) return Results.BadRequest(new { error = rateProblem });
            if (!Languages.IsKnown(input.Language)) return Results.BadRequest(new { error = Languages.Error });
            var country = Countries.Normalize(input.Country);
            // Alleen de vorm, zonder VIES. Facturen die al verstuurd zijn houden het nummer dat erop staat.
            if (VatNumbers.Problem(input.VatNumber, country) is { } vatProblem) return Results.BadRequest(new { error = vatProblem });
            if (!TryKvk(input.Kvk, country, out var kvk)) return Results.BadRequest(new { error = KvkError });
            c.Kvk = kvk;
            c.Iban = iban;
            c.KilometreRate = TripEndpoints.CleanRate(input.KilometreRate);
            c.Name = input.Name; c.Company = input.Company; c.Email = input.Email; c.Phone = input.Phone;
            c.Address = input.Address; c.City = input.City; c.Country = country; c.VatNumber = VatNumbers.Clean(input.VatNumber, country); c.Notes = input.Notes;
            c.RemindersOff = input.RemindersOff;
            // Alleen voor nieuwe facturen en offertes: een concept houdt zijn taal tot je die zelf aanpast, een verstuurde ligt vast.
            c.Language = Languages.Of(input.Language);
            db.Log("klant", $"Klant {c.Name} bijgewerkt");
            await db.SaveChangesAsync();
            return Results.Ok(c);
        });

        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var c = await db.Customers.FindAsync(id);
            if (c is null) return Results.NotFound();
            if (await db.Invoices.AnyAsync(i => i.CustomerId == id))
                return Results.Conflict(new { error = "Deze klant heeft facturen en kan niet verwijderd worden" });
            if (await db.ImportedInvoices.AnyAsync(i => i.CustomerId == id))
                return Results.Conflict(new { error = "Deze klant heeft geïmporteerde facturen uit je vorige pakket en kan daarom niet verwijderd worden." });
            if (await db.Projects.AnyAsync(p => p.CustomerId == id))
                return Results.Conflict(new { error = "Deze klant heeft projecten. Verwijder die eerst." });
            if (await db.RecurringInvoices.AnyAsync(r => r.CustomerId == id))
                return Results.Conflict(new { error = "Deze klant heeft terugkerende facturen. Verwijder die eerst." });
            if (await db.Quotes.AnyAsync(q => q.CustomerId == id))
                return Results.Conflict(new { error = "Deze klant heeft offertes. Een verstuurde offerte bewaar je, dus deze klant kan niet verwijderd worden." });
            // Afspraken en leads blijven bestaan, maar wijzen niet meer naar een klant die er niet is.
            await db.Appointments.Where(a => a.CustomerId == id).ExecuteUpdateAsync(x => x.SetProperty(a => a.CustomerId, (int?)null));
            await db.Leads.Where(l => l.CustomerId == id).ExecuteUpdateAsync(x => x.SetProperty(l => l.CustomerId, (int?)null));
            db.Customers.Remove(c);
            db.Log("klant", $"Klant {c.Name} verwijderd");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Alle open uren van deze klant in één keer op een nieuwe conceptfactuur, een regel per project op uurbasis.
        // Voor de snelle actie "Factureer open uren van ..." in het zoekvenster. Net als daar alleen voor beheerders.
        g.MapPost("/{id:int}/invoice-hours", async (AppDb db, BusinessClock clock, int id) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            var c = await db.Customers.FindAsync(id);
            if (c is null) return Results.NotFound();
            var projects = await db.Projects.Where(p => p.CustomerId == id && p.Billing == Billing.Hourly).OrderBy(p => p.Name).ToListAsync();
            var parts = new List<(InvoiceLine Line, List<TimeEntry> Entries)>();
            foreach (var p in projects)
            {
                var entries = await ProjectEndpoints.OpenEntries(db, p.Id);
                if (entries.Count > 0) parts.AddRange(ProjectEndpoints.HourLines(p, entries, false, c.Language));
            }
            var claimed = parts.SelectMany(x => x.Entries).ToList();
            if (claimed.Count == 0) return Results.BadRequest(new { error = $"Er staan geen open uren voor {c.Company ?? c.Name}" });

            var settings = await SettingsEndpoints.GetAsync(db);
            var today = clock.Today;
            var regime = VatRegimes.DefaultFor(settings, c);
            var lines = parts.Select(x => x.Line).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                lines[i].Position = i;
                if (VatRegimes.ZeroVat(regime)) lines[i].VatRate = 0;
            }
            var inv = new Invoice
            {
                CustomerId = c.Id, IssueDate = today, DueDate = today.AddDays(settings.PaymentTermDays), Status = InvoiceStatus.Draft,
                VatRegime = regime, DeliveryFrom = claimed.Min(e => e.Date).Date, DeliveryTo = claimed.Max(e => e.Date).Date,
                Language = c.Language, Notes = SettingsEndpoints.Note(c.Language), Lines = lines,
            };
            db.Invoices.Add(inv);
            await db.SaveChangesAsync();
            if (!await ProjectEndpoints.ClaimAsync(db, inv.Id, parts)) return Results.Conflict(new { error = InvoiceEndpoints.HoursTaken });
            var minutes = claimed.Sum(e => e.Minutes);
            db.Log("factuur", $"Conceptfactuur gemaakt van {ProjectEndpoints.Hours(minutes):0.##} uur voor {c.Company ?? c.Name}");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Created($"/api/invoices/{inv.Id}", new { id = inv.Id, minutes, projects = lines.Count });
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }
}
