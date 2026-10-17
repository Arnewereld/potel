using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Potel.Api.Data;
using Potel.Api.Invoicing;

namespace Potel.Api.Endpoints;

// Wat de frontend meestuurt voor een vaste kost. FromExpenseId: maak er een van een uitgave die er al staat; die uitgave wordt de
// eerste periode en de startdatum is haar datum.
public record RecurringExpenseInput(
    string? Supplier, string? Description, string? Category, decimal Amount, decimal? VatRate, bool ReverseCharge, string? SupplierRegion,
    int? ProjectId, int? CustomerId, string? Interval, string? Action, DateTime StartDate, DateTime? EndDate, int? FromExpenseId = null);

// Een periode die wacht om geboekt te worden (bij alleen herinneren, of als de planner nog niet langs is geweest).
public record RecurringExpenseDue(DateTime PeriodStart, string Period, int Count);

public record RecurringExpenseDto(
    int Id, string Supplier, string? Description, string Category, decimal Amount, decimal? VatRate, decimal VatAmount, bool ReverseCharge,
    string? SupplierRegion, int? ProjectId, string? ProjectName, int? CustomerId, string? CustomerName, string Interval, string Action,
    DateTime StartDate, DateTime? EndDate, DateTime NextPeriodStart, DateTime? NextRunDate, string? NextPeriod, string State,
    decimal PerMonth, int ExpenseCount, bool Locked, RecurringExpenseDue? Due, string? Hint, bool Demo, DateTime CreatedAt);

// Een uitgave die een vaste kost als concept klaarzette en die je nog moet nakijken.
public record RecurringExpenseReview(int Id, DateTime Date, string Supplier, string? Description, decimal Amount, int? RecurringExpenseId);

// Het overzicht: alle vaste kosten, wat ze samen per maand en per jaar kosten (met btw, zoals je betaalt; afgelopen vaste kosten
// tellen niet mee) en de uitgaven die nog nagekeken moeten worden.
public record RecurringExpenseOverview(List<RecurringExpenseDto> Items, decimal PerMonth, decimal PerYear, List<RecurringExpenseReview> ToReview);

public record RecurringExpensePreview(List<RecurringPeriodDto> Periods, int DueNow, string? Description, string? Error);

// Vaste kosten (abonnementen). Net als bij kosten mag iedereen in de werkruimte ze bekijken, toevoegen, aanpassen en boeken;
// alleen een beheerder verwijdert er een. De uitgaven die een vaste kost maakte blijven dan gewoon staan.
public static class RecurringExpenseEndpoints
{
    public const string LockedError = "Deze vaste kost heeft al uitgaven. De startdatum en hoe vaak hij terugkomt liggen daarom vast. Wil je iets anders? Zet een einddatum en maak een nieuwe.";
    public const string DemoError = "Dit is een voorbeeld uit de welkomstwizard; die maakt geen echte uitgaven. Maak een eigen vaste kost.";
    public const string NotDueError = "Deze periode is nog niet aan de beurt. Je kunt hem boeken vanaf de dag dat het abonnement wordt afgeschreven.";
    public const string FinishedError = "Deze vaste kost is afgelopen: de einddatum is voorbij. Pas de einddatum aan om verder te gaan.";
    public const string RaceError = "Deze periode is net al geboekt of overgeslagen. Vernieuw de pagina.";

    static RecurringPeriodDto Dto(RecurringPeriod p) => new(p.From, p.To, p.RunDate, RecurringPeriods.Label(p.From, p.To));

    static string State(RecurringExpense t, DateTime today) =>
        t.NextRunDate is null ? "afgelopen" : t.NextRunDate <= today ? "wacht" : "actief";

    // Na de eerste uitgave (of een overgeslagen periode) liggen startdatum en interval vast.
    static bool Locked(RecurringExpense t, int expenseCount) => expenseCount > 0 || t.NextPeriodStart > t.StartDate;

    public static decimal PerMonth(RecurringExpense t) => Money.Round(t.Amount / RecurringIntervals.Months(t.Interval));

    // Een hint om op te zeggen, alleen bij een duidelijk teken: het project waar de vaste kost bij hoort is afgerond, of je sloeg
    // de laatste perioden over. Verder weet Potel niet of je een abonnement nog gebruikt, dus dan zeggen we niets.
    static string? Hint(RecurringExpense t, Project? project)
    {
        if (t.NextRunDate is null) return null;
        if (project is { Status: "afgerond" })
            return $"Het project {project.Name} is afgerond. Gebruik je dit abonnement nog? Zeg het anders op en zet hier een einddatum.";
        if (t.SkippedInARow >= 2)
            return $"Je hebt de laatste {t.SkippedInARow} perioden overgeslagen. Loopt dit abonnement nog? Zeg het anders op en zet hier een einddatum.";
        return null;
    }

    static RecurringExpenseDto ToDto(RecurringExpense t, int expenseCount, DateTime today, int maxCatchUp,
        IReadOnlyDictionary<int, Project> projects, IReadOnlyDictionary<int, Customer> customers)
    {
        var project = t.ProjectId is { } p ? projects.GetValueOrDefault(p) : null;
        var customer = t.CustomerId is { } c ? customers.GetValueOrDefault(c) : null;
        var next = t.NextRunDate is null ? null : RecurringPeriods.Period(t, t.NextPeriodStart);
        var dueCount = t.NextRunDate is null ? 0 : RecurringPeriods.DueCount(t, today, maxCatchUp);
        return new(
            t.Id, t.Supplier, t.Description, t.Category, t.Amount, t.VatRate, Expense.VatFor(t.Amount, t.VatRate, t.ReverseCharge), t.ReverseCharge,
            t.SupplierRegion, t.ProjectId, project?.Name, t.CustomerId, customer is null ? null : string.IsNullOrWhiteSpace(customer.Company) ? customer.Name : customer.Company,
            t.Interval, t.Action, t.StartDate, t.EndDate, t.NextPeriodStart, t.NextRunDate, next is null ? null : RecurringExpenseService.Label(next),
            State(t, today), PerMonth(t), expenseCount, Locked(t, expenseCount),
            dueCount > 0 && next is not null ? new RecurringExpenseDue(next.From, RecurringExpenseService.Label(next), dueCount) : null,
            Hint(t, project), t.IsDemo, t.CreatedAt);
    }

    static async Task<Dictionary<int, int>> CountsAsync(AppDb db) =>
        await db.Expenses.Where(e => e.RecurringExpenseId != null).GroupBy(e => e.RecurringExpenseId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);

    static async Task<List<RecurringExpenseDto>> ListAsync(AppDb db, BusinessClock clock, int maxCatchUp, int? id = null)
    {
        var list = await db.RecurringExpenses.AsNoTracking().Where(t => id == null || t.Id == id).ToListAsync();
        var counts = await CountsAsync(db);
        var projects = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id);
        var customers = await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Id);
        var today = clock.Today;
        return list.OrderBy(t => t.NextRunDate == null).ThenBy(t => t.NextRunDate).ThenBy(t => t.Supplier).ThenBy(t => t.Id)
            .Select(t => ToDto(t, counts.GetValueOrDefault(t.Id), today, maxCatchUp, projects, customers)).ToList();
    }

    // Controleert de invoer met dezelfde regels als een uitgave (leverancier, bedrag, btw, project en klant uit de eigen werkruimte)
    // en zet hem op de vaste kost.
    public static async Task<string?> ApplyAsync(AppDb db, RecurringExpense t, RecurringExpenseInput input, bool locked)
    {
        if (input.Description is { Length: > 900 }) return "De omschrijving is te lang.";
        if (input.Amount <= 0) return "Vul het bedrag in dat je elke keer betaalt.";
        var check = new Expense();
        if (await ExpenseEndpoints.ApplyAsync(db, check, new ExpenseInput(input.StartDate, input.Supplier, input.Description, input.Category, input.Amount,
                input.VatRate, null, input.ReverseCharge, input.SupplierRegion, input.ProjectId, input.CustomerId)) is { } error)
            return error == "Vul de datum van de uitgave in." ? "Vul de startdatum in." : error;
        var interval = input.Interval ?? RecurringIntervals.Monthly;
        if (!RecurringIntervals.All.Contains(interval)) return "Kies hoe vaak je betaalt: elke maand, elk kwartaal of elk jaar.";
        var action = input.Action ?? RecurringExpenseActions.Draft;
        if (!RecurringExpenseActions.All.Contains(action)) return "Kies of Potel de uitgave elke keer klaarzet of je alleen herinnert.";
        if (input.EndDate is { } end && (end.Year > 2100 || end.Date < input.StartDate.Date)) return "De einddatum ligt voor de startdatum.";
        if (locked && (t.StartDate.Date != input.StartDate.Date || t.Interval != interval)) return LockedError;

        t.Supplier = check.Supplier; t.Description = check.Description; t.Category = check.Category; t.Amount = check.Amount;
        t.VatRate = check.VatRate; t.ReverseCharge = check.ReverseCharge; t.SupplierRegion = check.SupplierRegion;
        t.ProjectId = check.ProjectId; t.CustomerId = check.CustomerId;
        t.Interval = interval; t.Action = action; t.StartDate = input.StartDate.Date; t.EndDate = input.EndDate?.Date;
        return null;
    }

    // Een uitgave waar je een vaste kost van maakt: uit je eigen werkruimte, echt, geen investering en nog niet van een vaste kost.
    static async Task<(Expense? Expense, string? Error)> SourceAsync(AppDb db, int expenseId)
    {
        var e = await db.Expenses.FirstOrDefaultAsync(x => x.Id == expenseId);
        if (e is null) return (null, "Kies een uitgave uit je eigen werkruimte.");
        if (e.IsDemo) return (null, "Van een voorbeelduitgave kun je geen vaste kost maken. Voer de uitgave zelf in.");
        if (e.Investment) return (null, "Een investering is geen vaste kost.");
        if (e.RecurringExpenseId is not null) return (null, "Deze uitgave hoort al bij een vaste kost.");
        if (e.Amount <= 0) return (null, "Van een terugbetaling kun je geen vaste kost maken.");
        return (e, null);
    }

    // Zou de vaste kost meteen meer uitgaven klaarzetten dan we na een storing zouden inhalen? Dan liever een latere startdatum.
    public static string? BacklogError(RecurringExpense t, DateTime today, int max) => RecurringPeriods.DueCount(t, today, max) > max
        ? $"Met deze startdatum zouden er meteen meer dan {max} uitgaven klaarstaan. Kies een latere startdatum, of voer de oudere uitgaven zelf in."
        : null;

    static IResult Outcome(RecurringExpenseResult r) => r.Outcome switch
    {
        RecurringExpenseOutcome.NotFound => Results.NotFound(),
        RecurringExpenseOutcome.Demo => Results.BadRequest(new { error = DemoError }),
        RecurringExpenseOutcome.NotDue => Results.Conflict(new { error = NotDueError }),
        RecurringExpenseOutcome.Finished => Results.Conflict(new { error = FinishedError }),
        RecurringExpenseOutcome.Invalid => Results.BadRequest(new { error = r.Error }),
        _ => Results.Conflict(new { error = RaceError }),
    };

    public static void MapRecurringExpenses(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/recurring-expenses");

        g.MapGet("/", async (AppDb db, BusinessClock clock, IOptions<InvoicingLimits> limits) =>
        {
            var items = await ListAsync(db, clock, Math.Max(1, limits.Value.MaxCatchUpPeriods));
            var running = items.Where(i => i.State != "afgelopen").ToList();
            var perMonth = running.Sum(i => i.PerMonth);
            var perYear = running.Sum(i => Money.Round(i.Amount * 12 / RecurringIntervals.Months(i.Interval)));
            var review = await db.Expenses.AsNoTracking().Where(e => e.NeedsReview).OrderBy(e => e.Date).ThenBy(e => e.Id).Take(100)
                .Select(e => new RecurringExpenseReview(e.Id, e.Date, e.Supplier, e.Description, e.Amount, e.RecurringExpenseId)).ToListAsync();
            return new RecurringExpenseOverview(items, perMonth, perYear, review);
        }).ForAccountant();

        g.MapGet("/{id:int}", async (AppDb db, BusinessClock clock, IOptions<InvoicingLimits> limits, int id) =>
            (await ListAsync(db, clock, Math.Max(1, limits.Value.MaxCatchUpPeriods), id)).FirstOrDefault() is { } t ? Results.Ok(t) : Results.NotFound()).ForAccountant();

        // Rekent uit wat deze instellingen doen, zonder iets op te slaan: de eerstvolgende perioden, hoeveel er meteen aan de beurt zijn
        // en de omschrijving op de eerste uitgave. Met een id: vanaf de volgende periode van die vaste kost. Booked: de eerste periode
        // komt uit een regel van je bank, dus de volgende begint een periode later.
        g.MapPost("/preview", async (AppDb db, BusinessClock clock, IOptions<InvoicingLimits> limits, RecurringExpenseInput input, int? id, bool? booked) =>
        {
            var existing = id is { } rid ? await db.RecurringExpenses.AsNoTracking().FirstOrDefaultAsync(t => t.Id == rid) : null;
            var t = new RecurringExpense();
            var start = input.StartDate;
            if (existing is null && input.FromExpenseId is { } fromId)
            {
                var (source, sourceError) = await SourceAsync(db, fromId);
                if (source is null) return Results.Ok(new RecurringExpensePreview([], 0, null, sourceError));
                start = source.Date;
            }
            if (await ApplyAsync(db, t, input with { StartDate = start }, locked: false) is { } error)
                return Results.Ok(new RecurringExpensePreview([], 0, null, error));
            var sameGrid = existing is not null && existing.StartDate == t.StartDate && existing.Interval == t.Interval;
            // Van een uitgave: die is de eerste periode al, dus de volgende begint een periode later.
            RecurringPeriods.Schedule(t, sameGrid ? existing!.NextPeriodStart
                : existing is null && (input.FromExpenseId is not null || booked == true) ? RecurringPeriods.Start(t.StartDate, t.Interval, 1) : t.StartDate);
            var max = Math.Max(1, limits.Value.MaxCatchUpPeriods);
            var periods = RecurringPeriods.Upcoming(t).Take(3).ToList();
            var description = periods.Count > 0 ? RecurringExpenseService.DescriptionFor(t, periods[0]) : null;
            return Results.Ok(new RecurringExpensePreview(periods.Select(Dto).ToList(), RecurringPeriods.DueCount(t, clock.Today, max), description,
                BacklogError(t, clock.Today, max)));
        });

        g.MapPost("/", async (AppDb db, BusinessClock clock, IOptions<InvoicingLimits> limits, RecurringExpenseService service, RecurringExpenseInput input) =>
        {
            var max = Math.Max(1, limits.Value.MaxCatchUpPeriods);
            int id;
            await using (var tx = await WriteLock.BeginAsync(db))
            {
                Expense? source = null;
                var start = input.StartDate;
                if (input.FromExpenseId is { } fromId)
                {
                    var (found, sourceError) = await SourceAsync(db, fromId);
                    if (found is null) return Results.BadRequest(new { error = sourceError });
                    source = found;
                    start = found.Date;
                }
                var t = new RecurringExpense();
                if (await ApplyAsync(db, t, input with { StartDate = start }, locked: false) is { } error) return Results.BadRequest(new { error });
                RecurringPeriods.Schedule(t, source is null ? t.StartDate : RecurringPeriods.Start(t.StartDate, t.Interval, 1));
                if (BacklogError(t, clock.Today, max) is { } backlog) return Results.BadRequest(new { error = backlog });
                db.RecurringExpenses.Add(t);
                db.Log("kosten", $"Vaste kost bij {t.Supplier} aangemaakt");
                await db.SaveChangesAsync();
                // De uitgave waar je hem van maakte, is de eerste periode.
                if (source is not null)
                {
                    source.RecurringExpenseId = t.Id;
                    source.RecurringPeriodStart = t.StartDate;
                    await db.SaveChangesAsync();
                }
                await tx.CommitAsync();
                id = t.Id;
            }
            // Is een periode al aan de beurt, dan staat die uitgave meteen klaar.
            await service.RunOneAsync(id);
            return Results.Created($"/api/recurring-expenses/{id}", (await ListAsync(db, clock, max, id)).Single());
        });

        g.MapPut("/{id:int}", async (AppDb db, BusinessClock clock, IOptions<InvoicingLimits> limits, RecurringExpenseService service, int id, RecurringExpenseInput input) =>
        {
            var max = Math.Max(1, limits.Value.MaxCatchUpPeriods);
            await using (var tx = await WriteLock.BeginAsync(db))
            {
                var t = await db.RecurringExpenses.FirstOrDefaultAsync(x => x.Id == id);
                if (t is null) return Results.NotFound();
                var locked = Locked(t, await db.Expenses.CountAsync(e => e.RecurringExpenseId == id));
                if (await ApplyAsync(db, t, input, locked) is { } error) return Results.BadRequest(new { error });
                // Zolang er niets geboekt is, begint hij bij de (nieuwe) startdatum. Daarna loopt hij door bij de volgende periode.
                RecurringPeriods.Schedule(t, locked ? t.NextPeriodStart : t.StartDate);
                if (!locked && BacklogError(t, clock.Today, max) is { } backlog) return Results.BadRequest(new { error = backlog });
                db.Log("kosten", $"Vaste kost bij {t.Supplier} bijgewerkt");
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            await service.RunOneAsync(id);
            return Results.Ok((await ListAsync(db, clock, max, id)).Single());
        });

        // De periode die aan de beurt is boeken als uitgave, bijvoorbeeld na een herinnering. Daarna schuift hij een periode op.
        g.MapPost("/{id:int}/book", async (AppDb db, BusinessClock clock, IOptions<InvoicingLimits> limits, RecurringExpenseService service, int id) =>
        {
            var t = await db.RecurringExpenses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return Results.NotFound();
            if (t.NextRunDate is null) return Results.Conflict(new { error = FinishedError });
            var result = await service.BookAsync(id, t.NextPeriodStart, RecurringBooking.Manual);
            if (result.Outcome is not (RecurringExpenseOutcome.Created or RecurringExpenseOutcome.Adopted)) return Outcome(result);
            return Results.Ok(new
            {
                expenseId = result.Expense!.Id, adopted = result.Outcome == RecurringExpenseOutcome.Adopted,
                recurring = (await ListAsync(db, clock, Math.Max(1, limits.Value.MaxCatchUpPeriods), id)).Single(),
            });
        });

        g.MapPost("/{id:int}/skip", async (AppDb db, BusinessClock clock, IOptions<InvoicingLimits> limits, RecurringExpenseService service, int id) =>
        {
            var t = await db.RecurringExpenses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return Results.NotFound();
            if (t.NextRunDate is null) return Results.Conflict(new { error = FinishedError });
            var result = await service.SkipAsync(id, t.NextPeriodStart);
            return result.Outcome == RecurringExpenseOutcome.Skipped
                ? Results.Ok((await ListAsync(db, clock, Math.Max(1, limits.Value.MaxCatchUpPeriods), id)).Single())
                : Outcome(result);
        });

        // Weg met de vaste kost. De uitgaven die hij maakte blijven gewoon staan, zonder koppeling.
        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            // In één schrijfronde, zodat de planner er niet tussendoor nog een uitgave aan koppelt.
            await using var tx = await WriteLock.BeginAsync(db);
            var t = await db.RecurringExpenses.FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return Results.NotFound();
            await db.Expenses.Where(e => e.RecurringExpenseId == id)
                .ExecuteUpdateAsync(x => x.SetProperty(e => e.RecurringExpenseId, (int?)null).SetProperty(e => e.RecurringPeriodStart, (DateTime?)null));
            db.RecurringExpenses.Remove(t);
            db.Log("kosten", $"Vaste kost bij {t.Supplier} verwijderd");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }
}
