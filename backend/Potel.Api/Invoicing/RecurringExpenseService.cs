using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Potel.Api.Banking;
using Potel.Api.Data;
using Potel.Api.Endpoints;

namespace Potel.Api.Invoicing;

public enum RecurringExpenseOutcome { Created, Adopted, Skipped, AlreadyMade, Finished, NotDue, OutsidePeriod, Busy, Demo, NotFound, Invalid }

// Wat één periode boeken of overslaan opleverde, met de uitgave en bij een fout de reden.
public record RecurringExpenseResult(RecurringExpenseOutcome Outcome, Expense? Expense = null, string? Error = null);

// Hoe een uitgave voor een periode ontstaat: vanzelf door de planner (als concept), met de knop Boeken, of vanuit een regel uit je bank.
public enum RecurringBooking { Planner, Manual, Bank }

// Maakt uitgaven van vaste kosten: per periode precies één, ook na een herstart of als twee rondes tegelijk lopen. Net als bij
// terugkerende facturen bewaken drie dingen dat samen: een WriteLock, de controle dat de volgende periode nog dezelfde is als
// waarvoor we begonnen, en de unieke index op vaste kost en begin van de periode. Stond de uitgave er al (met de hand ingevoerd of
// vanuit je bank), dan koppelen we die in plaats van er een tweede naast te zetten.
public class RecurringExpenseService(AppDb db, BusinessClock clock, IOptions<InvoicingLimits> options, ILogger<RecurringExpenseService> logger)
{
    readonly InvoicingLimits limits = options.Value;

    public static string Label(RecurringPeriod p) => RecurringPeriods.Label(p.From, p.To);

    // De omschrijving op de uitgave: met de periode erin, zodat je altijd ziet welke maand het is.
    public static string DescriptionFor(RecurringExpense t, RecurringPeriod p)
    {
        var label = Label(p);
        if (string.IsNullOrWhiteSpace(t.Description)) return $"Vaste kost, {label}";
        var filled = RecurringPeriods.Fill(t.Description, p.From, p.To);
        return filled != t.Description ? filled : $"{t.Description}, {label}";
    }

    // Een afschrijving uit je bank hoort bij een periode als hij van een week ervoor tot 45 dagen erna komt, net als bij gewone kosten.
    public static bool BankFits(DateTime bankDate, RecurringPeriod p) =>
        bankDate.Date >= p.From.AddDays(-BankMatcher.ExpenseDaysAfter) && bankDate.Date <= p.From.AddDays(BankMatcher.ExpenseDaysBefore);

    // Alle vaste kosten van deze werkruimte die vandaag aan de beurt zijn, samen hooguit max uitgaven.
    public async Task<int> RunDueAsync(int? max = null)
    {
        var limit = Math.Max(0, max ?? limits.MaxRecurringPerRun);
        var today = clock.Today;
        var ids = await db.RecurringExpenses.AsNoTracking()
            .Where(t => !t.IsDemo && t.NextRunDate != null && t.NextRunDate <= today)
            .OrderBy(t => t.NextRunDate).ThenBy(t => t.Id).Select(t => t.Id).ToListAsync();
        var made = 0;
        foreach (var id in ids)
        {
            if (made >= limit) break;
            try
            {
                made += await CatchUpAsync(id, today, limit - made);
            }
            catch (Exception ex)
            {
                // Eén kapotte vaste kost houdt de rest niet tegen.
                logger.LogError(ex, "Vaste kost {Id} mislukt", id);
                db.ChangeTracker.Clear();
            }
        }
        return made;
    }

    // Eén vaste kost bijwerken tot vandaag, bijvoorbeeld meteen na opslaan.
    public async Task<int> RunOneAsync(int id)
    {
        var t = await db.RecurringExpenses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return t is null || t.IsDemo ? 0 : await CatchUpAsync(id, clock.Today, Math.Max(1, limits.MaxRecurringPerRun));
    }

    // Zet voor elke periode die vandaag aan de beurt is een concept klaar, de oudste eerst. Bij alleen herinneren komt er één regel
    // in het logboek voor de oudste periode die wacht; de uitgave boek je zelf.
    async Task<int> CatchUpAsync(int id, DateTime today, int max)
    {
        await SkipBeyondCatchUpAsync(id, today);
        var made = 0;
        for (var round = 0; made < max && round < max + limits.MaxCatchUpPeriods + 2; round++)
        {
            var t = await db.RecurringExpenses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (t is not { NextRunDate: { } run } || run > today) break;
            if (t.Action == RecurringExpenseActions.Remind)
            {
                await RemindAsync(id);
                break;
            }
            var result = await BookAsync(id, t.NextPeriodStart, RecurringBooking.Planner);
            if (result.Outcome is RecurringExpenseOutcome.Created or RecurringExpenseOutcome.Adopted) made++;
            else if (result.Outcome != RecurringExpenseOutcome.AlreadyMade) break;
        }
        return made;
    }

    async Task RemindAsync(int id)
    {
        db.ChangeTracker.Clear();
        await using var tx = await WriteLock.BeginAsync(db);
        var t = await db.RecurringExpenses.FirstOrDefaultAsync(x => x.Id == id);
        if (t is not { NextRunDate: not null } || t.RemindedPeriodStart == t.NextPeriodStart) return;
        var p = RecurringPeriods.Period(t, t.NextPeriodStart);
        t.RemindedPeriodStart = t.NextPeriodStart;
        db.Log("kosten", $"Herinnering: je vaste kost bij {t.Supplier} ({Label(p)}) is aan de beurt. Boek de uitgave bij Kosten, Vaste kosten, of sla deze periode over.");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    // Lag de server lang stil, dan halen we hooguit MaxCatchUpPeriods perioden in en slaan we de oudere over, met een regel in het logboek.
    async Task SkipBeyondCatchUpAsync(int id, DateTime today)
    {
        var max = Math.Max(1, limits.MaxCatchUpPeriods);
        var current = await db.RecurringExpenses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (current is null || RecurringPeriods.DueCount(current, today, max) <= max) return;
        db.ChangeTracker.Clear();
        await using var tx = await WriteLock.BeginAsync(db);
        var t = await db.RecurringExpenses.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return;
        var due = RecurringPeriods.Upcoming(t).TakeWhile(p => p.RunDate <= today).Take(10_000).ToList();
        if (due.Count <= max) return;
        var skipped = due[..^max];
        RecurringPeriods.Schedule(t, due[^max].From);
        db.Log("kosten", $"Vaste kost bij {t.Supplier}: {skipped.Count} gemiste perioden ({RecurringPeriods.Label(skipped[0].From, skipped[^1].To)}) " +
                         $"zijn overgeslagen. Na een lange onderbreking zetten we alleen de laatste {max} perioden nog klaar. Voer de rest zo nodig zelf in bij Kosten.");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    // Een uitgave die er al staat voor deze periode en nog niet bij een vaste kost hoort: dezelfde leverancier, een datum in de periode
    // en ongeveer hetzelfde bedrag. Een uitgave die al aan een andere regel uit je bank hangt, slaan we bij een bankregel over.
    async Task<Expense?> AdoptableAsync(RecurringExpense t, RecurringPeriod p, decimal amount, int? bankTransactionId)
    {
        var candidates = await db.Expenses
            .Where(e => e.RecurringExpenseId == null && !e.IsDemo && !e.Investment && e.Date >= p.From && e.Date <= p.To).ToListAsync();
        candidates = candidates.Where(e => BankMatcher.SameName(e.Supplier, t.Supplier) && BankMatcher.CloseTo(e.Amount, amount)).ToList();
        if (candidates.Count == 0) return null;
        var ids = candidates.Select(e => e.Id).ToList();
        var linked = bankTransactionId is null
            ? []
            : await db.BankTransactions.Where(b => b.ExpenseId != null && ids.Contains(b.ExpenseId.Value) && b.Id != bankTransactionId).Select(b => b.ExpenseId!.Value).ToListAsync();
        return candidates.Where(e => !linked.Contains(e.Id)).OrderBy(e => Math.Abs((e.Date - p.From).TotalDays)).ThenBy(e => e.Id).FirstOrDefault();
    }

    // Boekt de periode die op periodStart begint, als dat nog steeds de volgende periode is. De planner zet een concept klaar
    // ("nog controleren"); met de knop Boeken of vanuit je bank is hij meteen goed. Vanuit je bank komen bedrag en datum uit de bank
    // en hangt de uitgave daarna aan die regel. Begint met een lege context, zodat we altijd de stand uit de database zien.
    public async Task<RecurringExpenseResult> BookAsync(int id, DateTime periodStart, RecurringBooking booking, int? bankTransactionId = null)
    {
        db.ChangeTracker.Clear();
        await using var tx = await WriteLock.BeginAsync(db);
        var t = await db.RecurringExpenses.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return new(RecurringExpenseOutcome.NotFound);
        if (t.IsDemo) return new(RecurringExpenseOutcome.Demo);
        // Een andere ronde was ons voor en heeft deze periode al geboekt of overgeslagen.
        if (t.NextPeriodStart != periodStart.Date) return new(RecurringExpenseOutcome.AlreadyMade);
        if (!RecurringPeriods.WithinEnd(t, t.NextPeriodStart)) return new(RecurringExpenseOutcome.Finished);
        var period = RecurringPeriods.Period(t, t.NextPeriodStart);
        var next = RecurringPeriods.Start(t.StartDate, t.Interval, period.Index + 1);

        BankTransaction? bank = null;
        if (bankTransactionId is { } bankId)
        {
            bank = await db.BankTransactions.FirstOrDefaultAsync(b => b.Id == bankId);
            if (bank is null) return new(RecurringExpenseOutcome.NotFound);
            if (bank.Amount >= 0 || bank.ExpenseId is not null || bank.Mark is not null || await db.InvoicePayments.AnyAsync(p => p.BankTransactionId == bankId))
                return new(RecurringExpenseOutcome.Busy);
            if (!BankFits(bank.Date, period)) return new(RecurringExpenseOutcome.OutsidePeriod);
        }
        else if (period.RunDate > clock.Today) return new(RecurringExpenseOutcome.NotDue);

        // Een eerdere ronde maakte de uitgave al maar kwam niet meer aan doorschuiven toe: alleen doorschuiven.
        if (await db.Expenses.AnyAsync(e => e.RecurringExpenseId == t.Id && e.RecurringPeriodStart == period.From))
        {
            RecurringPeriods.Schedule(t, next);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return new(RecurringExpenseOutcome.AlreadyMade);
        }

        var amount = bank is null ? t.Amount : -bank.Amount;
        Expense e;
        RecurringExpenseOutcome outcome;
        if (await AdoptableAsync(t, period, amount, bankTransactionId) is { } existing)
        {
            e = existing;
            outcome = RecurringExpenseOutcome.Adopted;
            db.Log("kosten", $"Uitgave bij {e.Supplier} van {e.Date:dd-MM-yyyy} gekoppeld aan je vaste kost ({Label(period)}); er is geen tweede uitgave gemaakt");
        }
        else
        {
            e = new Expense();
            var input = new ExpenseInput(bank?.Date ?? period.From, t.Supplier, DescriptionFor(t, period), t.Category, amount, t.VatRate, null,
                t.ReverseCharge, t.SupplierRegion, t.ProjectId, t.CustomerId);
            if (await ExpenseEndpoints.ApplyAsync(db, e, input) is { } error)
            {
                // Eén regel in het logboek per periode, niet bij elke ronde van de planner opnieuw.
                db.ChangeTracker.Clear();
                var again = await db.RecurringExpenses.FirstAsync(x => x.Id == id);
                if (again.RemindedPeriodStart != again.NextPeriodStart)
                {
                    again.RemindedPeriodStart = again.NextPeriodStart;
                    db.Log("kosten", $"Vaste kost bij {t.Supplier}: de uitgave voor {Label(period)} kon niet worden gemaakt. {error} Pas de vaste kost aan.");
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                }
                return new(RecurringExpenseOutcome.Invalid, Error: error);
            }
            e.NeedsReview = booking == RecurringBooking.Planner;
            db.Expenses.Add(e);
            outcome = RecurringExpenseOutcome.Created;
            db.Log("kosten", booking == RecurringBooking.Planner
                ? $"Uitgave bij {t.Supplier} ({Label(period)}) klaargezet als concept door je vaste kost. Kijk hem na bij Kosten."
                : $"Uitgave bij {t.Supplier} ({Label(period)}) geboekt vanuit je vaste kost{(bank is null ? "" : " en je bankafschrift")}");
        }
        e.RecurringExpenseId = t.Id;
        e.RecurringPeriodStart = period.From;
        t.SkippedInARow = 0;
        RecurringPeriods.Schedule(t, next);
        try
        {
            await db.SaveChangesAsync();
            if (bank is not null)
            {
                bank.ExpenseId = e.Id;
                await db.SaveChangesAsync();
            }
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // De unieke index: deze periode (of deze bankregel) is al geboekt. Alles terugdraaien; er is niets meer te doen.
            db.ChangeTracker.Clear();
            return new(RecurringExpenseOutcome.AlreadyMade);
        }
        await tx.CommitAsync();
        return new(outcome, e);
    }

    // Slaat de volgende periode over, bijvoorbeeld omdat je die maand niets betaalde. Kan alleen als hij aan de beurt is.
    public async Task<RecurringExpenseResult> SkipAsync(int id, DateTime periodStart)
    {
        db.ChangeTracker.Clear();
        await using var tx = await WriteLock.BeginAsync(db);
        var t = await db.RecurringExpenses.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return new(RecurringExpenseOutcome.NotFound);
        if (t.IsDemo) return new(RecurringExpenseOutcome.Demo);
        if (t.NextPeriodStart != periodStart.Date) return new(RecurringExpenseOutcome.AlreadyMade);
        if (!RecurringPeriods.WithinEnd(t, t.NextPeriodStart)) return new(RecurringExpenseOutcome.Finished);
        var period = RecurringPeriods.Period(t, t.NextPeriodStart);
        if (period.RunDate > clock.Today) return new(RecurringExpenseOutcome.NotDue);
        RecurringPeriods.Schedule(t, RecurringPeriods.Start(t.StartDate, t.Interval, period.Index + 1));
        t.SkippedInARow++;
        db.Log("kosten", $"Vaste kost bij {t.Supplier}: {Label(period)} overgeslagen");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new(RecurringExpenseOutcome.Skipped);
    }
}

// De planner: vaste kosten die aan de beurt zijn, vanaf StartHour Nederlandse tijd en niet in een werkruimte die alleen-lezen is.
public class RecurringExpenseJob(RecurringExpenseService service, AppDb db, BusinessClock clock, IOptions<InvoicingLimits> options) : IWorkspaceJob
{
    public async Task RunAsync(CancellationToken ct)
    {
        if (clock.Now.Hour < options.Value.StartHour) return;
        if (await db.Workspaces.FindAsync([db.TenantId], ct) is not { } w || w.ReadOnly(DateTime.UtcNow)) return;
        await service.RunDueAsync();
    }
}
