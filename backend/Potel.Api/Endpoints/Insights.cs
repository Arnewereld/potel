using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Invoicing;

namespace Potel.Api.Endpoints;

// Eén klant in de inzichten over een periode. Revenue is de omzet zonder btw op factuurdatum, OpenValue de waarde van factureerbare
// uren uit de periode die nog niet (of alleen op een concept) gefactureerd zijn. EffectiveRate is de omzet gedeeld door alle uren.
public record InsightCustomer(int Id, string Name, int Minutes, int BillableMinutes, decimal Revenue, int OpenMinutes, decimal OpenValue,
    decimal? EffectiveRate);

// Eén project, net als een klant. Voor een vaste prijs ook wat je tot nu toe per uur verdient: de prijs gedeeld door alle uren die er
// ooit op geboekt zijn (MinutesTotal). BelowNormal: dat is minder dan je normale tarief (NormalRate).
public record InsightProject(int Id, string Name, int CustomerId, string? CustomerName, string Color, string Billing, string Status,
    int Minutes, int BillableMinutes, decimal Revenue, int OpenMinutes, decimal OpenValue, decimal? EffectiveRate,
    decimal? FixedPrice, int MinutesTotal, decimal? EarnedRate, decimal NormalRate, bool BelowNormal);

// De omzet van een maand en van dezelfde maand een jaar eerder.
public record InsightMonth(string Month, decimal Revenue, decimal Previous);

public record InsightShare(int CustomerId, string Name, decimal Revenue, decimal Share);

// Hoe je omzet van de afgelopen 12 maanden over je klanten verdeeld is. Warning: één klant had meer dan 70 procent.
public record InsightConcentration(DateTime From, DateTime To, decimal Revenue, List<InsightShare> Customers, decimal? TopShare, bool Warning);

// Wat je echt verdient in een periode. BillableRate is je afgesproken tarief, gemiddeld over je factureerbare uren op projecten per uur.
// BillableShare is welk deel van je uren factureerbaar was; Occupancy (bezettingsgraad) zet je factureerbare uren af tegen je doel
// per week, voor de dagen van de periode die voorbij zijn (GoalHours).
public record InsightsDto(
    string Period, DateTime From, DateTime To, string Label, DateTime Today, bool DemoData, bool IncludesDemo,
    decimal Revenue, int Minutes, int BillableMinutes, decimal? EffectiveRate, decimal? BillableRate, decimal? BillableShare,
    decimal? Occupancy, decimal GoalHours, int WeeklyTarget, decimal NormalRate, int OpenMinutes, decimal OpenValue, decimal UnassignedRevenue,
    List<InsightCustomer> Customers, List<InsightProject> Projects, List<InsightMonth> Months, InsightConcentration Concentration);

// De perioden die je op de pagina Inzichten kiest.
public static class InsightPeriods
{
    public const string Month = "maand";
    public const string Quarter = "kwartaal";
    public const string Year = "jaar";
    public const string Last12 = "12-maanden";
    public const string Custom = "eigen";
    public static readonly string[] All = [Month, Quarter, Year, Last12, Custom];

    public const string UnknownError = "Kies een periode: deze maand, dit kwartaal, dit jaar, de laatste 12 maanden of een eigen periode.";
    public const string CustomError = "Vul voor een eigen periode een begin- en einddatum in, met het begin op of voor het einde.";
    public const string TooLongError = "Een eigen periode kan hooguit tien jaar zijn.";

    // Van en tot en met, zoals ze in Nederland lopen. De laatste 12 maanden zijn deze maand en de 11 maanden ervoor.
    public static (DateTime From, DateTime To)? Resolve(string period, DateTime today, DateTime? from, DateTime? to)
    {
        var month = new DateTime(today.Year, today.Month, 1);
        return period switch
        {
            Month => (month, month.AddMonths(1).AddDays(-1)),
            Quarter => VatReturn.Period(today.Year, VatReturn.QuarterOf(today)),
            Year => (new DateTime(today.Year, 1, 1), new DateTime(today.Year, 12, 31)),
            Last12 => (month.AddMonths(-11), month.AddMonths(1).AddDays(-1)),
            Custom when from is { } f && to is { } t && f.Date <= t.Date => (f.Date, t.Date),
            _ => null,
        };
    }
}

public static class Insights
{
    // Boven dit deel van je omzet bij één klant tonen we een rustige waarschuwing.
    public const decimal ConcentrationLimit = 0.70m;

    static string Name(Customer c) => string.IsNullOrWhiteSpace(c.Company) ? c.Name : c.Company;

    static decimal? Rate(decimal amount, int minutes) => minutes > 0 ? Money.Round(amount * 60 / minutes) : null;

    // De uren van een project per uur tegen het tarief van het project, op dezelfde manier afgerond als de open uren bij Projecten.
    static decimal HourValue(int minutes, decimal rate) => Money.Round(ProjectEndpoints.Hours(minutes) * rate);

    public static async Task<InsightsDto> CalculateAsync(AppDb db, BusinessClock clock, string period, DateTime from, DateTime to, bool includeDemo)
    {
        var today = clock.Today;
        var settings = await SettingsEndpoints.GetAsync(db);
        var month = new DateTime(today.Year, today.Month, 1);
        var chartFrom = month.AddMonths(-23);
        var chartTo = month.AddMonths(1).AddDays(-1);

        // Omzet op factuurdatum, zonder btw en zonder concepten. Creditnota's hebben negatieve regels en trekken dus vanzelf af.
        var loadFrom = from < chartFrom ? from : chartFrom;
        var loadTo = to > chartTo ? to : chartTo;
        var invoices = await db.Invoices.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.Status != InvoiceStatus.Draft && (includeDemo || !i.IsDemo) && i.IssueDate >= loadFrom && i.IssueDate < loadTo.AddDays(1))
            .AsSplitQuery().ToListAsync();
        var inPeriod = invoices.Where(i => i.IssueDate.Date >= from && i.IssueDate.Date <= to).ToList();

        var entries = await db.TimeEntries.AsNoTracking().Where(t => (includeDemo || !t.IsDemo) && t.Date >= from && t.Date <= to)
            .Select(t => new { t.ProjectId, t.Minutes, t.Billable, t.InvoiceId }).ToListAsync();
        // Uren op een concept zijn nog niet gefactureerd: die tellen als open.
        var invoiceIds = entries.Where(e => e.InvoiceId != null).Select(e => e.InvoiceId!.Value).Distinct().ToList();
        var drafts = (await db.Invoices.Where(i => invoiceIds.Contains(i.Id) && i.Status == InvoiceStatus.Draft).Select(i => i.Id).ToListAsync()).ToHashSet();
        var projects = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id);
        var customers = await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Id);

        var split = await RevenueSplit.ByProjectAsync(db, inPeriod);
        var revenue = inPeriod.Sum(i => i.Totals.Subtotal);
        var minutes = entries.Sum(e => e.Minutes);
        var billable = entries.Where(e => e.Billable).Sum(e => e.Minutes);
        bool Open(int projectId, bool isBillable, int? invoiceId) =>
            isBillable && projects[projectId].Billing == Billing.Hourly && (invoiceId is not { } id || drafts.Contains(id));

        // Per project: uren, omzet en open uren in de periode. Een vaste prijs ook met alle uren die er ooit op staan.
        var projectRevenue = split.Values.SelectMany(s => s).Where(x => x.Key != RevenueSplit.NoProject)
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Sum(x => x.Value));
        var projectIds = entries.Select(e => e.ProjectId).Concat(projectRevenue.Keys).Where(projects.ContainsKey).Distinct().ToList();
        var fixedIds = projectIds.Where(id => projects[id].Billing == Billing.Fixed).ToList();
        var lifetime = await db.TimeEntries.AsNoTracking().Where(t => fixedIds.Contains(t.ProjectId) && (includeDemo || !t.IsDemo))
            .GroupBy(t => t.ProjectId).Select(g => new { g.Key, Minutes = g.Sum(t => t.Minutes) }).ToDictionaryAsync(x => x.Key, x => x.Minutes);
        var projectRows = projectIds.Select(id =>
        {
            var p = projects[id];
            var mine = entries.Where(e => e.ProjectId == id).ToList();
            var open = mine.Where(e => Open(e.ProjectId, e.Billable, e.InvoiceId)).Sum(e => e.Minutes);
            var rev = projectRevenue.GetValueOrDefault(id);
            var normal = p.HourlyRate > 0 ? p.HourlyRate : settings.DefaultHourlyRate;
            var total = lifetime.GetValueOrDefault(id);
            var earned = p.Billing == Billing.Fixed ? Rate(p.FixedPrice, total) : null;
            return new InsightProject(p.Id, p.Name, p.CustomerId, customers.TryGetValue(p.CustomerId, out var c) ? Name(c) : null, p.Color, p.Billing, p.Status,
                mine.Sum(e => e.Minutes), mine.Where(e => e.Billable).Sum(e => e.Minutes), rev, open, p.Billing == Billing.Hourly ? HourValue(open, p.HourlyRate) : 0,
                Rate(rev, mine.Sum(e => e.Minutes)), p.Billing == Billing.Fixed ? p.FixedPrice : null, p.Billing == Billing.Fixed ? total : 0,
                earned, normal, earned is { } e && e < normal);
        }).OrderByDescending(p => p.Revenue).ThenByDescending(p => p.Minutes).ThenBy(p => p.Name).ToList();

        // Per klant: de omzet van zijn facturen en de uren van zijn projecten.
        var customerRevenue = inPeriod.GroupBy(i => i.CustomerId).ToDictionary(g => g.Key, g => g.Sum(i => i.Totals.Subtotal));
        var customerIds = customerRevenue.Keys.Concat(projectRows.Select(p => p.CustomerId)).Where(customers.ContainsKey).Distinct();
        var customerRows = customerIds.Select(id =>
        {
            var mine = projectRows.Where(p => p.CustomerId == id).ToList();
            var mins = mine.Sum(p => p.Minutes);
            var rev = customerRevenue.GetValueOrDefault(id);
            return new InsightCustomer(id, Name(customers[id]), mins, mine.Sum(p => p.BillableMinutes), rev, mine.Sum(p => p.OpenMinutes), mine.Sum(p => p.OpenValue), Rate(rev, mins));
        }).OrderByDescending(c => c.Revenue).ThenByDescending(c => c.Minutes).ThenBy(c => c.Name).ToList();

        // Je afgesproken tarief, gemiddeld over de factureerbare uren op projecten per uur.
        var hourly = entries.Where(e => e.Billable && projects[e.ProjectId].Billing == Billing.Hourly).ToList();
        var hourlyMinutes = hourly.Sum(e => e.Minutes);
        decimal? billableRate = hourlyMinutes > 0 ? Money.Round(hourly.Sum(e => e.Minutes * projects[e.ProjectId].HourlyRate) / hourlyMinutes) : null;

        // Het doel: je uren per week, voor de dagen van de periode tot en met vandaag.
        var elapsedTo = to < today ? to : today;
        var elapsedDays = elapsedTo >= from ? (elapsedTo - from).Days + 1 : 0;
        var goal = Math.Round(settings.WeeklyHoursTarget * elapsedDays / 7m, 1, MidpointRounding.AwayFromZero);
        decimal? occupancy = goal > 0 ? Math.Round(billable / 60m / goal, 4, MidpointRounding.AwayFromZero) : null;
        decimal? share = minutes > 0 ? Math.Round((decimal)billable / minutes, 4, MidpointRounding.AwayFromZero) : null;

        // Omzet per maand: de laatste 12 maanden, met dezelfde maand een jaar eerder.
        decimal MonthRevenue(DateTime m) => invoices.Where(i => i.IssueDate.Year == m.Year && i.IssueDate.Month == m.Month).Sum(i => i.Totals.Subtotal);
        var months = Enumerable.Range(0, 12).Select(i => month.AddMonths(i - 11))
            .Select(m => new InsightMonth(m.ToString("yyyy-MM"), MonthRevenue(m), MonthRevenue(m.AddMonths(-12)))).ToList();

        // Hoe afhankelijk je bent van één klant: de omzet van de laatste 12 maanden per klant. Een klant die per saldo niets
        // opleverde (bijvoorbeeld door een creditnota) telt niet mee.
        var yearFrom = month.AddMonths(-11);
        var byCustomer = invoices.Where(i => i.IssueDate.Date >= yearFrom && i.IssueDate.Date <= chartTo).GroupBy(i => i.CustomerId)
            .Select(g => (Id: g.Key, Revenue: g.Sum(i => i.Totals.Subtotal))).Where(x => x.Revenue > 0).ToList();
        var yearRevenue = byCustomer.Sum(x => x.Revenue);
        var shares = byCustomer.OrderByDescending(x => x.Revenue)
            .Select(x => new InsightShare(x.Id, customers.TryGetValue(x.Id, out var c) ? Name(c) : "Onbekende klant", x.Revenue, Math.Round(x.Revenue / yearRevenue, 4, MidpointRounding.AwayFromZero)))
            .ToList();
        var topShare = shares.Count > 0 ? shares[0].Share : (decimal?)null;
        var concentration = new InsightConcentration(yearFrom, chartTo, yearRevenue, shares, topShare, topShare > ConcentrationLimit);

        var demoData = await db.Invoices.AnyAsync(i => i.IsDemo) || await db.TimeEntries.AnyAsync(t => t.IsDemo);
        var openMinutes = projectRows.Sum(p => p.OpenMinutes);
        return new InsightsDto(period, from, to, RecurringPeriods.Label(from, to), today, demoData, includeDemo && demoData,
            revenue, minutes, billable, Rate(revenue, minutes), billableRate, share, occupancy, goal, settings.WeeklyHoursTarget, settings.DefaultHourlyRate,
            openMinutes, projectRows.Sum(p => p.OpenValue), split.Values.Sum(s => s.GetValueOrDefault(RevenueSplit.NoProject)),
            customerRows, projectRows, months, concentration);
    }
}

// Hoe de omzet van facturen over projecten verdeeld is. Een factuur hangt zelf niet aan een project, dus kijken we per regel: een
// urenregel hoort bij het project van zijn uren en een regel met ritten bij het project van die ritten. Wat dan overblijft op een
// factuur uit een offerte met een project, hoort bij dat project. Een creditnota trekt naar rato af bij de projecten van de factuur
// die hij corrigeert. Wat nergens bij hoort, staat onder NoProject (zonder project).
public static class RevenueSplit
{
    // Project-ids beginnen bij 1, dus 0 is: zonder project.
    public const int NoProject = 0;

    public static async Task<Dictionary<int, Dictionary<int, decimal>>> ByProjectAsync(AppDb db, List<Invoice> invoices)
    {
        var known = invoices.ToDictionary(i => i.Id);
        var missing = invoices.Where(i => i.CreditForInvoiceId is { } o && !known.ContainsKey(o)).Select(i => i.CreditForInvoiceId!.Value).Distinct().ToList();
        foreach (var original in await db.Invoices.AsNoTracking().Include(i => i.Lines).Where(i => missing.Contains(i.Id)).AsSplitQuery().ToListAsync())
            known[original.Id] = original;
        var ids = known.Keys.ToList();

        var entries = await db.TimeEntries.AsNoTracking().Where(t => t.InvoiceId != null && ids.Contains(t.InvoiceId.Value))
            .Select(t => new { InvoiceId = t.InvoiceId!.Value, t.InvoiceLineId, t.ProjectId, t.Minutes }).ToListAsync();
        var trips = await db.Trips.AsNoTracking().Where(t => t.InvoiceId != null && t.InvoiceLineId != null && t.ProjectId != null && ids.Contains(t.InvoiceId.Value))
            .Select(t => new { InvoiceLineId = t.InvoiceLineId!.Value, ProjectId = t.ProjectId!.Value }).ToListAsync();
        var quotes = await db.Quotes.AsNoTracking().Where(q => q.ProjectId != null
                && ((q.InvoiceId != null && ids.Contains(q.InvoiceId.Value)) || (q.DepositInvoiceId != null && ids.Contains(q.DepositInvoiceId.Value))))
            .Select(q => new { ProjectId = q.ProjectId!.Value, q.InvoiceId, q.DepositInvoiceId }).ToListAsync();
        var rates = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.HourlyRate);

        var lineProject = new Dictionary<int, int>();
        foreach (var e in entries.Where(e => e.InvoiceLineId != null)) lineProject.TryAdd(e.InvoiceLineId!.Value, e.ProjectId);
        foreach (var t in trips) lineProject.TryAdd(t.InvoiceLineId, t.ProjectId);

        var direct = new Dictionary<int, Dictionary<int, decimal>>();
        Dictionary<int, decimal> Direct(Invoice inv)
        {
            if (direct.TryGetValue(inv.Id, out var done)) return done;
            var result = new Dictionary<int, decimal>();
            void Add(int project, decimal amount) { if (amount != 0) result[project] = result.GetValueOrDefault(project) + amount; }
            var lineIds = inv.Lines.Select(l => l.Id).ToHashSet();
            var rest = 0m;
            foreach (var line in inv.Lines)
            {
                if (lineProject.TryGetValue(line.Id, out var p)) Add(p, Money.LineAmount(line));
                else rest += Money.LineAmount(line);
            }
            // Uren van voor de koppeling per regel tellen tegen het uurtarief van hun project, niet meer dan er nog over is.
            foreach (var g in entries.Where(e => e.InvoiceId == inv.Id && (e.InvoiceLineId is not { } l || !lineIds.Contains(l))).GroupBy(e => e.ProjectId))
            {
                if (rest <= 0) break;
                var value = Math.Min(rest, Money.Round(ProjectEndpoints.Hours(g.Sum(e => e.Minutes)) * rates.GetValueOrDefault(g.Key)));
                Add(g.Key, value);
                rest -= value;
            }
            if (quotes.FirstOrDefault(q => q.InvoiceId == inv.Id || q.DepositInvoiceId == inv.Id) is { } quote)
            {
                Add(quote.ProjectId, rest);
                rest = 0;
            }
            Add(NoProject, rest);
            return direct[inv.Id] = result;
        }

        var split = new Dictionary<int, Dictionary<int, decimal>>();
        foreach (var inv in invoices)
        {
            var subtotal = inv.Totals.Subtotal;
            if (inv.CreditForInvoiceId is { } originalId && known.TryGetValue(originalId, out var original) && original.Totals.Subtotal != 0)
                split[inv.Id] = Proportional(Direct(original), original.Totals.Subtotal, subtotal);
            else
                split[inv.Id] = Direct(inv);
        }
        return split;
    }

    // Verdeelt een bedrag naar rato van de verdeling van de oorspronkelijke factuur. Afronden op centen; wat dat scheelt, gaat naar
    // het grootste deel, zodat de delen samen precies het bedrag zijn.
    public static Dictionary<int, decimal> Proportional(Dictionary<int, decimal> parts, decimal total, decimal amount)
    {
        var result = parts.Where(p => p.Value != 0).ToDictionary(p => p.Key, p => Money.Round(amount * p.Value / total));
        if (result.Count == 0) return amount == 0 ? [] : new() { [NoProject] = amount };
        var diff = amount - result.Values.Sum();
        if (diff != 0)
        {
            var largest = result.OrderByDescending(p => Math.Abs(p.Value)).First().Key;
            result[largest] += diff;
        }
        return result;
    }
}

public static class InsightsEndpoints
{
    // Inzichten laten zien wat je bedrijf verdient: net als de belastingpot en de bank alleen voor beheerders.
    public static void MapInsights(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/insights").RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Zonder periode: dit jaar. Een eigen periode met from en to (tot en met).
        g.MapGet("/", async (AppDb db, BusinessClock clock, string? period, DateTime? from, DateTime? to, bool? demo) =>
        {
            var p = string.IsNullOrWhiteSpace(period) ? InsightPeriods.Year : period.Trim();
            if (!InsightPeriods.All.Contains(p)) return Results.BadRequest(new { error = InsightPeriods.UnknownError });
            if (InsightPeriods.Resolve(p, clock.Today, from, to) is not { } range) return Results.BadRequest(new { error = InsightPeriods.CustomError });
            var (f, t) = range;
            if (f.Year < 2000 || t.Year > 2100) return Results.BadRequest(new { error = InsightPeriods.CustomError });
            if (t > f.AddYears(10)) return Results.BadRequest(new { error = InsightPeriods.TooLongError });
            return Results.Ok(await Insights.CalculateAsync(db, clock, p, f, t, demo == true));
        });

        // De cashflow-prognose staat op dezelfde pagina.
        g.MapCashflow();
    }
}
