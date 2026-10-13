using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

// Een bedrijfsmiddel zoals de afschrijving het ziet: aankoopdatum, aanschafprijs voor je winst, restwaarde, gebruiksduur
// en eventueel wanneer en voor hoeveel (zonder btw) je het verkocht of wegdeed.
public record AssetPlan(DateTime Purchased, decimal Cost, decimal Residual, int Years, DateTime? DisposedOn = null, decimal? DisposalPrice = null);

public record DepreciationYear(int Year, decimal Depreciation, decimal BookValue);

// Verkocht of weggedaan op een datum, voor een prijs zonder btw. Geen prijs is: niets gekregen.
public record DisposalInput(DateTime? Date, decimal? Price);

public record InvestmentDto(
    int Id, DateTime Date, string Supplier, string? Description, string Category, decimal Cost, decimal Residual, int Years, bool PassengerCar,
    bool CountsForKia, decimal PerYear, bool AboveMaximum, decimal Depreciation, decimal BookValue,
    DateTime? DisposedOn, decimal? DisposalPrice, decimal? BookValueAtDisposal, decimal? DisposalResult, bool Demo, List<DepreciationYear> Schedule);

// De investeringen van een jaar, zonder voorbeelddata, zoals ze op het jaaroverzicht meetellen. Invested is wat je dat jaar
// kocht, Qualifying het deel dat meetelt voor de kleinschaligheidsinvesteringsaftrek (KIA). Warnings zijn opmerkingen die
// alleen gelden als ze erbij staan; Assumptions en KiaNotes leggen uit hoe we rekenen.
public record InvestmentSummary(
    int Count, decimal Depreciation, decimal BookValue, decimal DisposalLoss, decimal DisposalGain, decimal Invested, decimal Qualifying,
    decimal Kia, bool KiaKnown, string KiaSource, List<string> Assumptions, List<string> KiaNotes, List<string> Warnings);

public record InvestmentYearDto(
    int Year, bool Deduction, List<InvestmentDto> Investments, InvestmentSummary Summary, bool IncludesDemo,
    string DepreciationSource, string CalculationSource, string KiaExclusionsSource);

// Lineair afschrijven: (aanschafprijs min restwaarde) gedeeld door de gebruiksduur, per maand vanaf de maand van aankoop,
// tot en met de maand waarin je het verkoopt of wegdoet, en nooit onder de restwaarde. Bron: TaxYears.DepreciationCalculationSource.
public static class Depreciation
{
    public const int DefaultYears = 5;
    public const int MaxYears = 20;

    public static AssetPlan Plan(Expense e, bool deduction) =>
        new(e.Date, e.CostForProfit(deduction), e.ResidualValue ?? 0, e.UsefulLifeYears ?? DefaultYears, e.DisposedOn, e.DisposalPrice);

    static int Month(DateTime d) => d.Year * 12 + d.Month - 1;

    // Wat er tot en met een maand samen is afgeschreven. We ronden het totaal af, niet elke maand, zodat de jaren samen
    // precies uitkomen op de aanschafprijs min de restwaarde.
    static decimal UpTo(AssetPlan a, int month)
    {
        var last = a.DisposedOn is { } d ? Math.Min(month, Month(d)) : month;
        var months = Math.Clamp(last - Month(a.Purchased) + 1, 0, a.Years * 12);
        return Money.Round(Math.Max(0, a.Cost - a.Residual) * months / (a.Years * 12));
    }

    public static decimal ForYear(AssetPlan a, int year) => UpTo(a, year * 12 + 11) - UpTo(a, year * 12 - 1);

    public static decimal ForMonth(AssetPlan a, int year, int month) => UpTo(a, year * 12 + month - 1) - UpTo(a, year * 12 + month - 2);

    // De boekwaarde op 31 december. Na verkoop staat het niet meer op je balans.
    public static decimal BookValue(AssetPlan a, int year) =>
        a.DisposedOn is { } d && d.Year <= year ? 0 : a.Cost - UpTo(a, year * 12 + 11);

    public static decimal? BookValueAtDisposal(AssetPlan a) => a.DisposedOn is { } d ? a.Cost - UpTo(a, Month(d)) : null;

    // Verkoopprijs min boekwaarde: positief is boekwinst, negatief is de boekwaarde die je afboekt.
    public static decimal? DisposalResult(AssetPlan a) => BookValueAtDisposal(a) is { } book ? (a.DisposalPrice ?? 0) - book : null;

    public static decimal PerYear(AssetPlan a) => Money.Round(Math.Max(0, a.Cost - a.Residual) / a.Years);

    // Meer dan 20% van de aanschafprijs per jaar, wat fiscaal meestal niet mag.
    public static bool AboveMaximum(AssetPlan a) =>
        a.Cost > 0 && (a.Cost - a.Residual) / a.Years > a.Cost * TaxYears.MaxDepreciationPercentage / 100;

    // Elk jaar van de aankoop tot het einde van de afschrijving of de verkoop.
    public static List<DepreciationYear> Schedule(AssetPlan a)
    {
        var last = a.DisposedOn?.Year ?? (Month(a.Purchased) + a.Years * 12 - 1) / 12;
        return Enumerable.Range(a.Purchased.Year, Math.Max(1, last - a.Purchased.Year + 1))
            .Select(y => new DepreciationYear(y, ForYear(a, y), BookValue(a, y))).ToList();
    }

    // Telt mee voor de KIA: minstens € 450 (zonder btw als je die aftrekt) en geen personenauto die niet bedoeld is voor beroepsvervoer.
    public static bool CountsForKia(Expense e, bool deduction) => !e.PassengerCar && e.CostForProfit(deduction) >= TaxYears.DepreciationThreshold;
}

public static class InvestmentEndpoints
{
    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    static string Euro(decimal d) => d.ToString("C", Dutch);

    // De investeringen die in een jaar op je balans staan: gekocht voor of in dat jaar en niet al eerder verkocht. De totalen
    // zijn zonder voorbeelddata, tenzij je die erbij vraagt om te zien hoe het werkt; het jaaroverzicht doet dat nooit.
    // ByMonth is de afschrijving plus afgeboekte boekwaarde per maand, voor het jaaroverzicht.
    public static async Task<(List<InvestmentDto> Rows, InvestmentSummary Summary, decimal[] ByMonth)> YearAsync(AppDb db, int year, bool deduction,
        bool includeDemo = false)
    {
        var start = new DateTime(year, 1, 1);
        var end = new DateTime(year, 12, 31);
        var assets = await db.Expenses.AsNoTracking()
            .Where(e => e.Investment && e.Date <= end && (e.DisposedOn == null || e.DisposedOn >= start))
            .OrderBy(e => e.Date).ThenBy(e => e.Id).ToListAsync();

        var rows = new List<InvestmentDto>();
        var byMonth = new decimal[12];
        decimal depreciation = 0, bookValue = 0, loss = 0, gain = 0, invested = 0, qualifying = 0;
        var aboveMaximum = 0;
        var disposedInYear = false;
        foreach (var e in assets)
        {
            var a = Depreciation.Plan(e, deduction);
            var kia = Depreciation.CountsForKia(e, deduction);
            var result = Depreciation.DisposalResult(a);
            rows.Add(new InvestmentDto(e.Id, e.Date, e.Supplier, e.Description, e.Category, a.Cost, a.Residual, a.Years, e.PassengerCar, kia,
                Depreciation.PerYear(a), Depreciation.AboveMaximum(a), Depreciation.ForYear(a, year), Depreciation.BookValue(a, year),
                e.DisposedOn, e.DisposalPrice, Depreciation.BookValueAtDisposal(a), result, e.IsDemo, Depreciation.Schedule(a)));
            if (e.IsDemo && !includeDemo) continue;

            depreciation += Depreciation.ForYear(a, year);
            bookValue += Depreciation.BookValue(a, year);
            for (var m = 1; m <= 12; m++) byMonth[m - 1] += Depreciation.ForMonth(a, year, m);
            if (e.DisposedOn is { } d && d.Year == year && result is { } r)
            {
                disposedInYear = true;
                if (r < 0) { loss -= r; byMonth[d.Month - 1] -= r; }
                else gain += r;
            }
            if (e.Date.Year == year)
            {
                invested += a.Cost;
                if (kia) qualifying += a.Cost;
            }
            if (Depreciation.AboveMaximum(a)) aboveMaximum++;
        }

        var table = TaxYears.KiaFor(year);
        var count = assets.Count(e => includeDemo || !e.IsDemo);
        var assumptions = new List<string>
        {
            "We schrijven lineair af: de aanschafprijs min de restwaarde, gedeeld door de gebruiksduur. Dat begint in de maand van aankoop, loopt tot en met de maand waarin je het bedrijfsmiddel verkoopt of wegdoet, en komt nooit onder de restwaarde.",
            deduction ? "De aanschafprijs is zonder btw, omdat je de btw aftrekt." : "De aanschafprijs is met btw, omdat je de btw niet aftrekt.",
        };
        var kiaNotes = new List<string>
        {
            $"De kleinschaligheidsinvesteringsaftrek rekenen we over wat je in {year} kocht: per bedrijfsmiddel minstens {Euro(TaxYears.DepreciationThreshold)} "
                + (deduction ? "zonder btw" : "met btw")
                + ". Personenauto's die niet bedoeld zijn voor beroepsvervoer tellen niet mee. Andere uitzonderingen, zoals iets wat je vooral verhuurt of van familie koopt, kent Potel niet.",
            "We tellen een investering mee in het jaar van de aankoopdatum. Betaal je pas in een later jaar, dan kan de aftrek anders uitvallen.",
        };
        if (table is null)
            kiaNotes.Add($"De bedragen voor de kleinschaligheidsinvesteringsaftrek in {year} staan nog niet vast. Daarom telt die aftrek hier nog niet mee.");
        var warnings = new List<string>();
        if (aboveMaximum > 0)
            warnings.Add($"Bij {(aboveMaximum == 1 ? "1 bedrijfsmiddel" : $"{aboveMaximum} bedrijfsmiddelen")} schrijf je meer dan {TaxYears.MaxDepreciationPercentage:0}% van de aanschafprijs per jaar af. "
                + "Dat mag fiscaal meestal niet. Kies een langere gebruiksduur of een hogere restwaarde, of overleg met je boekhouder.");
        if (disposedInYear)
            warnings.Add("Verkoop je een bedrijfsmiddel binnen 5 jaar na het begin van het jaar waarin je het kocht, dan moet je misschien een deel van de investeringsaftrek terugbetalen (desinvesteringsbijtelling). Dat rekenen we niet uit.");

        var summary = new InvestmentSummary(count, depreciation, bookValue, loss, gain, invested, qualifying,
            table is null ? 0 : TaxYears.KiaAmount(table, qualifying), table is not null, table?.Source ?? TaxYears.KiaSource, assumptions, kiaNotes, warnings);
        return (rows, summary, byMonth);
    }

    public static void MapInvestments(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/investments");

        // Iedereen in de werkruimte mag de investeringen bekijken, net als de kosten.
        g.MapGet("/", async (AppDb db, BusinessClock clock, int? year, bool? demo) =>
        {
            var y = year ?? clock.Today.Year;
            if (y is < 2000 or > 2100) return Results.BadRequest(new { error = "Kies een geldig jaar." });
            var settings = await SettingsEndpoints.GetAsync(db);
            var deduction = settings.VatRegime is not (VatRegimes.Kor or VatRegimes.Exempt);
            var (rows, summary, _) = await YearAsync(db, y, deduction, demo == true);
            return Results.Ok(new InvestmentYearDto(y, deduction, rows, summary, demo == true && rows.Any(r => r.Demo),
                TaxYears.DepreciationSource, TaxYears.DepreciationCalculationSource, TaxYears.KiaExclusionsSource));
        });

        // Verkocht of weggedaan. De boekwaarde die dan nog over is boek je af; een hogere verkoopprijs is boekwinst.
        g.MapPut("/{id:int}/disposal", async (AppDb db, int id, DisposalInput input) =>
        {
            var e = await db.Expenses.FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            if (!e.Investment) return Results.BadRequest(new { error = "Deze uitgave is geen investering. Markeer hem eerst als investering bij Kosten." });
            if (input.Date is not { } date || date.Year > 2100) return Results.BadRequest(new { error = "Vul de datum in waarop je het verkocht of wegdeed." });
            if (date.Date < e.Date) return Results.BadRequest(new { error = $"Die datum ligt voor de aankoop op {e.Date.ToString("d MMMM yyyy", Dutch)}." });
            var price = input.Price is { } p ? Money.Round(p) : 0;
            if (price < 0) return Results.BadRequest(new { error = "De verkoopprijs kan niet negatief zijn. Kreeg je niets, laat hem dan leeg." });
            if (price > Money.MaxAmount) return Results.BadRequest(new { error = "Die verkoopprijs is te groot." });

            e.DisposedOn = date.Date;
            e.DisposalPrice = price == 0 ? null : price;
            db.Log("kosten", price > 0 ? $"{e.Supplier} verkocht voor {Euro(price)}" : $"{e.Supplier} weggedaan");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        g.MapDelete("/{id:int}/disposal", async (AppDb db, int id) =>
        {
            var e = await db.Expenses.FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            if (e.DisposedOn is null) return Results.NoContent();
            e.DisposedOn = null;
            e.DisposalPrice = null;
            db.Log("kosten", $"Verkoop van {e.Supplier} ongedaan gemaakt");
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }
}
