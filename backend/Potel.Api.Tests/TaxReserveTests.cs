using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// De som achter de belastingpot, stap voor stap, met de bedragen van belastingdienst.nl uit TaxYears. De verwachte bedragen
// zijn met de hand nagerekend: elke stap op centen afgerond, halve centen naar boven.
public class IncomeTaxTests
{
    static readonly IncomeTaxYear Y2026 = TaxYears.IncomeTaxFor(2026)!;
    static readonly IncomeTaxYear Y2025 = TaxYears.IncomeTaxFor(2025)!;

    static decimal[] Brackets(IncomeTaxYear t, decimal income) => IncomeTax.Brackets(t, income).Select(b => b.Tax).ToArray();

    [Fact]
    public void Brackets_switch_exactly_at_the_limits()
    {
        Assert.Equal([13900.67m, 0m, 0m], Brackets(Y2026, 38883));          // 35,75% tot en met € 38.883
        Assert.Equal([13900.67m, 0.38m, 0m], Brackets(Y2026, 38884));       // de euro erboven: 37,56%
        Assert.Equal([13900.67m, 14852.35m, 0m], Brackets(Y2026, 78426));
        Assert.Equal([13900.67m, 14852.35m, 0.50m], Brackets(Y2026, 78427)); // daarboven 49,50%
        Assert.Equal([0m, 0m, 0m], Brackets(Y2026, 0));
        Assert.Equal([13769.57m, 0m, 0m], Brackets(Y2025, 38441));          // 2025: 35,82% tot en met € 38.441
        Assert.Equal([13769.57m, 14383.32m, 0.50m], Brackets(Y2025, 76818));
        Assert.Equal(38883m, IncomeTax.Brackets(Y2026, 50000)[1].From);
        Assert.Equal(11117m, IncomeTax.Brackets(Y2026, 50000)[1].Income);
    }

    [Fact]
    public void General_credit_is_full_up_to_the_limit_and_then_phases_out_to_nothing()
    {
        Assert.Equal(3115m, IncomeTax.GeneralCredit(Y2026, 0));
        Assert.Equal(3115m, IncomeTax.GeneralCredit(Y2026, 29736));
        Assert.Equal(3114.94m, IncomeTax.GeneralCredit(Y2026, 29737));    // min 6,398% van het deel boven € 29.736
        Assert.Equal(1818.51m, IncomeTax.GeneralCredit(Y2026, 50000));
        Assert.Equal(0m, IncomeTax.GeneralCredit(Y2026, 78426));
        Assert.Equal(0m, IncomeTax.GeneralCredit(Y2026, 200000));
        Assert.Equal(3068m, IncomeTax.GeneralCredit(Y2025, 28406));
        Assert.Equal(0.19m, IncomeTax.GeneralCredit(Y2025, 76817));
    }

    [Fact]
    public void Labour_credit_follows_each_step_of_the_table()
    {
        Assert.Equal(0m, IncomeTax.LabourCredit(Y2026, 0));
        Assert.Equal(0m, IncomeTax.LabourCredit(Y2026, -500));
        Assert.Equal(995.97m, IncomeTax.LabourCredit(Y2026, 11965));      // 8,324%
        Assert.Equal(996.31m, IncomeTax.LabourCredit(Y2026, 11966));      // € 996 + 31,009%
        Assert.Equal(5300.05m, IncomeTax.LabourCredit(Y2026, 25845));
        Assert.Equal(5300.02m, IncomeTax.LabourCredit(Y2026, 25846));     // € 5.300 + 1,950%
        Assert.Equal(5685.07m, IncomeTax.LabourCredit(Y2026, 45592));
        Assert.Equal(5684.93m, IncomeTax.LabourCredit(Y2026, 45593));     // € 5.685 - 6,510%
        Assert.Equal(0m, IncomeTax.LabourCredit(Y2026, 132920));
        Assert.Equal(0m, IncomeTax.LabourCredit(Y2026, 132921));
        Assert.Equal(980.30m, IncomeTax.LabourCredit(Y2025, 12170));      // 2025: € 980 + 30,030%
        Assert.Equal(5598.96m, IncomeTax.LabourCredit(Y2025, 43071));
        Assert.Equal(0m, IncomeTax.LabourCredit(Y2025, 129079));
    }

    [Fact]
    public void Rate_adjustment_only_counts_the_part_above_the_top_bracket()
    {
        // Inkomen vóór aftrekposten precies op de grens: niets; een euro erboven: 11,94% van die euro.
        Assert.Equal(0m, IncomeTax.RateAdjustment(Y2026, 78426 - 5000, 5000));
        Assert.Equal(0.12m, IncomeTax.RateAdjustment(Y2026, 78427 - 5000, 5000));
        // Ver boven de grens: over alle aftrekposten.
        Assert.Equal(597m, IncomeTax.RateAdjustment(Y2026, 150000, 5000));
        Assert.Equal(0m, IncomeTax.RateAdjustment(Y2026, 150000, 0));
        Assert.Equal(601m, IncomeTax.RateAdjustment(Y2025, 150000, 5000));  // 2025: 12,02%
    }

    [Fact]
    public void Zvw_is_charged_on_taxable_profit_up_to_the_maximum_minus_wage()
    {
        Assert.Equal(79409m, IncomeTax.ZvwBase(Y2026, 100000, 0));
        Assert.Equal(39409m, IncomeTax.ZvwBase(Y2026, 50000, 40000));      // het voorbeeld van de Belastingdienst
        Assert.Equal(25000m, IncomeTax.ZvwBase(Y2026, 25000, 40000));
        Assert.Equal(0m, IncomeTax.ZvwBase(Y2026, 30000, 90000));
        Assert.Equal(0m, IncomeTax.ZvwBase(Y2026, -2000, 0));
        Assert.Equal(75864m, IncomeTax.ZvwBase(Y2025, 100000, 0));
    }

    static IncomeTaxEstimate Estimate(int year, decimal result, bool hours = true, bool starter = false, decimal wage = 0, decimal withheld = 0, decimal kia = 0) =>
        IncomeTax.Estimate(TaxYears.IncomeTaxFor(year)!, TaxYears.For(year)!, result, kia, hours, starter, wage, withheld);

    [Fact]
    public void A_freelancer_with_the_hours_criterion()
    {
        var e = Estimate(2026, 60000);
        Assert.Equal(1200m, e.SelfEmployedDeduction);
        Assert.Equal(7467.60m, e.SmeExemption);                            // 12,7% van 58.800
        Assert.Equal(51332.40m, e.TaxableProfit);
        Assert.Equal(18576.66m, e.TaxBeforeCredits);
        Assert.Equal(0m, e.RateAdjustment);
        Assert.Equal(1733.26m, e.GeneralCredit);
        Assert.Equal(60000m, e.LabourIncome);                              // vóór ondernemersaftrek en MKB-winstvrijstelling
        Assert.Equal(4747.04m, e.LabourCredit);
        Assert.Equal(12096.36m, e.IncomeTax);
        Assert.Equal(2489.62m, e.Zvw);
        Assert.Equal(14585.98m, e.Total);
        Assert.Equal(14585.98m, e.Due);

        var y2025 = Estimate(2025, 60000);
        Assert.Equal(2470m, y2025.SelfEmployedDeduction);
        Assert.Equal(50223.69m, y2025.TaxableProfit);
        Assert.Equal(14645.16m, y2025.Total);
    }

    [Fact]
    public void Without_the_hours_criterion_there_is_no_self_employed_deduction()
    {
        var e = Estimate(2026, 60000, hours: false, starter: true);
        Assert.Equal(0m, e.SelfEmployedDeduction);
        Assert.Equal(0m, e.StarterDeduction);
        Assert.Equal(7620m, e.SmeExemption);
        Assert.Equal(52380m, e.TaxableProfit);
        Assert.Equal(12556.86m, e.IncomeTax);
        Assert.Equal(2540.43m, e.Zvw);
        Assert.Equal(15097.29m, e.Total);
    }

    [Fact]
    public void Wage_from_employment_is_added_and_its_withheld_tax_is_deducted()
    {
        var e = Estimate(2026, 30000, wage: 40000, withheld: 9000);
        Assert.Equal(25142.40m, e.TaxableProfit);
        Assert.Equal(65142.40m, e.TaxableIncome);
        Assert.Equal(849.70m, e.GeneralCredit);
        Assert.Equal(70000m, e.LabourIncome);
        Assert.Equal(4096.04m, e.LabourCredit);
        Assert.Equal(18817.96m, e.IncomeTax);
        Assert.Equal(25142.40m, e.ZvwBase);
        Assert.Equal(1219.41m, e.Zvw);
        Assert.Equal(20037.37m, e.Total);
        Assert.Equal(11037.37m, e.Due);

        // Met meer loon: in de hoogste schijf, met tariefsaanpassing, en de Zvw alleen tot het maximum min het loon.
        var high = Estimate(2026, 30000, wage: 60000, withheld: 15000);
        Assert.Equal([13900.67m, 14852.35m, 3324.62m], high.Brackets.Select(b => b.Tax).ToArray());
        Assert.Equal(580.00m, high.RateAdjustment);
        Assert.Equal(0m, high.GeneralCredit);
        Assert.Equal(19409m, high.ZvwBase);
        Assert.Equal(941.34m, high.Zvw);
        Assert.Equal(15804.94m, high.Due);
    }

    [Fact]
    public void High_profit_gets_the_rate_adjustment_and_the_zvw_maximum()
    {
        var e = Estimate(2026, 100000);
        Assert.Equal(86252.40m, e.TaxableProfit);
        Assert.Equal(1641.46m, e.RateAdjustment);                          // 11,94% van 13.747,60 aan aftrekposten
        Assert.Equal(2143.04m, e.LabourCredit);
        Assert.Equal(3851.34m, e.Zvw);                                     // 4,85% van € 79.409
        Assert.Equal(35976.85m, e.Total);

        // Een starter met investeringsaftrek: de tariefsaanpassing alleen over het deel boven de grens.
        var starter = Estimate(2026, 85000, starter: true, kia: 3000);
        Assert.Equal(82000m, starter.Profit);
        Assert.Equal(2123m, starter.StarterDeduction);
        Assert.Equal(68685.02m, starter.TaxableProfit);
        Assert.Equal(426.74m, starter.RateAdjustment);                     // 11,94% van 3.574
        Assert.Equal(24914.39m, starter.Total);
    }

    [Fact]
    public void Credits_are_never_more_than_the_tax_and_a_loss_lowers_the_wage()
    {
        var low = Estimate(2026, 5000);
        Assert.Equal(1185.97m, low.TaxBeforeCredits);
        Assert.Equal(3531.20m, low.GeneralCredit + low.LabourCredit);
        Assert.Equal(1185.97m, low.Credits);
        Assert.Equal(0m, low.IncomeTax);
        Assert.Equal(160.89m, low.Total);

        // Een starter met verlies: dat verlies gaat van het loon af, en over verlies is er geen bijdrage Zvw.
        var loss = Estimate(2026, 800, starter: true, wage: 30000, withheld: 5000);
        Assert.Equal(-2202.58m, loss.TaxableProfit);
        Assert.Equal(27797.42m, loss.TaxableIncome);
        Assert.Equal(0m, loss.Zvw);
        Assert.Equal(1425.96m, loss.Total);
        Assert.Equal(-3574.04m, loss.Due);                                 // geld terug
    }

    [Fact]
    public void Years_that_are_not_published_have_no_figures()
    {
        Assert.Null(TaxYears.IncomeTaxFor(2027));
        Assert.Null(TaxYears.IncomeTaxFor(2024));
        Assert.All(TaxYears.IncomeTax, t =>
        {
            Assert.StartsWith("https://www.belastingdienst.nl/", t.BracketsSource);
            Assert.StartsWith("https://www.belastingdienst.nl/", t.GeneralCreditSource);
            Assert.StartsWith("https://www.belastingdienst.nl/", t.LabourCreditSource);
            Assert.StartsWith("https://www.belastingdienst.nl/", t.RateAdjustmentSource);
            Assert.StartsWith("https://www.belastingdienst.nl/", t.ZvwSource);
            Assert.Contains(t.Year.ToString(), t.GeneralCreditSource);
            Assert.Contains(t.Year.ToString(), t.LabourCreditSource);
            Assert.Null(t.Brackets[^1].UpTo);
            Assert.Null(t.LabourCredit[^1].UpTo);
        });
    }
}

// De klok staat op dinsdag 13 oktober 2026: de aangifte over Q3 kan nog tot en met 31 oktober, Q4 loopt.
public class TaxReserveFactory : PortalFactory
{
    public static readonly DateTime Start = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 13, 10, 0, 0), BusinessClock.Zone);
    public MovableClock Clock { get; } = new(Start);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<BusinessClock>(Clock));
    }
}

// De belastingpot: btw die nog betaald moet worden plus een schatting van de inkomstenbelasting en de Zvw, je instellingen,
// rollen, andere werkruimtes en alleen-lezen.
public class TaxReserveTests(TaxReserveFactory factory) : IClassFixture<TaxReserveFactory>
{
    // 13 oktober is dag 286 van 2026.
    const int Day = 286;

    static Task<JsonElement> ReserveAsync(HttpClient c, int? year = null) =>
        c.GetFromJsonAsync<JsonElement>($"/api/tax/reserve{(year is { } y ? $"?year={y}" : "")}");

    static object Settings(decimal wage = 0, decimal withheld = 0, bool starter = false, string provisional = "geen", decimal provisionalAmount = 0,
        int provisionalMonths = 12, string mode = "berekend", decimal percentage = 0) =>
        new { wage, wageTaxWithheld = withheld, starter, provisional, provisionalAmount, provisionalMonths, mode, percentage };

    static decimal Dec(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

    void AddHours(int ws, int projectId, int hours) => WorkflowKit.InWorkspace(factory, ws, db =>
    {
        db.TimeEntries.AddRange(Enumerable.Range(0, (hours + 9) / 10).Select(i => new TimeEntry
        {
            ProjectId = projectId, Date = new DateTime(2026, 1, 1).AddDays(i), Minutes = Math.Min(10, hours - i * 10) * 60, Billable = true, Description = "Werk",
        }));
        return db.SaveChanges();
    });

    // Versturen zet de factuurdatum op vandaag, dus de klok gaat even naar die dag.
    async Task<int> SentOnAsync(HttpClient c, int customer, decimal quantity, decimal unitPrice, string day)
    {
        factory.Clock.Value = TimeZoneInfo.ConvertTimeToUtc(DateTime.Parse(day).AddHours(10), BusinessClock.Zone);
        try { return await InvoiceAsync(c, customer, "verzonden", quantity, unitPrice, issueDate: day); }
        finally { factory.Clock.Value = TaxReserveFactory.Start; }
    }

    async Task<(HttpClient C, int Ws, int Customer, int Project)> WorkspaceAsync(string prefix)
    {
        factory.Clock.Value = TaxReserveFactory.Start;
        var c = await ReadyAsync(factory, prefix);
        var ws = await TestApi.WorkspaceIdAsync(c);
        var customer = await CustomerAsync(c);
        return (c, ws, customer, await ProjectAsync(c, customer, 95));
    }

    static IncomeTaxEstimate Expected(decimal result, bool hours = true, bool starter = false, decimal wage = 0, decimal withheld = 0) =>
        IncomeTax.Estimate(TaxYears.IncomeTaxFor(2026)!, TaxYears.For(2026)!, result, 0, hours, starter, wage, withheld);

    [Fact]
    public async Task Pot_holds_vat_still_to_pay_and_the_estimate_for_the_whole_year()
    {
        var (c, ws, customer, project) = await WorkspaceAsync("pot");
        await SentOnAsync(c, customer, 100, 100, "2026-08-14");     // Q3: 10.000 met 2.100 btw
        await SentOnAsync(c, customer, 20, 100, "2026-10-05");      // Q4: 2.000 met 420 btw
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 121m, date: "2026-10-06"));  // 21 btw terug in Q4
        AddHours(ws, project, 1000);                                                         // doorgerekend 1.276 uur

        var pot = await ReserveAsync(c);
        Assert.Equal(2026, pot.GetProperty("year").GetInt32());
        Assert.True(pot.GetProperty("current").GetBoolean());
        Assert.Equal(3, pot.GetProperty("monthsLeft").GetInt32());                           // oktober, november en december
        var vat = pot.GetProperty("vat").EnumerateArray().ToList();
        Assert.Equal(2, vat.Count);
        Assert.Equal("Q3 2026", vat[0].GetProperty("label").GetString());
        Assert.Equal(2100m, Dec(vat[0], "amount"));
        Assert.False(vat[0].GetProperty("running").GetBoolean());
        Assert.Equal("Q4 2026", vat[1].GetProperty("label").GetString());
        Assert.Equal(399m, Dec(vat[1], "amount"));
        Assert.True(vat[1].GetProperty("running").GetBoolean());
        Assert.Equal(2499m, Dec(pot, "vatTotal"));

        // Resultaat 12.000 - 100 tot nu toe, doorgerekend naar 365 dagen.
        Assert.Equal(11900m, Dec(pot, "resultSoFar"));
        var full = Money.Round(11900m * 365 / Day);
        Assert.Equal(full, Dec(pot, "resultFullYear"));
        Assert.Equal(1276m, Dec(pot, "expectedHours"));
        Assert.True(pot.GetProperty("hoursMet").GetBoolean());
        Assert.False(pot.GetProperty("hours").GetProperty("met").GetBoolean());
        var expected = Expected(full);
        var fullYear = pot.GetProperty("fullYear");
        Assert.Equal(1200m, Dec(fullYear, "selfEmployedDeduction"));
        Assert.Equal(expected.Total, Dec(fullYear, "total"));
        Assert.Equal(expected.Total, Dec(pot, "total"));
        Assert.Equal(Expected(11900m).Total, Dec(pot, "soFarAmount"));
        Assert.Equal(0m, Dec(pot, "provisional"));
        Assert.Equal(expected.Total, Dec(pot, "remaining"));
        Assert.Equal(Money.Round(expected.Total / 3), Dec(pot, "perMonth"));
        Assert.Contains(pot.GetProperty("explanation").EnumerateArray(), e => e.GetString()!.Contains("verwachten dat je het urencriterium haalt"));
        Assert.Contains(pot.GetProperty("leftOut").EnumerateArray(), e => e.GetString()!.Contains("box 3"));
        Assert.Contains(pot.GetProperty("sources").EnumerateArray(), s => s.GetProperty("url").GetString()!.Contains("tabel-arbeidskorting-2026"));

        // Het dashboard laat hetzelfde zien.
        var dash = (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("taxReserve");
        Assert.Equal(2499m, Dec(dash, "vat"));
        Assert.Equal(expected.Total, Dec(dash, "remaining"));
        Assert.Equal(Money.Round(expected.Total / 3), Dec(dash, "perMonth"));

        // De aangifte over Q3 gedaan: die telt niet meer mee. Het lopende kwartaal kun je nog niet als gedaan markeren.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/tax/vat-return/filed", new { year = 2026, quarter = 4, filed = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/tax/vat-return/filed", new { year = 2026, quarter = 5, filed = true })).StatusCode);
        var filed = await c.PutAsJsonAsync("/api/tax/vat-return/filed", new { year = 2026, quarter = 3, filed = true });
        Assert.Equal(HttpStatusCode.OK, filed.StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await filed.JsonAsync()).GetProperty("filedAt").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync("/api/tax/vat-return/filed", new { year = 2026, quarter = 3, filed = true })).StatusCode);
        Assert.Equal(1, WorkflowKit.InWorkspace(factory, ws, db => db.VatFilings.Count()));
        pot = await ReserveAsync(c);
        Assert.Equal("Q4 2026", Assert.Single(pot.GetProperty("vat").EnumerateArray()).GetProperty("label").GetString());
        Assert.Equal(399m, Dec(pot, "vatTotal"));
        var q4 = await c.GetFromJsonAsync<JsonElement>("/api/tax/vat-return?year=2026&quarter=4");
        Assert.True(q4.GetProperty("pending").GetProperty("filed").GetBoolean());
        var q3 = await c.GetFromJsonAsync<JsonElement>("/api/tax/vat-return?year=2026&quarter=3");
        Assert.NotEqual(JsonValueKind.Null, q3.GetProperty("filedAt").ValueKind);
        (await c.PutAsJsonAsync("/api/tax/vat-return/filed", new { year = 2026, quarter = 3, filed = false })).EnsureSuccessStatusCode();
        Assert.Equal(2499m, Dec(await ReserveAsync(c), "vatTotal"));

        // Na 31 oktober is de aangifte over Q3 niet meer op tijd en tellen we hem niet meer; in december is Q4 het enige kwartaal.
        factory.Clock.SetDay(2026, 11, 1);
        Assert.Equal("Q4 2026", Assert.Single((await ReserveAsync(c)).GetProperty("vat").EnumerateArray()).GetProperty("label").GetString());
        Assert.Equal(2, (await ReserveAsync(c)).GetProperty("monthsLeft").GetInt32());
        factory.Clock.Value = TaxReserveFactory.Start;

        // Een jaar dat voorbij is: geen btw, niet doorgerekend, geen bedrag per maand. Een jaar dat nog moet komen: niets.
        var last = await ReserveAsync(c, 2025);
        Assert.False(last.GetProperty("current").GetBoolean());
        Assert.Empty(last.GetProperty("vat").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("perMonth").ValueKind);
        Assert.Equal(JsonValueKind.Null, last.GetProperty("soFar").ValueKind);
        Assert.Equal(1m, Dec(last, "elapsed"));
        var next = await ReserveAsync(c, 2027);
        Assert.False(next.GetProperty("available").GetBoolean());
        Assert.Contains("nog niet begonnen", next.GetProperty("missing").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/tax/reserve?year=1999")).StatusCode);
    }

    [Fact]
    public async Task Wage_starter_and_provisional_assessment_change_the_pot()
    {
        var (c, ws, customer, project) = await WorkspaceAsync("pot-loon");
        await SentOnAsync(c, customer, 200, 100, "2026-06-10");     // 20.000
        AddHours(ws, project, 1300);

        var empty = await c.GetFromJsonAsync<JsonElement>("/api/tax/reserve/settings");
        Assert.False(empty.GetProperty("saved").GetBoolean());
        Assert.Equal("berekend", empty.GetProperty("mode").GetString());

        var saved = await c.PutAsJsonAsync("/api/tax/reserve/settings?year=2026",
            Settings(wage: 40000, withheld: 9000, starter: true, provisional: "per-maand", provisionalAmount: 500, provisionalMonths: 10));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var s = await saved.JsonAsync();
        Assert.True(s.GetProperty("saved").GetBoolean());
        Assert.Equal(5000m, Dec(s, "provisionalTotal"));

        var pot = await ReserveAsync(c);
        var full = Money.Round(20000m * 365 / Day);
        var expected = Expected(full, starter: true, wage: 40000, withheld: 9000);
        var fullYear = pot.GetProperty("fullYear");
        Assert.Equal(40000m, Dec(fullYear, "wage"));
        Assert.Equal(2123m, Dec(fullYear, "starterDeduction"));
        Assert.Equal(9000m, Dec(fullYear, "wageTaxWithheld"));
        Assert.Equal(expected.Due, Dec(fullYear, "due"));
        Assert.Equal(expected.Due, Dec(pot, "total"));
        Assert.Equal(5000m, Dec(pot, "provisional"));
        Assert.Equal(expected.Due - 5000, Dec(pot, "remaining"));
        Assert.Equal(Money.Round((expected.Due - 5000) / 3), Dec(pot, "perMonth"));
        // Tot nu toe: loon en loonheffing naar rato van de dagen.
        Assert.Equal(Money.Round(40000m * Day / 365), Dec(pot.GetProperty("soFar"), "wage"));
        Assert.Equal(Money.Round(9000m * Day / 365), Dec(pot.GetProperty("soFar"), "wageTaxWithheld"));

        // Het jaaroverzicht neemt de startersaftrek over, tenzij je hem daar zelf uitzet.
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/tax/annual?year=2026")).GetProperty("indication").GetProperty("starter").GetBoolean());
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/tax/annual?year=2026&starter=false")).GetProperty("indication").GetProperty("starter").GetBoolean());
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/tax/annual?year=2025")).GetProperty("indication").GetProperty("starter").GetBoolean());

        // Een voorlopige aanslag die hoger is dan de schatting: niets meer opzij te zetten.
        (await c.PutAsJsonAsync("/api/tax/reserve/settings?year=2026", Settings(provisional: "totaal", provisionalAmount: 50000))).EnsureSuccessStatusCode();
        pot = await ReserveAsync(c);
        Assert.Equal(50000m, Dec(pot, "provisional"));
        Assert.Equal(0m, Dec(pot, "remaining"));
        Assert.Equal(0m, Dec(pot, "perMonth"));
        Assert.Contains(pot.GetProperty("explanation").EnumerateArray(), e => e.GetString()!.Contains("hoger dan deze schatting"));

        // Wat niet kan.
        async Task<string> Refused(object body)
        {
            var res = await c.PutAsJsonAsync("/api/tax/reserve/settings?year=2026", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return await res.ErrorAsync();
        }
        Assert.Contains("negatief", await Refused(Settings(wage: -1)));
        Assert.Contains("niet hoger zijn dan je loon", await Refused(Settings(wage: 1000, withheld: 1001)));
        Assert.Contains("1 tot en met 12", await Refused(Settings(provisional: "per-maand", provisionalAmount: 100, provisionalMonths: 13)));
        Assert.Contains("voorlopige aanslag", await Refused(Settings(provisional: "soms")));
        Assert.Contains("uitrekent", await Refused(Settings(mode: "gokken")));
        Assert.Contains("welk percentage", await Refused(Settings(mode: "percentage")));
        Assert.Contains("hoger zijn dan 100", await Refused(Settings(mode: "percentage", percentage: 101)));
        Assert.Contains("te groot", await Refused(Settings(wage: 2_000_000_000)));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/tax/reserve/settings?year=3000", Settings())).StatusCode);
        Assert.Equal(1, WorkflowKit.InWorkspace(factory, ws, db => db.TaxReserveYears.Count()));
    }

    [Fact]
    public async Task Too_few_hours_means_no_self_employed_deduction_in_the_pot()
    {
        var (c, ws, customer, project) = await WorkspaceAsync("pot-uren");
        await SentOnAsync(c, customer, 100, 100, "2026-03-10");
        AddHours(ws, project, 400);                                                          // doorgerekend 510 uur

        var pot = await ReserveAsync(c);
        Assert.False(pot.GetProperty("hoursMet").GetBoolean());
        Assert.Equal(510m, Dec(pot, "expectedHours"));
        Assert.Equal(0m, Dec(pot.GetProperty("fullYear"), "selfEmployedDeduction"));
        Assert.Equal(Expected(Money.Round(10000m * 365 / Day), hours: false).Total, Dec(pot, "total"));
        Assert.Contains(pot.GetProperty("explanation").EnumerateArray(), e => e.GetString()!.Contains("minder dan 1.225"));
    }

    [Fact]
    public async Task A_fixed_percentage_reserves_a_share_of_what_was_paid()
    {
        var (c, ws, customer, _) = await WorkspaceAsync("pot-procent");
        var paid = await SentOnAsync(c, customer, 10, 100, "2026-09-01");     // 1.000 + 210 btw
        (await c.PostAsJsonAsync($"/api/invoices/{paid}/payments", new { amount = 1210m, date = "2026-09-10" })).EnsureSuccessStatusCode();
        var part = await SentOnAsync(c, customer, 10, 100, "2026-09-15");
        (await c.PostAsJsonAsync($"/api/invoices/{part}/payments", new { amount = 605m, date = "2026-10-01" })).EnsureSuccessStatusCode(); // de helft
        var demo = await SentOnAsync(c, customer, 10, 100, "2026-09-15");
        (await c.PostAsJsonAsync($"/api/invoices/{demo}/payments", new { amount = 1210m, date = "2026-10-01" })).EnsureSuccessStatusCode();
        WorkflowKit.InWorkspace(factory, ws, db => db.Invoices.Where(i => i.Id == demo).ExecuteUpdate(x => x.SetProperty(i => i.IsDemo, true).SetProperty(i => i.Number, "VOORBEELD-0001")));
        await SentOnAsync(c, customer, 10, 100, "2026-09-20");                 // niet betaald

        (await c.PutAsJsonAsync("/api/tax/reserve/settings", Settings(mode: "percentage", percentage: 30, wage: 50000, withheld: 15000))).EnsureSuccessStatusCode();
        var pot = await ReserveAsync(c);
        Assert.Equal("percentage", pot.GetProperty("mode").GetString());
        Assert.Equal(1500m, Dec(pot, "paidSoFar"));
        Assert.Equal(450m, Dec(pot, "soFarAmount"));
        var paidFullYear = Money.Round(1500m * 365 / Day);
        Assert.Equal(paidFullYear, Dec(pot, "paidFullYear"));
        Assert.Equal(Money.Round(paidFullYear * 0.3m), Dec(pot, "total"));
        Assert.Equal(JsonValueKind.Null, pot.GetProperty("fullYear").ValueKind);
        Assert.Contains(pot.GetProperty("explanation").EnumerateArray(), e => e.GetString()!.Contains("30% van wat er op je facturen betaald is"));

        // Een nieuw jaar zonder eigen instellingen neemt de manier van reserveren over, en kan zonder gepubliceerde bedragen.
        factory.Clock.SetDay(2027, 1, 15);
        var settings2027 = await c.GetFromJsonAsync<JsonElement>("/api/tax/reserve/settings");
        Assert.Equal(2027, settings2027.GetProperty("year").GetInt32());
        Assert.False(settings2027.GetProperty("saved").GetBoolean());
        Assert.Equal("percentage", settings2027.GetProperty("mode").GetString());
        Assert.Equal(0m, Dec(settings2027, "wage"));
        var pot2027 = await ReserveAsync(c);
        Assert.True(pot2027.GetProperty("available").GetBoolean());
        Assert.Equal("Q4 2026", pot2027.GetProperty("vat")[0].GetProperty("label").GetString());

        // Berekend in 2027: de bedragen staan nog niet op belastingdienst.nl, dus geen schatting.
        (await c.PutAsJsonAsync("/api/tax/reserve/settings", Settings())).EnsureSuccessStatusCode();
        pot2027 = await ReserveAsync(c);
        Assert.False(pot2027.GetProperty("available").GetBoolean());
        Assert.Contains("nog niet op belastingdienst.nl", pot2027.GetProperty("missing").GetString());
        Assert.Equal(0m, Dec(pot2027, "total"));
        // 2024 zit niet in Potel.
        Assert.Contains("zitten er niet in", (await ReserveAsync(c, 2024)).GetProperty("missing").GetString());
        factory.Clock.Value = TaxReserveFactory.Start;
    }

    [Fact]
    public async Task Only_admins_see_the_pot_and_each_workspace_has_its_own()
    {
        var (admin, ws, customer, _) = await WorkspaceAsync("pot-rollen");
        await SentOnAsync(admin, customer, 10, 100, "2026-08-01");
        (await admin.PutAsJsonAsync("/api/tax/reserve/settings", Settings(wage: 30000, withheld: 6000))).EnsureSuccessStatusCode();
        var employeeEmail = $"pot-medewerker-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(admin, employeeEmail);
        var employee = await TestApi.LoginAsync(factory, employeeEmail);

        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync("/api/tax/reserve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync("/api/tax/reserve/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PutAsJsonAsync("/api/tax/reserve/settings", Settings())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PutAsJsonAsync("/api/tax/vat-return/filed", new { year = 2026, quarter = 3, filed = true })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await employee.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("taxReserve").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, (await admin.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("taxReserve").ValueKind);
        // Het jaaroverzicht zelf blijft voor iedereen, net als de btw-aangifte.
        Assert.Equal(HttpStatusCode.OK, (await employee.GetAsync("/api/tax/annual")).StatusCode);

        // Een andere werkruimte ziet niets van deze instellingen en aangiftes.
        (await admin.PutAsJsonAsync("/api/tax/vat-return/filed", new { year = 2026, quarter = 3, filed = true })).EnsureSuccessStatusCode();
        var (other, otherWs, otherCustomer, _) = await WorkspaceAsync("pot-buur");
        await SentOnAsync(other, otherCustomer, 10, 100, "2026-08-01");
        var otherSettings = await other.GetFromJsonAsync<JsonElement>("/api/tax/reserve/settings");
        Assert.False(otherSettings.GetProperty("saved").GetBoolean());
        Assert.Equal(0m, Dec(otherSettings, "wage"));
        var otherPot = await ReserveAsync(other);
        Assert.Equal(210m, Dec(otherPot, "vatTotal"));                                       // Q3 telt hier nog wel
        Assert.Equal(0m, Dec(otherPot.GetProperty("fullYear"), "wage"));
        Assert.Equal(1, WorkflowKit.InWorkspace(factory, ws, db => db.VatFilings.Count() + db.TaxReserveYears.Count() - 1));
        Assert.Equal(0, WorkflowKit.InWorkspace(factory, otherWs, db => db.VatFilings.Count() + db.TaxReserveYears.Count()));

        // In de export, en weg met de werkruimte.
        var export = JsonDocument.Parse(await (await admin.GetAsync("/api/workspace/export")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(30000m, Assert.Single(export.GetProperty("taxReserveYears").EnumerateArray()).GetProperty("wage").GetDecimal());
        Assert.Equal(3, Assert.Single(export.GetProperty("vatFilings").EnumerateArray()).GetProperty("quarter").GetInt32());

        // Alleen-lezen: bekijken mag, wijzigen niet.
        await TestApi.ExpireTrialAsync(factory, ws);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/tax/reserve")).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.PutAsJsonAsync("/api/tax/reserve/settings", Settings())).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.PutAsJsonAsync("/api/tax/vat-return/filed", new { year = 2026, quarter = 3, filed = false })).StatusCode);

        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "geheim123" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(delete)).StatusCode);
        Assert.Equal(0, WorkflowKit.InWorkspace(factory, ws, db => db.VatFilings.Count() + db.TaxReserveYears.Count()));
    }
}

// De migratie voor de belastingpot voegt alleen tabellen toe: wat er al stond blijft staan.
public class TaxReserveMigrationTests : IDisposable
{
    const string Before = "20261013214823_Investeringen";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-migratie-belastingpot-{Guid.NewGuid():N}.db");

    AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path}").Options, new Tenant { WorkspaceId = 1 });

    [Fact]
    public void Existing_data_survives_and_the_new_tables_start_empty()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        using (var con = new SqliteConnection($"Data Source={path}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 'Oud bedrijf', 'team');
                INSERT INTO Expenses (Id, WorkspaceId, IsDemo, Date, Supplier, Category, Amount, VatRate, VatAmount, ReverseCharge, CreatedAt, Investment, PassengerCar)
                    VALUES (1, 1, 0, '2026-02-12 00:00:00', 'Coolblue', 'hardware', '2899.0', '21.0', '503.13', 0, '2026-02-12 00:00:00', 0, 0);
                """;
            cmd.ExecuteNonQuery();
        }

        using (var db = Db())
        {
            db.GetService<IMigrator>().Migrate();
            Assert.Equal("Coolblue", db.Expenses.Single().Supplier);
            Assert.Equal(2899m, db.Expenses.Single().Amount);
            Assert.Empty(db.TaxReserveYears);
            Assert.Empty(db.VatFilings);
            db.TaxReserveYears.Add(new TaxReserveYear { Year = 2026, Wage = 1000 });
            db.SaveChanges();
            Assert.Equal(1, db.TaxReserveYears.Single().WorkspaceId);
        }
    }

    public void Dispose()
    {
        TestDatabase.Delete(path);
    }
}
