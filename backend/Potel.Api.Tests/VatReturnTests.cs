using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// 20 mei 2026: versturen dateert facturen op die dag, dus ze vallen in Q2 2026.
public class MayInAmsterdamFactory : PortalFactory
{
    public static readonly DateTime UtcNow = new(2026, 5, 20, 10, 0, 0, DateTimeKind.Utc);

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<BusinessClock>(new FixedClock(UtcNow)));
    }
}

// De btw-aangifte per kwartaal: rubrieken, creditnota's, verlegde btw, KOR, vrijgesteld, de opgaaf ICP en het dashboard.
public class VatReturnTests(MayInAmsterdamFactory factory) : IClassFixture<MayInAmsterdamFactory>
{
    static async Task<JsonElement> ReturnAsync(HttpClient c, int year = 2026, int quarter = 2, bool demo = false) =>
        await c.GetFromJsonAsync<JsonElement>($"/api/tax/vat-return?year={year}&quarter={quarter}{(demo ? "&demo=true" : "")}");

    static JsonElement Rubric(JsonElement ret, string code) =>
        ret.GetProperty("rubrics").EnumerateArray().Single(r => r.GetProperty("code").GetString() == code);

    static decimal Base(JsonElement ret, string code) => Rubric(ret, code).GetProperty("base").GetDecimal();
    static decimal Vat(JsonElement ret, string code) => Rubric(ret, code).GetProperty("vat").GetDecimal();

    // Leverdatum en factuurdatum op vandaag (volgens de vaste klok).
    static async Task<int> SendAsync(HttpClient c, int customerId, decimal qty, decimal price, decimal rate = 21, string regime = "normaal") =>
        await InvoiceAsync(c, customerId, "verzonden", qty, price, rate, vatRegime: regime, issueDate: "2026-05-20");

    static async Task<int> CreditAsync(HttpClient c, int invoiceId)
    {
        var credit = await Json(await c.PostAsync($"/api/invoices/{invoiceId}/credit", null));
        var id = credit.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, id, "verzonden")).StatusCode);
        return id;
    }

    void Edit(int workspaceId, Action<AppDb> change)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        db.Tenant.WorkspaceId = workspaceId;
        change(db);
        db.SaveChanges();
    }

    [Fact]
    public async Task Normal_return_adds_rates_subtracts_credit_notes_and_deducts_expenses()
    {
        var c = await ReadyAsync(factory, "btw-normaal");
        var customerId = await CustomerAsync(c);
        var credited = await SendAsync(c, customerId, 1.5m, 95);          // 142,50 en 29,93 btw, wordt helemaal gecrediteerd
        await SendAsync(c, customerId, 1.5m, 95);                          // 142,50 en 29,93 btw
        await SendAsync(c, customerId, 10, 100);                           // 1.000 en 210 btw
        await SendAsync(c, customerId, 10, 31.6m, 9);                      // 316 en 28,44 btw
        await SendAsync(c, customerId, 1, 100, 0);                         // 0%: alleen omzet in 1e
        await InvoiceAsync(c, customerId, "concept", 1, 5000);             // concept telt niet mee
        await CreditAsync(c, credited);

        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 121m, date: "2026-04-01"));                                                  // 21 btw
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 31.60m, vatRate: 9, supplier: "NS", category: "reiskosten", date: "2026-06-30")); // 2,61 btw
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 289m, supplier: "JetBrains", category: "software", reverseCharge: true, supplierRegion: "eu"));       // 60,69
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 23m, supplier: "GitHub", category: "software", reverseCharge: true, supplierRegion: "buiten-eu"));    // 4,83
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 12m, vatRate: null, supplier: "Parkeren", category: "reiskosten"));          // geen btw
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 999m, date: "2026-03-31"));                                                  // vorig kwartaal
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 999m, date: "2026-07-01"));                                                  // volgend kwartaal

        var ret = await ReturnAsync(c);
        Assert.Equal("Q2 2026", ret.GetProperty("label").GetString());
        Assert.Equal(1142.50m, Base(ret, "1a"));
        // De creditnota haalt precies de eerste factuur weg: 29,93 + 210.
        Assert.Equal(239.93m, Vat(ret, "1a"));
        Assert.Equal(316m, Base(ret, "1b"));
        Assert.Equal(28.44m, Vat(ret, "1b"));
        Assert.Equal(100m, Base(ret, "1e"));
        Assert.Equal(JsonValueKind.Null, Rubric(ret, "1e").GetProperty("vat").ValueKind);
        Assert.Equal(0m, Base(ret, "3b"));
        Assert.Equal(23m, Base(ret, "4a"));
        Assert.Equal(4.83m, Vat(ret, "4a"));
        Assert.Equal(289m, Base(ret, "4b"));
        Assert.Equal(60.69m, Vat(ret, "4b"));
        Assert.DoesNotContain(ret.GetProperty("rubrics").EnumerateArray(), r => r.GetProperty("code").GetString() == "1c");

        var due = 239.93m + 28.44m + 4.83m + 60.69m;
        var deductible = 21m + 2.61m + 60.69m + 4.83m;
        Assert.Equal(due, Vat(ret, "5a"));
        Assert.Equal(deductible, Vat(ret, "5b"));
        Assert.Equal(due - deductible, ret.GetProperty("total").GetDecimal());
        Assert.Equal("2026-07-31T00:00:00", ret.GetProperty("dueDate").GetString());
        Assert.Equal(1142.50m + 316m + 100m, ret.GetProperty("revenue").GetDecimal());

        // Het dashboard laat hetzelfde bedrag zien en linkt naar dit kwartaal.
        var tile = (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("vat");
        Assert.Equal(due - deductible, tile.GetProperty("amount").GetDecimal());
        Assert.Equal(ret.GetProperty("revenue").GetDecimal(), tile.GetProperty("revenue").GetDecimal());
        Assert.Equal("Q2 2026", tile.GetProperty("label").GetString());
        Assert.Equal(2, tile.GetProperty("quarter").GetInt32());
        Assert.Equal("2026-07-31", tile.GetProperty("dueDate").GetString());

        // Het vorige en volgende kwartaal hebben alleen hun eigen uitgave.
        var q1 = await ReturnAsync(c, quarter: 1);
        Assert.Equal(0m, Vat(q1, "5a"));
        Assert.Equal(173.38m, Vat(q1, "5b"));
        Assert.Equal(-173.38m, q1.GetProperty("total").GetDecimal());
        Assert.Equal("2026-04-30T00:00:00", q1.GetProperty("dueDate").GetString());
    }

    [Fact]
    public async Task Vat_is_rounded_per_invoice_like_the_invoices_themselves()
    {
        var c = await ReadyAsync(factory, "btw-afronden");
        var customerId = await CustomerAsync(c);
        await SendAsync(c, customerId, 1.5m, 95);   // 29,925 wordt 29,93
        await SendAsync(c, customerId, 1.5m, 95);   // nog eens 29,93
        var ret = await ReturnAsync(c);
        Assert.Equal(285m, Base(ret, "1a"));
        Assert.Equal(59.86m, Vat(ret, "1a"));       // 21% van 285 zou 59,85 zijn
        Assert.Equal(59.86m, ret.GetProperty("total").GetDecimal());
        Assert.Equal(59.86m, (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("vat").GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Reverse_charge_goes_to_3b_and_the_icp_per_vat_number_by_service_period()
    {
        var c = await ReadyAsync(factory, "btw-icp");
        var ws = await TestApi.WorkspaceIdAsync(c);
        var german = await CustomerAsync(c, "Berlin", "Duitsland", "DE 123.456.789");
        var belgian = await CustomerAsync(c, "Gent", "België", "BE0123456789");
        var american = await CustomerAsync(c, "Austin", "Verenigde Staten");
        await SendAsync(c, german, 10, 100, regime: "verlegd");
        var creditedGerman = await SendAsync(c, german, 5, 100, regime: "verlegd");
        await SendAsync(c, belgian, 2.5m, 100, regime: "verlegd");
        var march = await SendAsync(c, belgian, 4, 100, regime: "verlegd");
        await SendAsync(c, american, 8, 100, regime: "buiten-eu");
        await CreditAsync(c, creditedGerman);
        // In maart geleverd en pas in mei gefactureerd: hoort in de opgaaf over Q1.
        Edit(ws, db =>
        {
            var inv = db.Invoices.Single(i => i.Id == march);
            inv.DeliveryFrom = new DateTime(2026, 3, 1);
            inv.DeliveryTo = new DateTime(2026, 3, 31);
        });

        var ret = await ReturnAsync(c);
        Assert.Equal(1250m, Base(ret, "3b"));
        Assert.Equal(0m, Vat(ret, "5a"));
        Assert.Equal(0m, ret.GetProperty("total").GetDecimal());
        Assert.Equal(800m, ret.GetProperty("outsideEu").GetDecimal());
        Assert.Equal(2050m, ret.GetProperty("revenue").GetDecimal());

        var icp = ret.GetProperty("icp").EnumerateArray().ToList();
        Assert.Equal(2, icp.Count);
        var be = icp.Single(l => l.GetProperty("vatNumber").GetString() == "BE0123456789");
        var de = icp.Single(l => l.GetProperty("vatNumber").GetString() == "DE123456789");
        Assert.Equal(250m, be.GetProperty("amount").GetDecimal());
        Assert.Equal("België", be.GetProperty("country").GetString());
        Assert.Equal(1000m, de.GetProperty("amount").GetDecimal());
        Assert.Equal("Berlin B.V.", de.GetProperty("customer").GetString());
        // De opgaaf ICP moet optellen tot rubriek 3b.
        Assert.Equal(Base(ret, "3b"), icp.Sum(l => l.GetProperty("amount").GetDecimal()));

        var q1 = await ReturnAsync(c, quarter: 1);
        Assert.Equal(400m, Base(q1, "3b"));
        Assert.Equal(400m, q1.GetProperty("icp")[0].GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Kor_workspace_does_not_file_and_does_not_deduct()
    {
        var c = await ReadyAsync(factory, "btw-kor", vatRegime: "kor");
        var customerId = await CustomerAsync(c);
        await SendAsync(c, customerId, 10, 100, regime: "kor");
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 121m));
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 23m, supplier: "GitHub", category: "software", reverseCharge: true, supplierRegion: "buiten-eu"));

        var ret = await ReturnAsync(c);
        Assert.True(ret.GetProperty("kor").GetBoolean());
        Assert.False(ret.GetProperty("deduction").GetBoolean());
        Assert.Equal(1000m, ret.GetProperty("korRevenue").GetDecimal());
        Assert.Equal(0m, Base(ret, "1a"));
        Assert.Equal(0m, Vat(ret, "5b"));
        // Verlegde btw draag je ook met de KOR af, maar je trekt hem niet af.
        Assert.Equal(4.83m, Vat(ret, "4a"));
        Assert.Equal(4.83m, ret.GetProperty("total").GetDecimal());
        Assert.Equal(JsonValueKind.Null, ret.GetProperty("pending").ValueKind);

        var tile = (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("vat");
        Assert.True(tile.GetProperty("kor").GetBoolean());
        Assert.Equal(JsonValueKind.Null, tile.GetProperty("dueDate").ValueKind);
    }

    [Fact]
    public async Task Exempt_revenue_is_shown_apart_and_its_costs_are_not_deducted()
    {
        var c = await ReadyAsync(factory, "btw-vrij", vatRegime: "vrijgesteld");
        var customerId = await CustomerAsync(c);
        await SendAsync(c, customerId, 1, 80, regime: "vrijgesteld");
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 121m));

        var ret = await ReturnAsync(c);
        Assert.Equal(80m, ret.GetProperty("exempt").GetDecimal());
        Assert.False(ret.GetProperty("deduction").GetBoolean());
        Assert.False(ret.GetProperty("kor").GetBoolean());
        Assert.Equal(0m, Vat(ret, "5b"));
        Assert.Equal(0m, ret.GetProperty("total").GetDecimal());
        Assert.All(ret.GetProperty("rubrics").EnumerateArray(), r => Assert.Equal(0m, r.GetProperty("base").GetDecimal()));
    }

    [Fact]
    public async Task Demo_invoices_and_expenses_only_count_when_asked()
    {
        var c = await ReadyAsync(factory, "btw-demo");
        var ws = await TestApi.WorkspaceIdAsync(c);
        var customerId = await CustomerAsync(c);
        await SendAsync(c, customerId, 10, 100);
        var demoInvoice = await SendAsync(c, customerId, 1, 50);
        var demoExpense = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 121m))).GetProperty("id").GetInt32();
        Edit(ws, db =>
        {
            db.Invoices.Single(i => i.Id == demoInvoice).IsDemo = true;
            db.Expenses.Single(e => e.Id == demoExpense).IsDemo = true;
        });

        var ret = await ReturnAsync(c);
        Assert.True(ret.GetProperty("demoData").GetBoolean());
        Assert.False(ret.GetProperty("includesDemo").GetBoolean());
        Assert.Equal(1000m, Base(ret, "1a"));
        Assert.Equal(0m, Vat(ret, "5b"));
        Assert.Equal(210m, (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("vat").GetProperty("amount").GetDecimal());

        var withDemo = await ReturnAsync(c, demo: true);
        Assert.True(withDemo.GetProperty("includesDemo").GetBoolean());
        Assert.Equal(1050m, Base(withDemo, "1a"));
        Assert.Equal(21m, Vat(withDemo, "5b"));
    }

    [Fact]
    public async Task Another_workspace_never_counts_in_your_return()
    {
        var a = await ReadyAsync(factory, "btw-a");
        var b = await ReadyAsync(factory, "btw-b");
        await SendAsync(a, await CustomerAsync(a), 10, 100);
        await ExpenseKit.CreateAsync(a, ExpenseKit.Body(amount: 121m));

        var ret = await ReturnAsync(b);
        Assert.All(ret.GetProperty("rubrics").EnumerateArray(), r => Assert.Equal(0m, r.GetProperty("base").GetDecimal()));
        Assert.Equal(0m, Vat(ret, "5b"));
        Assert.Empty(ret.GetProperty("sources").EnumerateArray());
    }

    [Fact]
    public async Task Read_only_workspace_can_still_see_its_return_and_bad_quarters_are_refused()
    {
        var c = await ReadyAsync(factory, "btw-lezen");
        await TestApi.ExpireTrialAsync(factory, await TestApi.WorkspaceIdAsync(c));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/tax/vat-return?year=2026&quarter=1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/tax/vat-return?year=2026&quarter=5")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/tax/vat-return?year=1999&quarter=1")).StatusCode);
        // Zonder kwartaal: het lopende kwartaal volgens de Nederlandse kalender.
        Assert.Equal("Q2 2026", (await c.GetFromJsonAsync<JsonElement>("/api/tax/vat-return")).GetProperty("label").GetString());
    }

    [Theory]
    [InlineData(2026, 1, "2026-04-30")]
    [InlineData(2026, 2, "2026-07-31")]
    [InlineData(2026, 3, "2026-10-31")]   // een zaterdag: de Belastingdienst schuift niet op
    [InlineData(2026, 4, "2027-01-31")]
    public void Deadline_is_the_last_day_of_the_month_after_the_quarter(int year, int quarter, string expected) =>
        Assert.Equal(DateTime.Parse(expected), VatReturn.DueDate(year, quarter));

    [Theory]
    [InlineData("2026-04-30", "Q1 2026")]
    [InlineData("2026-05-01", null)]
    [InlineData("2027-01-31", "Q4 2026")]
    [InlineData("2026-12-31", null)]
    public void Previous_quarter_is_pending_until_its_deadline(string today, string? expected) =>
        Assert.Equal(expected, VatReturn.PendingOn(DateTime.Parse(today))?.Label);
}
