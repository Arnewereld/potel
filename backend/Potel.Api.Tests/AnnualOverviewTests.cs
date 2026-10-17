using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Het jaaroverzicht: omzet, kosten per categorie, resultaat, uren, de indicatie van de aftrekposten en de CSV voor de boekhouder.
// De klok staat op 20 mei 2026 (MayInAmsterdamFactory), dus verstuurde facturen vallen in 2026.
public class AnnualOverviewTests(MayInAmsterdamFactory factory) : IClassFixture<MayInAmsterdamFactory>
{
    static Task<JsonElement> AnnualAsync(HttpClient c, int year = 2026, bool starter = false) =>
        c.GetFromJsonAsync<JsonElement>($"/api/tax/annual?year={year}{(starter ? "&starter=true" : "")}");

    static async Task<int> SendAsync(HttpClient c, int customerId, decimal qty, decimal price, decimal rate = 21, string regime = "normaal") =>
        await InvoiceAsync(c, customerId, "verzonden", qty, price, rate, vatRegime: regime, issueDate: "2026-05-20");

    void Edit(int workspaceId, Action<AppDb> change)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        db.Tenant.WorkspaceId = workspaceId;
        change(db);
        db.SaveChanges();
    }

    // Uren rechtstreeks in de database: 123 dagen van 10 uur is 1.230 uur, waarvan een derde niet factureerbaar.
    void AddHours(int workspaceId, int projectId, int days, int year = 2026, bool demo = false) => Edit(workspaceId, db =>
        db.TimeEntries.AddRange(Enumerable.Range(0, days).Select(i => new TimeEntry
        {
            ProjectId = projectId, Date = new DateTime(year, 1, 1).AddDays(i), Minutes = 600, Billable = i % 3 != 0, IsDemo = demo, Description = "Werk",
        })));

    [Fact]
    public async Task Overview_counts_revenue_costs_hours_and_the_indication()
    {
        var c = await ReadyAsync(factory, "jaar");
        var ws = await TestApi.WorkspaceIdAsync(c);
        var customerId = await CustomerAsync(c);
        var projectId = await ProjectAsync(c, customerId, 95);
        await SendAsync(c, customerId, 100, 100);                                   // 10.000
        var credited = await SendAsync(c, customerId, 1.5m, 95);                    // 142,50, helemaal gecrediteerd
        var credit = (await Json(await c.PostAsync($"/api/invoices/{credited}/credit", null))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, credit, "verzonden")).StatusCode);
        await InvoiceAsync(c, customerId, "concept", 1, 5000);                      // concept
        var demo = await SendAsync(c, customerId, 1, 700);                          // voorbeeld
        var lastYear = await SendAsync(c, customerId, 1, 900);                      // vorig jaar
        Edit(ws, db =>
        {
            db.Invoices.Single(i => i.Id == demo).IsDemo = true;
            db.Invoices.Single(i => i.Id == lastYear).IssueDate = new DateTime(2025, 12, 31);
        });

        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 2420m, supplier: "Coolblue", category: "hardware"));                                    // 2.000 zonder btw
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 289m, supplier: "JetBrains", category: "software", reverseCharge: true, supplierRegion: "eu")); // 289
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 31.60m, vatRate: 9, supplier: "NS", category: "reiskosten"));                             // 28,99
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 50m, date: "2025-12-31"));                                                                // vorig jaar
        var demoExpense = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 500m))).GetProperty("id").GetInt32();
        Edit(ws, db => db.Expenses.Single(e => e.Id == demoExpense).IsDemo = true);

        AddHours(ws, projectId, 123);
        AddHours(ws, projectId, 10, demo: true);
        AddHours(ws, projectId, 10, year: 2025);

        var y = await AnnualAsync(c);
        Assert.Equal(10000m, y.GetProperty("revenue").GetDecimal());
        Assert.Equal(2317.99m, y.GetProperty("costs").GetDecimal());
        Assert.Equal(7682.01m, y.GetProperty("result").GetDecimal());
        Assert.True(y.GetProperty("deduction").GetBoolean());
        Assert.Equal(1, y.GetProperty("possibleInvestments").GetInt32());
        var categories = y.GetProperty("costsByCategory").EnumerateArray().ToDictionary(x => x.GetProperty("category").GetString()!, x => x.GetProperty("amount").GetDecimal());
        Assert.Equal(new Dictionary<string, decimal> { ["hardware"] = 2000m, ["software"] = 289m, ["reiskosten"] = 28.99m }, categories);
        Assert.Equal(10000m, y.GetProperty("months")[4].GetProperty("revenue").GetDecimal());
        Assert.Equal(2317.99m, y.GetProperty("months")[4].GetProperty("costs").GetDecimal());

        var hours = y.GetProperty("hours");
        Assert.Equal(123 * 600, hours.GetProperty("minutes").GetInt32());
        Assert.Equal(41 * 600, hours.GetProperty("nonBillableMinutes").GetInt32());
        Assert.Equal(82 * 600, hours.GetProperty("billableMinutes").GetInt32());
        Assert.Equal(1225, hours.GetProperty("criterion").GetInt32());
        Assert.True(hours.GetProperty("met").GetBoolean());

        var ind = y.GetProperty("indication");
        Assert.True(ind.GetProperty("available").GetBoolean());
        Assert.Equal(1200m, ind.GetProperty("selfEmployedDeduction").GetDecimal());
        Assert.Equal(0m, ind.GetProperty("starterDeduction").GetDecimal());
        Assert.Equal(6482.01m, ind.GetProperty("profitAfterDeductions").GetDecimal());
        Assert.Equal(12.7m, ind.GetProperty("smePercentage").GetDecimal());
        Assert.Equal(823.22m, ind.GetProperty("smeExemption").GetDecimal());       // 12,7% van 6.482,01 = 823,215
        Assert.Equal(5658.79m, ind.GetProperty("taxableProfit").GetDecimal());

        var starter = (await AnnualAsync(c, starter: true)).GetProperty("indication");
        Assert.Equal(2123m, starter.GetProperty("starterDeduction").GetDecimal());
        Assert.Equal(4359.01m, starter.GetProperty("profitAfterDeductions").GetDecimal());
        Assert.Equal(553.59m, starter.GetProperty("smeExemption").GetDecimal());

        var previous = await AnnualAsync(c, 2025);
        Assert.Equal(900m, previous.GetProperty("revenue").GetDecimal());
        Assert.Equal(41.32m, previous.GetProperty("costs").GetDecimal());          // 50 met 21% btw
        Assert.Equal(6000, previous.GetProperty("hours").GetProperty("minutes").GetInt32());
        // 100 uur in 2025: geen zelfstandigenaftrek.
        Assert.False(previous.GetProperty("hours").GetProperty("met").GetBoolean());
        Assert.Equal(0m, previous.GetProperty("indication").GetProperty("selfEmployedDeduction").GetDecimal());
    }

    [Fact]
    public void Indication_follows_the_hours_criterion_and_the_profit_limit()
    {
        var y2026 = TaxYears.For(2026);
        var met = new HoursSummary(1225 * 60, 0, 1225 * 60, 1225, true);
        var short_ = new HoursSummary(1224 * 60 + 59, 0, 0, 1225, false);

        // Zonder het urencriterium geen zelfstandigenaftrek en geen startersaftrek; de MKB-winstvrijstelling blijft.
        var noHours = AnnualOverview.Indicate(y2026, 2026, 500m, short_, starter: true);
        Assert.Equal(0m, noHours.SelfEmployedDeduction);
        Assert.Equal(0m, noHours.StarterDeduction);
        Assert.Equal(63.50m, noHours.SmeExemption);
        Assert.Contains(noHours.Notes, n => n.Contains("1.224 van de 1.225 uur"));

        // Niet hoger dan de winst, behalve voor een starter.
        var capped = AnnualOverview.Indicate(y2026, 2026, 800m, met, starter: false);
        Assert.Equal(800m, capped.SelfEmployedDeduction);
        Assert.Equal(0m, capped.ProfitAfterDeductions);
        Assert.Equal(0m, capped.SmeExemption);
        var loss = AnnualOverview.Indicate(y2026, 2026, -100m, met, starter: false);
        Assert.Equal(0m, loss.SelfEmployedDeduction);

        // Een starter mag een verlies krijgen; de MKB-winstvrijstelling maakt dat verlies kleiner.
        var starter = AnnualOverview.Indicate(y2026, 2026, 800m, met, starter: true);
        Assert.Equal(1200m, starter.SelfEmployedDeduction);
        Assert.Equal(2123m, starter.StarterDeduction);
        Assert.Equal(-2523m, starter.ProfitAfterDeductions);
        Assert.Equal(-320.42m, starter.SmeExemption);
        Assert.Equal(-2202.58m, starter.TaxableProfit);
    }

    [Theory]
    [InlineData(2024, 3750, 2123, 13.31)]
    [InlineData(2025, 2470, 2123, 12.7)]
    [InlineData(2026, 1200, 2123, 12.7)]
    public void Year_table_has_the_published_amounts(int year, int selfEmployed, int starter, double sme)
    {
        var t = TaxYears.For(year)!;
        Assert.Equal(selfEmployed, t.SelfEmployedDeduction);
        Assert.Equal(starter, t.StarterDeduction);
        Assert.Equal((decimal)sme, t.SmeProfitExemption);
        Assert.NotEmpty(t.Sources);
        Assert.All(t.Sources, s => Assert.StartsWith("https://", s));
    }

    [Fact]
    public void Years_without_final_amounts_say_so()
    {
        var met = new HoursSummary(1300 * 60, 0, 0, 1225, true);
        var y2027 = AnnualOverview.Indicate(TaxYears.For(2027), 2027, 10000m, met, starter: true);
        Assert.True(y2027.Available);
        Assert.Equal(900m, y2027.SelfEmployedDeduction);
        Assert.Equal(0m, y2027.StarterDeduction);
        Assert.Null(y2027.SmePercentage);
        Assert.Contains(y2027.Notes, n => n.Contains("2027 staan nog niet vast"));

        var unknown = AnnualOverview.Indicate(TaxYears.For(2023), 2023, 10000m, met, starter: false);
        Assert.False(unknown.Available);
        Assert.Equal(0m, unknown.SelfEmployedDeduction);
    }

    [Fact]
    public async Task Kor_costs_include_the_vat_you_cannot_deduct()
    {
        var c = await ReadyAsync(factory, "jaar-kor", vatRegime: "kor");
        await SendAsync(c, await CustomerAsync(c), 10, 100, regime: "kor");
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 121m));
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 23m, supplier: "GitHub", category: "software", reverseCharge: true, supplierRegion: "buiten-eu"));

        var y = await AnnualAsync(c);
        Assert.False(y.GetProperty("deduction").GetBoolean());
        Assert.Equal(1000m, y.GetProperty("revenue").GetDecimal());
        Assert.Equal(121m + 27.83m, y.GetProperty("costs").GetDecimal());
        Assert.Equal(1000m - 148.83m, y.GetProperty("result").GetDecimal());
    }

    [Fact]
    public async Task Csv_is_utf8_with_bom_semicolons_dutch_numbers_and_safe_text()
    {
        var c = await ReadyAsync(factory, "jaar-csv");
        var ws = await TestApi.WorkspaceIdAsync(c);
        var customerId = await CustomerAsync(c, "=HYPERLINK(\"http://x\")");
        var invoice = await SendAsync(c, customerId, 1.5m, 95);
        var credit = (await Json(await c.PostAsync($"/api/invoices/{invoice}/credit", null))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, credit, "verzonden")).StatusCode);
        await SendAsync(c, customerId, 1000, 1.234m);                                // 1.234,00
        var expense = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 121m, supplier: "+31 Telecom; \"zakelijk\"", category: "telefoon"))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await ExpenseKit.UploadAsync(c, expense, ExpenseKit.Pdf, "bon.pdf")).StatusCode);
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 23m, supplier: "@GitHub", category: "software", reverseCharge: true, supplierRegion: "buiten-eu"));
        var demo = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 99m, supplier: "Voorbeeldleverancier"))).GetProperty("id").GetInt32();
        Edit(ws, db => db.Expenses.Single(e => e.Id == demo).IsDemo = true);

        var other = await ReadyAsync(factory, "jaar-csv-ander");
        await SendAsync(other, await CustomerAsync(other, "Geheime Klant"), 1, 100);
        await ExpenseKit.CreateAsync(other, ExpenseKit.Body(supplier: "Geheime leverancier"));

        var res = await c.GetAsync("/api/tax/annual.csv?year=2026");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", res.Content.Headers.ContentType.CharSet);
        Assert.Equal("attachment", res.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("jaaroverzicht-2026.csv", res.Content.Headers.ContentDisposition.FileName?.Trim('"'));

        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
        var lines = text.Split("\r\n");
        Assert.StartsWith("Jaaroverzicht 2026;Mijn Bedrijf", lines[0]);
        Assert.Contains("Omzet zonder btw;1234,00", lines);
        Assert.Contains("Resultaat voor belasting;1111,00", lines);              // 1.234 min 100 (121 met btw) min 23 (verlegd, zonder btw)
        Assert.Contains(lines, l => l.StartsWith("Kosten zonder btw;123,00"));

        // Facturen: de creditnota houdt zijn minteken als getal, de klantnaam kan geen formule worden.
        var credited = lines.Single(l => l.Contains(";-142,50;"));
        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\") B.V.\"", credited);
        Assert.Contains(";-29,93;-172,43;verzonden;", credited);
        Assert.Contains(lines, l => l.Contains(";normaal;1234,00;259,14;1493,14;verzonden;"));

        // Kosten: tekst met puntkomma en aanhalingstekens tussen aanhalingstekens, en een apostrof voor + en @.
        Assert.Contains(lines, l => l.Contains(";\"'+31 Telecom; \"\"zakelijk\"\"\";Aankoop;Telefoon en internet;121,00;21,00;21,00;100,00;nee;;ja"));
        Assert.Contains(lines, l => l.Contains(";'@GitHub;Aankoop;Software en abonnementen;23,00;21,00;4,83;23,00;ja;buiten de EU;nee"));
        Assert.Contains(lines, l => l.StartsWith("12-05-2026;"));

        Assert.DoesNotContain("Voorbeeldleverancier", text);
        Assert.DoesNotContain("Geheim", text);
    }

    [Fact]
    public async Task Overview_and_csv_stay_readable_in_a_read_only_workspace_and_check_the_year()
    {
        var c = await ReadyAsync(factory, "jaar-lezen");
        await TestApi.ExpireTrialAsync(factory, await TestApi.WorkspaceIdAsync(c));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/tax/annual")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/tax/annual.csv")).StatusCode);
        Assert.Equal(2026, (await c.GetFromJsonAsync<JsonElement>("/api/tax/annual")).GetProperty("year").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/tax/annual?year=1999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/tax/annual.csv?year=2101")).StatusCode);
    }
}
