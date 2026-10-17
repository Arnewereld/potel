using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Potel.Api.Banking;
using Potel.Api.Data;
using Potel.Api.Invoicing;
using Potel.Api.Workflows;

namespace Potel.Api.Endpoints;

public record BankMappingInput(string? Date, string? Amount, string? Description, string? Name, string? Iban, string? Sign);
public record BankImportRequest(string? FileName, string? Data, BankMappingInput? Mapping);
public record BankInvoiceLink(int InvoiceId, int? PaymentId, bool RememberIban);
public record BankExpenseLink(int ExpenseId);
public record BankNewExpense(string? Supplier, string? Description, string? Category, decimal? VatRate, bool ReverseCharge, string? SupplierRegion);
public record BankSubscriptionLink(int RecurringExpenseId, DateTime PeriodStart);
public record BankNewSubscription(string? Supplier, string? Description, string? Category, decimal? VatRate, bool ReverseCharge, string? SupplierRegion,
    string? Interval, string? Action, DateTime? EndDate);
public record BankMarkRequest(string? Mark);

// De pagina Bank: een afschrift inlezen en elke regel koppelen aan een factuur of uitgave, of op privé of negeren zetten.
// Alleen voor beheerders: een bankafschrift laat al je geldzaken zien. Potel koppelt nooit vanzelf; het doet voorstellen
// die je zelf bevestigt, en elke koppeling kun je ongedaan maken. Bedragen en datums komen altijd uit de bank, nooit uit
// wat de browser meestuurt.
public static class BankEndpoints
{
    public const int MaxRows = 300;
    public const string Busy = "Deze regel is al gekoppeld of staat op privé of negeren. Maak dat eerst ongedaan.";
    public const string NotFound = "Deze regel uit je bankafschrift bestaat niet (meer).";

    public static class States
    {
        public const string Open = "open", Invoice = "factuur", Expense = "kosten", Private = BankMarks.Private, Ignore = BankMarks.Ignore;
    }

    static IResult Problem(string error, int status = StatusCodes.Status400BadRequest) => Results.Json(new { error }, statusCode: status);

    static async Task<Dictionary<int, InvoicePayment>> LinkedPaymentsAsync(AppDb db, IReadOnlyCollection<int>? ids = null)
    {
        var q = db.InvoicePayments.AsNoTracking().Where(p => p.BankTransactionId != null);
        if (ids is not null) q = q.Where(p => ids.Contains(p.BankTransactionId!.Value));
        return (await q.ToListAsync()).ToDictionary(p => p.BankTransactionId!.Value);
    }

    static string StateOf(BankTransaction t, IReadOnlyDictionary<int, InvoicePayment> payments) =>
        payments.ContainsKey(t.Id) ? States.Invoice : t.ExpenseId is not null ? States.Expense : t.Mark ?? States.Open;

    // De werkruimte zelf, voor de matcher: open facturen (en betaalde met een betaling die je zelf invulde), uitgaven die nog niet
    // aan een regel uit je bank hangen en de eerstvolgende periode van elke vaste kost die nog loopt.
    static async Task<(List<InvoiceCandidate> Invoices, List<ExpenseCandidate> Expenses, List<ExpenseCandidate> History, List<SubscriptionCandidate> Subscriptions)>
        CandidatesAsync(AppDb db, DateTime from)
    {
        var invoices = await db.Invoices.AsNoTracking().Include(i => i.Lines).Include(i => i.Customer).Include(i => i.Credits).ThenInclude(c => c.Lines)
            .Include(i => i.Payments).AsSplitQuery()
            .Where(i => !i.IsDemo && i.Number != null && i.CreditForInvoiceId == null
                        && (i.Status == InvoiceStatus.Sent || i.Status == InvoiceStatus.Overdue || (i.Status == InvoiceStatus.Paid && i.PaidAt >= from)))
            .ToListAsync();
        var candidates = invoices.Where(i => !i.IsCredit).Select(i => new InvoiceCandidate(
            i.Id, i.Number!, CustomerName(i.Customer), i.Customer?.Iban,
            new[] { i.Customer?.Company, i.Customer?.Name }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList(),
            i.OpenAmount, i.Status,
            i.Payments.Where(p => p.BankTransactionId == null && p.Method != PaymentMethods.Ideal).Select(p => new PaymentCandidate(p.Id, p.Amount, p.Date)).ToList()))
            .ToList();
        var linked = await db.BankTransactions.AsNoTracking().Where(t => t.ExpenseId != null).Select(t => t.ExpenseId!.Value).ToListAsync();
        var recurring = await db.RecurringExpenses.AsNoTracking().Where(r => !r.IsDemo).ToDictionaryAsync(r => r.Id);
        string? PeriodOf(int? id, DateTime? start) => id is { } rid && start is { } s && recurring.TryGetValue(rid, out var r)
            ? RecurringExpenseService.Label(RecurringPeriods.Period(r, s)) : null;
        var history = (await db.Expenses.AsNoTracking().Where(e => !e.IsDemo && e.Date >= from.AddDays(-400))
            .Select(e => new { e.Id, e.Date, e.Supplier, e.Amount, e.Category, e.VatRate, e.RecurringExpenseId, e.RecurringPeriodStart, e.NeedsReview }).ToListAsync())
            .Select(e => new ExpenseCandidate(e.Id, e.Date, e.Supplier, e.Amount, e.Category, e.VatRate, e.RecurringExpenseId,
                PeriodOf(e.RecurringExpenseId, e.RecurringPeriodStart), e.NeedsReview)).ToList();
        var linkedSet = linked.ToHashSet();
        var subscriptions = recurring.Values.Where(r => r.NextRunDate != null).OrderBy(r => r.Id)
            .Select(r => new SubscriptionCandidate(r.Id, r.Supplier, r.Amount, r.NextPeriodStart, RecurringExpenseService.Label(RecurringPeriods.Period(r, r.NextPeriodStart))))
            .ToList();
        return (candidates, history.Where(e => !linkedSet.Contains(e.Id) && e.Date >= from).ToList(), history, subscriptions);
    }

    static string CustomerName(Customer? c) => c is null ? "" : string.IsNullOrWhiteSpace(c.Company) ? c.Name : c.Company;

    static object? MappingDto(BankCsvMapping? m) => m is null ? null
        : new { date = m.DateColumn, amount = m.AmountColumn, description = m.DescriptionColumn, name = m.NameColumn, iban = m.IbanColumn, sign = m.SignColumn };

    // Een korte tip bij regels waar je vaak niets mee hoeft: een uitbetaling van Mollie bestaat uit iDEAL-betalingen die al op je facturen staan.
    static string? Hint(BankTransaction t) =>
        t.Amount > 0 && t.CounterpartyName is { } n && n.Contains("mollie", StringComparison.OrdinalIgnoreCase)
            ? "Dit lijkt een uitbetaling van Mollie. Betaalden je klanten met iDEAL op hun factuur, dan staan die betalingen al op de facturen. Zet deze regel dan op negeren."
            : null;

    static bool Matches(BankTransaction t, string q)
    {
        if (StatementParser.ParseAmount(q) is { } amount && amount != 0 && Math.Abs(t.Amount) == Math.Abs(amount)) return true;
        return (t.CounterpartyName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
               || (t.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
               || (t.CounterpartyIban?.Contains(q.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public static void MapBank(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/bank").RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // De regels met hun stand en voorstellen. filter: open (te koppelen), gekoppeld, prive, negeren of alle.
        g.MapGet("/", async (AppDb db, BusinessClock clock, string? filter, string? q) =>
        {
            var all = await db.BankTransactions.AsNoTracking().OrderByDescending(t => t.Date).ThenByDescending(t => t.Id).ToListAsync();
            var payments = await LinkedPaymentsAsync(db);
            var states = all.ToDictionary(t => t.Id, t => StateOf(t, payments));
            var counts = new
            {
                open = states.Values.Count(s => s == States.Open),
                linked = states.Values.Count(s => s is States.Invoice or States.Expense),
                prive = states.Values.Count(s => s == States.Private),
                negeren = states.Values.Count(s => s == States.Ignore),
                all = all.Count,
            };
            filter = filter?.Trim().ToLowerInvariant() switch { "gekoppeld" => "gekoppeld", "prive" => "prive", "negeren" => "negeren", "alle" => "alle", _ => "open" };
            IEnumerable<BankTransaction> rows = all.Where(t => filter switch
            {
                "gekoppeld" => states[t.Id] is States.Invoice or States.Expense,
                "prive" => states[t.Id] == States.Private,
                "negeren" => states[t.Id] == States.Ignore,
                "alle" => true,
                _ => states[t.Id] == States.Open,
            });
            q = q?.Trim();
            if (!string.IsNullOrEmpty(q)) rows = rows.Where(t => Matches(t, q[..Math.Min(q.Length, 100)]));
            var filtered = rows.ToList();
            var page = filtered.Take(MaxRows).ToList();

            var from = (all.Count == 0 ? clock.Today : all.Min(t => t.Date)).AddDays(-BankMatcher.ExpenseDaysBefore - 30);
            var (invoices, expenses, history, subscriptions) = await CandidatesAsync(db, from);
            var matcher = new BankMatcher(invoices, expenses, subscriptions);
            var invoiceIds = page.Where(t => payments.ContainsKey(t.Id)).Select(t => payments[t.Id].InvoiceId).Distinct().ToList();
            var linkedInvoices = await db.Invoices.AsNoTracking().Include(i => i.Customer).Where(i => invoiceIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, i => new { i.Number, Customer = CustomerName(i.Customer), i.IsDemo });
            var expenseIds = page.Where(t => t.ExpenseId is not null).Select(t => t.ExpenseId!.Value).ToList();
            var linkedExpenses = await db.Expenses.AsNoTracking().Where(e => expenseIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => new { e.Supplier, e.Date, e.Amount, e.Category, Recurring = e.RecurringExpenseId != null });

            var transactions = page.Select(t =>
            {
                var state = states[t.Id];
                object? link = null;
                if (payments.TryGetValue(t.Id, out var p))
                {
                    var inv = linkedInvoices.GetValueOrDefault(p.InvoiceId);
                    link = new { kind = "invoice", invoiceId = p.InvoiceId, number = inv?.Number, customer = inv?.Customer, paymentId = p.Id, created = t.PaymentCreated };
                }
                else if (t.ExpenseId is { } eid && linkedExpenses.GetValueOrDefault(eid) is { } e)
                    link = new { kind = "expense", expenseId = eid, supplier = e.Supplier, date = e.Date, amount = e.Amount, category = e.Category, recurring = e.Recurring };
                var open = state == States.Open;
                return new
                {
                    t.Id, t.Date, t.Amount, t.CounterpartyName, t.CounterpartyIban, t.Description, t.Account, t.Mark, state, link,
                    proposals = open ? matcher.Propose(t) : [],
                    newExpense = open && t.Amount < 0 ? BankMatcher.Prefill(t, history) : null,
                    hint = open ? Hint(t) : null,
                    // Of we het rekeningnummer bij de klant kunnen bewaren als je koppelt.
                    ibanValid = Ibans.IsValid(t.CounterpartyIban),
                };
            }).ToList();

            var imports = await db.BankImports.AsNoTracking().OrderByDescending(i => i.Id).Take(10).ToListAsync();
            return Results.Ok(new
            {
                filter, counts, transactions, total = filtered.Count, more = filtered.Count > page.Count,
                imports, mapping = MappingDto(await db.BankCsvMappings.AsNoTracking().FirstOrDefaultAsync()),
                // Om zelf een factuur of uitgave te kiezen als er geen voorstel is.
                openInvoices = invoices.Where(i => i.Open > 0).OrderBy(i => i.Number)
                    .Select(i => new { id = i.Id, number = i.Number, customer = i.Customer, open = i.Open, customerIban = i.CustomerIban }),
                expenses = expenses.OrderByDescending(e => e.Date).Take(500).Select(e => new { id = e.Id, date = e.Date, supplier = e.Supplier, amount = e.Amount }),
            });
        });

        // Een afschrift inlezen. Herkennen we de CSV niet, dan krijg je de kolommen terug om zelf aan te wijzen; die keuze
        // onthouden we voor de volgende keer. Regels die er al staan slaan we over.
        g.MapPost("/import", async (AppDb db, ClaimsPrincipal user, BankImportRequest req) =>
        {
            var data = req.Data?.Trim() ?? "";
            if (data.StartsWith("data:", StringComparison.Ordinal)) data = data[(data.IndexOf(',') + 1)..];
            if (data == "") return Problem("Kies een bestand met je bankafschrift.");
            if (data.Length > (StatementParser.MaxBytes + 2) / 3 * 4 + 4) return Problem(StatementParser.TooBig);
            byte[] bytes;
            try { bytes = Convert.FromBase64String(data); }
            catch (FormatException) { return Problem(StatementParser.NotAStatement); }

            var saved = await db.BankCsvMappings.AsNoTracking().FirstOrDefaultAsync();
            var result = StatementParser.Parse(bytes);
            CsvMapping? used = null;
            if (result.NeedsMapping)
            {
                var headers = result.Headers!;
                var explicitMapping = req.Mapping is { } m ? new CsvMapping(m.Date?.Trim() ?? "", m.Amount?.Trim() ?? "", Blank(m.Description), Blank(m.Name), Blank(m.Iban), Blank(m.Sign)) : null;
                var remembered = saved is null ? null : new CsvMapping(saved.DateColumn, saved.AmountColumn, saved.DescriptionColumn, saved.NameColumn, saved.IbanColumn, saved.SignColumn);
                used = explicitMapping ?? (remembered is not null && Fits(remembered, headers) ? remembered : null);
                if (used is null)
                    return Results.Ok(new { needsMapping = true, headers, sample = result.Sample, mapping = MappingDto(saved) });
                if (explicitMapping is not null && (explicitMapping.Date == "" || explicitMapping.Amount == ""))
                    return Problem("Kies in welke kolom de datum en het bedrag staan.");
                result = StatementParser.Parse(bytes, used);
            }
            if (result.Error is { } error) return Problem(error);
            if (result.Transactions.Count == 0)
                return Problem(result.SkippedOtherCurrency + result.SkippedNotBooked > 0
                    ? "In dit afschrift staan geen geboekte transacties in euro's. Andere valuta en nog niet geboekte regels lezen we niet in."
                    : "In dit afschrift staan geen transacties.");

            var fileName = Path.GetFileName((req.FileName ?? "").Replace('\\', '/')).Trim();
            if (fileName.Length > 120) fileName = fileName[..120];
            if (fileName == "") fileName = "afschrift";
            var keys = StatementParser.DedupeKeys(result.Transactions);

            await using var tx = await WriteLock.BeginAsync(db);
            var existing = (await db.BankTransactions.Select(t => t.DedupeKey).ToListAsync()).ToHashSet();
            var fresh = Enumerable.Range(0, result.Transactions.Count).Where(i => existing.Add(keys[i])).ToList();
            var added = fresh.Count;
            var skipped = result.Transactions.Count - added;
            // Stond alles er al, dan komt er geen import in de lijst; er valt dan ook niets ongedaan te maken.
            BankImport? import = null;
            if (added > 0)
            {
                import = new BankImport
                {
                    FileName = fileName, Format = result.Format, ImportedBy = user.FindFirstValue(ClaimTypes.Name), ImportedAt = DateTime.UtcNow,
                    Added = added, Skipped = skipped,
                };
                db.BankImports.Add(import);
                await db.SaveChangesAsync();
                foreach (var i in fresh)
                {
                    var p = result.Transactions[i];
                    db.BankTransactions.Add(new BankTransaction
                    {
                        ImportId = import.Id, Account = p.Account, Date = p.Date, Amount = p.Amount, CounterpartyName = p.CounterpartyName,
                        CounterpartyIban = p.CounterpartyIban, Description = p.Description, BankReference = p.Reference, DedupeKey = keys[i], CreatedAt = DateTime.UtcNow,
                    });
                }
            }
            // Een kolomindeling die werkte, onthouden we voor deze werkruimte.
            if (used is not null && req.Mapping is not null)
            {
                var m = await db.BankCsvMappings.FirstOrDefaultAsync() ?? db.BankCsvMappings.Add(new BankCsvMapping()).Entity;
                (m.DateColumn, m.AmountColumn, m.DescriptionColumn, m.NameColumn, m.IbanColumn, m.SignColumn, m.UpdatedAt) =
                    (used.Date, used.Amount, used.Description, used.Name, used.Iban, used.Sign, DateTime.UtcNow);
            }
            db.Log("bank", $"Bankafschrift {fileName} ingelezen ({result.Format}): {added} nieuwe regels" + (skipped > 0 ? $", {skipped} stonden er al" : ""));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(new
            {
                needsMapping = false, importId = import?.Id, format = result.Format, added, skipped,
                skippedOtherCurrency = result.SkippedOtherCurrency, skippedNotBooked = result.SkippedNotBooked,
            });
        }).WithMetadata(new RequestSizeLimitAttribute(15 * 1024 * 1024));

        // Een import ongedaan maken, bijvoorbeeld na een verkeerde kolomindeling. Kan alleen zolang er niets uit gekoppeld is.
        g.MapDelete("/imports/{id:int}", async (AppDb db, int id) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            var import = await db.BankImports.FirstOrDefaultAsync(i => i.Id == id);
            if (import is null) return Results.NotFound();
            var ids = await db.BankTransactions.Where(t => t.ImportId == id).Select(t => t.Id).ToListAsync();
            if (await db.BankTransactions.AnyAsync(t => t.ImportId == id && t.ExpenseId != null)
                || await db.InvoicePayments.AnyAsync(p => p.BankTransactionId != null && ids.Contains(p.BankTransactionId.Value)))
                return Problem("Uit dit afschrift zijn al regels gekoppeld aan een factuur of uitgave. Maak die koppelingen eerst ongedaan.", StatusCodes.Status409Conflict);
            await db.BankTransactions.Where(t => t.ImportId == id).ExecuteDeleteAsync();
            db.BankImports.Remove(import);
            db.Log("bank", $"Import van {import.FileName} ongedaan gemaakt; {ids.Count} regels weggehaald");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.NoContent();
        });

        // Geld dat binnenkwam koppelen aan een factuur. Het bedrag en de datum komen uit de bank. Met paymentId hoort de regel bij
        // een betaling die je zelf al had ingevuld (met hetzelfde bedrag); anders komt er een betaling bij, en is het minder dan er
        // openstaat, dan is dat een deelbetaling.
        g.MapPost("/transactions/{id:int}/invoice", async (AppDb db, BusinessClock clock, WorkflowEngine engine, ClaimsPrincipal user, int id, BankInvoiceLink req) =>
        {
            Invoice inv;
            var becamePaid = false;
            await using (var tx = await WriteLock.BeginAsync(db))
            {
                var t = await db.BankTransactions.FirstOrDefaultAsync(x => x.Id == id);
                if (t is null) return Problem(NotFound, StatusCodes.Status404NotFound);
                if (t.Amount <= 0) return Problem("Alleen geld dat binnenkwam kun je aan een factuur koppelen.");
                if (await IsBusyAsync(db, t)) return Problem(Busy, StatusCodes.Status409Conflict);
                var found = await db.Invoices.Include(i => i.Lines).Include(i => i.Customer).FirstOrDefaultAsync(i => i.Id == req.InvoiceId);
                if (found is null) return Problem("Kies een factuur uit je eigen werkruimte.", StatusCodes.Status404NotFound);
                inv = found;
                if (inv.IsDemo) return Problem("Een voorbeeldfactuur kun je niet aan je bankafschrift koppelen.");
                var by = user.FindFirstValue(ClaimTypes.Name);

                if (req.PaymentId is { } paymentId)
                {
                    var p = await db.InvoicePayments.FirstOrDefaultAsync(x => x.Id == paymentId && x.InvoiceId == inv.Id);
                    if (p is null) return Problem("Deze betaling hoort niet bij deze factuur.", StatusCodes.Status404NotFound);
                    if (p.BankTransactionId is not null) return Problem("Deze betaling hangt al aan een andere regel uit je bank.", StatusCodes.Status409Conflict);
                    if (p.Amount != t.Amount) return Problem("Het bedrag van deze betaling is niet gelijk aan het bedrag uit je bank. Kies een andere betaling of koppel zonder betaling.");
                    p.BankTransactionId = t.Id;
                    t.PaymentCreated = false;
                    db.Log("bank", $"Bankregel van {InvoicePayments.Euro(t.Amount)} gekoppeld aan de betaling van {p.Date:dd-MM-yyyy} op {InvoiceEndpoints.Label(inv).ToLowerInvariant()}");
                    try { await db.SaveChangesAsync(); }
                    catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
                    {
                        return Problem(Busy, StatusCodes.Status409Conflict);
                    }
                }
                else
                {
                    var note = $"Uit je bankafschrift{(t.CounterpartyName is { } n ? $", van {n}" : "")}";
                    var result = await InvoicePayments.AddAsync(db, clock, inv, t.Amount, t.Date, PaymentMethods.Bank, note.Length > 500 ? note[..500] : note, by,
                        bankTransactionId: t.Id);
                    if (result.Problem is not null) return result.Problem.ToResult();
                    becamePaid = result.BecamePaid;
                    t.PaymentCreated = true;
                }

                if (req.RememberIban && Ibans.IsValid(t.CounterpartyIban) && inv.Customer is { IsDemo: false } customer && customer.Iban != t.CounterpartyIban)
                {
                    var c = await db.Customers.FirstAsync(x => x.Id == customer.Id);
                    c.Iban = t.CounterpartyIban;
                    db.Log("klant", $"Rekeningnummer {t.CounterpartyIban} bewaard bij klant {c.Name}");
                }
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            if (becamePaid) await InvoiceEndpoints.TriggerPaid(db, engine, inv);
            return Results.Ok(new { becamePaid });
        });

        // Geld dat eraf ging koppelen aan een uitgave die er al staat.
        g.MapPost("/transactions/{id:int}/expense", async (AppDb db, int id, BankExpenseLink req) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            var t = await db.BankTransactions.FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return Problem(NotFound, StatusCodes.Status404NotFound);
            if (t.Amount >= 0) return Problem("Alleen geld dat eraf ging kun je aan een uitgave koppelen.");
            if (await IsBusyAsync(db, t)) return Problem(Busy, StatusCodes.Status409Conflict);
            var e = await db.Expenses.FirstOrDefaultAsync(x => x.Id == req.ExpenseId);
            if (e is null) return Problem("Kies een uitgave uit je eigen werkruimte.", StatusCodes.Status404NotFound);
            if (e.IsDemo) return Problem("Een voorbeelduitgave kun je niet aan je bankafschrift koppelen.");
            if (await db.BankTransactions.AnyAsync(x => x.ExpenseId == e.Id))
                return Problem("Deze uitgave hangt al aan een andere regel uit je bank.", StatusCodes.Status409Conflict);
            t.ExpenseId = e.Id;
            db.Log("bank", $"Bankregel van {InvoicePayments.Euro(-t.Amount)} gekoppeld aan de uitgave bij {e.Supplier}");
            // Een concept van een vaste kost krijgt het bedrag uit je bank (een wisselkoers of een prijsverhoging) en is daarmee nagekeken.
            // Wijkt het bedrag veel af, dan blijft hij staan als nog te controleren.
            string? message = null;
            if (e.RecurringExpenseId is not null && e.NeedsReview && (e.Amount == -t.Amount || BankMatcher.CloseTo(-t.Amount, e.Amount)))
            {
                if (e.Amount != -t.Amount)
                {
                    message = $"Gekoppeld. Het bedrag van de uitgave is nu {InvoicePayments.Euro(-t.Amount)}, zoals in je bank, in plaats van {InvoicePayments.Euro(e.Amount)}.";
                    db.Log("kosten", $"Bedrag van de uitgave bij {e.Supplier} van {e.Date:dd-MM-yyyy} aangepast naar {InvoicePayments.Euro(-t.Amount)}, zoals in je bankafschrift");
                    e.Amount = -t.Amount;
                    e.VatAmount = Expense.VatFor(e.Amount, e.VatRate, e.ReverseCharge);
                }
                e.NeedsReview = false;
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(new { expenseId = e.Id, message });
        });

        // Geld dat eraf ging hoort bij de periode van een vaste kost die nog niet geboekt is: de uitgave voor die periode komt erbij,
        // met bedrag en datum uit de bank, of een uitgave die je al had ingevoerd wordt eraan gekoppeld. Per periode één uitgave.
        g.MapPost("/transactions/{id:int}/subscription", async (AppDb db, RecurringExpenseService service, int id, BankSubscriptionLink req) =>
        {
            var t = await db.BankTransactions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return Problem(NotFound, StatusCodes.Status404NotFound);
            if (t.Amount >= 0) return Problem("Alleen geld dat eraf ging kun je aan een vaste kost koppelen.");
            var result = await service.BookAsync(req.RecurringExpenseId, req.PeriodStart, RecurringBooking.Bank, id);
            return result.Outcome switch
            {
                RecurringExpenseOutcome.Created => Results.Ok(new { expenseId = result.Expense!.Id, message = "De uitgave voor deze periode is gemaakt en gekoppeld." }),
                RecurringExpenseOutcome.Adopted => Results.Ok(new { expenseId = result.Expense!.Id, message = "Je had de uitgave voor deze periode al ingevoerd; die is nu gekoppeld." }),
                RecurringExpenseOutcome.Busy => Problem(Busy, StatusCodes.Status409Conflict),
                RecurringExpenseOutcome.OutsidePeriod => Problem("Deze betaling valt niet in de periode van de vaste kost. Koppel hem aan een uitgave of maak er een kost van."),
                RecurringExpenseOutcome.NotFound => Problem("Deze vaste kost bestaat niet (meer).", StatusCodes.Status404NotFound),
                RecurringExpenseOutcome.Demo => Problem(RecurringExpenseEndpoints.DemoError),
                RecurringExpenseOutcome.Finished => Problem(RecurringExpenseEndpoints.FinishedError, StatusCodes.Status409Conflict),
                RecurringExpenseOutcome.Invalid => Problem(result.Error!),
                _ => Problem(RecurringExpenseEndpoints.RaceError, StatusCodes.Status409Conflict),
            };
        });

        // Van een afschrijving een vaste kost maken: de uitgave voor de eerste periode komt uit de bank (bedrag en datum), en vanaf
        // dan komt hij elke maand, elk kwartaal of elk jaar terug. Leverancier, categorie en btw kies je zelf.
        g.MapPost("/transactions/{id:int}/create-subscription", async (AppDb db, BusinessClock clock, IOptions<InvoicingLimits> limits, RecurringExpenseService service,
            int id, BankNewSubscription req) =>
        {
            int recurringId, expenseId;
            await using (var tx = await WriteLock.BeginAsync(db))
            {
                var t = await db.BankTransactions.FirstOrDefaultAsync(x => x.Id == id);
                if (t is null) return Problem(NotFound, StatusCodes.Status404NotFound);
                if (t.Amount >= 0) return Problem("Alleen van geld dat eraf ging kun je een vaste kost maken.");
                if (await IsBusyAsync(db, t)) return Problem(Busy, StatusCodes.Status409Conflict);
                var r = new RecurringExpense();
                var input = new RecurringExpenseInput(req.Supplier ?? t.CounterpartyName, req.Description, req.Category, -t.Amount, req.VatRate, req.ReverseCharge,
                    req.SupplierRegion, null, null, req.Interval, req.Action, t.Date, req.EndDate);
                if (await RecurringExpenseEndpoints.ApplyAsync(db, r, input, locked: false) is { } error) return Problem(error);
                RecurringPeriods.Schedule(r, RecurringPeriods.Start(r.StartDate, r.Interval, 1));
                if (RecurringExpenseEndpoints.BacklogError(r, clock.Today, Math.Max(1, limits.Value.MaxCatchUpPeriods)) is { } backlog) return Problem(backlog);
                db.RecurringExpenses.Add(r);
                await db.SaveChangesAsync();
                var first = RecurringPeriods.Period(r.StartDate, r.Interval, RecurringPeriodRules.InAdvance, 0);
                var e = new Expense();
                if (await ExpenseEndpoints.ApplyAsync(db, e, new ExpenseInput(t.Date, r.Supplier, RecurringExpenseService.DescriptionFor(r, first), r.Category, -t.Amount,
                        r.VatRate, null, r.ReverseCharge, r.SupplierRegion, null, null)) is { } expenseError)
                    return Problem(expenseError);
                e.RecurringExpenseId = r.Id;
                e.RecurringPeriodStart = first.From;
                db.Expenses.Add(e);
                await db.SaveChangesAsync();
                t.ExpenseId = e.Id;
                db.Log("kosten", $"Vaste kost bij {r.Supplier} gemaakt vanuit je bankafschrift, met de uitgave van {t.Date:dd-MM-yyyy} als eerste periode");
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                (recurringId, expenseId) = (r.Id, e.Id);
            }
            // Lag de afschrijving al een tijd terug, dan staan de perioden daarna meteen klaar.
            await service.RunOneAsync(recurringId);
            return Results.Ok(new { recurringExpenseId = recurringId, expenseId, message = "Vaste kost gemaakt. De uitgave uit je bank is de eerste periode." });
        });

        // Van een afschrijving een nieuwe uitgave maken. Bedrag (inclusief btw) en datum komen uit de bank; leverancier,
        // omschrijving, categorie en btw-tarief kies je zelf. Dezelfde regels als bij Kosten.
        g.MapPost("/transactions/{id:int}/create-expense", async (AppDb db, int id, BankNewExpense req) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            var t = await db.BankTransactions.FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return Problem(NotFound, StatusCodes.Status404NotFound);
            if (t.Amount >= 0) return Problem("Alleen van geld dat eraf ging kun je een uitgave maken.");
            if (await IsBusyAsync(db, t)) return Problem(Busy, StatusCodes.Status409Conflict);
            var e = new Expense();
            var input = new ExpenseInput(t.Date, req.Supplier ?? t.CounterpartyName, req.Description, req.Category, -t.Amount, req.VatRate, null,
                req.ReverseCharge, req.SupplierRegion, null, null);
            if (await ExpenseEndpoints.ApplyAsync(db, e, input) is { } error) return Problem(error);
            db.Expenses.Add(e);
            await db.SaveChangesAsync();
            t.ExpenseId = e.Id;
            db.Log("kosten", $"Uitgave bij {e.Supplier} toegevoegd vanuit je bankafschrift");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(new { expenseId = e.Id });
        });

        // Op privé of negeren zetten, of dat weer weghalen (mark leeg).
        g.MapPost("/transactions/{id:int}/mark", async (AppDb db, int id, BankMarkRequest req) =>
        {
            var mark = string.IsNullOrWhiteSpace(req.Mark) ? null : req.Mark.Trim();
            if (mark is not null && !BankMarks.All.Contains(mark)) return Problem("Kies privé of negeren.");
            await using var tx = await WriteLock.BeginAsync(db);
            var t = await db.BankTransactions.FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return Problem(NotFound, StatusCodes.Status404NotFound);
            if (mark is not null && (t.ExpenseId is not null || await db.InvoicePayments.AnyAsync(p => p.BankTransactionId == t.Id)))
                return Problem(Busy, StatusCodes.Status409Conflict);
            t.Mark = mark;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(new { mark });
        });

        // Een koppeling ongedaan maken. Een betaling die door de koppeling op de factuur kwam, gaat weer weg (en de factuur
        // staat dan weer open); een betaling die je zelf invulde blijft staan. Een uitgave blijft altijd staan.
        g.MapPost("/transactions/{id:int}/unlink", async (AppDb db, BusinessClock clock, ClaimsPrincipal user, int id) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            var t = await db.BankTransactions.FirstOrDefaultAsync(x => x.Id == id);
            if (t is null) return Problem(NotFound, StatusCodes.Status404NotFound);
            var p = await db.InvoicePayments.FirstOrDefaultAsync(x => x.BankTransactionId == t.Id);
            string message;
            if (p is not null)
            {
                var inv = await db.Invoices.Include(i => i.Lines).FirstAsync(i => i.Id == p.InvoiceId);
                if (t.PaymentCreated)
                {
                    await InvoicePayments.RemoveAsync(db, clock, inv, p, user.FindFirstValue(ClaimTypes.Name));
                    message = $"De betaling van {InvoicePayments.Euro(p.Amount)} is weer van {InvoiceEndpoints.Label(inv).ToLowerInvariant()} gehaald.";
                }
                else
                {
                    p.BankTransactionId = null;
                    message = $"De betaling op {InvoiceEndpoints.Label(inv).ToLowerInvariant()} blijft staan; hij hangt alleen niet meer aan deze regel.";
                }
                t.PaymentCreated = false;
                db.Log("bank", $"Koppeling van bankregel ({InvoicePayments.Euro(t.Amount)}) met {InvoiceEndpoints.Label(inv).ToLowerInvariant()} ongedaan gemaakt");
            }
            else if (t.ExpenseId is not null)
            {
                t.ExpenseId = null;
                message = "De uitgave blijft staan bij Kosten. Heb je hem niet meer nodig, verwijder hem daar dan.";
                db.Log("bank", $"Koppeling van bankregel ({InvoicePayments.Euro(t.Amount)}) met een uitgave ongedaan gemaakt");
            }
            else if (t.Mark is not null)
            {
                t.Mark = null;
                message = "De regel staat weer bij Te koppelen.";
            }
            else return Problem("Deze regel is niet gekoppeld.", StatusCodes.Status409Conflict);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(new { message });
        });
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static bool Fits(CsvMapping m, List<string> headers) =>
        new[] { m.Date, m.Amount, m.Description, m.Name, m.Iban, m.Sign }.Where(c => c is not null)
            .All(c => headers.Any(h => h.Equals(c, StringComparison.OrdinalIgnoreCase)));

    static async Task<bool> IsBusyAsync(AppDb db, BankTransaction t) =>
        t.ExpenseId is not null || t.Mark is not null || await db.InvoicePayments.AnyAsync(p => p.BankTransactionId == t.Id);
}
