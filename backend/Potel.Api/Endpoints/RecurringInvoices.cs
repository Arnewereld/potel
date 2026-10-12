using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Potel.Api.Data;
using Potel.Api.Invoicing;
using Potel.Api.Workflows;

namespace Potel.Api.Endpoints;

public record RecurringLineInput(string? Description, decimal Quantity, string? Unit, decimal UnitPrice, decimal VatRate);

public record RecurringInput(
    string? Name, int CustomerId, string? VatRegime, string? Interval, string? PeriodRule, string? Action,
    DateTime StartDate, DateTime? EndDate, string? Reference, string? Notes, List<RecurringLineInput>? Lines);

public record RecurringPeriodDto(DateTime From, DateTime To, DateTime RunDate, string Label);

public record RecurringInvoiceRef(int Id, string? Number, string Status, DateTime? PeriodStart, string? Period, decimal Total);

public record RecurringDto(
    int Id, string Name, int CustomerId, string? CustomerName, string VatRegime, string Interval, string PeriodRule, string Action,
    DateTime StartDate, DateTime? EndDate, DateTime NextPeriodStart, DateTime? NextRunDate, RecurringPeriodDto? NextPeriod, bool Active,
    string State, string? Reference, string? Notes, List<RecurringInvoiceLine> Lines, InvoiceTotals Totals, int InvoiceCount, bool Locked,
    bool Demo, DateTime CreatedAt, List<RecurringInvoiceRef>? Invoices);

// Wat een nieuwe of gewijzigde terugkerende factuur gaat doen: de eerstvolgende perioden, hoeveel er meteen klaarstaan
// en hoe de regels van de eerste factuur eruitzien.
public record RecurringPreview(List<RecurringPeriodDto> Periods, int DueNow, List<string> Lines, string? Error);

// Terugkerende facturen voor onderhoud, hosting en retainers. Iedereen in de werkruimte kan ze bekijken; alleen een beheerder
// maakt, wijzigt, pauzeert of verwijdert ze, of maakt er handmatig een factuur van.
public static class RecurringInvoiceEndpoints
{
    public const string LockedError = "Deze terugkerende factuur heeft al facturen gemaakt. De klant, de startdatum en hoe vaak hij komt liggen daarom vast. Wil je iets anders? Stop deze en maak een nieuwe.";
    public const string DemoError = "Dit is een voorbeeld uit de welkomstwizard; die maakt geen echte facturen. Maak een eigen terugkerende factuur voor een echte klant.";
    public const string DemoCustomerError = "Dit is een voorbeeldklant. Voor voorbeeldklanten maakt Potel geen facturen. Kies een echte klant.";
    public const string SendNotPossible = "Direct versturen kan pas als er een mailserver is ingesteld. Kies \"Concept klaarzetten\", of stel eerst een mailserver in.";
    static readonly decimal[] VatRates = [21, 9, 0];

    static string CustomerName(Customer? c) => c is null ? "" : string.IsNullOrWhiteSpace(c.Company) ? c.Name : c.Company;

    static RecurringPeriodDto Dto(RecurringPeriod p) => new(p.From, p.To, p.RunDate, RecurringPeriods.Label(p.From, p.To));

    static string State(RecurringInvoice t) =>
        t.NextRunDate is null ? "afgelopen" : !t.Active ? "gepauzeerd" : "actief";

    static InvoiceTotals Totals(RecurringInvoice t) => Money.Totals(t.Lines.Select(l => new InvoiceLine
    {
        Quantity = l.Quantity, UnitPrice = l.UnitPrice, VatRate = VatRegimes.ZeroVat(t.VatRegime) ? 0 : l.VatRate,
    }));

    static RecurringDto ToDto(RecurringInvoice t, int invoiceCount, List<RecurringInvoiceRef>? invoices = null) => new(
        t.Id, t.Name, t.CustomerId, CustomerName(t.Customer), t.VatRegime, t.Interval, t.PeriodRule, t.Action, t.StartDate, t.EndDate,
        t.NextPeriodStart, t.NextRunDate, t.NextRunDate is null ? null : Dto(RecurringPeriods.Period(t, t.NextPeriodStart)), t.Active, State(t),
        t.Reference, t.Notes, t.Lines.OrderBy(l => l.Position).ThenBy(l => l.Id).ToList(), Totals(t), invoiceCount,
        Locked(t, invoiceCount), RecurringInvoiceService.IsDemo(t), t.CreatedAt, invoices);

    // Na de eerste factuur (of een overgeslagen periode) liggen klant, startdatum en interval vast.
    static bool Locked(RecurringInvoice t, int invoiceCount) => invoiceCount > 0 || t.NextPeriodStart > t.StartDate;

    static IQueryable<RecurringInvoice> Full(AppDb db) => db.RecurringInvoices.Include(t => t.Lines).Include(t => t.Customer).AsSplitQuery();

    static async Task<Dictionary<int, int>> CountsAsync(AppDb db) =>
        await db.Invoices.Where(i => i.RecurringInvoiceId != null).GroupBy(i => i.RecurringInvoiceId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);

    static async Task<RecurringDto> OneAsync(AppDb db, int id)
    {
        var t = await Full(db).AsNoTracking().FirstAsync(x => x.Id == id);
        var invoices = (await db.Invoices.AsNoTracking().Include(i => i.Lines).Where(i => i.RecurringInvoiceId == id).ToListAsync())
            .OrderByDescending(i => i.RecurringPeriodStart).ThenByDescending(i => i.Id)
            .Select(i => new RecurringInvoiceRef(i.Id, i.Number, i.Status, i.RecurringPeriodStart,
                i.DeliveryFrom is { } from && i.DeliveryTo is { } to ? RecurringPeriods.Label(from, to) : null, InvoiceEndpoints.Total(i))).ToList();
        return ToDto(t, invoices.Count, invoices);
    }

    // Controleert de invoer en zet hem op de terugkerende factuur. De klant moet in de eigen werkruimte bestaan.
    static async Task<string?> ApplyAsync(AppDb db, RecurringInvoice t, RecurringInput input, bool locked, bool canEmail)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == input.CustomerId);
        if (customer is null) return "Kies een klant uit je eigen werkruimte.";
        if (customer.IsDemo && !t.IsDemo) return DemoCustomerError;
        var name = input.Name?.Trim() ?? "";
        if (name.Length > 200) return "De naam is te lang.";
        var regime = input.VatRegime ?? VatRegimes.Normal;
        if (!VatRegimes.All.Contains(regime)) return "Kies een btw-regeling.";
        if (InvoiceEndpoints.RegimeError(regime, customer) is { } regimeError) return regimeError;
        var interval = input.Interval ?? RecurringIntervals.Monthly;
        if (!RecurringIntervals.All.Contains(interval)) return "Kies hoe vaak de factuur komt: elke maand, elk kwartaal of elk jaar.";
        var rule = input.PeriodRule ?? RecurringPeriodRules.InAdvance;
        if (!RecurringPeriodRules.All.Contains(rule)) return "Kies of de factuur vooraf of achteraf komt.";
        var action = input.Action ?? RecurringActions.Draft;
        if (!RecurringActions.All.Contains(action)) return "Kies wat er met elke factuur gebeurt.";
        if (input.StartDate == default || input.StartDate.Year < 2000 || input.StartDate.Year > 2100) return "Vul de startdatum in.";
        if (input.EndDate is { } end && (end.Year > 2100 || end.Date < input.StartDate.Date)) return "De einddatum ligt voor de startdatum.";
        var lines = input.Lines ?? [];
        if (lines.Count == 0) return "Voeg minstens één regel toe.";
        if (lines.Count > 100) return "Een factuur kan hooguit 100 regels hebben.";
        if (lines.Any(l => string.IsNullOrWhiteSpace(l.Description))) return "Elke regel heeft een omschrijving nodig.";
        if (lines.Any(l => l.Description!.Length > 1000)) return "Een omschrijving is te lang.";
        if (lines.Any(l => Math.Abs(l.Quantity) > Money.MaxQuantity || Math.Abs(l.UnitPrice) > Money.MaxAmount)) return Money.TooLargeError;
        if (!VatRegimes.ZeroVat(regime) && lines.Any(l => !VatRates.Contains(l.VatRate))) return "Kies 21%, 9% of 0% btw per regel.";
        var total = Money.Totals(lines.Select(l => new InvoiceLine { Quantity = l.Quantity, UnitPrice = l.UnitPrice, VatRate = l.VatRate })).Total;
        if (total < 0) return "Het totaal is negatief. Een terugkerende factuur moet een bedrag hebben dat de klant betaalt.";
        if (locked && (t.CustomerId != input.CustomerId || t.StartDate.Date != input.StartDate.Date || t.Interval != interval)) return LockedError;
        if (action == RecurringActions.Send && !canEmail) return SendNotPossible;

        t.Name = name == "" ? $"{lines[0].Description!.Trim()}" : name;
        if (t.Name.Length > 200) t.Name = t.Name[..200].Trim();
        t.CustomerId = customer.Id; t.Customer = customer; t.VatRegime = regime; t.Interval = interval; t.PeriodRule = rule; t.Action = action;
        t.StartDate = input.StartDate.Date; t.EndDate = input.EndDate?.Date;
        t.Reference = string.IsNullOrWhiteSpace(input.Reference) ? null : input.Reference.Trim();
        t.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        t.Lines.Clear();
        t.Lines.AddRange(lines.Select((l, i) => new RecurringInvoiceLine
        {
            Description = l.Description!.Trim(), Quantity = l.Quantity, Unit = string.IsNullOrWhiteSpace(l.Unit) ? "stuk" : l.Unit.Trim(),
            UnitPrice = l.UnitPrice, VatRate = VatRegimes.ZeroVat(regime) ? 0 : l.VatRate, Position = i,
        }));
        return null;
    }

    // Zou de terugkerende factuur meteen meer facturen klaarzetten dan we na een storing zouden inhalen? Dan liever een andere startdatum.
    static string? BacklogError(RecurringInvoice t, DateTime today, int max)
    {
        var due = RecurringPeriods.DueCount(t, today, max);
        return due > max
            ? $"Met deze startdatum zouden er meteen meer dan {max} facturen klaarstaan. Kies een latere startdatum, of maak de oudere facturen zelf."
            : null;
    }

    public static void MapRecurringInvoices(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/recurring-invoices");

        g.MapGet("/", async (AppDb db, int? customerId) =>
        {
            var counts = await CountsAsync(db);
            var list = await Full(db).AsNoTracking().Where(t => customerId == null || t.CustomerId == customerId).ToListAsync();
            return list.OrderBy(t => t.NextRunDate == null).ThenBy(t => !t.Active).ThenBy(t => t.NextRunDate).ThenBy(t => t.Name)
                .Select(t => ToDto(t, counts.GetValueOrDefault(t.Id)));
        });

        g.MapGet("/{id:int}", async (AppDb db, int id) =>
            await db.RecurringInvoices.AnyAsync(t => t.Id == id) ? Results.Ok(await OneAsync(db, id)) : Results.NotFound());

        // Rekent uit wat deze instellingen zouden doen, zonder iets op te slaan. Met een id: vanaf de volgende periode van die factuur.
        g.MapPost("/preview", async (AppDb db, BusinessClock clock, IOptions<InvoicingLimits> limits, RecurringInput input, int? id) =>
        {
            var existing = id is { } rid ? await Full(db).AsNoTracking().FirstOrDefaultAsync(t => t.Id == rid) : null;
            // Het voorbeeld uit de welkomstwizard mag zijn voorbeeldklant houden.
            var t = new RecurringInvoice { IsDemo = existing?.IsDemo ?? false };
            var error = await ApplyAsync(db, t, input with { Action = RecurringActions.Draft }, locked: false, canEmail: true);
            if (error is not null) return Results.Ok(new RecurringPreview([], 0, [], error));
            var sameGrid = existing is not null && existing.StartDate == t.StartDate && existing.Interval == t.Interval;
            RecurringPeriods.Schedule(t, sameGrid ? existing!.NextPeriodStart : t.StartDate);
            var max = Math.Max(1, limits.Value.MaxCatchUpPeriods);
            var periods = RecurringPeriods.Upcoming(t).Take(3).Select(Dto).ToList();
            var first = periods.FirstOrDefault();
            var lines = first is null ? [] : t.Lines.Select(l => RecurringPeriods.Fill(l.Description, first.From, first.To)).ToList();
            return Results.Ok(new RecurringPreview(periods, RecurringPeriods.DueCount(t, clock.Today, max), lines, BacklogError(t, clock.Today, max)));
        });

        g.MapPost("/", async (AppDb db, BusinessClock clock, IOptions<InvoicingLimits> limits, RecurringInvoiceService service, IEmailSender email, RecurringInput input) =>
        {
            var t = new RecurringInvoice();
            if (await ApplyAsync(db, t, input, locked: false, email.Configured) is { } error) return Results.BadRequest(new { error });
            RecurringPeriods.Schedule(t, t.StartDate);
            if (BacklogError(t, clock.Today, Math.Max(1, limits.Value.MaxCatchUpPeriods)) is { } backlog) return Results.BadRequest(new { error = backlog });
            db.RecurringInvoices.Add(t);
            db.Log("factuur", $"Terugkerende factuur \"{t.Name}\" voor {CustomerName(t.Customer)} aangemaakt");
            await db.SaveChangesAsync();
            // Is de eerste periode al aan de beurt, dan staat de factuur meteen klaar.
            await service.RunOneAsync(t.Id);
            return Results.Created($"/api/recurring-invoices/{t.Id}", await OneAsync(db, t.Id));
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        g.MapPut("/{id:int}", async (AppDb db, BusinessClock clock, IOptions<InvoicingLimits> limits, RecurringInvoiceService service, IEmailSender email, int id, RecurringInput input) =>
        {
            await using (var tx = await WriteLock.BeginAsync(db))
            {
                var t = await Full(db).FirstOrDefaultAsync(x => x.Id == id);
                if (t is null) return Results.NotFound();
                var locked = Locked(t, await db.Invoices.CountAsync(i => i.RecurringInvoiceId == id));
                db.RecurringInvoiceLines.RemoveRange(t.Lines);
                if (await ApplyAsync(db, t, input, locked, email.Configured) is { } error) return Results.BadRequest(new { error });
                // Zolang er niets gemaakt is, begint hij gewoon bij de (nieuwe) startdatum. Daarna loopt hij door bij de volgende periode.
                RecurringPeriods.Schedule(t, locked ? t.NextPeriodStart : t.StartDate);
                if (!locked && BacklogError(t, clock.Today, Math.Max(1, limits.Value.MaxCatchUpPeriods)) is { } backlog) return Results.BadRequest(new { error = backlog });
                db.Log("factuur", $"Terugkerende factuur \"{t.Name}\" bijgewerkt");
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            if (await db.RecurringInvoices.AsNoTracking().AnyAsync(t => t.Id == id && t.Active)) await service.RunOneAsync(id);
            return Results.Ok(await OneAsync(db, id));
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        g.MapPost("/{id:int}/pause", async (AppDb db, int id) =>
        {
            await using (var tx = await WriteLock.BeginAsync(db))
            {
                var t = await db.RecurringInvoices.FirstOrDefaultAsync(x => x.Id == id);
                if (t is null) return Results.NotFound();
                if (t.Active)
                {
                    t.Active = false;
                    db.Log("factuur", $"Terugkerende factuur \"{t.Name}\" gepauzeerd");
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                }
            }
            return Results.Ok(await OneAsync(db, id));
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Hervatten gaat verder bij de eerstvolgende periode. Perioden die tijdens de pauze aan de beurt waren, slaan we over;
        // een periode die vandaag aan de beurt is, komt er wel meteen.
        g.MapPost("/{id:int}/resume", async (AppDb db, BusinessClock clock, RecurringInvoiceService service, int id) =>
        {
            await using (var tx = await WriteLock.BeginAsync(db))
            {
                var t = await db.RecurringInvoices.FirstOrDefaultAsync(x => x.Id == id);
                if (t is null) return Results.NotFound();
                if (!t.Active)
                {
                    var today = clock.Today;
                    var missed = RecurringPeriods.Upcoming(t).TakeWhile(p => p.RunDate < today).Take(10_000).ToList();
                    if (missed.Count > 0) RecurringPeriods.Schedule(t, RecurringPeriods.Start(t.StartDate, t.Interval, missed[^1].Index + 1));
                    t.Active = true;
                    db.Log("factuur", missed.Count == 0
                        ? $"Terugkerende factuur \"{t.Name}\" hervat"
                        : $"Terugkerende factuur \"{t.Name}\" hervat. {missed.Count} {(missed.Count == 1 ? "periode" : "perioden")} uit de pauze ({RecurringPeriods.Label(missed[0].From, missed[^1].To)}) {(missed.Count == 1 ? "is" : "zijn")} overgeslagen.");
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                }
            }
            await service.RunOneAsync(id);
            return Results.Ok(await OneAsync(db, id));
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Nu aanmaken: de factuur voor de volgende periode, ook als die nog niet aan de beurt is. Daarna schuift hij een periode op.
        g.MapPost("/{id:int}/run", async (AppDb db, RecurringInvoiceService service, int id) =>
        {
            var t = await db.RecurringInvoices.AsNoTracking().Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return Results.NotFound();
            if (RecurringInvoiceService.IsDemo(t)) return Results.BadRequest(new { error = t.IsDemo ? DemoError : DemoCustomerError });
            if (t.NextRunDate is null) return Results.Conflict(new { error = "Deze terugkerende factuur is afgelopen: de einddatum is voorbij. Pas de einddatum aan om verder te gaan." });
            var result = await service.CreateAsync(id, t.NextPeriodStart);
            return result.Outcome switch
            {
                RecurringOutcome.Created => Results.Created($"/api/invoices/{result.Invoice!.Id}", new
                {
                    invoiceId = result.Invoice.Id, number = result.Invoice.Number, status = result.Invoice.Status, fallbackReason = result.FallbackReason,
                    recurring = await OneAsync(db, id),
                }),
                RecurringOutcome.NotFound => Results.NotFound(),
                RecurringOutcome.Finished => Results.Conflict(new { error = "Deze terugkerende factuur is afgelopen: de einddatum is voorbij. Pas de einddatum aan om verder te gaan." }),
                _ => Results.Conflict(new { error = "De factuur voor deze periode is net al gemaakt. Vernieuw de pagina." }),
            };
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Weg met de terugkerende factuur. De facturen die hij al maakte blijven gewoon staan.
        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            // In één schrijfronde, zodat de planner er niet tussendoor nog een factuur aan koppelt.
            await using var tx = await WriteLock.BeginAsync(db);
            var t = await db.RecurringInvoices.FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return Results.NotFound();
            await db.Invoices.Where(i => i.RecurringInvoiceId == id).ExecuteUpdateAsync(x => x.SetProperty(i => i.RecurringInvoiceId, (int?)null));
            db.RecurringInvoices.Remove(t);
            db.Log("factuur", $"Terugkerende factuur \"{t.Name}\" verwijderd");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }
}
