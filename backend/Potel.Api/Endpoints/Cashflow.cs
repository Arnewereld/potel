using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Invoicing;

namespace Potel.Api.Endpoints;

// De instellingen voor de prognose. Saved is false zolang je nog niets hebt opgeslagen.
public record CashflowSettingsDto(bool Saved, decimal? BankBalance, DateTime? BankBalanceDate, decimal FixedCostsPerMonth, int QuoteSpreadMonths,
    DateTime? UpdatedAt, string? UpdatedBy)
{
    public static CashflowSettingsDto From(CashflowSettings? s) => s is null
        ? new(false, null, null, 0, 1, null, null)
        : new(true, s.BankBalance, s.BankBalanceDate, s.FixedCostsPerMonth, s.QuoteSpreadMonths, s.UpdatedAt, s.UpdatedBy);
}

public record CashflowSettingsInput(decimal? BankBalance, decimal FixedCostsPerMonth, int QuoteSpreadMonths);

// Eén verwachte post in een maand. Amount is positief voor geld dat binnenkomt en negatief voor geld dat eruit gaat. Late is een
// factuur waarvan de vervaldatum al voorbij is: die staat in de lopende maand.
public record CashflowItem(string Source, string Month, DateTime Date, string Label, string? Detail, decimal Amount, string? Link, bool Late = false);

// Een btw-betaling in de maand van de uiterste datum. Base is wat er nu in de aangifte van dat kwartaal staat (5a min 5b). Parts is
// de btw op verwachte facturen in dat kwartaal, per bron; die telt mee als die bron aan staat. Krijg je geld terug, dan is het nul.
// Running: het kwartaal loopt nog.
public record CashflowVat(int Year, int Quarter, string Label, DateTime DueDate, string Month, decimal Base, Dictionary<string, decimal> Parts, bool Running);

public record CashflowSource(string Id, string Label, bool Incoming);

public record CashflowSupplier(string Supplier, decimal Average);

// Een voorstel voor je vaste kosten: leveranciers die in elk van de laatste drie hele maanden terugkomen, met hun gemiddelde per
// maand, en wat je in die maanden gemiddeld aan alle kosten uitgaf. Investeringen tellen niet mee.
public record CashflowHint(DateTime From, DateTime To, List<CashflowSupplier> Suppliers, decimal SuppliersTotal, decimal AverageExpenses);

// De prognose voor de lopende maand en de vijf maanden erna. ProvisionalOnce: je voorlopige aanslag betaal je in één keer, en
// wanneer weten we niet. NextYearEstimate: voor maanden in volgend jaar schatten we wat je opzij zet met de cijfers van dit jaar.
public record CashflowDto(
    DateTime Today, List<string> Months, int PaymentTermDays, List<CashflowSource> Sources, List<CashflowItem> Items, List<CashflowVat> Vat,
    CashflowSettingsDto Settings, CashflowHint Hint, bool Kor, bool ProvisionalOnce, bool NextYearEstimate, bool DemoData, bool IncludesDemo);

// Waar een post vandaan komt. Elke bron kun je op de pagina aan- en uitzetten.
public static class CashflowSources
{
    public const string Invoices = "facturen";
    public const string Recurring = "terugkerend";
    public const string Quotes = "offertes";
    public const string Unbilled = "te-factureren";
    public const string Vat = "btw";
    public const string Tax = "belasting";
    public const string FixedCosts = "vaste-kosten";

    public static readonly List<CashflowSource> All =
    [
        new(Invoices, "Openstaande facturen", true),
        new(Recurring, "Terugkerende facturen", true),
        new(Quotes, "Geaccepteerde offertes", true),
        new(Unbilled, "Nog te factureren", true),
        new(Vat, "Btw", false),
        new(Tax, "Inkomstenbelasting", false),
        new(FixedCosts, "Vaste kosten", false),
    ];
}

// Een schatting van wat er de komende maanden binnenkomt en eruit gaat, met wat er nu in Potel staat. Geen belastingadvies.
public static class Cashflow
{
    public const int Months = 6;
    public const int MaxSpread = 6;
    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    static string Day(DateTime d) => d.ToString("d MMMM yyyy", Dutch);
    static string MonthKey(DateTime d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    static string Name(Customer? c) => c is null ? "Onbekende klant" : string.IsNullOrWhiteSpace(c.Company) ? c.Name : c.Company;
    static string QuarterKey(int year, int quarter) => $"{year}-{quarter}";

    // Een bedrag in gelijke delen op centen; wat het afronden scheelt, komt bij het laatste deel.
    public static List<decimal> Split(decimal amount, int parts)
    {
        var part = Money.Round(amount / parts);
        return [.. Enumerable.Repeat(part, parts - 1), amount - part * (parts - 1)];
    }

    // Het venster (de lopende maand en de vijf erna) met de posten die erin vallen en de btw op verwachte facturen per kwartaal.
    sealed class Run(DateTime today)
    {
        public DateTime Today { get; } = today;
        public DateTime First { get; } = new(today.Year, today.Month, 1);
        public DateTime End => First.AddMonths(Months).AddDays(-1);
        public List<CashflowItem> Items { get; } = [];
        public Dictionary<string, Dictionary<string, decimal>> VatParts { get; } = [];

        // Een post op een dag. Wat vóór vandaag valt, staat op vandaag; na het venster valt hij weg. Zijn btw hoort bij het kwartaal
        // waarin de factuur gemaakt wordt, ook als het geld pas na het venster binnenkomt.
        public void Add(string source, DateTime date, string label, string? detail, decimal amount, string? link, bool late = false,
            decimal vat = 0, DateTime? invoiceDate = null)
        {
            if (amount == 0) return;
            if (vat != 0 && invoiceDate is { } made && made.Date <= End)
            {
                var key = QuarterKey(made.Year, VatReturn.QuarterOf(made));
                if (!VatParts.TryGetValue(key, out var parts)) VatParts[key] = parts = [];
                parts[source] = parts.GetValueOrDefault(source) + vat;
            }
            var day = date.Date < Today ? Today : date.Date;
            if (day <= End) Items.Add(new CashflowItem(source, MonthKey(day), day, label, detail, amount, link, late));
        }
    }

    // Wat er binnenkomt: openstaande facturen, terugkerende facturen, geaccepteerde offertes en wat nog gefactureerd moet worden.
    static async Task IncomingAsync(AppDb db, Run run, Settings settings, CashflowSettings? saved, bool includeDemo)
    {
        var today = run.Today;
        var term = settings.PaymentTermDays;
        var customers = await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Id);
        var projects = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id);
        Customer? CustomerOf(int? id) => id is { } x ? customers.GetValueOrDefault(x) : null;
        decimal VatOn(decimal amount, Customer? c) => VatRegimes.ZeroVat(VatRegimes.DefaultFor(settings, c)) ? 0 : Money.Round(amount * 21 / 100);

        // Openstaande facturen op hun vervaldatum, min wat er al gecrediteerd en (deels) betaald is. Een te late factuur staat in de
        // lopende maand. Hun btw staat al in de aangifte.
        var open = await db.Invoices.AsNoTracking().Include(i => i.Lines).Include(i => i.Credits).ThenInclude(c => c.Lines).Include(i => i.Payments)
            .Where(i => (i.Status == InvoiceStatus.Sent || i.Status == InvoiceStatus.Overdue) && i.CreditForInvoiceId == null && (includeDemo || !i.IsDemo))
            .AsSplitQuery().ToListAsync();
        foreach (var inv in open.OrderBy(i => i.DueDate).ThenBy(i => i.Id))
        {
            var amount = inv.OpenAmount;
            if (amount <= 0) continue;
            var late = inv.DueDate.Date < today;
            var who = Name(CustomerOf(inv.CustomerId));
            run.Add(CashflowSources.Invoices, inv.DueDate, $"Factuur {inv.Number}",
                late ? $"{who} · vervaldatum {Day(inv.DueDate)} is voorbij" : who, amount, $"/facturen/{inv.Id}", late);
        }

        // Terugkerende facturen per periode die nog komt: gemaakt op hun dag (of vandaag, als die al voorbij is) en betaald na de
        // betaaltermijn. Gepauzeerde tellen niet mee, voorbeelden alleen als je dat vraagt.
        var templates = await db.RecurringInvoices.AsNoTracking().Include(t => t.Lines).Include(t => t.Customer)
            .Where(t => t.Active && t.NextRunDate != null).AsSplitQuery().ToListAsync();
        foreach (var t in templates.Where(t => includeDemo || !RecurringInvoiceService.IsDemo(t)).OrderBy(t => t.Id))
        {
            foreach (var p in RecurringPeriods.Upcoming(t).Take(120))
            {
                var made = p.RunDate.Date < today ? today : p.RunDate.Date;
                if (made > run.End) break;
                var paid = made.AddDays(term);
                var totals = RecurringInvoiceService.Build(t, p, settings, made).Totals;
                if (totals.Total <= 0) continue;
                run.Add(CashflowSources.Recurring, paid, t.Name, $"{Name(t.Customer)} · {RecurringPeriods.Label(p.From, p.To)}", totals.Total,
                    $"/facturen/terugkerend/{t.Id}", vat: totals.Vat, invoiceDate: made);
            }
        }

        // Geaccepteerde offertes zonder eindfactuur: het totaal min de aanbetaling, en bij een project per uur min de uren die al
        // gefactureerd zijn of nog openstaan (die tellen elders). Vanaf volgende maand, verdeeld over het aantal maanden dat je kiest.
        var spread = Math.Clamp(saved?.QuoteSpreadMonths ?? 1, 1, MaxSpread);
        var quotes = await db.Quotes.AsNoTracking().Include(q => q.Lines)
            .Where(q => q.Status == QuoteStatus.Accepted && q.InvoiceId == null && (includeDemo || !q.IsDemo)).AsSplitQuery().ToListAsync();
        var depositIds = quotes.Where(q => q.DepositInvoiceId != null).Select(q => q.DepositInvoiceId!.Value).ToList();
        var deposits = (await db.Invoices.AsNoTracking().Include(i => i.Lines).Where(i => depositIds.Contains(i.Id)).AsSplitQuery().ToListAsync())
            .ToDictionary(i => i.Id, i => i.Totals.Subtotal);
        var quoteProjectIds = quotes.Where(q => q.ProjectId != null).Select(q => q.ProjectId!.Value).ToList();
        var quoteProjects = (await ProjectEndpoints.ListAsync(db, q => q.Where(p => quoteProjectIds.Contains(p.Id)))).ToDictionary(p => p.Id);
        foreach (var q in quotes.OrderBy(q => q.DecidedAt ?? q.IssueDate).ThenBy(q => q.Id))
        {
            var totals = q.Totals;
            var remaining = totals.Subtotal;
            if (q.DepositInvoiceId is { } depositId) remaining -= deposits.GetValueOrDefault(depositId);
            if (q.ProjectId is { } pid && quoteProjects.TryGetValue(pid, out var project) && project.Billing == Billing.Hourly)
                remaining -= project.InvoicedValue + project.UnbilledValue;
            if (remaining <= 0) continue;
            var gross = totals.Subtotal != 0 ? Money.Round(remaining * totals.Total / totals.Subtotal) : remaining;
            var amounts = Split(gross, spread);
            var vats = Split(gross - remaining, spread);
            var who = Name(CustomerOf(q.CustomerId));
            for (var k = 0; k < spread; k++)
            {
                var month = run.First.AddMonths(k + 1);
                run.Add(CashflowSources.Quotes, month, $"Offerte {q.Number}", spread > 1 ? $"{who} · deel {k + 1} van {spread}" : who, amounts[k],
                    $"/offertes/{q.Id}", vat: vats[k], invoiceDate: month);
            }
        }

        // Nog te factureren: open uren op projecten per uur, open ritten met een kilometertarief en conceptfacturen. Alsof je ze
        // vandaag factureert en ze na de betaaltermijn betaald worden.
        var expected = today.AddDays(term);
        var hours = await db.TimeEntries.AsNoTracking().Where(t => t.Billable && t.InvoiceId == null && (includeDemo || !t.IsDemo))
            .GroupBy(t => t.ProjectId).Select(g => new { ProjectId = g.Key, Minutes = g.Sum(t => t.Minutes) }).ToListAsync();
        foreach (var h in hours.Where(h => projects.TryGetValue(h.ProjectId, out var p) && p.Billing == Billing.Hourly).OrderBy(h => projects[h.ProjectId].Name))
        {
            var p = projects[h.ProjectId];
            var value = Money.Round(ProjectEndpoints.Hours(h.Minutes) * p.HourlyRate);
            if (value <= 0) continue;
            var customer = CustomerOf(p.CustomerId);
            var vat = VatOn(value, customer);
            run.Add(CashflowSources.Unbilled, expected, p.Name, $"{Name(customer)} · {ProjectEndpoints.Hours(h.Minutes).ToString("0.##", Dutch)} uur open",
                value + vat, $"/projecten/{p.Id}", vat: vat, invoiceDate: today);
        }

        var trips = await db.Trips.AsNoTracking()
            .Where(t => t.Billable && t.InvoiceId == null && t.Kind != TripKinds.Private && (includeDemo || !t.IsDemo)).ToListAsync();
        var tripsByCustomer = trips.Select(t =>
            {
                var project = t.ProjectId is { } pid ? projects.GetValueOrDefault(pid) : null;
                var customerId = project?.CustomerId ?? t.CustomerId;
                return (Trip: t, CustomerId: customerId, Rate: project?.KilometreRate ?? CustomerOf(customerId)?.KilometreRate);
            })
            .Where(x => x.CustomerId != null && x.Rate is > 0).GroupBy(x => x.CustomerId!.Value);
        foreach (var g in tripsByCustomer.OrderBy(g => Name(CustomerOf(g.Key))))
        {
            // Net als op de factuur: één regel per kilometertarief.
            var value = g.GroupBy(x => x.Rate!.Value).Sum(r => Money.Round(r.Sum(x => x.Trip.Kilometres) * r.Key));
            var customer = CustomerOf(g.Key);
            var vat = VatOn(value, customer);
            run.Add(CashflowSources.Unbilled, expected, "Ritten", $"{Name(customer)} · {TripEndpoints.Km(g.Sum(x => x.Trip.Kilometres))} km open",
                value + vat, "/ritten", vat: vat, invoiceDate: today);
        }

        var drafts = await db.Invoices.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.Status == InvoiceStatus.Draft && i.CreditForInvoiceId == null && (includeDemo || !i.IsDemo)).AsSplitQuery().ToListAsync();
        foreach (var inv in drafts.OrderBy(i => i.Id))
        {
            var totals = inv.Totals;
            if (totals.Total <= 0) continue;
            // Een concept krijgt bij versturen de datum van die dag en houdt zijn betaaltermijn.
            var days = Math.Max(0, (inv.DueDate.Date - inv.IssueDate.Date).Days);
            run.Add(CashflowSources.Unbilled, today.AddDays(days), "Conceptfactuur", Name(CustomerOf(inv.CustomerId)), totals.Total, $"/facturen/{inv.Id}",
                vat: totals.Vat, invoiceDate: today);
        }
    }

    // Btw: elk kwartaal waarvan de uiterste datum vandaag of later in het venster valt en dat je niet als gedaan markeerde. Met de KOR
    // doe je geen aangifte.
    static async Task<List<CashflowVat>> VatAsync(AppDb db, BusinessClock clock, Run run, bool includeDemo)
    {
        var filed = (await db.VatFilings.AsNoTracking().Select(f => new { f.Year, f.Quarter }).ToListAsync()).Select(f => QuarterKey(f.Year, f.Quarter)).ToHashSet();
        var result = new List<CashflowVat>();
        var current = VatReturn.QuarterOf(run.Today);
        var (year, quarter) = current == 1 ? (run.Today.Year - 1, 4) : (run.Today.Year, current - 1);
        for (var i = 0; i < 4; i++, (year, quarter) = quarter == 4 ? (year + 1, 1) : (year, quarter + 1))
        {
            var due = VatReturn.DueDate(year, quarter);
            if (due > run.End) break;
            if (due < run.Today || filed.Contains(QuarterKey(year, quarter))) continue;
            var r = await VatReturn.CalculateAsync(db, clock, year, quarter, includeDemo);
            var parts = run.VatParts.TryGetValue(QuarterKey(year, quarter), out var p) ? new Dictionary<string, decimal>(p) : [];
            result.Add(new CashflowVat(year, quarter, r.Label, due, MonthKey(due), r.Total, parts, r.To >= run.Today));
        }
        return result;
    }

    // Inkomstenbelasting: wat de belastingpot per maand aangeeft om opzij te zetten, en de termijnen van je voorlopige aanslag per
    // maand (aan het eind van het jaar). Voor volgend jaar: de termijnen die je daar al invulde en een schatting met de cijfers van
    // dit jaar. De belastingpot rekent altijd zonder voorbeelddata.
    static async Task<(bool Once, bool NextYear)> TaxAsync(AppDb db, BusinessClock clock, Run run)
    {
        var year = run.Today.Year;
        var reserve = await TaxReserve.CalculateAsync(db, clock, year);
        var (thisRow, _) = await TaxReserve.SettingsAsync(db, year);
        var (nextRow, _) = await TaxReserve.SettingsAsync(db, year + 1);
        bool once = false, nextYear = false;
        for (var m = run.First; m <= run.End; m = m.AddMonths(1))
        {
            var row = m.Year == year ? thisRow : nextRow;
            if (row.Provisional == ProvisionalKinds.Monthly && row.ProvisionalAmount > 0 && m.Month > 12 - row.ProvisionalMonths)
                run.Add(CashflowSources.Tax, m, $"Voorlopige aanslag {m.Year}", $"Termijn {m.Month - (12 - row.ProvisionalMonths)} van {row.ProvisionalMonths}",
                    -Money.Round(row.ProvisionalAmount), "/jaaroverzicht#belastingpot");
            if (row.Provisional == ProvisionalKinds.Total && row.ProvisionalAmount > 0) once = true;
            if (m.Year == year)
            {
                if (reserve.PerMonth is > 0 and var perMonth)
                    run.Add(CashflowSources.Tax, m, "Opzij voor de inkomstenbelasting", "Wat de belastingpot per maand aangeeft", -perMonth, "/jaaroverzicht#belastingpot");
            }
            else if (reserve.Available && reserve.Total > 0)
            {
                var estimate = Money.Round(Math.Max(0, reserve.Total - Money.Round(nextRow.ProvisionalTotal)) / 12);
                if (estimate <= 0) continue;
                run.Add(CashflowSources.Tax, m, "Opzij voor de inkomstenbelasting", $"Geschat met de cijfers van {year}", -estimate, "/jaaroverzicht#belastingpot");
                nextYear = true;
            }
        }
        return (once, nextYear);
    }

    static async Task<CashflowHint> HintAsync(AppDb db, Run run, bool includeDemo)
    {
        var from = run.First.AddMonths(-3);
        var to = run.First.AddDays(-1);
        var expenses = await db.Expenses.AsNoTracking().Where(e => !e.Investment && e.Date >= from && e.Date <= to && (includeDemo || !e.IsDemo))
            .Select(e => new { e.Supplier, e.Date, e.Amount }).ToListAsync();
        var suppliers = expenses.Where(e => !string.IsNullOrWhiteSpace(e.Supplier)).GroupBy(e => e.Supplier.Trim().ToUpperInvariant())
            .Where(g => g.Select(e => e.Date.Month).Distinct().Count() == 3)
            .Select(g => new CashflowSupplier(g.OrderByDescending(e => e.Date).First().Supplier.Trim(), Money.Round(g.Sum(e => e.Amount) / 3)))
            .OrderByDescending(s => s.Average).ThenBy(s => s.Supplier).ToList();
        return new CashflowHint(from, to, suppliers, suppliers.Sum(s => s.Average), Money.Round(expenses.Sum(e => e.Amount) / 3));
    }

    public static async Task<CashflowDto> CalculateAsync(AppDb db, BusinessClock clock, bool includeDemo)
    {
        var run = new Run(clock.Today);
        var settings = await SettingsEndpoints.GetAsync(db);
        var saved = await db.CashflowSettings.AsNoTracking().FirstOrDefaultAsync();
        await IncomingAsync(db, run, settings, saved, includeDemo);
        var kor = settings.VatRegime == VatRegimes.Kor;
        List<CashflowVat> vat = kor ? [] : await VatAsync(db, clock, run, includeDemo);
        var (once, nextYear) = await TaxAsync(db, clock, run);
        if (saved is { FixedCostsPerMonth: > 0 })
            for (var m = run.First; m <= run.End; m = m.AddMonths(1))
                run.Add(CashflowSources.FixedCosts, m, "Vaste kosten", null, -saved.FixedCostsPerMonth, null);
        var hint = await HintAsync(db, run, includeDemo);
        var demoData = await db.Invoices.AnyAsync(i => i.IsDemo) || await db.Quotes.AnyAsync(q => q.IsDemo) || await db.RecurringInvoices.AnyAsync(t => t.IsDemo)
                       || await db.TimeEntries.AnyAsync(t => t.IsDemo) || await db.Trips.AnyAsync(t => t.IsDemo) || await db.Expenses.AnyAsync(e => e.IsDemo);
        var months = Enumerable.Range(0, Months).Select(i => MonthKey(run.First.AddMonths(i))).ToList();
        var order = CashflowSources.All.Select(s => s.Id).ToList();
        var items = run.Items.OrderBy(i => i.Month).ThenBy(i => order.IndexOf(i.Source)).ThenBy(i => i.Date).ToList();
        return new CashflowDto(run.Today, months, settings.PaymentTermDays, CashflowSources.All, items, vat, CashflowSettingsDto.From(saved), hint,
            kor, once, nextYear, demoData, includeDemo && demoData);
    }

    // Wat er deze maand naar verwachting binnenkomt, voor de tegel op het dashboard: alle bronnen die geld opleveren, zonder
    // voorbeelddata. Late is het deel van facturen die al te laat zijn.
    public static async Task<(decimal ExpectedIn, decimal Late)> ThisMonthAsync(AppDb db, BusinessClock clock)
    {
        var run = new Run(clock.Today);
        var settings = await SettingsEndpoints.GetAsync(db);
        var saved = await db.CashflowSettings.AsNoTracking().FirstOrDefaultAsync();
        await IncomingAsync(db, run, settings, saved, false);
        var month = run.Items.Where(i => i.Month == MonthKey(run.First)).ToList();
        return (month.Sum(i => i.Amount), month.Where(i => i.Late).Sum(i => i.Amount));
    }

    public static string? Check(CashflowSettingsInput i)
    {
        if (i.BankBalance is { } b && Math.Abs(b) > Money.MaxAmount) return "Dat saldo is te groot.";
        if (i.FixedCostsPerMonth < 0) return "Vaste kosten kunnen niet negatief zijn.";
        if (i.FixedCostsPerMonth > Money.MaxAmount) return "Dat bedrag is te groot.";
        if (i.QuoteSpreadMonths is < 1 or > MaxSpread) return $"Kies over hoeveel maanden een offerte binnenkomt, van 1 tot en met {MaxSpread}.";
        return null;
    }
}

public static class CashflowEndpoints
{
    // Onder /insights, dus net als de rest van de inzichten alleen voor beheerders. Opslaan kan niet in een werkruimte die alleen-lezen is.
    public static void MapCashflow(this RouteGroupBuilder insights)
    {
        insights.MapGet("/cashflow", async (AppDb db, BusinessClock clock, bool? demo) => Results.Ok(await Cashflow.CalculateAsync(db, clock, demo == true)));

        insights.MapPut("/cashflow/settings", async (AppDb db, BusinessClock clock, ClaimsPrincipal user, CashflowSettingsInput input) =>
        {
            if (Cashflow.Check(input) is { } error) return Results.BadRequest(new { error });
            await using var tx = await WriteLock.BeginAsync(db);
            var row = await db.CashflowSettings.FirstOrDefaultAsync();
            if (row is null) db.CashflowSettings.Add(row = new CashflowSettings());
            // Het saldo geldt op de dag dat je het invult; een gelijk saldo houdt zijn datum.
            var balance = input.BankBalance is { } b ? Money.Round(b) : (decimal?)null;
            if (balance != row.BankBalance || row.BankBalanceDate is null) row.BankBalanceDate = balance is null ? null : clock.Today;
            row.BankBalance = balance;
            row.FixedCostsPerMonth = Money.Round(input.FixedCostsPerMonth);
            row.QuoteSpreadMonths = input.QuoteSpreadMonths;
            row.UpdatedAt = clock.UtcNow;
            row.UpdatedBy = user.FindFirstValue(ClaimTypes.Name);
            db.Log("instellingen", "Instellingen voor de cashflow-prognose bijgewerkt");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(CashflowSettingsDto.From(row));
        });
    }
}
