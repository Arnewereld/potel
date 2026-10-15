using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// De cashflow-prognose. De klok staat op donderdag 15 oktober 2026, dus het venster loopt van oktober 2026 tot en met maart 2027
// en de betaaltermijn is 14 dagen.
public class CashflowTests(InsightsFactory factory) : IClassFixture<InsightsFactory>
{
    static decimal Dec(JsonElement e, string name) => e.GetProperty(name).GetDecimal();
    static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    static DateTime D(int y, int m, int d) => new(y, m, d);

    static Task<JsonElement> CashflowAsync(HttpClient c, bool demo = false) => c.GetFromJsonAsync<JsonElement>($"/api/insights/cashflow{(demo ? "?demo=true" : "")}");

    static List<JsonElement> Items(JsonElement data, string source) =>
        data.GetProperty("items").EnumerateArray().Where(i => Str(i, "source") == source).ToList();

    // Kort: maand en bedrag per post, om in één keer te vergelijken.
    static List<(string Month, decimal Amount)> Short(JsonElement data, string source) =>
        Items(data, source).Select(i => (Str(i, "month"), Dec(i, "amount"))).ToList();

    async Task<(HttpClient C, int Ws, int Customer)> WorkspaceAsync(string prefix)
    {
        factory.Clock.Value = InsightsFactory.Start;
        var c = await ReadyAsync(factory, prefix);
        return (c, await TestApi.WorkspaceIdAsync(c), await CustomerAsync(c));
    }

    void Edit(int ws, Action<AppDb> change) => WorkflowKit.InWorkspace(factory, ws, db => { change(db); return db.SaveChanges(); });

    void SetDue(int ws, int invoiceId, DateTime due) =>
        WorkflowKit.InWorkspace(factory, ws, db => db.Invoices.Where(i => i.Id == invoiceId).ExecuteUpdate(x => x.SetProperty(i => i.DueDate, due)));

    // Een verstuurde factuur met een factuurdatum in het verleden: verstuurd op die dag.
    async Task<int> SentOnAsync(HttpClient c, int customer, decimal quantity, decimal unitPrice, string day)
    {
        factory.Clock.Value = TimeZoneInfo.ConvertTimeToUtc(DateTime.Parse(day).AddHours(10), BusinessClock.Zone);
        try { return await InvoiceAsync(c, customer, "verzonden", quantity, unitPrice, issueDate: day); }
        finally { factory.Clock.Value = InsightsFactory.Start; }
    }

    static object Template(int customerId, string start, string interval = "maand", string rule = "vooraf", string? end = null, decimal price = 100) => new
    {
        name = "Hosting", customerId, vatRegime = "normaal", interval, periodRule = rule, action = "concept", startDate = start, endDate = end,
        lines = new[] { new { description = "Hosting {maand}", quantity = 1, unit = "maand", unitPrice = price, vatRate = 21 } },
    };

    static async Task<int> RecurringAsync(HttpClient c, object template)
    {
        var res = await c.PostAsJsonAsync("/api/recurring-invoices", template);
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await res.JsonAsync()).GetProperty("id").GetInt32();
    }

    int AcceptedQuote(int ws, int customer, decimal quantity, decimal price, string number, Action<Quote>? more = null)
    {
        var q = new Quote
        {
            CustomerId = customer, Number = number, Status = QuoteStatus.Accepted,
            Lines = [new QuoteLine { Description = "Werk", Quantity = quantity, Unit = "stuk", UnitPrice = price, VatRate = 21 }],
        };
        more?.Invoke(q);
        Edit(ws, db => db.Quotes.Add(q));
        return q.Id;
    }

    static object Settings(decimal? bankBalance = null, decimal fixedCosts = 0, int spread = 1) =>
        new { bankBalance, fixedCostsPerMonth = fixedCosts, quoteSpreadMonths = spread };

    [Fact]
    public async Task Open_invoices_come_in_on_their_due_date_and_late_ones_count_this_month()
    {
        var (c, ws, customer) = await WorkspaceAsync("cash-facturen");
        var late = await InvoiceAsync(c, customer, "verzonden", 10, 100);             // 1.210 per factuur
        var partly = await InvoiceAsync(c, customer, "verzonden", 10, 100);
        var credited = await InvoiceAsync(c, customer, "verzonden", 10, 100);
        var lastDay = await InvoiceAsync(c, customer, "verzonden", 10, 100);
        var later = await InvoiceAsync(c, customer, "verzonden", 10, 100);
        var paid = await InvoiceAsync(c, customer, "verzonden", 10, 100);
        var demo = await InvoiceAsync(c, customer, "verzonden", 5, 100);
        SetDue(ws, late, D(2026, 9, 30));
        SetDue(ws, partly, D(2026, 12, 5));
        SetDue(ws, credited, D(2026, 11, 30));
        SetDue(ws, lastDay, D(2027, 3, 31));                                          // de laatste dag van het venster
        SetDue(ws, later, D(2027, 4, 1));                                             // net erna
        SetDue(ws, demo, D(2026, 10, 20));
        Edit(ws, db => { var d = db.Invoices.Single(i => i.Id == demo); d.IsDemo = true; d.Number = "VOORBEELD-0001"; });   // voorbeelden hebben een eigen reeks
        (await c.PostAsJsonAsync($"/api/invoices/{partly}/payments", new { amount = 500m, date = "2026-10-10" })).EnsureSuccessStatusCode();
        (await c.PostAsJsonAsync($"/api/invoices/{paid}/payments", new { amount = 1210m, date = "2026-10-10" })).EnsureSuccessStatusCode();
        await InsightsTests.CreditAsync(c, credited, -2, 100);                       // 242 eraf

        var data = await CashflowAsync(c);
        Assert.Equal(["2026-10", "2026-11", "2026-12", "2027-01", "2027-02", "2027-03"], data.GetProperty("months").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(14, data.GetProperty("paymentTermDays").GetInt32());
        Assert.Equal([("2026-10", 1210m), ("2026-11", 968m), ("2026-12", 710m), ("2027-03", 1210m)], Short(data, "facturen"));
        var first = Items(data, "facturen")[0];
        Assert.True(first.GetProperty("late").GetBoolean());
        Assert.StartsWith("2026-10-15", Str(first, "date"));
        Assert.Contains("vervaldatum 30 september 2026 is voorbij", Str(first, "detail"));
        Assert.Equal($"/facturen/{late}", Str(first, "link"));
        Assert.False(Items(data, "facturen")[1].GetProperty("late").GetBoolean());
        Assert.True(data.GetProperty("demoData").GetBoolean());
        Assert.False(data.GetProperty("includesDemo").GetBoolean());

        // Met voorbeelddata komt de voorbeeldfactuur erbij.
        var withDemo = await CashflowAsync(c, demo: true);
        Assert.True(withDemo.GetProperty("includesDemo").GetBoolean());
        Assert.Contains(("2026-10", 605m), Short(withDemo, "facturen"));

        // De tegel op het dashboard: wat er deze maand binnenkomt (de te late factuur), zonder voorbeelddata.
        var tile = (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("cashflow");
        Assert.Equal("2026-10", Str(tile, "month"));
        Assert.Equal(1210m, Dec(tile, "expectedIn"));
        Assert.Equal(1210m, Dec(tile, "late"));
        Assert.True(tile.GetProperty("demoData").GetBoolean());
    }

    [Fact]
    public async Task Recurring_invoices_come_in_per_period_after_the_payment_term()
    {
        var (c, ws, customer) = await WorkspaceAsync("cash-terug");
        await RecurringAsync(c, Template(customer, "2026-11-01", end: "2026-12-31"));                       // november en december: 121
        await RecurringAsync(c, Template(customer, "2026-11-01", price: 50));                               // zonder einde: 60,50
        // Per kwartaal achteraf vanaf juli: het derde kwartaal is meteen een concept geworden, het vierde komt op 1 januari.
        var quarterly = await RecurringAsync(c, Template(customer, "2026-07-01", interval: "kwartaal", rule: "achteraf", price: 300));
        var paused = await RecurringAsync(c, Template(customer, "2026-11-01", price: 1000));
        (await c.PostAsync($"/api/recurring-invoices/{paused}/pause", null)).EnsureSuccessStatusCode();
        var demo = await RecurringAsync(c, Template(customer, "2026-11-01", price: 7));
        Edit(ws, db => db.RecurringInvoices.Single(t => t.Id == demo).IsDemo = true);

        var data = await CashflowAsync(c);
        Assert.Equal(
            [("2026-11", 121m), ("2026-11", 60.50m), ("2026-12", 121m), ("2026-12", 60.50m), ("2027-01", 363m), ("2027-01", 60.50m), ("2027-02", 60.50m), ("2027-03", 60.50m)],
            Short(data, "terugkerend").OrderBy(x => x.Month).ThenByDescending(x => x.Amount).ToList());
        var q4 = Items(data, "terugkerend").Single(i => Dec(i, "amount") == 363m);
        Assert.StartsWith("2027-01-15", Str(q4, "date"));                            // gemaakt op 1 januari, 14 dagen later betaald
        Assert.Contains("Q4 2026", Str(q4, "detail"));
        Assert.Equal($"/facturen/terugkerend/{quarterly}", Str(q4, "link"));
        // Het concept van het derde kwartaal telt één keer, als nog te factureren.
        Assert.Equal([("2026-10", 363m)], Short(data, "te-factureren"));
        Assert.Contains(Items(await CashflowAsync(c, demo: true), "terugkerend"), i => Dec(i, "amount") == 8.47m);

        // De btw van wat in het vierde kwartaal gemaakt wordt, hoort bij de aangifte over dat kwartaal (betalen in januari).
        var vat = data.GetProperty("vat").EnumerateArray().Single(v => v.GetProperty("quarter").GetInt32() == 4);
        Assert.Equal("2027-01", Str(vat, "month"));
        Assert.Equal(63m, Dec(vat.GetProperty("parts"), "terugkerend"));             // 2 x 21 en 2 x 10,50
        Assert.Equal(63m, Dec(vat.GetProperty("parts"), "te-factureren"));
    }

    [Fact]
    public async Task Vat_on_an_invoice_made_in_the_window_counts_even_when_the_money_comes_later()
    {
        var (c, ws, customer) = await WorkspaceAsync("cash-btw-later");
        Edit(ws, db => db.Settings.Single().PaymentTermDays = 75);
        factory.Clock.SetDay(2026, 9, 15);                                                // venster september t/m februari
        try
        {
            await RecurringAsync(c, Template(customer, "2026-12-20", end: "2026-12-31"));    // gemaakt op 20 december, betaald op 5 maart
            var data = await CashflowAsync(c);
            Assert.Empty(Items(data, "terugkerend"));
            var q4 = data.GetProperty("vat").EnumerateArray().Single(v => v.GetProperty("quarter").GetInt32() == 4);
            Assert.Equal("2027-01", Str(q4, "month"));
            Assert.Equal(21m, Dec(q4.GetProperty("parts"), "terugkerend"));             // de btw betaal je in januari al
        }
        finally { factory.Clock.Value = InsightsFactory.Start; }
    }

    [Fact]
    public async Task Accepted_quotes_count_what_is_left_spread_over_the_chosen_months()
    {
        var (c, ws, customer) = await WorkspaceAsync("cash-offertes");
        AcceptedQuote(ws, customer, 10, 100, "OFF-1");                                                     // 1.210 in november
        var deposit = await InvoiceAsync(c, customer, "verzonden", 3, 100);
        AcceptedQuote(ws, customer, 10, 100, "OFF-2", q => q.DepositInvoiceId = deposit);                   // 700 over: 847
        var final = await InvoiceAsync(c, customer, "concept", 10, 100);
        AcceptedQuote(ws, customer, 10, 100, "OFF-3", q => q.InvoiceId = final);                            // al gefactureerd
        AcceptedQuote(ws, customer, 10, 100, "OFF-4", q => q.Status = QuoteStatus.Sent);                    // nog niet geaccepteerd
        AcceptedQuote(ws, customer, 10, 100, "OFF-5", q => q.IsDemo = true);
        // Een project per uur uit een offerte: 3 uur gefactureerd en 2 uur open tellen elders, dus 500 over.
        var project = await ProjectAsync(c, customer, 100);
        var entry = (await (await c.PostAsJsonAsync("/api/time", new { projectId = project, date = "2026-10-01", minutes = 180, description = "Werk", billable = true })).JsonAsync()).GetProperty("id").GetInt32();
        await TimeAsync(c, project, 120, "2026-10-02");
        var hoursInvoice = (await (await c.PostAsJsonAsync($"/api/projects/{project}/invoice", new { entryIds = new[] { entry } })).JsonAsync()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, hoursInvoice, "verzonden")).StatusCode);
        AcceptedQuote(ws, customer, 10, 100, "OFF-6", q => q.ProjectId = project);

        var data = await CashflowAsync(c);
        var quotes = Items(data, "offertes");
        Assert.Equal(["Offerte OFF-1", "Offerte OFF-2", "Offerte OFF-6"], quotes.Select(q => Str(q, "label")));
        Assert.Equal([("2026-11", 1210m), ("2026-11", 847m), ("2026-11", 605m)], Short(data, "offertes"));
        Assert.StartsWith("2026-11-01", Str(quotes[0], "date"));
        Assert.Equal(210m + 147m + 105m, Dec(data.GetProperty("vat").EnumerateArray().Single(v => v.GetProperty("quarter").GetInt32() == 4).GetProperty("parts"), "offertes"));

        // Over drie maanden: precies op de cent verdeeld. Wat in januari valt, hoort bij de btw van het eerste kwartaal.
        (await c.PutAsJsonAsync("/api/insights/cashflow/settings", Settings(spread: 3))).EnsureSuccessStatusCode();
        data = await CashflowAsync(c);
        var first = Items(data, "offertes").Where(i => Str(i, "label") == "Offerte OFF-1").ToList();
        Assert.Equal([("2026-11", 403.33m), ("2026-12", 403.33m), ("2027-01", 403.34m)], first.Select(i => (Str(i, "month"), Dec(i, "amount"))));
        Assert.Contains("deel 3 van 3", Str(first[2], "detail"));
        Assert.Equal(70m + 70m + 49m + 49m + 35m + 35m, Dec(data.GetProperty("vat").EnumerateArray().Single(v => v.GetProperty("quarter").GetInt32() == 4).GetProperty("parts"), "offertes"));

        // Over zes maanden valt de laatste maand (april) buiten het venster.
        (await c.PutAsJsonAsync("/api/insights/cashflow/settings", Settings(spread: 6))).EnsureSuccessStatusCode();
        first = Items(await CashflowAsync(c), "offertes").Where(i => Str(i, "label") == "Offerte OFF-1").ToList();
        Assert.Equal(5, first.Count);
        Assert.All(first, i => Assert.Equal(201.67m, Dec(i, "amount")));
        Assert.Equal("2027-03", Str(first[^1], "month"));

        // Voorbeeldoffertes alleen als je dat vraagt.
        Assert.Contains(Items(await CashflowAsync(c, demo: true), "offertes"), i => Str(i, "label") == "Offerte OFF-5");
    }

    [Fact]
    public async Task Unbilled_hours_trips_and_drafts_come_in_after_the_payment_term()
    {
        var (c, ws, customer) = await WorkspaceAsync("cash-open");
        var abroad = await CustomerAsync(c, "Acme", country: "Verenigde Staten");
        var nl = await ProjectAsync(c, customer, 100, "Website");
        var us = await ProjectAsync(c, abroad, 80, "App");
        await TimeAsync(c, nl, 90, "2026-10-05");                                      // 150 plus 21% btw
        (await c.PostAsJsonAsync("/api/time", new { projectId = nl, date = "2026-10-05", minutes = 60, description = "Intern", billable = false })).EnsureSuccessStatusCode();
        await TimeAsync(c, us, 60, "2026-10-05");                                      // buiten de EU: geen btw
        var fixedProject = (await (await c.PostAsJsonAsync("/api/projects", new { name = "Vast", customerId = customer, status = "actief", billing = "vast", fixedPrice = 5000, hourlyRate = 0, color = "#000000" })).JsonAsync()).GetProperty("id").GetInt32();
        await TimeAsync(c, fixedProject, 120, "2026-10-05");                           // een vaste prijs factureer je niet per uur
        var demoProject = await ProjectAsync(c, customer, 100, "Voorbeeld");
        await TimeAsync(c, demoProject, 600, "2026-10-05");
        Edit(ws, db =>
        {
            foreach (var t in db.TimeEntries.Where(t => t.ProjectId == demoProject)) t.IsDemo = true;
            db.Customers.Single(x => x.Id == customer).KilometreRate = 0.23m;
            db.Trips.AddRange(
                new Trip { Date = D(2026, 10, 6), From = "Utrecht", To = "Amsterdam", Kilometres = 100, CustomerId = customer, Billable = true },
                new Trip { Date = D(2026, 10, 6), From = "Utrecht", To = "Zee", Kilometres = 50, CustomerId = customer, Billable = true, Kind = TripKinds.Private },
                new Trip { Date = D(2026, 10, 6), From = "Utrecht", To = "New York", Kilometres = 70, CustomerId = abroad, Billable = true });   // geen tarief
        });
        // Een concept met een termijn van 30 dagen, en een concept-creditnota die niets oplevert.
        var draft = await InvoiceAsync(c, customer, "concept", 2, 100);
        Edit(ws, db => { var d = db.Invoices.Single(i => i.Id == draft); d.IssueDate = D(2026, 10, 1); d.DueDate = D(2026, 10, 31); });
        var sent = await InvoiceAsync(c, customer, "verzonden", 1, 100);
        (await c.PostAsync($"/api/invoices/{sent}/credit", null)).EnsureSuccessStatusCode();

        var data = await CashflowAsync(c);
        var open = Items(data, "te-factureren");
        Assert.Equal(["App", "Website", "Ritten", "Conceptfactuur"], open.Select(i => Str(i, "label")));
        Assert.Equal(181.50m, Dec(open.Single(i => Str(i, "label") == "Website"), "amount"));
        Assert.Contains("1,5 uur open", Str(open.Single(i => Str(i, "label") == "Website"), "detail"));
        Assert.Equal(80m, Dec(open.Single(i => Str(i, "label") == "App"), "amount"));
        Assert.Equal(27.83m, Dec(open.Single(i => Str(i, "label") == "Ritten"), "amount"));      // 100 km à 0,23 met btw
        Assert.All(open.Where(i => Str(i, "label") != "Conceptfactuur"), i => Assert.StartsWith("2026-10-29", Str(i, "date")));
        var concept = open.Single(i => Str(i, "label") == "Conceptfactuur");
        Assert.Equal(242m, Dec(concept, "amount"));
        Assert.StartsWith("2026-11-14", Str(concept, "date"));                        // vandaag plus zijn eigen termijn van 30 dagen
        var parts = data.GetProperty("vat").EnumerateArray().Single(v => v.GetProperty("quarter").GetInt32() == 4).GetProperty("parts");
        Assert.Equal(31.50m + 4.83m + 42m, Dec(parts, "te-factureren"));

        // Met voorbeelddata: de voorbeelduren erbij.
        Assert.Contains(Items(await CashflowAsync(c, demo: true), "te-factureren"), i => Str(i, "label") == "Voorbeeld");
    }

    [Fact]
    public async Task Vat_is_paid_in_the_month_of_the_deadline()
    {
        var (c, ws, customer) = await WorkspaceAsync("cash-btw");
        await SentOnAsync(c, customer, 10, 100, "2026-08-14");                         // Q3: 210 btw
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 121m, date: "2026-09-01"));   // 21 voorbelasting
        await InvoiceAsync(c, customer, "verzonden", 5, 100);                          // Q4 tot nu toe: 105
        var project = await ProjectAsync(c, customer, 100);
        await TimeAsync(c, project, 60, "2026-10-05");                                 // 21 btw als je het nu factureert

        var vat = (await CashflowAsync(c)).GetProperty("vat").EnumerateArray().ToList();
        Assert.Equal(2, vat.Count);
        Assert.Equal((2026, 3, "2026-10"), (vat[0].GetProperty("year").GetInt32(), vat[0].GetProperty("quarter").GetInt32(), Str(vat[0], "month")));
        Assert.StartsWith("2026-10-31", Str(vat[0], "dueDate"));
        Assert.Equal(189m, Dec(vat[0], "base"));
        Assert.False(vat[0].GetProperty("running").GetBoolean());
        Assert.Empty(vat[0].GetProperty("parts").EnumerateObject());
        Assert.Equal((4, "2027-01", 105m), (vat[1].GetProperty("quarter").GetInt32(), Str(vat[1], "month"), Dec(vat[1], "base")));
        Assert.True(vat[1].GetProperty("running").GetBoolean());
        Assert.Equal(21m, Dec(vat[1].GetProperty("parts"), "te-factureren"));

        // Op de uiterste dag telt het derde kwartaal nog; een dag later niet meer, en dan valt april (Q1 2027) in het venster.
        factory.Clock.SetDay(2026, 10, 31);
        Assert.Equal([3, 4], (await CashflowAsync(c)).GetProperty("vat").EnumerateArray().Select(v => v.GetProperty("quarter").GetInt32()));
        factory.Clock.SetDay(2026, 11, 1);
        Assert.Equal(["2026-4", "2027-1"],
            (await CashflowAsync(c)).GetProperty("vat").EnumerateArray().Select(v => $"{v.GetProperty("year").GetInt32()}-{v.GetProperty("quarter").GetInt32()}"));
        factory.Clock.Value = InsightsFactory.Start;

        // Een aangifte die je als gedaan markeerde, telt niet meer mee.
        (await c.PutAsJsonAsync("/api/tax/vat-return/filed", new { year = 2026, quarter = 3, filed = true })).EnsureSuccessStatusCode();
        Assert.Equal([4], (await CashflowAsync(c)).GetProperty("vat").EnumerateArray().Select(v => v.GetProperty("quarter").GetInt32()));

        // Met de KOR doe je geen aangifte.
        (await c.PutAsJsonAsync("/api/settings", Company("kor"))).EnsureSuccessStatusCode();
        var kor = await CashflowAsync(c);
        Assert.True(kor.GetProperty("kor").GetBoolean());
        Assert.Empty(kor.GetProperty("vat").EnumerateArray());
    }

    [Fact]
    public async Task Income_tax_follows_the_pot_and_the_provisional_terms()
    {
        var (c, _, customer) = await WorkspaceAsync("cash-belasting");
        var invoice = await SentOnAsync(c, customer, 100, 100, "2026-05-01");           // 10.000 zonder btw, betaald in mei
        (await c.PostAsJsonAsync($"/api/invoices/{invoice}/payments", new { amount = 12100m, date = "2026-05-20" })).EnsureSuccessStatusCode();
        static object Pot(string provisional, decimal amount, int months) =>
            new { wage = 0, wageTaxWithheld = 0, starter = false, provisional, provisionalAmount = amount, provisionalMonths = months, mode = "percentage", percentage = 30 };
        (await c.PutAsJsonAsync("/api/tax/reserve/settings?year=2026", Pot("per-maand", 300, 10))).EnsureSuccessStatusCode();   // maart t/m december
        (await c.PutAsJsonAsync("/api/tax/reserve/settings?year=2027", Pot("per-maand", 100, 12))).EnsureSuccessStatusCode();

        var pot = await c.GetFromJsonAsync<JsonElement>("/api/tax/reserve");
        var perMonth = Dec(pot, "perMonth");
        var total = Dec(pot, "total");
        Assert.True(perMonth > 0);
        var estimate = Math.Round(Math.Max(0, total - 1200) / 12, 2, MidpointRounding.AwayFromZero);
        Assert.True(estimate > 0);

        var data = await CashflowAsync(c);
        var tax = Items(data, "belasting");
        Assert.Equal(
            [("2026-10", -300m), ("2026-10", -perMonth), ("2026-11", -300m), ("2026-11", -perMonth), ("2026-12", -300m), ("2026-12", -perMonth),
             ("2027-01", -100m), ("2027-01", -estimate), ("2027-02", -100m), ("2027-02", -estimate), ("2027-03", -100m), ("2027-03", -estimate)],
            tax.Select(i => (Str(i, "month"), Dec(i, "amount"))));
        Assert.Equal("Termijn 8 van 10", Str(tax[0], "detail"));
        Assert.Equal("Termijn 10 van 10", Str(tax[4], "detail"));
        Assert.Equal("Termijn 1 van 12", Str(tax[6], "detail"));
        Assert.Contains("cijfers van 2026", Str(tax[7], "detail"));
        Assert.True(data.GetProperty("nextYearEstimate").GetBoolean());
        Assert.False(data.GetProperty("provisionalOnce").GetBoolean());

        // Een voorlopige aanslag in één keer: wanneer weten we niet, dus die staat er niet in. Is die hoger dan de schatting, dan
        // zet je volgend jaar niets extra opzij.
        (await c.PutAsJsonAsync("/api/tax/reserve/settings?year=2027", Pot("totaal", 50000, 12))).EnsureSuccessStatusCode();
        data = await CashflowAsync(c);
        Assert.True(data.GetProperty("provisionalOnce").GetBoolean());
        Assert.False(data.GetProperty("nextYearEstimate").GetBoolean());
        Assert.DoesNotContain(Items(data, "belasting"), i => Str(i, "month").StartsWith("2027"));
    }

    [Fact]
    public async Task Settings_fixed_costs_and_the_hint_from_recent_expenses()
    {
        var (c, ws, _) = await WorkspaceAsync("cash-instellingen");
        var empty = (await CashflowAsync(c)).GetProperty("settings");
        Assert.False(empty.GetProperty("saved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("bankBalance").ValueKind);
        Assert.Equal(1, empty.GetProperty("quoteSpreadMonths").GetInt32());

        // Een negatief saldo kan, afgerond op centen, met de dag van vandaag.
        var saved = await (await c.PutAsJsonAsync("/api/insights/cashflow/settings", Settings(-250.555m, 1500, 2))).JsonAsync();
        Assert.Equal(-250.56m, Dec(saved, "bankBalance"));
        Assert.StartsWith("2026-10-15", Str(saved, "bankBalanceDate"));
        var data = await CashflowAsync(c);
        Assert.Equal(Enumerable.Range(0, 6).Select(i => (D(2026, 10, 1).AddMonths(i).ToString("yyyy-MM"), -1500m)), Short(data, "vaste-kosten"));
        Assert.StartsWith("2026-10-15", Str(Items(data, "vaste-kosten")[0], "date"));

        // Hetzelfde saldo later opslaan houdt de datum; een ander saldo krijgt de nieuwe dag; leeg is geen saldo.
        factory.Clock.SetDay(2026, 10, 20);
        Assert.StartsWith("2026-10-15", Str(await (await c.PutAsJsonAsync("/api/insights/cashflow/settings", Settings(-250.56m, 1500, 2))).JsonAsync(), "bankBalanceDate"));
        Assert.StartsWith("2026-10-20", Str(await (await c.PutAsJsonAsync("/api/insights/cashflow/settings", Settings(1000, 1500, 2))).JsonAsync(), "bankBalanceDate"));
        var cleared = await (await c.PutAsJsonAsync("/api/insights/cashflow/settings", Settings(null, 0, 1))).JsonAsync();
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("bankBalance").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("bankBalanceDate").ValueKind);
        factory.Clock.Value = InsightsFactory.Start;
        Assert.Empty(Items(await CashflowAsync(c), "vaste-kosten"));

        async Task<string> Refused(object body)
        {
            var res = await c.PutAsJsonAsync("/api/insights/cashflow/settings", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return await res.ErrorAsync();
        }
        Assert.Contains("van 1 tot en met 6", await Refused(Settings(spread: 0)));
        Assert.Contains("van 1 tot en met 6", await Refused(Settings(spread: 7)));
        Assert.Contains("negatief", await Refused(Settings(fixedCosts: -1)));
        Assert.Contains("te groot", await Refused(Settings(bankBalance: -(Money.MaxAmount + 1))));
        Assert.Contains("te groot", await Refused(Settings(fixedCosts: Money.MaxAmount + 1)));

        // Het voorstel: leveranciers die in juli, augustus en september terugkomen. Investeringen, oktober en juni tellen niet mee.
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 30m, supplier: "Hosting BV", category: "hosting", date: "2026-07-03"));
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 30m, supplier: "hosting bv", category: "hosting", date: "2026-08-03"));
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 36m, supplier: "Hosting BV", category: "hosting", date: "2026-09-30"));
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 300m, supplier: "Coolblue", date: "2026-08-10"));
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 50m, supplier: "Hosting BV", category: "hosting", date: "2026-10-01"));
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 99m, supplier: "Hosting BV", category: "hosting", date: "2026-06-30"));
        var laptop = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 1800m, supplier: "Apple", date: "2026-08-20"))).GetProperty("id").GetInt32();
        var demo = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 40m, supplier: "Demo", date: "2026-07-20"))).GetProperty("id").GetInt32();
        Edit(ws, db => { db.Expenses.Single(e => e.Id == laptop).Investment = true; db.Expenses.Single(e => e.Id == demo).IsDemo = true; });

        var hint = (await CashflowAsync(c)).GetProperty("hint");
        Assert.StartsWith("2026-07-01", Str(hint, "from"));
        Assert.StartsWith("2026-09-30", Str(hint, "to"));
        var supplier = Assert.Single(hint.GetProperty("suppliers").EnumerateArray());
        Assert.Equal("Hosting BV", Str(supplier, "supplier"));
        Assert.Equal(32m, Dec(supplier, "average"));
        Assert.Equal(32m, Dec(hint, "suppliersTotal"));
        Assert.Equal(132m, Dec(hint, "averageExpenses"));                              // (30 + 30 + 36 + 300) / 3
        Assert.Equal(145.33m, Dec((await CashflowAsync(c, demo: true)).GetProperty("hint"), "averageExpenses"));
    }

    [Fact]
    public async Task Only_admins_each_workspace_its_own_and_read_only_blocks_saving()
    {
        var (admin, ws, customer) = await WorkspaceAsync("cash-rollen");
        await InvoiceAsync(admin, customer, "verzonden", 10, 100);
        (await admin.PutAsJsonAsync("/api/insights/cashflow/settings", Settings(5000, 750, 2))).EnsureSuccessStatusCode();
        var employeeEmail = $"cash-medewerker-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(admin, employeeEmail);
        var employee = await TestApi.LoginAsync(factory, employeeEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync("/api/insights/cashflow")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PutAsJsonAsync("/api/insights/cashflow/settings", Settings())).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await employee.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("cashflow").ValueKind);
        Assert.Equal(1210m, Dec((await admin.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("cashflow"), "expectedIn"));

        // Een andere werkruimte ziet niets van deze instellingen en facturen.
        var (other, otherWs, _) = await WorkspaceAsync("cash-buur");
        var theirs = await CashflowAsync(other);
        Assert.False(theirs.GetProperty("settings").GetProperty("saved").GetBoolean());
        Assert.Empty(theirs.GetProperty("items").EnumerateArray());
        Assert.Equal(0, WorkflowKit.InWorkspace(factory, otherWs, db => db.CashflowSettings.Count()));
        Assert.Equal(5000m, Dec((await CashflowAsync(admin)).GetProperty("settings"), "bankBalance"));

        // In de export.
        var export = JsonDocument.Parse(await (await admin.GetAsync("/api/workspace/export")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(750m, Dec(export.GetProperty("cashflowSettings"), "fixedCostsPerMonth"));

        // Alleen-lezen: bekijken mag, opslaan niet.
        await TestApi.ExpireTrialAsync(factory, ws);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/insights/cashflow")).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.PutAsJsonAsync("/api/insights/cashflow/settings", Settings())).StatusCode);
        Assert.Equal(5000m, Dec((await CashflowAsync(admin)).GetProperty("settings"), "bankBalance"));

        // Weg met de werkruimte.
        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "geheim123" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(delete)).StatusCode);
        Assert.Equal(0, WorkflowKit.InWorkspace(factory, ws, db => db.CashflowSettings.Count()));
    }

    [Fact]
    public void An_amount_is_split_in_equal_parts_without_losing_a_cent()
    {
        Assert.Equal([403.33m, 403.33m, 403.34m], Cashflow.Split(1210m, 3));
        Assert.Equal([-33.33m, -33.33m, -33.34m], Cashflow.Split(-100m, 3));
        Assert.Equal([10m], Cashflow.Split(10m, 1));
    }
}

// De migratie voor de cashflow-prognose voegt alleen een tabel toe: wat er al stond blijft staan.
public class CashflowMigrationTests : IDisposable
{
    const string Before = "20261014125141_InloggenInTweeStappen";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-migratie-cashflow-{Guid.NewGuid():N}.db");

    AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path}").Options, new Tenant { WorkspaceId = 1 });

    [Fact]
    public void Existing_data_survives_and_the_new_table_starts_empty()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        using (var con = new SqliteConnection($"Data Source={path}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 'Oud bedrijf', 'team');
                INSERT INTO TaxReserveYears (Id, WorkspaceId, Year, Wage, WageTaxWithheld, Starter, Provisional, ProvisionalAmount, ProvisionalMonths, Mode, Percentage, UpdatedAt)
                    VALUES (1, 1, 2026, '30000.0', '6000.0', 0, 'per-maand', '250.0', 12, 'berekend', '0.0', '2026-03-01 00:00:00');
                """;
            cmd.ExecuteNonQuery();
        }

        using (var db = Db())
        {
            db.GetService<IMigrator>().Migrate();
            Assert.Equal(30000m, db.TaxReserveYears.Single().Wage);
            Assert.Equal(250m, db.TaxReserveYears.Single().ProvisionalAmount);
            Assert.Empty(db.CashflowSettings);
            db.CashflowSettings.Add(new CashflowSettings { BankBalance = 1234.56m, FixedCostsPerMonth = 400 });
            db.SaveChanges();
            var row = db.CashflowSettings.Single();
            Assert.Equal(1, row.WorkspaceId);
            Assert.Equal(1, row.QuoteSpreadMonths);
            Assert.Equal(1234.56m, row.BankBalance);
        }
    }

    public void Dispose()
    {
        TestDatabase.Delete(path);
    }
}
