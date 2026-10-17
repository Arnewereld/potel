using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Afschrijven en de kleinschaligheidsinvesteringsaftrek als losse sommen, met de voorbeelden van de Belastingdienst.
public class DepreciationTests
{
    static AssetPlan Plan(string purchased, decimal cost, decimal residual, int years, string? disposed = null, decimal? price = null) =>
        new(DateTime.Parse(purchased, CultureInfo.InvariantCulture), cost, residual, years,
            disposed is null ? null : DateTime.Parse(disposed, CultureInfo.InvariantCulture), price);

    [Fact]
    public void Depreciation_starts_in_the_purchase_month_and_never_goes_below_the_residual_value()
    {
        // Het voorbeeld van de Belastingdienst: (30.000 - 5.000) / 10 = 2.500 per jaar; gekocht op 1 oktober is dat 3/12 deel.
        var machine = Plan("2026-10-01", 30000, 5000, 10);
        Assert.Equal(625m, Depreciation.ForYear(machine, 2026));
        Assert.Equal(2500m, Depreciation.ForYear(machine, 2027));
        Assert.Equal(1875m, Depreciation.ForYear(machine, 2036));                  // januari tot en met september
        Assert.Equal(0m, Depreciation.ForYear(machine, 2037));
        Assert.Equal(0m, Depreciation.ForYear(machine, 2025));
        Assert.Equal(25000m, Enumerable.Range(2026, 15).Sum(y => Depreciation.ForYear(machine, y)));
        Assert.Equal(5000m, Depreciation.BookValue(machine, 2036));
        Assert.Equal(5000m, Depreciation.BookValue(machine, 2040));
        Assert.Equal(2500m, Depreciation.PerYear(machine));
        var schedule = Depreciation.Schedule(machine);
        Assert.Equal(2026, schedule[0].Year);
        Assert.Equal(2036, schedule[^1].Year);
        Assert.Equal(29375m, schedule[0].BookValue);

        // Afgerond op het totaal: de jaren komen samen precies op de aanschafprijs.
        var laptop = Plan("2026-02-15", 1000, 0, 3);
        Assert.Equal(new[] { 305.56m, 333.33m, 333.33m, 27.78m }, Enumerable.Range(2026, 4).Select(y => Depreciation.ForYear(laptop, y)));
        Assert.Equal(0m, Depreciation.BookValue(laptop, 2029));
        Assert.Equal(new[] { 27.78m, 27.78m, 27.77m }, Enumerable.Range(2, 3).Select(m => Depreciation.ForMonth(laptop, 2026, m)));
        Assert.Equal(305.56m, Enumerable.Range(1, 12).Sum(m => Depreciation.ForMonth(laptop, 2026, m)));
    }

    [Fact]
    public void Selling_writes_off_the_book_value_and_a_higher_price_is_a_book_profit()
    {
        // Na 8 jaar is de boekwaarde 10.000: voor 11.500 verkocht is 1.500 winst, voor 7.500 is 2.500 verlies.
        Assert.Equal(1500m, Depreciation.DisposalResult(Plan("2016-01-01", 30000, 5000, 10, "2023-12-31", 11500)));
        Assert.Equal(-2500m, Depreciation.DisposalResult(Plan("2016-01-01", 30000, 5000, 10, "2023-12-31", 7500)));
        // Na de gebruiksduur blijft de restwaarde staan: voor 7.000 is 2.000 winst, voor 3.500 is 1.500 verlies.
        Assert.Equal(2000m, Depreciation.DisposalResult(Plan("2016-01-01", 30000, 5000, 10, "2028-06-30", 7000)));
        Assert.Equal(-1500m, Depreciation.DisposalResult(Plan("2016-01-01", 30000, 5000, 10, "2028-06-30", 3500)));

        // De afschrijving loopt tot en met de maand van verkoop en stopt daarna; daarna is er geen boekwaarde meer.
        var sold = Plan("2016-01-01", 30000, 5000, 10, "2023-06-15", null);
        Assert.Equal(1250m, Depreciation.ForYear(sold, 2023));
        Assert.Equal(0m, Depreciation.ForYear(sold, 2024));
        Assert.Equal(11250m, Depreciation.BookValueAtDisposal(sold));
        Assert.Equal(-11250m, Depreciation.DisposalResult(sold));                 // weggedaan zonder iets te krijgen
        Assert.Equal(0m, Depreciation.BookValue(sold, 2023));
        Assert.Equal(12500m, Depreciation.BookValue(sold, 2022));
        Assert.Equal(2023, Depreciation.Schedule(sold)[^1].Year);
        Assert.Null(Depreciation.DisposalResult(Plan("2016-01-01", 30000, 5000, 10)));
    }

    [Fact]
    public void More_than_twenty_percent_a_year_is_flagged()
    {
        Assert.True(Depreciation.AboveMaximum(Plan("2026-01-01", 1500, 0, 3)));
        Assert.False(Depreciation.AboveMaximum(Plan("2026-01-01", 1500, 0, 5)));
        Assert.False(Depreciation.AboveMaximum(Plan("2026-01-01", 1500, 600, 3)));  // 300 per jaar is precies 20%
    }

    [Theory]
    [InlineData(2026, "2900", "0")]
    [InlineData(2026, "2900.01", "812.00")]
    [InlineData(2026, "2901", "812.28")]
    [InlineData(2026, "71683", "20071.24")]
    [InlineData(2026, "71684", "20072")]
    [InlineData(2026, "132746", "20072")]
    [InlineData(2026, "132747", "20071.92")]
    [InlineData(2026, "398236", "0.96")]
    [InlineData(2026, "398237", "0")]
    [InlineData(2025, "2900", "0")]
    [InlineData(2025, "2901", "812.28")]
    [InlineData(2025, "70602", "19768.56")]
    [InlineData(2025, "70603", "19769")]
    [InlineData(2025, "130744", "19769")]
    [InlineData(2025, "130745", "19768.92")]
    [InlineData(2025, "392230", "0.66")]
    [InlineData(2025, "392231", "0")]
    [InlineData(2024, "2800", "0")]
    [InlineData(2024, "2801", "784.28")]
    [InlineData(2024, "69765", "19534.20")]
    [InlineData(2024, "69766", "19535")]
    [InlineData(2024, "129195", "19534.92")]
    [InlineData(2024, "387580", "1.02")]
    [InlineData(2024, "387581", "0")]
    public void Kia_follows_the_published_brackets_at_their_edges(int year, string invested, string expected)
    {
        var table = TaxYears.KiaFor(year)!;
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), TaxYears.KiaAmount(table, decimal.Parse(invested, CultureInfo.InvariantCulture)));
        Assert.StartsWith("https://www.belastingdienst.nl/", table.Source);
    }

    [Fact]
    public void Kia_for_2027_is_not_known_yet()
    {
        Assert.Null(TaxYears.KiaFor(2027));
        Assert.NotNull(TaxYears.KiaFor(2026));
    }

    [Fact]
    public void Indication_takes_the_kia_off_the_profit_before_the_self_employed_deduction()
    {
        var met = new HoursSummary(1300 * 60, 0, 0, 1225, true);
        var y = AnnualOverview.Indicate(TaxYears.For(2026), 2026, 10000m, met, starter: false, kia: 1120m);
        Assert.Equal(1120m, y.InvestmentDeduction);
        Assert.Equal(1200m, y.SelfEmployedDeduction);
        Assert.Equal(7680m, y.ProfitAfterDeductions);
        Assert.Equal(975.36m, y.SmeExemption);
        Assert.Equal(6704.64m, y.TaxableProfit);

        // De zelfstandigenaftrek is niet hoger dan de winst na de KIA.
        var small = AnnualOverview.Indicate(TaxYears.For(2026), 2026, 2000m, met, starter: false, kia: 1120m);
        Assert.Equal(880m, small.SelfEmployedDeduction);
        Assert.Equal(0m, small.ProfitAfterDeductions);
    }
}

// Investeringen via de API: markeren, verkopen, rollen, alleen-lezen en werkruimtes.
public class InvestmentTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    public static object Asset(decimal amount, string date = "2026-05-12", int? years = null, decimal? residual = null, bool passengerCar = false,
        string supplier = "Coolblue", bool investment = true, decimal? vatRate = 21, bool reverseCharge = false, string category = "hardware") => new
    {
        date, supplier, description = "Aankoop", category, amount, vatRate, vatAmount = (decimal?)null, reverseCharge,
        supplierRegion = reverseCharge ? "eu" : null, projectId = (int?)null, customerId = (int?)null,
        investment, usefulLifeYears = years, residualValue = residual, passengerCar,
    };

    static int Id(JsonElement e) => e.GetProperty("id").GetInt32();

    static Task<HttpResponseMessage> DisposeAsync(HttpClient c, int id, string? date, decimal? price = null) =>
        c.PutAsJsonAsync($"/api/investments/{id}/disposal", new { date, price });

    static Task<JsonElement> YearAsync(HttpClient c, int year = 2026) => c.GetFromJsonAsync<JsonElement>($"/api/investments?year={year}");

    [Fact]
    public async Task An_expense_of_at_least_450_without_vat_can_be_marked_as_an_investment()
    {
        var c = await ReadyAsync(factory, "invest-markeren");

        // 544,49 met 21% btw is 449,99 zonder btw: te weinig. 544,50 is precies 450.
        var tooCheap = await c.PostAsJsonAsync("/api/expenses", Asset(544.49m));
        Assert.Equal(HttpStatusCode.BadRequest, tooCheap.StatusCode);
        Assert.Contains("minstens € 450 zonder btw", await tooCheap.ErrorAsync());
        var e = await ExpenseKit.CreateAsync(c, Asset(544.50m));
        Assert.True(e.GetProperty("investment").GetBoolean());
        Assert.Equal(5, e.GetProperty("usefulLifeYears").GetInt32());
        Assert.Equal(JsonValueKind.Null, e.GetProperty("residualValue").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/expenses", Asset(1210m, years: 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/expenses", Asset(1210m, years: 21))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/expenses", Asset(1210m, residual: -1))).StatusCode);
        var tooHigh = await c.PostAsJsonAsync("/api/expenses", Asset(1210m, residual: 1000));
        Assert.Equal(HttpStatusCode.BadRequest, tooHigh.StatusCode);
        Assert.Contains("lager zijn dan de aanschafprijs", await tooHigh.ErrorAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/expenses", Asset(-1210m))).StatusCode);

        // Btw verlegd: het bedrag is al zonder btw.
        await ExpenseKit.CreateAsync(c, Asset(450m, reverseCharge: true, supplier: "Dell Ierland"));
        var laptop = await ExpenseKit.CreateAsync(c, Asset(1815m, date: "2026-03-01", years: 3, residual: 300.004m, supplier: "Apple"));
        Assert.Equal(300m, laptop.GetProperty("residualValue").GetDecimal());

        var y = await YearAsync(c);
        var row = y.GetProperty("investments").EnumerateArray().Single(i => Id(i) == Id(laptop));
        Assert.Equal(1500m, row.GetProperty("cost").GetDecimal());
        Assert.Equal(400m, row.GetProperty("perYear").GetDecimal());
        Assert.Equal(333.33m, row.GetProperty("depreciation").GetDecimal());          // maart tot en met december: 10 van de 36 maanden
        Assert.Equal(1166.67m, row.GetProperty("bookValue").GetDecimal());
        Assert.True(row.GetProperty("aboveMaximum").GetBoolean());
        Assert.True(row.GetProperty("countsForKia").GetBoolean());
        Assert.Equal(4, row.GetProperty("schedule").GetArrayLength());
        Assert.Equal(3, y.GetProperty("investments").GetArrayLength());
        Assert.Contains(y.GetProperty("summary").GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("meer dan 20%"));

        // Weer gewone kosten: de gegevens van de investering gaan weg.
        var plain = await c.PutAsJsonAsync($"/api/expenses/{Id(laptop)}", Asset(1815m, date: "2026-03-01", years: 3, residual: 300, supplier: "Apple", investment: false));
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        var back = await plain.JsonAsync();
        Assert.False(back.GetProperty("investment").GetBoolean());
        Assert.Equal(JsonValueKind.Null, back.GetProperty("usefulLifeYears").ValueKind);
        Assert.Equal(2, (await YearAsync(c)).GetProperty("investments").GetArrayLength());

        // Met de KOR trek je geen btw af, dus dan telt het bedrag met btw: 500 met btw is genoeg.
        var kor = await ReadyAsync(factory, "invest-kor", vatRegime: "kor");
        var korAsset = await ExpenseKit.CreateAsync(kor, Asset(500m));
        Assert.Equal(500m, (await YearAsync(kor)).GetProperty("investments")[0].GetProperty("cost").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, (await kor.PostAsJsonAsync("/api/expenses", Asset(449m))).StatusCode);
        Assert.False((await YearAsync(kor)).GetProperty("deduction").GetBoolean());
        Assert.True(korAsset.GetProperty("investment").GetBoolean());
    }

    [Fact]
    public async Task Only_admins_mark_an_asset_as_sold_and_the_dates_and_price_are_checked()
    {
        var admin = await ReadyAsync(factory, "invest-rollen");
        var email = $"invest-medewerker-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(admin, email);
        var staff = await TestApi.LoginAsync(factory, email);

        // Een medewerker mag een investering vastleggen, maar niet verkopen.
        var asset = Id(await ExpenseKit.CreateAsync(staff, Asset(1210m, date: "2026-02-10")));
        Assert.Equal(HttpStatusCode.Forbidden, (await DisposeAsync(staff, asset, "2026-06-01", 500)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.DeleteAsync($"/api/investments/{asset}/disposal")).StatusCode);

        var plain = Id(await ExpenseKit.CreateAsync(admin, ExpenseKit.Body(amount: 1210m)));
        Assert.Equal(HttpStatusCode.BadRequest, (await DisposeAsync(admin, plain, "2026-06-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DisposeAsync(admin, asset, "2026-02-09")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DisposeAsync(admin, asset, null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DisposeAsync(admin, asset, "2026-06-01", -1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DisposeAsync(admin, 999999, "2026-06-01")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DisposeAsync(admin, asset, "2026-06-01", 950)).StatusCode);

        // 1.000 over 5 jaar, februari tot en met juni: 5 maanden is 83,33. Boekwaarde 916,67, verkocht voor 950: 33,33 boekwinst.
        var row = (await YearAsync(staff)).GetProperty("investments").EnumerateArray().Single(i => Id(i) == asset);
        Assert.Equal(83.33m, row.GetProperty("depreciation").GetDecimal());
        Assert.Equal(916.67m, row.GetProperty("bookValueAtDisposal").GetDecimal());
        Assert.Equal(33.33m, row.GetProperty("disposalResult").GetDecimal());
        Assert.Equal(0m, row.GetProperty("bookValue").GetDecimal());
        Assert.Empty((await YearAsync(staff, 2027)).GetProperty("investments").EnumerateArray());

        // Zolang hij verkocht is, blijft het een investering en kan de aankoop niet na de verkoop liggen.
        var unmark = await staff.PutAsJsonAsync($"/api/expenses/{asset}", Asset(1210m, date: "2026-02-10", investment: false));
        Assert.Equal(HttpStatusCode.BadRequest, unmark.StatusCode);
        Assert.Contains("verkocht of weggedaan", await unmark.ErrorAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutAsJsonAsync($"/api/expenses/{asset}", Asset(1210m, date: "2026-06-02"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"/api/expenses/{asset}", Asset(1210m, date: "2026-06-01", years: 4))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/investments/{asset}/disposal")).StatusCode);
        var expense = await admin.GetFromJsonAsync<JsonElement>($"/api/expenses/{asset}");
        Assert.Equal(JsonValueKind.Null, expense.GetProperty("disposedOn").ValueKind);
        Assert.Equal(JsonValueKind.Null, expense.GetProperty("disposalPrice").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"/api/expenses/{asset}", Asset(1210m, date: "2026-02-10", investment: false))).StatusCode);

        // Alleen lezen: bekijken mag, verkopen of een investering toevoegen niet.
        var other = Id(await ExpenseKit.CreateAsync(admin, Asset(1210m)));
        await TestApi.ExpireTrialAsync(factory, await TestApi.WorkspaceIdAsync(admin));
        Assert.Equal(HttpStatusCode.PaymentRequired, (await DisposeAsync(admin, other, "2026-07-01")).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.DeleteAsync($"/api/investments/{other}/disposal")).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.PostAsJsonAsync("/api/expenses", Asset(1210m))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/investments?year=2026")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/investments?year=1999")).StatusCode);
    }

    [Fact]
    public async Task Investments_stay_inside_their_own_workspace()
    {
        var a = await ReadyAsync(factory, "invest-a");
        var b = await ReadyAsync(factory, "invest-b");
        var mine = Id(await ExpenseKit.CreateAsync(a, Asset(2420m, supplier: "Geheime laptop")));

        var seen = await YearAsync(b);
        Assert.Empty(seen.GetProperty("investments").EnumerateArray());
        Assert.Equal(0m, seen.GetProperty("summary").GetProperty("invested").GetDecimal());
        Assert.Equal(HttpStatusCode.NotFound, (await DisposeAsync(b, mine, "2026-07-01", 100)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/investments/{mine}/disposal")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsJsonAsync($"/api/expenses/{mine}", Asset(2420m, investment: false))).StatusCode);
        Assert.DoesNotContain("Geheime laptop", await b.GetStringAsync("/api/tax/annual.csv?year=2026"));
        Assert.Equal(2000m, (await YearAsync(a)).GetProperty("summary").GetProperty("invested").GetDecimal());
    }

    [Fact]
    public async Task The_export_contains_the_investment_details()
    {
        var c = await ReadyAsync(factory, "invest-export");
        var id = Id(await ExpenseKit.CreateAsync(c, Asset(2420m, years: 3, residual: 250)));
        Assert.Equal(HttpStatusCode.NoContent, (await DisposeAsync(c, id, "2026-09-30", 900)).StatusCode);
        var export = await c.GetFromJsonAsync<JsonElement>("/api/workspace/export");
        var e = Assert.Single(export.GetProperty("expenses").EnumerateArray());
        Assert.True(e.GetProperty("investment").GetBoolean());
        Assert.Equal(3, e.GetProperty("usefulLifeYears").GetInt32());
        Assert.Equal(250m, e.GetProperty("residualValue").GetDecimal());
        Assert.Equal(900m, e.GetProperty("disposalPrice").GetDecimal());
        Assert.StartsWith("2026-09-30", e.GetProperty("disposedOn").GetString());
    }

    [Fact]
    public async Task The_vat_on_an_investment_stays_in_the_quarter_of_purchase()
    {
        var c = await ReadyAsync(factory, "invest-btw");
        await ExpenseKit.CreateAsync(c, Asset(2420m, date: "2026-05-12", years: 5, residual: 200));
        var q2 = await c.GetFromJsonAsync<JsonElement>("/api/tax/vat-return?year=2026&quarter=2");
        Assert.Equal(420m, q2.GetProperty("deductible").GetDecimal());
        var q3 = await c.GetFromJsonAsync<JsonElement>("/api/tax/vat-return?year=2026&quarter=3");
        Assert.Equal(0m, q3.GetProperty("deductible").GetDecimal());
    }
}

// Het jaaroverzicht met investeringen: afschrijving in plaats van de aanschafprijs, verkopen en de KIA. De klok staat op 20 mei 2026.
public class InvestmentAnnualTests(MayInAmsterdamFactory factory) : IClassFixture<MayInAmsterdamFactory>
{
    void Edit(int workspaceId, Action<AppDb> change)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        db.Tenant.WorkspaceId = workspaceId;
        change(db);
        db.SaveChanges();
    }

    static async Task<int> AssetAsync(HttpClient c, object body) => (await ExpenseKit.CreateAsync(c, body)).GetProperty("id").GetInt32();

    [Fact]
    public async Task Annual_overview_counts_depreciation_disposals_and_the_kia()
    {
        var c = await ReadyAsync(factory, "invest-jaar");
        var ws = await TestApi.WorkspaceIdAsync(c);
        await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 10, 1000, issueDate: "2026-05-20");           // 10.000 omzet

        // Laptop: 4.000 zonder btw, restwaarde 400, 5 jaar: 60 per maand, maart tot en met december is 600.
        await AssetAsync(c, InvestmentTests.Asset(4840m, date: "2026-03-10", residual: 400, supplier: "Coolblue"));
        // Bureau van vorig jaar: 1.000 over 5 jaar, in 2026 helemaal: 200.
        await AssetAsync(c, InvestmentTests.Asset(1210m, date: "2025-07-01", supplier: "Ahrend", category: "kantoor"));
        // Telefoon uit 2024, verkocht eind april 2026 voor 700: 66,67 afschrijving, boekwaarde 533,33, dus 166,67 boekwinst.
        var phone = await AssetAsync(c, InvestmentTests.Asset(1210m, date: "2024-01-15", supplier: "Apple"));
        // Printer uit 2025, eind januari 2026 weggedaan zonder iets te krijgen: 10 afschrijving en 480 afgeboekt.
        var printer = await AssetAsync(c, InvestmentTests.Asset(726m, date: "2025-02-01", supplier: "HP"));
        // Personenauto: 30.000 zonder btw, restwaarde 6.000, april tot en met december 3.600. Telt niet mee voor de KIA.
        await AssetAsync(c, InvestmentTests.Asset(36300m, date: "2026-04-01", residual: 6000, passengerCar: true, supplier: "Autobedrijf"));
        // Een monitor als gewone kosten: 500 zonder btw. Kan een investering zijn.
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 605m, supplier: "Monitorshop"));
        // Voorbeelddata en een investering in 2027 tellen niet mee in 2026.
        var demo = await AssetAsync(c, InvestmentTests.Asset(12100m, date: "2026-02-01", supplier: "Voorbeeldleverancier"));
        Edit(ws, db => db.Expenses.Single(e => e.Id == demo).IsDemo = true);
        await AssetAsync(c, InvestmentTests.Asset(6050m, date: "2027-01-10", supplier: "Toekomst"));

        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync($"/api/investments/{phone}/disposal", new { date = "2026-04-30", price = 700 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync($"/api/investments/{printer}/disposal", new { date = "2026-01-31" })).StatusCode);

        var y = await c.GetFromJsonAsync<JsonElement>("/api/tax/annual?year=2026");
        var inv = y.GetProperty("investments");
        Assert.Equal(5, inv.GetProperty("count").GetInt32());
        Assert.Equal(4476.67m, inv.GetProperty("depreciation").GetDecimal());       // 600 + 200 + 66,67 + 10 + 3.600
        Assert.Equal(480m, inv.GetProperty("disposalLoss").GetDecimal());
        Assert.Equal(166.67m, inv.GetProperty("disposalGain").GetDecimal());
        Assert.Equal(34000m, inv.GetProperty("invested").GetDecimal());
        Assert.Equal(4000m, inv.GetProperty("qualifying").GetDecimal());
        Assert.Equal(1120m, inv.GetProperty("kia").GetDecimal());                   // 28% van 4.000
        Assert.True(inv.GetProperty("kiaKnown").GetBoolean());
        Assert.Equal(30500m, inv.GetProperty("bookValue").GetDecimal());            // 3.400 + 700 + 26.400
        Assert.Contains(inv.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("desinvesteringsbijtelling"));

        Assert.Equal(10000m, y.GetProperty("revenue").GetDecimal());
        Assert.Equal(5456.67m, y.GetProperty("costs").GetDecimal());                // 500 + 4.476,67 + 480
        Assert.Equal(4710m, y.GetProperty("result").GetDecimal());                  // 10.000 - 5.456,67 + 166,67
        Assert.Equal(1, y.GetProperty("possibleInvestments").GetInt32());
        var categories = y.GetProperty("costsByCategory").EnumerateArray().ToDictionary(x => x.GetProperty("category").GetString()!, x => x.GetProperty("amount").GetDecimal());
        Assert.Equal(new Dictionary<string, decimal> { ["hardware"] = 500m }, categories);
        var months = y.GetProperty("months").EnumerateArray().Select(m => m.GetProperty("costs").GetDecimal()).ToList();
        Assert.Equal(5456.67m, months.Sum());
        Assert.Equal(523.34m, months[0]);                                           // printer 10 + 480, bureau en telefoon 16,67

        // Indicatie: eerst de KIA, dan de rest. Zonder uren geen zelfstandigenaftrek.
        var ind = y.GetProperty("indication");
        Assert.Equal(1120m, ind.GetProperty("investmentDeduction").GetDecimal());
        Assert.Equal(3590m, ind.GetProperty("profitAfterDeductions").GetDecimal());
        Assert.Equal(455.93m, ind.GetProperty("smeExemption").GetDecimal());
        Assert.Equal(3134.07m, ind.GetProperty("taxableProfit").GetDecimal());
        Assert.Contains(ind.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("Personenauto's die niet bedoeld zijn voor beroepsvervoer"));

        var csv = Encoding.UTF8.GetString(await (await c.GetAsync("/api/tax/annual.csv?year=2026")).Content.ReadAsByteArrayAsync());
        var lines = csv.Split("\r\n");
        Assert.Contains("Kosten zonder btw;5456,67", lines);
        Assert.Contains("Afschrijving bedrijfsmiddelen;4476,67", lines);
        Assert.Contains("Afgeboekte boekwaarde bij verkoop of wegdoen;480,00", lines);
        Assert.Contains("Boekwinst bij verkoop;166,67", lines);
        Assert.Contains("Investeringen in 2026 (aanschafprijs);34000,00", lines);
        Assert.Contains("Investeringen die meetellen voor de KIA;4000,00", lines);
        Assert.Contains("Kleinschaligheidsinvesteringsaftrek (indicatie);1120,00", lines);
        Assert.Contains("Boekwaarde bedrijfsmiddelen 31-12-2026;30500,00", lines);
        Assert.Contains("Resultaat voor belasting;4710,00", lines);
        Assert.Contains("Investeringen", lines);
        Assert.Contains("10-03-2026;Coolblue;Aankoop;4000,00;400,00;5;600,00;3400,00;;;;ja", lines);
        Assert.Contains("01-04-2026;Autobedrijf;Aankoop;30000,00;6000,00;5;3600,00;26400,00;;;;nee", lines);
        Assert.Contains("15-01-2024;Apple;Aankoop;1000,00;0,00;5;66,67;0,00;30-04-2026;700,00;166,67;", lines);
        Assert.Contains("01-02-2025;HP;Aankoop;600,00;0,00;5;10,00;0,00;31-01-2026;0,00;-480,00;", lines);
        Assert.Contains(lines, l => l.StartsWith("10-03-2026;Coolblue;Aankoop;Hardware;4840,00;") && l.EndsWith(";nee;ja"));
        Assert.Contains(lines, l => l.StartsWith("12-05-2026;Monitorshop;") && l.EndsWith(";nee;nee"));
        Assert.DoesNotContain("Voorbeeldleverancier", csv);
        Assert.DoesNotContain("Toekomst", csv);

        // 2025: bureau 100, printer 110 en telefoon 200. Samen 1.600 gekocht: onder de 2.900, dus geen KIA.
        var last = (await c.GetFromJsonAsync<JsonElement>("/api/tax/annual?year=2025")).GetProperty("investments");
        Assert.Equal(410m, last.GetProperty("depreciation").GetDecimal());
        Assert.Equal(1600m, last.GetProperty("qualifying").GetDecimal());
        Assert.Equal(0m, last.GetProperty("kia").GetDecimal());

        // 2027: de KIA-bedragen staan nog niet vast.
        var next = await c.GetFromJsonAsync<JsonElement>("/api/tax/annual?year=2027");
        Assert.False(next.GetProperty("investments").GetProperty("kiaKnown").GetBoolean());
        Assert.Equal(0m, next.GetProperty("investments").GetProperty("kia").GetDecimal());
        Assert.Contains(next.GetProperty("indication").GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("kleinschaligheidsinvesteringsaftrek in 2027 staan nog niet vast"));

        // De pagina Investeringen rekent hetzelfde, en toont voorbeelddata wel, maar telt die niet mee.
        var page = await c.GetFromJsonAsync<JsonElement>("/api/investments?year=2026");
        Assert.Equal(6, page.GetProperty("investments").GetArrayLength());
        Assert.Equal(4476.67m, page.GetProperty("summary").GetProperty("depreciation").GetDecimal());
        Assert.True(page.GetProperty("investments").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == demo).GetProperty("demo").GetBoolean());
        Assert.False(page.GetProperty("includesDemo").GetBoolean());
        // Met de voorbeelddata erbij: 10.000 over 5 jaar, februari tot en met december is 1.833,33 extra.
        var withDemo = await c.GetFromJsonAsync<JsonElement>("/api/investments?year=2026&demo=true");
        Assert.True(withDemo.GetProperty("includesDemo").GetBoolean());
        Assert.Equal(6310m, withDemo.GetProperty("summary").GetProperty("depreciation").GetDecimal());
        Assert.Equal(6, withDemo.GetProperty("summary").GetProperty("count").GetInt32());
        Assert.Equal(4476.67m, (await c.GetFromJsonAsync<JsonElement>("/api/tax/annual?year=2026")).GetProperty("investments").GetProperty("depreciation").GetDecimal());
    }
}
