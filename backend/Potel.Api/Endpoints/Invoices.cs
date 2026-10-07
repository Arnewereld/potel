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
    public const string ChangedElsewhere = "Deze factuur is intussen ergens anders opgeslagen, bijvoorbeeld in een ander tabblad. Vernieuw de pagina en probeer het opnieuw.";
    public const string FullyCreditedError = "Deze factuur is helemaal gecrediteerd met een creditnota. Er valt niets meer te betalen.";

    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    [GeneratedRegex(@"^NL\d{9}B\d{2}$")]
    private static partial Regex DutchVatId();

    public static decimal Total(Invoice i) => Money.Totals(i.Lines).Total;

    // Hoe de factuur heet in het logboek en in meldingen.
    public static string Label(Invoice i) => i.Number is null ? "Conceptfactuur" : $"{(i.IsCredit ? "Creditnota" : "Factuur")} {i.Number}";

    static string CustomerName(Customer c) => string.IsNullOrWhiteSpace(c.Company) ? c.Name : c.Company;

    static string Euro(decimal d) => d.ToString("C", Dutch);

    static string Join(List<string> items) => items.Count == 1 ? items[0] : $"{string.Join(", ", items[..^1])} en {items[^1]}";

    // Verzonden facturen voorbij de vervaldatum gaan op verlopen. Een creditnota hoeft niemand te betalen en verloopt nooit,
    // en een factuur die met creditnota's helemaal is tegengeboekt ook niet.
    public static async Task MarkOverdue(AppDb db, BusinessClock clock)
    {
        var today = clock.Today;
        var overdue = (await db.Invoices.Include(i => i.Lines).Include(i => i.Credits).ThenInclude(c => c.Lines)
                .Where(i => i.Status == InvoiceStatus.Sent && i.DueDate < today && i.CreditForInvoiceId == null).ToListAsync())
            .Where(i => !i.IsCredit && !i.FullyCredited).ToList();
        if (overdue.Count == 0) return;
        foreach (var i in overdue) { i.Status = InvoiceStatus.Overdue; db.Log("factuur", $"{Label(i)} is verlopen"); }
        await db.SaveChangesAsync();
    }

    // Het volgende nummer in de reeks van het jaar van de factuurdatum, per werkruimte en zonder gaten: 2026-0001, 2026-0002, ...
    // Voorbeeldfacturen hebben een eigen reeks en tellen niet mee.
    // Alleen binnen een WriteLock gebruiken om een nummer te geven, anders kunnen twee verzoeken hetzelfde nummer krijgen.
    public static async Task<string> NextNumber(AppDb db, int year)
    {
        var prefix = $"{year}-";
        var numbers = await db.Invoices.Where(i => !i.IsDemo && i.Number != null && i.Number.StartsWith(prefix)).Select(i => i.Number!).ToListAsync();
        var max = numbers.Select(n => int.TryParse(n[prefix.Length..], out var x) ? x : 0).DefaultIfEmpty(0).Max();
        return $"{prefix}{max + 1:0000}";
    }

    // Controles bij opslaan van een concept. Een creditnota volgt de btw-regeling van de factuur die hij corrigeert; die
    // klopte toen hij verstuurd werd, ook als de klant inmiddels verhuisd is of zijn btw-nummer kwijt is.
    static async Task<string?> ValidateAsync(AppDb db, Invoice input, bool credit = false)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == input.CustomerId);
        if (customer is null) return "Kies een klant";
        if (!InvoiceStatus.All.Contains(input.Status)) return "Onbekende status";
        if (input.Lines.Count == 0) return "Voeg minstens één regel toe";
        if (input.Lines.Any(l => string.IsNullOrWhiteSpace(l.Description))) return "Elke regel heeft een omschrijving nodig";
        if (input.Lines.Any(Money.TooLarge)) return Money.TooLargeError;
        if (input.DueDate < input.IssueDate) return "De vervaldatum ligt voor de factuurdatum";
        if (input.DeliveryFrom is { } from && input.DeliveryTo is { } to && to < from) return "De leverperiode eindigt voordat hij begint";
        if (!VatRegimes.All.Contains(input.VatRegime)) return "Kies een btw-regeling";
        return credit ? null : RegimeError(input.VatRegime, customer);
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
    // Een creditnota heeft de klantgegevens van de factuur die hij corrigeert al bij zich; anders tellen die van de klant nu.
    public static List<string> Missing(Invoice inv, Settings s, Customer c)
    {
        var buyer = inv.Buyer ?? InvoiceParty.Buyer(c);
        var missing = new List<string>();
        void Need(string? value, string label) { if (string.IsNullOrWhiteSpace(value)) missing.Add(label); }
        Need(s.CompanyName, "je bedrijfsnaam");
        Need(s.Address, "je adres");
        Need(s.City, "je postcode en plaats");
        Need(s.Kvk, "je KvK-nummer");
        Need(s.Btw, "je btw-id");
        Need(buyer.Name, "de naam van de klant");
        Need(buyer.Address, "het adres van de klant");
        Need(buyer.City, "de postcode en plaats van de klant");
        if (inv.VatRegime == VatRegimes.ReverseCharge) Need(buyer.VatNumber, "het btw-nummer van de klant");
        if (inv.DeliveryFrom is null) missing.Add("de leverdatum of periode");
        if (inv.Lines.Count == 0) missing.Add("minstens één regel");
        return missing;
    }

    static async Task<InvoiceProblem?> CheckSendableAsync(AppDb db, Invoice inv, Settings s, Customer c)
    {
        var missing = Missing(inv, s, c);
        if (missing.Count > 0) return new($"Vul eerst {Join(missing)} in. Daarna kun je de factuur versturen.", missing);
        // Jij factureert altijd als Nederlands bedrijf, dus je btw-id is altijd NL, 9 cijfers, B en 2 cijfers.
        if (!DutchVatId().IsMatch(InvoiceParty.NormalizeVatId(s.Btw)!))
            return new("Je btw-id klopt niet. Een Nederlands btw-id is NL, 9 cijfers, B en 2 cijfers, bijvoorbeeld NL001234567B01. Pas het aan bij Instellingen.");
        if (inv.CreditForInvoiceId is null && RegimeError(inv.VatRegime, c) is { } regimeError) return new(regimeError);

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
    // De factuurdatum is de dag van versturen: een oud concept wordt niet teruggedateerd (en is dus niet meteen verlopen),
    // en de nummers lopen gelijk op met de datums. De vervaldatum schuift mee, zodat de betaaltermijn hetzelfde blijft.
    // Een creditnota houdt de klantgegevens van de factuur die hij corrigeert. Alleen binnen een WriteLock aanroepen.
    static async Task<InvoiceProblem?> SendAsync(AppDb db, BusinessClock clock, Invoice inv)
    {
        var settings = await SettingsEndpoints.GetAsync(db);
        var customer = await db.Customers.FirstAsync(c => c.Id == inv.CustomerId);
        if (await CheckSendableAsync(db, inv, settings, customer) is { } problem) return problem;
        var today = clock.Today;
        if (inv.IssueDate.Date != today)
        {
            var term = inv.DueDate.Date - inv.IssueDate.Date;
            inv.IssueDate = today;
            inv.DueDate = today + (term < TimeSpan.Zero ? TimeSpan.Zero : term);
        }
        inv.Number = await NextNumber(db, inv.IssueDate.Year);
        inv.Seller = InvoiceParty.Seller(settings);
        inv.Buyer = inv.CreditForInvoiceId is not null && inv.Buyer is { } original ? original : InvoiceParty.Buyer(customer);
        inv.SentAt = DateTime.UtcNow;
        return null;
    }

    // Is de factuur met verstuurde creditnota's (eventueel met deze erbij) helemaal tegengeboekt?
    static async Task<bool> FullyCreditedAsync(AppDb db, int invoiceId, Invoice? sending = null)
    {
        var original = await db.Invoices.Include(i => i.Lines).FirstAsync(i => i.Id == invoiceId);
        var credited = (await db.Invoices.Include(i => i.Lines)
            .Where(i => i.CreditForInvoiceId == invoiceId && i.Status != InvoiceStatus.Draft && (sending == null || i.Id != sending.Id)).ToListAsync()).Sum(Total);
        if (sending is not null) credited += Total(sending);
        return credited < 0 && Total(original) + credited <= 0;
    }

    // Is een factuur helemaal gecrediteerd, dan komen zijn uren weer vrij: zo kun je ze op een nieuwe, goede factuur zetten.
    static async Task ReleaseCreditedHoursAsync(AppDb db, Invoice credit)
    {
        if (credit.CreditForInvoiceId is not { } originalId || !await FullyCreditedAsync(db, originalId, credit)) return;
        var entries = await db.TimeEntries.Where(t => t.InvoiceId == originalId).ToListAsync();
        if (entries.Count == 0) return;
        foreach (var t in entries) { t.InvoiceId = null; t.InvoiceLineId = null; }
        var number = await db.Invoices.Where(i => i.Id == originalId).Select(i => i.Number).FirstAsync();
        db.Log("factuur", $"{ProjectEndpoints.Hours(entries.Sum(t => t.Minutes)):0.##} uur van factuur {number} staan weer open: die factuur is helemaal gecrediteerd");
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
        // Een helemaal gecrediteerde factuur hoeft niemand meer te betalen; "betaald" zou omzet en de werkstroom "Factuur betaald" starten.
        if (status == InvoiceStatus.Paid && inv.Status != InvoiceStatus.Draft && inv.CreditForInvoiceId is null && await FullyCreditedAsync(db, inv.Id))
            return (new(FullyCreditedError, Status: StatusCodes.Status409Conflict), false);
        var sending = inv.Status == InvoiceStatus.Draft;
        if (sending && await SendAsync(db, clock, inv) is { } problem) return (problem, false);
        if (sending && inv.CreditForInvoiceId is not null) await ReleaseCreditedHoursAsync(db, inv);
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

    // Regels overnemen, in dezelfde volgorde. Bij btw verlegd, KOR, vrijgesteld of buiten de EU staat er geen btw op de regels.
    static List<InvoiceLine> CopyLines(IEnumerable<InvoiceLine> lines, string regime, decimal sign = 1) =>
        lines.Select((l, i) => new InvoiceLine
        {
            Description = l.Description, Quantity = sign * l.Quantity, Unit = string.IsNullOrWhiteSpace(l.Unit) ? "stuk" : l.Unit.Trim(),
            UnitPrice = l.UnitPrice, VatRate = VatRegimes.ZeroVat(regime) ? 0 : l.VatRate, Position = i,
        }).ToList();

    // Een factuur met alles wat de frontend toont: regels op volgorde, klant, de factuur die hij crediteert en zijn creditnota's.
    static IQueryable<Invoice> Full(AppDb db) =>
        db.Invoices.Include(i => i.Lines.OrderBy(l => l.Position).ThenBy(l => l.Id)).Include(i => i.Customer).Include(i => i.CreditFor)
            .Include(i => i.Credits).ThenInclude(c => c.Lines).AsSplitQuery();

    // De leverperiode van de uren op deze factuur.
    public static async Task SetDeliveryFromHoursAsync(AppDb db, Invoice inv)
    {
        var dates = await db.TimeEntries.Where(t => t.InvoiceId == inv.Id).Select(t => t.Date).ToListAsync();
        if (dates.Count == 0) return;
        inv.DeliveryFrom = dates.Min().Date;
        inv.DeliveryTo = dates.Max().Date;
    }

    // Start werkstromen met de trigger "Factuur betaald".
    public static async Task TriggerPaid(AppDb db, WorkflowEngine engine, Invoice inv)
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
                .OrderBy(i => i.Status != InvoiceStatus.Draft).ThenBy(i => i.IsDemo).ThenByDescending(i => i.Number).ThenByDescending(i => i.Id).ToListAsync();
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

        // Alleen een concept kan nog veranderen. Een regel met een id blijft dezelfde regel (met zijn uren); regels zonder id
        // komen erbij en regels die er niet meer in staan gaan eraf en geven hun uren weer vrij. Twee keer hetzelfde opslaan
        // (een dubbelklik) verandert dus niets. Staat er een id in dat niet (meer) op de factuur staat, dan was de factuur
        // intussen ergens anders opgeslagen: dan liever een melding dan uren die stilletjes vrijkomen.
        g.MapPut("/{id:int}", async (AppDb db, BusinessClock clock, WorkflowEngine engine, int id, Invoice input) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            var inv = await db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id);
            if (inv is null) return Results.NotFound();
            if (inv.Status != InvoiceStatus.Draft) return Results.Conflict(new { error = FrozenError });
            // Een creditnota hoort bij de klant en de btw-regeling van de factuur die hij corrigeert.
            var credit = inv.CreditForInvoiceId is not null;
            if (credit) { input.CustomerId = inv.CustomerId; input.VatRegime = inv.VatRegime; }
            if (await ValidateAsync(db, input, credit) is { } error) return Results.BadRequest(new { error });
            var rows = inv.Lines.ToDictionary(l => l.Id);
            if (input.Lines.Any(l => l.Id != 0 && !rows.ContainsKey(l.Id))) return Results.Conflict(new { error = ChangedElsewhere });

            var newLines = CopyLines(input.Lines, input.VatRegime);
            // Elke bestaande regel hooguit één keer; een id dat twee keer voorkomt, wordt de tweede keer een nieuwe regel.
            var keptIds = new HashSet<int>();
            var plan = input.Lines.Select((l, i) => (Row: rows.TryGetValue(l.Id, out var row) && keptIds.Add(l.Id) ? row : null, Line: newLines[i])).ToList();
            var entries = await db.TimeEntries.Where(t => t.InvoiceId == id).ToListAsync();
            // Uren van een oudere versie hebben geen regel; die komen vrij zodra er geen urenregel meer op de factuur staat.
            var noHourLines = !newLines.Any(l => l.Unit == "uur");
            var released = entries.Where(t => t.InvoiceLineId is { } lineId ? !keptIds.Contains(lineId) : noHourLines).ToList();
            if (inv.CustomerId != input.CustomerId && entries.Count > released.Count)
                return Results.BadRequest(new { error = "Op deze factuur staan uren van een project; de klant kan niet meer veranderen." });

            foreach (var t in released) { t.InvoiceId = null; t.InvoiceLineId = null; }
            if (released.Count > 0)
                db.Log("factuur", $"{ProjectEndpoints.Hours(released.Sum(t => t.Minutes)):0.##} uur van de conceptfactuur gehaald; die uren staan weer open");
            inv.CustomerId = input.CustomerId; inv.IssueDate = input.IssueDate.Date; inv.DueDate = input.DueDate.Date;
            inv.Reference = input.Reference; inv.VatRegime = input.VatRegime; inv.Notes = input.Notes;
            inv.DeliveryFrom = input.DeliveryFrom?.Date; inv.DeliveryTo = input.DeliveryTo?.Date;
            var removed = inv.Lines.Where(l => !keptIds.Contains(l.Id)).ToList();
            db.InvoiceLines.RemoveRange(removed);
            inv.Lines.RemoveAll(removed.Contains);
            foreach (var (row, line) in plan)
            {
                if (row is null) { inv.Lines.Add(line); continue; }
                row.Description = line.Description; row.Quantity = line.Quantity; row.Unit = line.Unit;
                row.UnitPrice = line.UnitPrice; row.VatRate = line.VatRate; row.Position = line.Position;
            }
            await db.SaveChangesAsync();

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
            if (source.IsDemo) return Results.BadRequest(new { error = "Dit is een voorbeeldfactuur. Die kun je gewoon verwijderen." });
            var credits = await db.Invoices.Include(i => i.Lines).Where(i => i.CreditForInvoiceId == id).ToListAsync();
            if (credits.Count > 0 && credits.Sum(Total) <= -Total(source))
                return Results.Conflict(new
                {
                    error = $"Factuur {source.Number} is al voor het volledige bedrag gecrediteerd met {string.Join(", ", credits.Select(Label))}. Pas die aan zolang het een concept is.",
                });
            var today = clock.Today;
            // De klantgegevens komen van de factuur zelf: een creditnota corrigeert die levering, aan die klant zoals hij toen was.
            var inv = new Invoice
            {
                CustomerId = source.CustomerId, IssueDate = today, DueDate = today, Reference = source.Reference, VatRegime = source.VatRegime,
                DeliveryFrom = source.DeliveryFrom, DeliveryTo = source.DeliveryTo, CreditForInvoiceId = source.Id, Buyer = source.Buyer?.Copy(),
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
            // De nieuwe regels komen onderaan.
            var position = inv.Lines.Select(l => l.Position).DefaultIfEmpty(-1).Max() + 1;
            foreach (var line in parts.Select(p => p.Line))
            {
                if (VatRegimes.ZeroVat(inv.VatRegime)) line.VatRate = 0;
                line.Position = position++;
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
        // Een voorbeeldfactuur uit de welkomstwizard is nooit echt verstuurd en mag altijd weg.
        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var inv = await db.Invoices.FindAsync(id);
            if (inv is null) return Results.NotFound();
            if (inv.Status != InvoiceStatus.Draft && !inv.IsDemo)
                return Results.Conflict(new { error = "Een verstuurde factuur kun je niet verwijderen: je moet hem 7 jaar bewaren. Maak een creditnota om hem te corrigeren." });
            if (await db.Invoices.AnyAsync(i => i.CreditForInvoiceId == id))
                return Results.Conflict(new { error = "Bij deze factuur hoort een creditnota. Verwijder die eerst." });
            db.Invoices.Remove(inv);
            db.Log("factuur", inv.IsDemo ? $"Voorbeeldfactuur {inv.Number} verwijderd" : inv.CreditForInvoiceId is null ? "Conceptfactuur verwijderd" : "Conceptcreditnota verwijderd");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
