using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

// Eén rubriek van de btw-aangifte. Vat is null bij een rubriek zonder btw-kolom (1e en 3b).
public record VatRubric(string Code, string Title, decimal Base, decimal? Vat);

// Wat er per btw-nummer van een klant in een ander EU-land in de opgaaf ICP hoort.
public record IcpLine(string VatNumber, string Customer, string Country, decimal Amount);

// Een factuur of uitgave die meetelt, zodat je kunt nagaan waar een bedrag vandaan komt.
public record VatSource(string Kind, int Id, DateTime Date, string Name, string? Number, string Rubric, decimal Base, decimal Vat);

public record VatQuarterRef(int Year, int Quarter, string Label, DateTime DueDate);

public record VatReturnDto(
    int Year, int Quarter, string Label, DateTime From, DateTime To, DateTime DueDate,
    string Regime, bool Kor, bool Deduction,
    List<VatRubric> Rubrics, decimal Due, decimal Deductible, decimal Total,
    decimal Revenue, decimal OutsideEu, decimal Exempt, decimal KorRevenue,
    List<IcpLine> Icp, List<VatSource> Sources, bool DemoData, bool IncludesDemo, VatQuarterRef? Pending);

// Rekent de btw-aangifte per kwartaal uit. Dezelfde som voor de pagina Btw-aangifte en de tegel op het dashboard.
// Bronnen per rubriek staan bij de rubrieken hieronder en op de pagina; dit is hulp bij het invullen, geen belastingadvies.
public static class VatReturn
{
    // Titels zoals de Belastingdienst ze noemt.
    public const string R1a = "Leveringen/diensten belast met hoog tarief";
    public const string R1b = "Leveringen/diensten belast met laag tarief";
    public const string R1c = "Leveringen/diensten belast met overige tarieven, behalve 0%";
    public const string R1e = "Leveringen/diensten belast met 0% of niet bij u belast";
    public const string R3b = "Leveringen naar of diensten in landen binnen de EU";
    public const string R4a = "Leveringen/diensten uit landen buiten de EU";
    public const string R4b = "Leveringen/diensten uit landen binnen de EU";
    public const string R5a = "Verschuldigde btw";
    public const string R5b = "Voorbelasting";

    public static (DateTime From, DateTime To) Period(int year, int quarter)
    {
        var from = new DateTime(year, (quarter - 1) * 3 + 1, 1);
        return (from, from.AddMonths(3).AddDays(-1));
    }

    // Uiterlijk de laatste dag van de maand na het kwartaal, ook als dat in het weekend valt.
    public static DateTime DueDate(int year, int quarter) => Period(year, quarter).From.AddMonths(4).AddDays(-1);

    public static int QuarterOf(DateTime d) => (d.Month - 1) / 3 + 1;

    public static string Label(int year, int quarter) => $"Q{quarter} {year}";

    // Het tijdvak waarin een factuur meetelt. Btw in Nederland hoort bij de factuurdatum. Een dienst aan een ondernemer in een
    // ander EU-land (3b en de opgaaf ICP) geef je aan in het tijdvak waarin je hem leverde: het einde van de leverperiode op
    // de factuur, of de factuurdatum als die er niet op staat. Een creditnota telt op zijn eigen datum.
    public static DateTime PeriodDate(Invoice i) =>
        i.VatRegime == VatRegimes.ReverseCharge && i.CreditForInvoiceId is null ? (i.DeliveryTo ?? i.DeliveryFrom ?? i.IssueDate).Date : i.IssueDate.Date;

    public static async Task<VatReturnDto> CalculateAsync(AppDb db, BusinessClock clock, int year, int quarter, bool includeDemo = false)
    {
        var (from, to) = Period(year, quarter);
        var settings = await SettingsEndpoints.GetAsync(db);
        var kor = settings.VatRegime == VatRegimes.Kor;
        // Met de KOR, of als je omzet vrijgesteld is, trek je de btw op je kosten niet af.
        var deduction = settings.VatRegime is not (VatRegimes.Kor or VatRegimes.Exempt);

        // Ruim genoeg om ook diensten te vinden die in dit kwartaal geleverd zijn en later zijn gefactureerd.
        var invoices = await db.Invoices.AsNoTracking().Include(i => i.Lines).Include(i => i.Customer)
            .Where(i => i.Status != InvoiceStatus.Draft && (includeDemo || !i.IsDemo) && i.IssueDate >= from.AddYears(-1) && i.IssueDate < to.AddYears(1).AddDays(1))
            .AsSplitQuery().ToListAsync();
        invoices = invoices.Where(i => PeriodDate(i) >= from && PeriodDate(i) <= to).OrderBy(i => i.IssueDate).ThenBy(i => i.Id).ToList();
        var expenses = await db.Expenses.AsNoTracking()
            .Where(e => e.Date >= from && e.Date <= to && (includeDemo || !e.IsDemo)).OrderBy(e => e.Date).ThenBy(e => e.Id).ToListAsync();

        var bases = new Dictionary<string, decimal>();
        var vats = new Dictionary<string, decimal>();
        void Add(string rubric, decimal b, decimal v)
        {
            bases[rubric] = bases.GetValueOrDefault(rubric) + b;
            vats[rubric] = vats.GetValueOrDefault(rubric) + v;
        }

        var sources = new List<VatSource>();
        decimal outsideEu = 0, exempt = 0, korRevenue = 0;
        var icp = new Dictionary<string, (string Customer, string Country, decimal Amount)>();
        foreach (var inv in invoices)
        {
            var totals = inv.Totals;
            var name = inv.Buyer?.Name ?? (string.IsNullOrWhiteSpace(inv.Customer?.Company) ? inv.Customer?.Name : inv.Customer.Company) ?? "";
            void Source(string rubric, decimal b, decimal v) =>
                sources.Add(new VatSource("factuur", inv.Id, PeriodDate(inv), name, inv.Number, rubric, b, v));
            switch (inv.VatRegime)
            {
                case VatRegimes.Normal:
                    // Per tarief, met de btw zoals hij op de factuur staat (afgerond per tarief).
                    foreach (var g in totals.VatGroups)
                    {
                        var rubric = g.Rate switch { 21 => "1a", 9 => "1b", 0 => "1e", _ => "1c" };
                        Add(rubric, g.Base, rubric == "1e" ? 0 : g.Vat);
                        Source(rubric, g.Base, g.Vat);
                    }
                    break;
                case VatRegimes.ReverseCharge:
                    Add("3b", totals.Subtotal, 0);
                    Source("3b", totals.Subtotal, 0);
                    var vatNumber = InvoiceParty.NormalizeVatId(inv.Buyer?.VatNumber ?? inv.Customer?.VatNumber) ?? "onbekend";
                    var entry = icp.TryGetValue(vatNumber, out var known) ? known : (Customer: name, Country: Countries.Normalize(inv.Buyer?.Country ?? inv.Customer?.Country), Amount: 0m);
                    icp[vatNumber] = (entry.Customer, entry.Country, entry.Amount + totals.Subtotal);
                    break;
                case VatRegimes.OutsideEu:
                    outsideEu += totals.Subtotal;
                    Source("buiten-eu", totals.Subtotal, 0);
                    break;
                case VatRegimes.Exempt:
                    exempt += totals.Subtotal;
                    Source("vrijgesteld", totals.Subtotal, 0);
                    break;
                default:
                    korRevenue += totals.Subtotal;
                    Source("kor", totals.Subtotal, 0);
                    break;
            }
        }

        // Kosten op de datum van de uitgave. Verlegde btw geef je zelf aan (4a of 4b) en trek je als voorbelasting weer af.
        decimal deductible = 0;
        foreach (var e in expenses)
        {
            var rubric = e.ReverseCharge ? (e.SupplierRegion == SupplierRegions.OutsideEu ? "4a" : "4b") : "5b";
            if (e.ReverseCharge) Add(rubric, e.Amount, e.VatAmount);
            if (deduction) deductible += e.VatAmount;
            if (e.ReverseCharge || e.VatAmount != 0)
                sources.Add(new VatSource("kosten", e.Id, e.Date, e.Supplier, null, rubric, e.AmountExclVat, e.VatAmount));
        }

        var rubrics = new List<VatRubric>
        {
            new("1a", R1a, bases.GetValueOrDefault("1a"), vats.GetValueOrDefault("1a")),
            new("1b", R1b, bases.GetValueOrDefault("1b"), vats.GetValueOrDefault("1b")),
        };
        // Een ander tarief dan 21%, 9% of 0% hoort bij een gewone dienst niet voor te komen; we tonen het alleen als het er is.
        if (bases.ContainsKey("1c")) rubrics.Add(new("1c", R1c, bases["1c"], vats["1c"]));
        rubrics.Add(new("1e", R1e, bases.GetValueOrDefault("1e"), null));
        rubrics.Add(new("3b", R3b, bases.GetValueOrDefault("3b"), null));
        rubrics.Add(new("4a", R4a, bases.GetValueOrDefault("4a"), vats.GetValueOrDefault("4a")));
        rubrics.Add(new("4b", R4b, bases.GetValueOrDefault("4b"), vats.GetValueOrDefault("4b")));
        var due = rubrics.Sum(r => r.Vat ?? 0);
        rubrics.Add(new("5a", R5a, 0, due));
        rubrics.Add(new("5b", R5b, 0, deductible));

        var revenue = bases.Where(b => b.Key is "1a" or "1b" or "1c" or "1e" or "3b").Sum(b => b.Value) + outsideEu + exempt + korRevenue;
        var demoData = await db.Invoices.AnyAsync(i => i.IsDemo) || await db.Expenses.AnyAsync(e => e.IsDemo);

        return new VatReturnDto(
            year, quarter, Label(year, quarter), from, to, DueDate(year, quarter), settings.VatRegime, kor, deduction,
            rubrics, due, deductible, due - deductible, revenue, outsideEu, exempt, korRevenue,
            icp.OrderBy(x => x.Value.Country).ThenBy(x => x.Key).Select(x => new IcpLine(x.Key, x.Value.Customer, x.Value.Country, x.Value.Amount)).ToList(),
            sources, demoData, includeDemo && demoData, kor ? null : PendingOn(clock.Today));
    }

    // Het vorige kwartaal, zolang de aangifte daarvan nog op tijd kan.
    public static VatQuarterRef? PendingOn(DateTime today)
    {
        var (year, quarter) = QuarterOf(today) == 1 ? (today.Year - 1, 4) : (today.Year, QuarterOf(today) - 1);
        return today.Date <= DueDate(year, quarter) ? new VatQuarterRef(year, quarter, Label(year, quarter), DueDate(year, quarter)) : null;
    }
}

public static class TaxEndpoints
{
    public static void MapTax(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/tax");

        // De btw-aangifte van een kwartaal; zonder jaar en kwartaal het lopende kwartaal.
        g.MapGet("/vat-return", async (AppDb db, BusinessClock clock, int? year, int? quarter, bool? demo) =>
        {
            var today = clock.Today;
            var y = year ?? today.Year;
            var q = quarter ?? VatReturn.QuarterOf(today);
            if (y is < 2000 or > 2100 || q is < 1 or > 4) return Results.BadRequest(new { error = "Kies een kwartaal van 1 tot en met 4 en een geldig jaar." });
            return Results.Ok(await VatReturn.CalculateAsync(db, clock, y, q, demo == true));
        });
    }
}
