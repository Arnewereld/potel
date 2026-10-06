using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Workflows;

namespace Potel.Api.Endpoints;

public record StatusRequest(string Status, DateTime? PaidAt);
public record AddHoursRequest(int ProjectId, bool Detailed);

// Een melding voor de gebruiker; bij versturen met de lijst van wat er nog ontbreekt.
public record InvoiceProblem(string Error, List<string>? Missing = null, int Status = StatusCodes.Status400BadRequest)
{
    public IResult ToResult() => Results.Json(new { error = Error, missing = Missing }, statusCode: Status);
}

// Facturen volgen de regels van de Belastingdienst: alleen een concept mag nog veranderen of weg. Bij versturen krijgt
// de factuur het volgende nummer en worden de gegevens van beide partijen vastgelegd; daarna gaat alleen de status nog vooruit.
// Een fout herstel je met een creditnota die vast aan de oorspronkelijke factuur hangt.
public static partial class InvoiceEndpoints
{
    public const string FrozenError = "Deze factuur is al verstuurd en ligt vast. Klopt er iets niet? Maak dan een creditnota en zo nodig een nieuwe factuur.";
    public const string HoursTaken = "Een deel van deze uren is net op een andere factuur gezet. Vernieuw de pagina en probeer het opnieuw.";

    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    [GeneratedRegex(@"^NL\d{9}B\d{2}$")]
    private static partial Regex DutchVatId();

    public static decimal Total(Invoice i) => Money.Totals(i.Lines).Total;

    // Hoe de factuur heet in het logboek en in meldingen.
    public static string Label(Invoice i) => i.Number is null ? "Conceptfactuur" : $"{(i.IsCredit ? "Creditnota" : "Factuur")} {i.Number}";

    static string CustomerName(Customer c) => string.IsNullOrWhiteSpace(c.Company) ? c.Name : c.Company;

    static string Euro(decimal d) => d.ToString("C", Dutch);

    static string Join(List<string> items) => items.Count == 1 ? items[0] : $"{string.Join(", ", items[..^1])} en {items[^1]}";

    // Verzonden facturen voorbij de vervaldatum gaan op verlopen. Een creditnota hoeft niemand te betalen en verloopt nooit.
    public static async Task MarkOverdue(AppDb db, BusinessClock clock)
    {
        var today = clock.Today;
        var overdue = (await db.Invoices.Include(i => i.Lines)
                .Where(i => i.Status == InvoiceStatus.Sent && i.DueDate < today && i.CreditForInvoiceId == null).ToListAsync())
            .Where(i => !i.IsCredit).ToList();
        if (overdue.Count == 0) return;
        foreach (var i in overdue) { i.Status = InvoiceStatus.Overdue; db.Log("factuur", $"{Label(i)} is verlopen"); }
        await db.SaveChangesAsync();
    }

    // Het volgende nummer in de reeks van het jaar van de factuurdatum, per werkruimte en zonder gaten: 2026-0001, 2026-0002, ...
    // Alleen binnen een WriteLock gebruiken om een nummer te geven, anders kunnen twee verzoeken hetzelfde nummer krijgen.
    public static async Task<string> NextNumber(AppDb db, int year)
    {
        var prefix = $"{year}-";
        var numbers = await db.Invoices.Where(i => i.Number != null && i.Number.StartsWith(prefix)).Select(i => i.Number!).ToListAsync();
        var max = numbers.Select(n => int.TryParse(n[prefix.Length..], out var x) ? x : 0).DefaultIfEmpty(0).Max();
        return $"{prefix}{max + 1:0000}";
    }

    // Controles bij opslaan van een concept.
    static async Task<string?> ValidateAsync(AppDb db, Invoice input)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == input.CustomerId);
        if (customer is null) return "Kies een klant";
        if (!InvoiceStatus.All.Contains(input.Status)) return "Onbekende status";
        if (input.Lines.Count == 0) return "Voeg minstens één regel toe";
        if (input.Lines.Any(l => string.IsNullOrWhiteSpace(l.Description))) return "Elke regel heeft een omschrijving nodig";
        if (input.DueDate < input.IssueDate) return "De vervaldatum ligt voor de factuurdatum";
        if (input.DeliveryFrom is { } from && input.DeliveryTo is { } to && to < from) return "De leverperiode eindigt voordat hij begint";
        if (!VatRegimes.All.Contains(input.VatRegime)) return "Kies een btw-regeling";
        return RegimeError(input.VatRegime, customer);
    }

    // Past de btw-regeling bij de klant? Binnen Nederland wordt btw voor IT-werk nooit verlegd.
    static string? RegimeError(string regime, Customer c) => regime switch
    {
        VatRegimes.ReverseCharge when !Countries.InEu(c.Country) || Countries.IsNetherlands(c.Country) =>
            "Btw verlegd kan alleen voor een zakelijke klant in een ander EU-land. Een klant in Nederland betaalt gewoon btw. Zit je klant in een ander EU-land? Vul dan het land in bij de klant.",
        VatRegimes.ReverseCharge when string.IsNullOrWhiteSpace(c.VatNumber) =>
            "Voor btw verlegd moet het btw-nummer van de klant bekend zijn. Vul het in bij de klant.",
        VatRegimes.OutsideEu when Countries.InEu(c.Country) =>
            "\"Buiten de EU\" kan alleen voor een klant buiten de EU. Vul het land van de klant in bij de klant.",
        _ => null,
    };

    // Wat er nog ontbreekt om te mogen versturen (de factuureisen van de Belastingdienst), in gewone woorden.
    // De frontend toont dezelfde lijst (missingForSending in frontend/src/lib/invoice.ts).
    public static List<string> Missing(Invoice inv, Settings s, Customer c)
    {
        var missing = new List<string>();
        void Need(string? value, string label) { if (string.IsNullOrWhiteSpace(value)) missing.Add(label); }
        Need(s.CompanyName, "je bedrijfsnaam");
        Need(s.Address, "je adres");
        Need(s.City, "je postcode en plaats");
        Need(s.Kvk, "je KvK-nummer");
        Need(s.Btw, "je btw-id");
        Need(c.Name, "de naam van de klant");
        Need(c.Address, "het adres van de klant");
        Need(c.City, "de postcode en plaats van de klant");
        if (inv.VatRegime == VatRegimes.ReverseCharge) Need(c.VatNumber, "het btw-nummer van de klant");
        if (inv.DeliveryFrom is null) missing.Add("de leverdatum of periode");
        if (inv.Lines.Count == 0) missing.Add("minstens één regel");
        return missing;
    }

    static async Task<InvoiceProblem?> CheckSendableAsync(AppDb db, Invoice inv, Settings s, Customer c)
    {
        var missing = Missing(inv, s, c);
        if (missing.Count > 0) return new($"Vul eerst {Join(missing)} in. Daarna kun je de factuur versturen.", missing);
        var btw = s.Btw!.Replace(" ", "").Replace(".", "").ToUpperInvariant();
        if (btw.StartsWith("NL") && !DutchVatId().IsMatch(btw))
            return new("Je btw-id klopt niet. Een Nederlands btw-id is NL, 9 cijfers, B en 2 cijfers, bijvoorbeeld NL001234567B01. Pas het aan bij Instellingen.");
        if (RegimeError(inv.VatRegime, c) is { } regimeError) return new(regimeError);

        var total = Total(inv);
        if (inv.CreditForInvoiceId is not { } originalId)
            return total < 0 ? new("Het totaal is negatief. Een correctie maak je met \"Creditnota maken\" op de oorspronkelijke factuur.") : null;
        if (total >= 0) return new("Een creditnota moet een negatief totaal hebben.");
        var original = await db.Invoices.Include(i => i.Lines).FirstAsync(i => i.Id == originalId);
        var credited = (await db.Invoices.Include(i => i.Lines)
            .Where(i => i.CreditForInvoiceId == originalId && i.Id != inv.Id && i.Status != InvoiceStatus.Draft).ToListAsync()).Sum(Total);
        if (credited + total < -Total(original))
            return new($"Met deze creditnota crediteer je meer dan factuur {original.Number} waard is. Er valt nog {Euro(Total(original) + credited)} te crediteren.");
        return null;
    }

    // Versturen: alles moet kloppen, dan krijgt de factuur het volgende nummer en leggen we de gegevens van jou en je klant vast.
    // Alleen binnen een WriteLock aanroepen.
    static async Task<InvoiceProblem?> SendAsync(AppDb db, Invoice inv)
    {
        var settings = await SettingsEndpoints.GetAsync(db);
        var customer = await db.Customers.FirstAsync(c => c.Id == inv.CustomerId);
        if (await CheckSendableAsync(db, inv, settings, customer) is { } problem) return problem;
        inv.Number = await NextNumber(db, inv.IssueDate.Year);
        inv.Seller = InvoiceParty.Seller(settings);
        inv.Buyer = InvoiceParty.Buyer(customer);
        inv.SentAt = DateTime.UtcNow;
        return null;
    }

    // Zet een nieuwe status volgens de regels. Een concept wordt daarbij verstuurd; daarna gaat de status alleen nog vooruit.
    // Geeft ook terug of de factuur net betaald is. Een creditnota telt daarvoor niet mee: die start geen werkstroom "Factuur betaald".
    // De factuur moet met zijn regels geladen zijn, en dit hoort binnen een WriteLock.
    public static async Task<(InvoiceProblem? Problem, bool BecamePaid)> ChangeStatusAsync(AppDb db, BusinessClock clock, Invoice inv, string status, DateTime? paidAt = null)
    {
        if (!InvoiceStatus.All.Contains(status)) return (new("Onbekende status"), false);
        if (status == inv.Status)
        {
            if (status == InvoiceStatus.Paid && paidAt is { } date) inv.PaidAt = date.Date;
            return (null, false);
        }
        if (!InvoiceStatus.CanMove(inv.Status, status)) return (new(StatusError(inv, status), Status: StatusCodes.Status409Conflict), false);
        if (inv.Status == InvoiceStatus.Draft && await SendAsync(db, inv) is { } problem) return (problem, false);
        db.Log("factuur", $"{Label(inv)}: {inv.Status} → {status}");
        inv.Status = status;
        if (status != InvoiceStatus.Paid) return (null, false);
        inv.PaidAt = (paidAt ?? clock.Today).Date;
        return (null, !inv.IsCredit);
    }

    static string StatusError(Invoice inv, string to) => to == InvoiceStatus.Draft
        ? "Een verstuurde factuur kan niet terug naar concept. Klopt er iets niet? Maak dan een creditnota."
        : inv.Status == InvoiceStatus.Paid
            ? "Deze factuur is al betaald; de status kan niet meer terug."
            : $"Van {inv.Status} naar {to} kan niet: de status van een verstuurde factuur gaat alleen vooruit.";

    // Regels overnemen. Bij btw verlegd, KOR of buiten de EU staat er geen btw op de regels.
    static List<InvoiceLine> CopyLines(IEnumerable<InvoiceLine> lines, string regime, decimal sign = 1) =>
        lines.Select(l => new InvoiceLine
        {
            Description = l.Description, Quantity = sign * l.Quantity, Unit = string.IsNullOrWhiteSpace(l.Unit) ? "stuk" : l.Unit.Trim(),
            UnitPrice = l.UnitPrice, VatRate = VatRegimes.ZeroVat(regime) ? 0 : l.VatRate,
        }).ToList();

    static IQueryable<Invoice> Full(AppDb db) =>
        db.Invoices.Include(i => i.Lines.OrderBy(l => l.Id)).Include(i => i.Customer).Include(i => i.CreditFor);

    // De leverperiode van de uren op deze factuur.
    public static async Task SetDeliveryFromHoursAsync(AppDb db, Invoice inv)
    {
        var dates = await db.TimeEntries.Where(t => t.InvoiceId == inv.Id).Select(t => t.Date).ToListAsync();
        if (dates.Count == 0) return;
        inv.DeliveryFrom = dates.Min().Date;
        inv.DeliveryTo = dates.Max().Date;
    }

    // Start werkstromen met de trigger "Factuur betaald".
    static async Task TriggerPaid(AppDb db, WorkflowEngine engine, Invoice inv)
    {
        var full = await Full(db).FirstAsync(i => i.Id == inv.Id);
        var ctx = new Dictionary<string, string>();
        WorkflowContext.AddInvoice(ctx, full);
        await engine.TriggerAsync("trigger.paid", ctx, $"Factuur {full.Number} betaald");
    }

    public static void MapInvoices(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/invoices");

        // Concepten bovenaan, daarna de verstuurde facturen van nieuw naar oud.
        g.MapGet("/", async (AppDb db, BusinessClock clock) =>
        {
            await MarkOverdue(db, clock);
            return await Full(db).AsNoTracking()
                .OrderBy(i => i.Status != InvoiceStatus.Draft).ThenByDescending(i => i.Number).ThenByDescending(i => i.Id).ToListAsync();
        });

        // Het nummer dat een factuur met deze datum zou krijgen als je hem nu verstuurt.
        g.MapGet("/next-number", async (AppDb db, BusinessClock clock, DateTime? date) =>
            new { number = await NextNumber(db, (date ?? clock.Today).Year) });

        g.MapGet("/{id:int}", async (AppDb db, int id) =>
            await Full(db).AsNoTracking().FirstOrDefaultAsync(i => i.Id == id) is { } inv ? Results.Ok(inv) : Results.NotFound());

        // Een nieuwe factuur is een concept. Met een andere status wordt hij meteen verstuurd (of als betaald vastgelegd).
        g.MapPost("/", async (AppDb db, BusinessClock clock, WorkflowEngine engine, Invoice input) =>
        {
            if (await ValidateAsync(db, input) is { } error) return Results.BadRequest(new { error });
            await using var tx = await WriteLock.BeginAsync(db);
            var customer = await db.Customers.FirstAsync(c => c.Id == input.CustomerId);
            var inv = new Invoice
            {
                CustomerId = input.CustomerId, IssueDate = input.IssueDate.Date, DueDate = input.DueDate.Date, Reference = input.Reference,
                // Zonder leverdatum gaan we uit van de factuurdatum; in het concept is dat nog aan te passen.
                VatRegime = input.VatRegime, DeliveryFrom = (input.DeliveryFrom ?? input.IssueDate).Date, DeliveryTo = input.DeliveryTo?.Date, Notes = input.Notes,
                Lines = CopyLines(input.Lines, input.VatRegime),
            };
            db.Invoices.Add(inv);
            db.Log("factuur", $"Conceptfactuur voor {CustomerName(customer)} aangemaakt");
            await db.SaveChangesAsync();
            var (problem, becamePaid) = await ChangeStatusAsync(db, clock, inv, input.Status, input.PaidAt);
            if (problem is not null) return problem.ToResult();
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            if (becamePaid) await TriggerPaid(db, engine, inv);
            return Results.Created($"/api/invoices/{inv.Id}", await Full(db).AsNoTracking().FirstAsync(i => i.Id == inv.Id));
        });

        // Alleen een concept kan nog veranderen. Regels die van de factuur gaan geven hun uren weer vrij.
        g.MapPut("/{id:int}", async (AppDb db, BusinessClock clock, WorkflowEngine engine, int id, Invoice input) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            var inv = await db.Invoices.Include(i => i.Lines.OrderBy(l => l.Id)).FirstOrDefaultAsync(i => i.Id == id);
            if (inv is null) return Results.NotFound();
            if (inv.Status != InvoiceStatus.Draft) return Results.Conflict(new { error = FrozenError });
            // Een creditnota hoort bij de klant en de btw-regeling van de factuur die hij corrigeert.
            if (inv.CreditForInvoiceId is not null) { input.CustomerId = inv.CustomerId; input.VatRegime = inv.VatRegime; }
            if (await ValidateAsync(db, input) is { } error) return Results.BadRequest(new { error });

            var newLines = CopyLines(input.Lines, input.VatRegime);
            var oldIds = inv.Lines.Select(l => l.Id).ToHashSet();
            var kept = input.Lines.Select((l, i) => (l.Id, Line: newLines[i])).Where(x => oldIds.Contains(x.Id))
                .GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First().Line);
            var entries = await db.TimeEntries.Where(t => t.InvoiceId == id).ToListAsync();
            // Uren van een oudere versie hebben geen regel; die komen vrij zodra er geen urenregel meer op de factuur staat.
            var noHourLines = !newLines.Any(l => l.Unit == "uur");
            var released = entries.Where(t => t.InvoiceLineId is { } lineId ? !kept.ContainsKey(lineId) : noHourLines).ToList();
            if (inv.CustomerId != input.CustomerId && entries.Count > released.Count)
                return Results.BadRequest(new { error = "Op deze factuur staan uren van een project; de klant kan niet meer veranderen." });
            var relink = entries.Where(t => t.InvoiceLineId is { } lineId && kept.ContainsKey(lineId)).Select(t => (Entry: t, Line: kept[t.InvoiceLineId!.Value])).ToList();

            foreach (var t in released) { t.InvoiceId = null; t.InvoiceLineId = null; }
            if (released.Count > 0)
                db.Log("factuur", $"{ProjectEndpoints.Hours(released.Sum(t => t.Minutes)):0.##} uur van de conceptfactuur gehaald; die uren staan weer open");
            inv.CustomerId = input.CustomerId; inv.IssueDate = input.IssueDate.Date; inv.DueDate = input.DueDate.Date;
            inv.Reference = input.Reference; inv.VatRegime = input.VatRegime; inv.Notes = input.Notes;
            inv.DeliveryFrom = input.DeliveryFrom?.Date; inv.DeliveryTo = input.DeliveryTo?.Date;
            db.InvoiceLines.RemoveRange(inv.Lines);
            inv.Lines = newLines;
            await db.SaveChangesAsync();
            // De regels zijn opnieuw opgeslagen; de uren wijzen weer naar hun (nieuwe) regel.
            foreach (var (entry, line) in relink) entry.InvoiceLineId = line.Id;

            var (problem, becamePaid) = await ChangeStatusAsync(db, clock, inv, input.Status, input.PaidAt);
            if (problem is not null) return problem.ToResult();
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            if (becamePaid) await TriggerPaid(db, engine, inv);
            return Results.Ok(await Full(db).AsNoTracking().FirstAsync(i => i.Id == id));
        });

        // Alleen de status wijzigen: versturen, verlopen of betaald.
        g.MapPost("/{id:int}/status", async (AppDb db, BusinessClock clock, WorkflowEngine engine, int id, StatusRequest req) =>
        {
            await using (var tx = await WriteLock.BeginAsync(db))
            {
                var inv = await db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id);
                if (inv is null) return Results.NotFound();
                var (problem, becamePaid) = await ChangeStatusAsync(db, clock, inv, req.Status, req.PaidAt);
                if (problem is not null) return problem.ToResult();
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                if (becamePaid) await TriggerPaid(db, engine, inv);
            }
            await MarkOverdue(db, clock);
            return Results.Ok(await Full(db).AsNoTracking().FirstAsync(i => i.Id == id));
        });

        // Een nieuw concept met dezelfde klant en regels en verse datums.
        g.MapPost("/{id:int}/duplicate", async (AppDb db, BusinessClock clock, int id) =>
        {
            var source = await Full(db).AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
            if (source is null) return Results.NotFound();
            if (source.IsCredit) return Results.BadRequest(new { error = "Een creditnota kun je niet kopiëren. Maak een creditnota vanuit de factuur zelf." });
            var settings = await SettingsEndpoints.GetAsync(db);
            var today = clock.Today;
            var inv = new Invoice
            {
                CustomerId = source.CustomerId, IssueDate = today, DueDate = today.AddDays(settings.PaymentTermDays), Reference = source.Reference,
                VatRegime = source.VatRegime, DeliveryFrom = today, Notes = source.Notes, Lines = CopyLines(source.Lines, source.VatRegime),
            };
            db.Invoices.Add(inv);
            db.Log("factuur", $"Conceptfactuur gemaakt als kopie van {Label(source)}");
            await db.SaveChangesAsync();
            return Results.Created($"/api/invoices/{inv.Id}", await Full(db).AsNoTracking().FirstAsync(i => i.Id == inv.Id));
        });

        // Creditnota: dezelfde regels met negatieve aantallen, vast gekoppeld aan de factuur die hij corrigeert.
        g.MapPost("/{id:int}/credit", async (AppDb db, BusinessClock clock, int id) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            var source = await Full(db).AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
            if (source is null) return Results.NotFound();
            if (source.Status == InvoiceStatus.Draft) return Results.BadRequest(new { error = "Een concept kun je gewoon aanpassen of verwijderen." });
            if (source.IsCredit) return Results.BadRequest(new { error = "Dit is al een creditnota; die kun je niet nog eens crediteren." });
            var credits = await db.Invoices.Include(i => i.Lines).Where(i => i.CreditForInvoiceId == id).ToListAsync();
            if (credits.Count > 0 && credits.Sum(Total) <= -Total(source))
                return Results.Conflict(new
                {
                    error = $"Factuur {source.Number} is al voor het volledige bedrag gecrediteerd met {string.Join(", ", credits.Select(Label))}. Pas die aan zolang het een concept is.",
                });
            var today = clock.Today;
            var inv = new Invoice
            {
                CustomerId = source.CustomerId, IssueDate = today, DueDate = today, Reference = source.Reference, VatRegime = source.VatRegime,
                DeliveryFrom = source.DeliveryFrom, DeliveryTo = source.DeliveryTo, CreditForInvoiceId = source.Id,
                Lines = CopyLines(source.Lines, source.VatRegime, -1),
            };
            db.Invoices.Add(inv);
            db.Log("factuur", $"Conceptcreditnota gemaakt voor factuur {source.Number}");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Created($"/api/invoices/{inv.Id}", await Full(db).AsNoTracking().FirstAsync(i => i.Id == inv.Id));
        });

        // Zet de open uren van een project van dezelfde klant erbij op deze conceptfactuur.
        g.MapPost("/{id:int}/hours", async (AppDb db, int id, AddHoursRequest req) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            var inv = await db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id);
            if (inv is null) return Results.NotFound();
            if (inv.Status != InvoiceStatus.Draft) return Results.BadRequest(new { error = "Uren toevoegen kan alleen op een concept" });
            if (inv.CreditForInvoiceId is not null) return Results.BadRequest(new { error = "Op een creditnota kun je geen uren zetten" });
            var project = await db.Projects.FindAsync(req.ProjectId);
            if (project is null || project.CustomerId != inv.CustomerId) return Results.BadRequest(new { error = "Dit project hoort niet bij de klant van deze factuur" });
            if (project.Billing != Billing.Hourly) return Results.BadRequest(new { error = "Dit project heeft een vaste prijs" });
            var entries = await ProjectEndpoints.OpenEntries(db, project.Id);
            if (entries.Count == 0) return Results.BadRequest(new { error = "Er zijn geen open uren op dit project" });
            // Een leeg beginregeltje van een nieuwe factuur hoeft niet te blijven staan.
            db.InvoiceLines.RemoveRange(inv.Lines.Where(l => string.IsNullOrWhiteSpace(l.Description) || (l.Description == "-" && l.UnitPrice == 0)));
            var parts = ProjectEndpoints.HourLines(project, entries, req.Detailed);
            foreach (var line in parts.Select(p => p.Line))
            {
                if (VatRegimes.ZeroVat(inv.VatRegime)) line.VatRate = 0;
                inv.Lines.Add(line);
            }
            await db.SaveChangesAsync();
            if (!await ProjectEndpoints.ClaimAsync(db, inv.Id, parts)) return Results.Conflict(new { error = HoursTaken });
            await SetDeliveryFromHoursAsync(db, inv);
            db.Log("factuur", $"{ProjectEndpoints.Hours(entries.Sum(e => e.Minutes)):0.##} uur van {project.Name} op {Label(inv).ToLowerInvariant()} gezet");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(await Full(db).AsNoTracking().FirstAsync(i => i.Id == id));
        });

        // Alleen een concept mag weg. Een verstuurde factuur moet je bewaren; corrigeren doe je met een creditnota.
        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var inv = await db.Invoices.FindAsync(id);
            if (inv is null) return Results.NotFound();
            if (inv.Status != InvoiceStatus.Draft)
                return Results.Conflict(new { error = "Een verstuurde factuur kun je niet verwijderen: je moet hem 7 jaar bewaren. Maak een creditnota om hem te corrigeren." });
            db.Invoices.Remove(inv);
            db.Log("factuur", inv.CreditForInvoiceId is null ? "Conceptfactuur verwijderd" : "Conceptcreditnota verwijderd");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
