using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Invoicing;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Vaste kosten (abonnementen): per periode precies één uitgave, ook na een storing en met rondes tegelijk, met de btw zoals bij
// een gewone uitgave. De bank koppelt het liefst aan de verwachte uitgave, de cashflow rekent ermee, en alles blijft in de eigen
// werkruimte. De klok staat op maandag 12 oktober 2026.
public class RecurringExpenseTests(RecurringFactory factory) : IClassFixture<RecurringFactory>
{
    static DateTime D(int y, int m, int d) => new(y, m, d);
    static string Mail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    static object Body(string supplier = "Adobe", decimal amount = 60.50m, string start = "2026-10-01", string interval = "maand", string action = "concept",
        string? end = null, decimal? vatRate = 21, bool reverseCharge = false, string? supplierRegion = null, int? projectId = null, int? customerId = null,
        string? description = "Creative Cloud", string category = "software", int? fromExpenseId = null) => new
    {
        supplier, description, category, amount, vatRate, reverseCharge, supplierRegion, projectId, customerId, interval, action,
        startDate = start, endDate = end, fromExpenseId,
    };

    async Task<(HttpClient C, int Ws)> ReadyAsync(string prefix)
    {
        factory.Clock.SetDay(2026, 10, 12);
        var c = await InvoiceKit.ReadyAsync(factory, prefix);
        return (c, await TestApi.WorkspaceIdAsync(c));
    }

    static async Task<JsonElement> CreateAsync(HttpClient c, object body)
    {
        var res = await c.PostAsJsonAsync("/api/recurring-expenses", body);
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await res.JsonAsync();
    }

    static async Task<string> RefusedAsync(HttpClient c, object body, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        var res = await c.PostAsJsonAsync("/api/recurring-expenses", body);
        Assert.Equal(status, res.StatusCode);
        return await res.ErrorAsync();
    }

    static Task<JsonElement> OverviewAsync(HttpClient c) => c.GetFromJsonAsync<JsonElement>("/api/recurring-expenses");

    static Task<JsonElement> OneAsync(HttpClient c, int id) => c.GetFromJsonAsync<JsonElement>($"/api/recurring-expenses/{id}");

    async Task<int> RunJobAsync(int workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = workspaceId;
        return await scope.ServiceProvider.GetRequiredService<RecurringExpenseService>().RunDueAsync();
    }

    List<Expense> Made(int ws, int id) => WorkflowKit.InWorkspace(factory, ws, db =>
        db.Expenses.Where(e => e.RecurringExpenseId == id).OrderBy(e => e.RecurringPeriodStart).ToList());

    List<string> Log(int ws) => WorkflowKit.InWorkspace(factory, ws, db => db.Activities.Select(a => a.Text).ToList());

    void Edit(int ws, Action<AppDb> change) => WorkflowKit.InWorkspace(factory, ws, db => { change(db); return db.SaveChanges(); });

    [Fact]
    public async Task A_due_period_becomes_one_draft_expense_that_you_can_mark_as_checked()
    {
        var (c, ws) = await ReadyAsync("vast-eerste");
        var t = await CreateAsync(c, Body());
        var id = t.GetProperty("id").GetInt32();

        var e = Assert.Single(Made(ws, id));
        Assert.True(e.NeedsReview);
        Assert.Equal(D(2026, 10, 1), e.Date);
        Assert.Equal(D(2026, 10, 1), e.RecurringPeriodStart);
        Assert.Equal("Creative Cloud, oktober 2026", e.Description);
        Assert.Equal((60.50m, 10.50m, "software"), (e.Amount, e.VatAmount, e.Category));
        Assert.Contains(Log(ws), l => l.Contains("klaargezet als concept door je vaste kost"));

        var after = await OneAsync(c, id);
        Assert.Equal("2026-11-01T00:00:00", after.GetProperty("nextRunDate").GetString());
        Assert.Equal("november 2026", after.GetProperty("nextPeriod").GetString());
        Assert.Equal(1, after.GetProperty("expenseCount").GetInt32());
        Assert.True(after.GetProperty("locked").GetBoolean());
        Assert.Equal("actief", after.GetProperty("state").GetString());

        // Nog een ronde op dezelfde dag doet niets.
        Assert.Equal(0, await RunJobAsync(ws));
        Assert.Single(Made(ws, id));

        // In de lijst met kosten: de periode en dat hij nog nagekeken moet worden. Ook in het overzicht.
        var listed = (await c.GetFromJsonAsync<List<JsonElement>>("/api/expenses?from=2026-10-01&to=2026-10-31"))!.Single();
        Assert.Equal(id, listed.GetProperty("recurringExpenseId").GetInt32());
        Assert.Equal("oktober 2026", listed.GetProperty("recurringPeriod").GetString());
        Assert.True(listed.GetProperty("needsReview").GetBoolean());
        var overview = await OverviewAsync(c);
        Assert.Equal(e.Id, Assert.Single(overview.GetProperty("toReview").EnumerateArray()).GetProperty("id").GetInt32());
        Assert.Equal(60.50m, overview.GetProperty("perMonth").GetDecimal());
        Assert.Equal(726m, overview.GetProperty("perYear").GetDecimal());

        // De btw-aangifte waarschuwt zolang hij niet nagekeken is; Klopt haalt hem van de lijst.
        Assert.Equal(1, (await c.GetFromJsonAsync<JsonElement>("/api/tax/vat-return?year=2026&quarter=4")).GetProperty("toReview").GetInt32());
        var checkedExpense = await (await c.PostAsync($"/api/expenses/{e.Id}/reviewed", null)).JsonAsync();
        Assert.False(checkedExpense.GetProperty("needsReview").GetBoolean());
        Assert.Empty((await OverviewAsync(c)).GetProperty("toReview").EnumerateArray());
        Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>("/api/tax/vat-return?year=2026&quarter=4")).GetProperty("toReview").GetInt32());

        // Opslaan vanuit het formulier is ook nakijken.
        factory.Clock.SetDay(2026, 11, 2);
        Assert.Equal(1, await RunJobAsync(ws));
        var november = Made(ws, id)[1];
        Assert.True(november.NeedsReview);
        (await c.PutAsJsonAsync($"/api/expenses/{november.Id}", ExpenseKit.Body(amount: 62m, supplier: "Adobe", category: "software", date: "2026-11-01"))).EnsureSuccessStatusCode();
        Assert.False(Made(ws, id)[1].NeedsReview);
    }

    [Fact]
    public async Task After_downtime_each_missed_period_is_made_once_and_a_long_outage_skips_the_oldest()
    {
        var (c, ws) = await ReadyAsync("vast-storing");
        var id = (await CreateAsync(c, Body(start: "2026-11-01"))).GetProperty("id").GetInt32();
        Assert.Empty(Made(ws, id));

        factory.Clock.SetDay(2027, 1, 15);
        Assert.Equal(3, await RunJobAsync(ws));
        Assert.Equal([D(2026, 11, 1), D(2026, 12, 1), D(2027, 1, 1)], Made(ws, id).Select(e => e.RecurringPeriodStart!.Value));
        Assert.Equal(["Creative Cloud, november 2026", "Creative Cloud, december 2026", "Creative Cloud, januari 2027"], Made(ws, id).Select(e => e.Description));
        Assert.Equal(0, await RunJobAsync(ws));
        Assert.Equal(3, Made(ws, id).Count);

        // Een halfjaar stil: hooguit drie perioden inhalen, de rest overslaan met een regel in het logboek.
        factory.Clock.SetDay(2027, 9, 10);
        Assert.Equal(3, await RunJobAsync(ws));
        Assert.Equal([D(2027, 7, 1), D(2027, 8, 1), D(2027, 9, 1)], Made(ws, id).Skip(3).Select(e => e.RecurringPeriodStart!.Value));
        Assert.Contains(Log(ws), l => l.Contains("5 gemiste perioden (februari t/m juni 2027) zijn overgeslagen"));
        Assert.Equal(0, await RunJobAsync(ws));

        // Een nieuwe vaste kost die meteen meer dan drie perioden zou inhalen, kan niet.
        Assert.Contains("meer dan 3 uitgaven", await RefusedAsync(c, Body(supplier: "Oud", start: "2027-01-01")));
    }

    [Fact]
    public async Task Concurrent_runs_and_booking_clicks_make_each_period_exactly_once()
    {
        var (c, ws) = await ReadyAsync("vast-tegelijk");
        var id = (await CreateAsync(c, Body(start: "2026-11-01"))).GetProperty("id").GetInt32();
        factory.Clock.SetDay(2027, 1, 2);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => RunJobAsync(ws))));
        Assert.Equal(3, results.Sum());
        Assert.Equal([D(2026, 11, 1), D(2026, 12, 1), D(2027, 1, 1)], Made(ws, id).Select(e => e.RecurringPeriodStart!.Value));

        // Boeken bij alleen herinneren, vijf keer tegelijk: elke periode hooguit één keer, nooit een tweede ernaast.
        var remind = (await CreateAsync(c, Body(supplier: "KPN", amount: 30.25m, start: "2026-12-01", action: "herinneren"))).GetProperty("id").GetInt32();
        Assert.Empty(Made(ws, remind));
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SafeSend(() => c.PostAsync($"/api/recurring-expenses/{remind}/book", null))));
        Assert.All(responses, r => Assert.True(r?.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, r?.StatusCode.ToString()));
        var ok = responses.Count(r => r?.StatusCode == HttpStatusCode.OK);
        var periods = Made(ws, remind).Select(e => e.RecurringPeriodStart!.Value).ToList();
        Assert.Equal(periods.Distinct().Count(), periods.Count);
        Assert.Equal(ok, periods.Count);
        Assert.InRange(ok, 1, 2);
        Assert.All(Made(ws, remind), e => Assert.False(e.NeedsReview));

        // En de database zelf weigert een tweede uitgave voor dezelfde periode.
        Assert.Throws<DbUpdateException>(() => WorkflowKit.InWorkspace(factory, ws, db =>
        {
            db.Expenses.Add(new Expense { Date = D(2026, 11, 3), Supplier = "Adobe", Category = "software", Amount = 1, RecurringExpenseId = id, RecurringPeriodStart = D(2026, 11, 1) });
            return db.SaveChanges();
        }));
    }

    [Fact]
    public async Task The_end_date_stops_the_subscription()
    {
        var (c, ws) = await ReadyAsync("vast-einde");
        var id = (await CreateAsync(c, Body(start: "2026-10-01", end: "2026-12-15"))).GetProperty("id").GetInt32();
        Assert.Single(Made(ws, id));

        factory.Clock.SetDay(2027, 3, 1);
        Assert.Equal(2, await RunJobAsync(ws));
        Assert.Equal([D(2026, 10, 1), D(2026, 11, 1), D(2026, 12, 1)], Made(ws, id).Select(e => e.RecurringPeriodStart!.Value));
        var t = await OneAsync(c, id);
        Assert.Equal("afgelopen", t.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, t.GetProperty("nextRunDate").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/recurring-expenses/{id}/book", null)).StatusCode);
        Assert.Equal(0, await RunJobAsync(ws));
        // Afgelopen vaste kosten tellen niet mee in het totaal per maand.
        Assert.Equal(0m, (await OverviewAsync(c)).GetProperty("perMonth").GetDecimal());

        // De einddatum opschuiven laat hem verder lopen vanaf de volgende periode.
        (await c.PutAsJsonAsync($"/api/recurring-expenses/{id}", Body(start: "2026-10-01", end: "2027-02-28"))).EnsureSuccessStatusCode();
        Assert.Equal([D(2026, 10, 1), D(2026, 11, 1), D(2026, 12, 1), D(2027, 1, 1), D(2027, 2, 1)], Made(ws, id).Select(e => e.RecurringPeriodStart!.Value));
        Assert.Equal("De einddatum ligt voor de startdatum.", await RefusedAsync(c, Body(start: "2026-12-01", end: "2026-11-30")));
    }

    [Fact]
    public async Task Vat_and_reverse_charge_follow_the_rules_of_an_expense()
    {
        var (c, ws) = await ReadyAsync("vast-btw");
        // Buiten de EU, btw verlegd: het bedrag is zonder btw en je geeft 21% zelf aan in rubriek 4a.
        var github = (await CreateAsync(c, Body(supplier: "GitHub, Inc.", amount: 23m, reverseCharge: true, supplierRegion: "buiten-eu"))).GetProperty("id").GetInt32();
        var g = Assert.Single(Made(ws, github));
        Assert.Equal((23m, 4.83m, true, "buiten-eu"), (g.Amount, g.VatAmount, g.ReverseCharge, g.SupplierRegion));
        // Een 9%-tarief en geen btw.
        var nine = (await CreateAsync(c, Body(supplier: "Vakblad", amount: 109m, vatRate: 9))).GetProperty("id").GetInt32();
        Assert.Equal(9m, Assert.Single(Made(ws, nine)).VatAmount);
        var none = (await CreateAsync(c, Body(supplier: "Verzekeraar", amount: 40m, vatRate: null, category: "overig"))).GetProperty("id").GetInt32();
        Assert.Equal((0m, (decimal?)null), (Assert.Single(Made(ws, none)).VatAmount, Made(ws, none)[0].VatRate));

        var vat = await c.GetFromJsonAsync<JsonElement>("/api/tax/vat-return?year=2026&quarter=4");
        var r4a = vat.GetProperty("rubrics").EnumerateArray().Single(r => r.GetProperty("code").GetString() == "4a");
        Assert.Equal((23m, 4.83m), (r4a.GetProperty("base").GetDecimal(), r4a.GetProperty("vat").GetDecimal()));
        Assert.Equal(3, vat.GetProperty("toReview").GetInt32());

        // Dezelfde controles als bij een uitgave.
        Assert.Contains("Bij btw verlegd", await RefusedAsync(c, Body(reverseCharge: true, supplierRegion: "eu", vatRate: 0)));
        Assert.Contains("Kies waar de leverancier zit", await RefusedAsync(c, Body(reverseCharge: true, supplierRegion: "mars")));
        Assert.Contains("Kies 21%, 9%, 0% of geen btw", await RefusedAsync(c, Body(vatRate: 19)));
        Assert.Contains("Vul het bedrag in", await RefusedAsync(c, Body(amount: -5)));
        Assert.Contains("Vul de leverancier in", await RefusedAsync(c, Body(supplier: " ")));
        Assert.Contains("Kies hoe vaak", await RefusedAsync(c, Body(interval: "week")));
        Assert.Contains("Kies of Potel", await RefusedAsync(c, Body(action: "versturen")));
        Assert.Contains("Kies een categorie", await RefusedAsync(c, Body(category: "snoep")));
    }

    [Fact]
    public async Task Remind_mode_logs_once_per_period_and_booking_or_skipping_moves_on_with_a_hint_only_on_a_clear_signal()
    {
        var (c, ws) = await ReadyAsync("vast-herinner");
        var customer = await CustomerAsync(c, "Klant");
        var project = await ProjectAsync(c, customer, 90, "Webshop");
        var id = (await CreateAsync(c, Body(supplier: "Odido", amount: 30.25m, action: "herinneren", projectId: project))).GetProperty("id").GetInt32();
        Assert.Empty(Made(ws, id));
        await RunJobAsync(ws);
        await RunJobAsync(ws);
        Assert.Single(Log(ws), l => l.StartsWith("Herinnering: je vaste kost bij Odido (oktober 2026)"));

        var t = await OneAsync(c, id);
        Assert.Equal("wacht", t.GetProperty("state").GetString());
        Assert.Equal("oktober 2026", t.GetProperty("due").GetProperty("period").GetString());
        Assert.Equal(customer, t.GetProperty("customerId").GetInt32());
        Assert.Equal(JsonValueKind.Null, t.GetProperty("hint").ValueKind);

        var booked = await (await c.PostAsync($"/api/recurring-expenses/{id}/book", null)).JsonAsync();
        var e = Assert.Single(Made(ws, id));
        Assert.False(e.NeedsReview);
        Assert.Equal((project, customer), (e.ProjectId!.Value, e.CustomerId!.Value));
        Assert.Equal("actief", booked.GetProperty("recurring").GetProperty("state").GetString());
        // De volgende periode is nog niet aan de beurt.
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/recurring-expenses/{id}/book", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/recurring-expenses/{id}/skip", null)).StatusCode);

        // Eén keer overslaan zegt nog niets; twee keer na elkaar wel.
        factory.Clock.SetDay(2026, 11, 2);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/recurring-expenses/{id}/skip", null)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await OneAsync(c, id)).GetProperty("hint").ValueKind);
        factory.Clock.SetDay(2026, 12, 2);
        var skipped = await (await c.PostAsync($"/api/recurring-expenses/{id}/skip", null)).JsonAsync();
        Assert.Contains("laatste 2 perioden overgeslagen", skipped.GetProperty("hint").GetString());
        Assert.Single(Made(ws, id));
        Assert.Contains(Log(ws), l => l.Contains("Vaste kost bij Odido: december 2026 overgeslagen"));

        // Weer boeken zet de teller terug; een afgerond project is ook een duidelijk teken.
        factory.Clock.SetDay(2027, 1, 2);
        (await c.PostAsync($"/api/recurring-expenses/{id}/book", null)).EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null, (await OneAsync(c, id)).GetProperty("hint").ValueKind);
        Edit(ws, db => db.Projects.Single(p => p.Id == project).Status = "afgerond");
        Assert.Contains("Het project Webshop is afgerond", (await OneAsync(c, id)).GetProperty("hint").GetString());
    }

    [Fact]
    public async Task An_expense_you_entered_yourself_is_linked_instead_of_doubled()
    {
        var (c, ws) = await ReadyAsync("vast-adopteren");
        var own = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 61.20m, supplier: "ADOBE", category: "software", date: "2026-10-03"))).GetProperty("id").GetInt32();
        var other = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 60.50m, supplier: "Coolblue", date: "2026-10-02"))).GetProperty("id").GetInt32();
        var id = (await CreateAsync(c, Body())).GetProperty("id").GetInt32();

        var e = Assert.Single(Made(ws, id));
        Assert.Equal(own, e.Id);
        Assert.Equal((61.20m, D(2026, 10, 1)), (e.Amount, e.RecurringPeriodStart!.Value));
        Assert.False(e.NeedsReview);
        Assert.Equal(2, WorkflowKit.InWorkspace(factory, ws, db => db.Expenses.Count()));
        Assert.Null(WorkflowKit.InWorkspace(factory, ws, db => db.Expenses.Single(x => x.Id == other).RecurringExpenseId));
        Assert.Contains(Log(ws), l => l.Contains("gekoppeld aan je vaste kost (oktober 2026); er is geen tweede uitgave gemaakt"));
    }

    [Fact]
    public async Task A_subscription_can_start_from_an_existing_expense()
    {
        var (c, ws) = await ReadyAsync("vast-van-uitgave");
        var source = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 12.10m, supplier: "Spotify", category: "software", date: "2026-09-05"))).GetProperty("id").GetInt32();

        var preview = await (await c.PostAsJsonAsync("/api/recurring-expenses/preview", Body(supplier: "Spotify", amount: 12.10m, start: "2026-01-01", fromExpenseId: source))).JsonAsync();
        Assert.StartsWith("2026-10-05", preview.GetProperty("periods")[0].GetProperty("from").GetString());
        Assert.Equal(1, preview.GetProperty("dueNow").GetInt32());

        var t = await CreateAsync(c, Body(supplier: "Spotify", amount: 12.10m, start: "2026-01-01", fromExpenseId: source));
        var id = t.GetProperty("id").GetInt32();
        Assert.StartsWith("2026-09-05", t.GetProperty("startDate").GetString());
        // De uitgave zelf is de eerste periode; de periode van 5 oktober is al aan de beurt en staat klaar.
        Assert.Equal([(source, D(2026, 9, 5)), (Made(ws, id)[1].Id, D(2026, 10, 5))], Made(ws, id).Select(e => (e.Id, e.RecurringPeriodStart!.Value)));
        Assert.False(Made(ws, id)[0].NeedsReview);
        Assert.True(Made(ws, id)[1].NeedsReview);
        Assert.Equal("2026-11-05T00:00:00", (await OneAsync(c, id)).GetProperty("nextRunDate").GetString());

        // Nog een keer van dezelfde uitgave kan niet, en ook niet van een investering of een uitgave uit een andere werkruimte.
        Assert.Contains("hoort al bij een vaste kost", await RefusedAsync(c, Body(supplier: "Spotify", fromExpenseId: source)));
        var laptop = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 1800m, date: "2026-09-01"))).GetProperty("id").GetInt32();
        Edit(ws, db => db.Expenses.Single(e => e.Id == laptop).Investment = true);
        Assert.Contains("investering is geen vaste kost", await RefusedAsync(c, Body(fromExpenseId: laptop)));
        var (theirs, _) = await ReadyAsync("vast-van-uitgave-buur");
        var foreign = (await ExpenseKit.CreateAsync(theirs, ExpenseKit.Body(date: "2026-09-01"))).GetProperty("id").GetInt32();
        Assert.Contains("eigen werkruimte", await RefusedAsync(c, Body(fromExpenseId: foreign)));
        Assert.Null(WorkflowKit.InWorkspace(factory, await TestApi.WorkspaceIdAsync(theirs), db => db.Expenses.Single(e => e.Id == foreign).RecurringExpenseId));
    }

    [Fact]
    public async Task The_bank_prefers_the_expected_period_expense_and_books_open_periods_once()
    {
        var (c, ws) = await ReadyAsync("vast-bank");
        var adobe = (await CreateAsync(c, Body(supplier: "Adobe Systems", amount: 60.50m))).GetProperty("id").GetInt32();
        var draft = Assert.Single(Made(ws, adobe));
        // Een gewone uitgave met precies het bedrag uit de bank, bij een andere leverancier.
        var plain = (await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 61.20m, supplier: "Coolblue", date: "2026-10-02"))).GetProperty("id").GetInt32();
        var kpn = (await CreateAsync(c, Body(supplier: "KPN", amount: 30.25m, action: "herinneren", category: "telefoon"))).GetProperty("id").GetInt32();

        await BankKit.ImportedAsync(c, StatementKit.Camt(
            StatementKit.Entry(61.20m, "DBIT", "2026-10-02", "ADOBE SYSTEMS", StatementKit.ShopIban, "Creative Cloud", "REF-A"),
            StatementKit.Entry(30.25m, "DBIT", "2026-10-03", "KPN B.V.", null, "Abonnement", "REF-K"),
            StatementKit.Entry(30.25m, "DBIT", "2026-10-04", "KPN B.V.", null, "Dubbel afgeschreven", "REF-K2"),
            StatementKit.Entry(30.25m, "DBIT", "2026-12-20", "KPN B.V.", null, "Veel later", "REF-K3")));
        var list = await BankKit.ListAsync(c);

        // De verwachte uitgave van de vaste kost staat bovenaan, ook al wijkt het bedrag iets af (een wisselkoers).
        var adobeRow = BankKit.Row(list, -61.20m);
        var proposals = BankKit.Proposals(adobeRow);
        Assert.Equal(("expense", draft.Id, "vaste kost en leverancier"), (proposals[0].GetProperty("kind").GetString(), proposals[0].GetProperty("expenseId").GetInt32(), proposals[0].GetProperty("reason").GetString()));
        Assert.Equal("oktober 2026", proposals[0].GetProperty("period").GetString());
        Assert.Equal(60.50m, proposals[0].GetProperty("expenseAmount").GetDecimal());
        Assert.Contains(proposals, p => p.GetProperty("kind").GetString() == "expense" && p.GetProperty("expenseId").GetInt32() == plain);
        // Koppelen neemt het bedrag uit de bank over, rekent de btw opnieuw uit en dan is het concept nagekeken.
        var linked = await (await c.PostAsJsonAsync($"/api/bank/transactions/{BankKit.Id(adobeRow)}/expense", new { expenseId = draft.Id })).JsonAsync();
        Assert.Contains("is nu", linked.GetProperty("message").GetString());
        Assert.Contains("61,20", linked.GetProperty("message").GetString());
        var after = Made(ws, adobe).Single();
        Assert.Equal((61.20m, 10.62m, false), (after.Amount, after.VatAmount, after.NeedsReview));

        // Een periode die nog niet geboekt is: boeken vanuit de bank, met bedrag en datum uit de bank. Eén keer per periode.
        var kpnRows = BankKit.Rows(await BankKit.ListAsync(c)).Where(r => r.GetProperty("amount").GetDecimal() == -30.25m).OrderBy(r => r.GetProperty("date").GetString()).ToList();
        var first = kpnRows[0];
        var proposal = BankKit.Proposals(first).First();
        Assert.Equal(("subscription", kpn), (proposal.GetProperty("kind").GetString(), proposal.GetProperty("recurringExpenseId").GetInt32()));
        Assert.StartsWith("2026-10-01", proposal.GetProperty("periodStart").GetString());
        var booked = await c.PostAsJsonAsync($"/api/bank/transactions/{BankKit.Id(first)}/subscription", new { recurringExpenseId = kpn, periodStart = "2026-10-01" });
        Assert.True(booked.StatusCode == HttpStatusCode.OK, await booked.Content.ReadAsStringAsync());
        var k = Assert.Single(Made(ws, kpn));
        Assert.Equal((30.25m, D(2026, 10, 3), D(2026, 10, 1), false), (k.Amount, k.Date, k.RecurringPeriodStart!.Value, k.NeedsReview));
        Assert.Equal(k.Id, WorkflowKit.InWorkspace(factory, ws, db => db.BankTransactions.Single(b => b.Id == BankKit.Id(first)).ExpenseId));
        // Dezelfde periode met de tweede afschrijving: die is al geboekt. En de regel zelf is al gekoppeld.
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/bank/transactions/{BankKit.Id(kpnRows[1])}/subscription", new { recurringExpenseId = kpn, periodStart = "2026-10-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/bank/transactions/{BankKit.Id(first)}/subscription", new { recurringExpenseId = kpn, periodStart = "2026-11-01" })).StatusCode);
        Assert.Single(Made(ws, kpn));
        // Een afschrijving ver na de periode hoort er niet bij.
        factory.Clock.SetDay(2026, 12, 21);
        var late = await c.PostAsJsonAsync($"/api/bank/transactions/{BankKit.Id(kpnRows[2])}/subscription", new { recurringExpenseId = kpn, periodStart = "2026-11-01" });
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.Contains("valt niet in de periode", await late.ErrorAsync());
        Assert.Single(Made(ws, kpn));
    }

    [Fact]
    public async Task A_bank_row_can_become_a_subscription_with_itself_as_the_first_period()
    {
        var (c, ws) = await ReadyAsync("vast-bank-nieuw");
        await BankKit.ImportedAsync(c, StatementKit.Camt(StatementKit.Entry(15m, "DBIT", "2026-09-14", "Notion Labs", null, "Notion Plus", "REF-N")));
        var row = BankKit.Rows(await BankKit.ListAsync(c)).Single();
        var res = await c.PostAsJsonAsync($"/api/bank/transactions/{BankKit.Id(row)}/create-subscription", new
        {
            supplier = "Notion Labs", description = "Notion Plus", category = "software", vatRate = 21, reverseCharge = true, supplierRegion = "buiten-eu",
            interval = "maand", action = "concept",
        });
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var id = (await res.JsonAsync()).GetProperty("recurringExpenseId").GetInt32();
        var e = Assert.Single(Made(ws, id));
        Assert.Equal((15m, 3.15m, D(2026, 9, 14), D(2026, 9, 14), true), (e.Amount, e.VatAmount, e.Date, e.RecurringPeriodStart!.Value, e.ReverseCharge));
        Assert.Equal("Notion Plus, 14 september t/m 13 oktober 2026", e.Description);
        Assert.Equal(e.Id, WorkflowKit.InWorkspace(factory, ws, db => db.BankTransactions.Single().ExpenseId));
        var t = await OneAsync(c, id);
        Assert.Equal((15m, "2026-10-14T00:00:00"), (t.GetProperty("amount").GetDecimal(), t.GetProperty("nextRunDate").GetString()));

        // Nog een keer van dezelfde regel kan niet: hij is al gekoppeld.
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/bank/transactions/{BankKit.Id(row)}/create-subscription", new { supplier = "Notion", category = "software", vatRate = 21 })).StatusCode);
        Assert.Single(WorkflowKit.InWorkspace(factory, ws, db => db.RecurringExpenses.ToList()));

        // Twee dagen later is de volgende periode aan de beurt en staat hij klaar.
        factory.Clock.SetDay(2026, 10, 14);
        Assert.Equal(1, await RunJobAsync(ws));
        Assert.Equal(15m, Made(ws, id)[1].Amount);
    }

    [Fact]
    public async Task The_cashflow_uses_subscriptions_and_keeps_your_own_fixed_costs_as_an_extra_line()
    {
        var (c, ws) = await ReadyAsync("vast-cashflow");
        factory.Clock.SetDay(2026, 10, 15);
        // Van juli tot en met september elke maand Adobe; dat is straks een vaste kost en staat dan niet meer in het voorstel.
        foreach (var month in new[] { 7, 8, 9 })
        {
            await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 60.50m, supplier: "Adobe", category: "software", date: $"2026-0{month}-01"));
            await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 20m, supplier: "Hosting BV", category: "hosting", date: $"2026-0{month}-10"));
        }
        await CreateAsync(c, Body(supplier: "Adobe", amount: 121m, start: "2026-11-01"));
        await CreateAsync(c, Body(supplier: "JetBrains", amount: 1210m, start: "2027-02-01", interval: "jaar", reverseCharge: true, supplierRegion: "eu"));
        // Een vaste kost die in oktober al geboekt is, staat niet nog eens in oktober.
        await CreateAsync(c, Body(supplier: "Odido", amount: 30.25m, start: "2026-10-01"));
        var ended = (await CreateAsync(c, Body(supplier: "Gestopt", amount: 50m, start: "2026-10-01", end: "2026-10-31"))).GetProperty("id").GetInt32();
        Edit(ws, db => db.RecurringExpenses.Add(new RecurringExpense
        {
            IsDemo = true, Supplier = "Voorbeeld", Category = "software", Amount = 999m, Interval = "maand", StartDate = D(2026, 11, 1),
            NextPeriodStart = D(2026, 11, 1), NextRunDate = D(2026, 11, 1),
        }));
        (await c.PutAsJsonAsync("/api/insights/cashflow/settings", new { bankBalance = 5000, fixedCostsPerMonth = 100, quoteSpreadMonths = 1 })).EnsureSuccessStatusCode();

        var data = await c.GetFromJsonAsync<JsonElement>("/api/insights/cashflow");
        var items = data.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("source").GetString() == "vaste-kosten").ToList();
        (string, string, decimal) Short(JsonElement i) => (i.GetProperty("label").GetString()!, i.GetProperty("month").GetString()!, i.GetProperty("amount").GetDecimal());
        var subscriptions = items.Where(i => i.GetProperty("label").GetString() != "Overige vaste kosten").Select(Short).ToList();
        Assert.Equal(
        [
            ("Adobe", "2026-11", -121m), ("Odido", "2026-11", -30.25m), ("Adobe", "2026-12", -121m), ("Odido", "2026-12", -30.25m),
            ("Adobe", "2027-01", -121m), ("Odido", "2027-01", -30.25m), ("Adobe", "2027-02", -121m), ("JetBrains", "2027-02", -1210m), ("Odido", "2027-02", -30.25m),
            ("Adobe", "2027-03", -121m), ("Odido", "2027-03", -30.25m),
        ], subscriptions.OrderBy(i => i.Item2).ThenBy(i => i.Item1).ToList());
        Assert.All(items.Where(i => i.GetProperty("label").GetString() != "Overige vaste kosten"), i => Assert.Equal("/kosten?tab=vast", i.GetProperty("link").GetString()));
        // Je eigen bedrag blijft er elke maand naast staan, als aparte regel.
        Assert.Equal(6, items.Count(i => i.GetProperty("label").GetString() == "Overige vaste kosten" && i.GetProperty("amount").GetDecimal() == -100m));
        Assert.Equal(3, data.GetProperty("subscriptions").GetInt32());
        Assert.Equal(121m + 100.83m + 30.25m, data.GetProperty("subscriptionsPerMonth").GetDecimal());

        // De btw die je terugkrijgt op november en december gaat af van de btw in januari; verlegde btw scheelt niets.
        var q4 = data.GetProperty("vat").EnumerateArray().Single(v => v.GetProperty("quarter").GetInt32() == 4);
        Assert.Equal(-(21m + 21m + 5.25m + 5.25m), q4.GetProperty("parts").GetProperty("vaste-kosten").GetDecimal());

        // Het voorstel voor je overige vaste kosten slaat over wat al een vaste kost is.
        var hint = data.GetProperty("hint");
        Assert.Equal("Hosting BV", Assert.Single(hint.GetProperty("suppliers").EnumerateArray()).GetProperty("supplier").GetString());
        Assert.Equal(20m, hint.GetProperty("averageExpenses").GetDecimal());

        // Met voorbeelddata erbij telt de voorbeeldvaste kost wel mee.
        var demo = await c.GetFromJsonAsync<JsonElement>("/api/insights/cashflow?demo=true");
        Assert.Contains(demo.GetProperty("items").EnumerateArray(), i => i.GetProperty("label").GetString() == "Voorbeeld");
        Assert.DoesNotContain(items, i => i.GetProperty("label").GetString() is "Voorbeeld" or "Gestopt");
        Assert.NotEqual(0, ended);

        // Zonder vaste kosten heet je eigen regel gewoon Vaste kosten.
        var (other, _) = await ReadyAsync("vast-cashflow-zonder");
        factory.Clock.SetDay(2026, 10, 15);
        (await other.PutAsJsonAsync("/api/insights/cashflow/settings", new { bankBalance = 0, fixedCostsPerMonth = 100, quoteSpreadMonths = 1 })).EnsureSuccessStatusCode();
        var plain = await other.GetFromJsonAsync<JsonElement>("/api/insights/cashflow");
        Assert.All(plain.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("source").GetString() == "vaste-kosten"),
            i => Assert.Equal("Vaste kosten", i.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task Each_workspace_only_sees_and_links_its_own()
    {
        var (c, ws) = await ReadyAsync("vast-eigen");
        var id = (await CreateAsync(c, Body())).GetProperty("id").GetInt32();
        var (other, otherWs) = await ReadyAsync("vast-eigen-buur");
        var theirCustomer = await CustomerAsync(other, "Hun klant");
        var theirProject = await ProjectAsync(other, theirCustomer, 80, "Hun project");

        Assert.Empty((await OverviewAsync(other)).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/recurring-expenses/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/recurring-expenses/{id}", Body())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/recurring-expenses/{id}/book", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/recurring-expenses/{id}/skip", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/recurring-expenses/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/expenses/{Made(ws, id)[0].Id}/reviewed", null)).StatusCode);

        // Een project of klant uit een andere werkruimte kan niet.
        Assert.Contains("eigen werkruimte", await RefusedAsync(c, Body(projectId: theirProject)));
        Assert.Contains("eigen werkruimte", await RefusedAsync(c, Body(customerId: theirCustomer)));
        Assert.Equal("Kies een project uit je eigen werkruimte.",
            await (await c.PutAsJsonAsync($"/api/recurring-expenses/{id}", Body(projectId: theirProject))).ErrorAsync());

        // Een bankregel van de buren boekt niet op onze vaste kost.
        await BankKit.ImportedAsync(other, StatementKit.Camt(StatementKit.Entry(60.50m, "DBIT", "2026-11-01", "Adobe", null, "Creative Cloud", "REF-X")));
        var row = BankKit.Rows(await BankKit.ListAsync(other)).Single();
        Assert.DoesNotContain(BankKit.Proposals(row), p => p.GetProperty("kind").GetString() == "subscription");
        var res = await other.PostAsJsonAsync($"/api/bank/transactions/{BankKit.Id(row)}/subscription", new { recurringExpenseId = id, periodStart = "2026-11-01" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Single(Made(ws, id));
        Assert.Equal(0, WorkflowKit.InWorkspace(factory, otherWs, db => db.Expenses.Count()));
        // En de planner van de buren raakt onze vaste kost niet.
        factory.Clock.SetDay(2026, 11, 2);
        Assert.Equal(0, await RunJobAsync(otherWs));
        Assert.Single(Made(ws, id));
    }

    [Fact]
    public async Task Everyone_manages_subscriptions_but_only_an_admin_deletes_one_and_its_expenses_stay()
    {
        var (admin, ws) = await ReadyAsync("vast-rollen");
        var employeeEmail = Mail("vast-rollen-m");
        await TestApi.CreateUserAsync(admin, employeeEmail);
        var employee = await TestApi.LoginAsync(factory, employeeEmail);

        var id = (await CreateAsync(employee, Body(action: "herinneren"))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await employee.PutAsJsonAsync($"/api/recurring-expenses/{id}", Body(amount: 65m, action: "herinneren"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await employee.PostAsync($"/api/recurring-expenses/{id}/book", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await employee.GetAsync("/api/recurring-expenses")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.DeleteAsync($"/api/recurring-expenses/{id}")).StatusCode);

        // Na de eerste uitgave liggen startdatum en interval vast.
        var locked = await employee.PutAsJsonAsync($"/api/recurring-expenses/{id}", Body(start: "2026-10-05"));
        Assert.Equal(HttpStatusCode.BadRequest, locked.StatusCode);
        Assert.Contains("liggen daarom vast", await locked.ErrorAsync());

        var expense = Assert.Single(Made(ws, id)).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/recurring-expenses/{id}")).StatusCode);
        var kept = WorkflowKit.InWorkspace(factory, ws, db => db.Expenses.Single(e => e.Id == expense));
        Assert.Equal((65m, (int?)null, (DateTime?)null), (kept.Amount, kept.RecurringExpenseId, kept.RecurringPeriodStart));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/recurring-expenses/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_read_only_workspace_can_look_but_not_change_and_the_planner_waits()
    {
        var (c, ws) = await ReadyAsync("vast-alleen-lezen");
        var id = (await CreateAsync(c, Body(start: "2026-11-01"))).GetProperty("id").GetInt32();
        var remind = (await CreateAsync(c, Body(supplier: "KPN", action: "herinneren"))).GetProperty("id").GetInt32();
        await TestApi.ExpireTrialAsync(factory, ws);

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/recurring-expenses")).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/recurring-expenses", Body(supplier: "Nieuw"))).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PutAsJsonAsync($"/api/recurring-expenses/{id}", Body(amount: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsync($"/api/recurring-expenses/{remind}/book", null)).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsync($"/api/recurring-expenses/{remind}/skip", null)).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.DeleteAsync($"/api/recurring-expenses/{id}")).StatusCode);

        // De planner zet in een werkruimte die alleen-lezen is niets klaar.
        factory.Clock.SetDay(2026, 11, 2);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = ws;
            var job = scope.ServiceProvider.GetServices<IWorkspaceJob>().OfType<RecurringExpenseJob>().Single();
            await job.RunAsync(CancellationToken.None);
        }
        Assert.Empty(Made(ws, id));
        Assert.Equal(60.50m, (await OneAsync(c, id)).GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Demo_subscriptions_never_book_and_leave_with_the_demo_data_export_and_delete()
    {
        factory.Clock.SetDay(2026, 10, 12);
        var c = await TestApi.RegisterAsync(factory, Mail("vast-demo"));
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/workspace/demo-data", null)).StatusCode);
        (await c.PutAsJsonAsync("/api/settings", Company())).EnsureSuccessStatusCode();
        var ws = await TestApi.WorkspaceIdAsync(c);
        var demo = (await OverviewAsync(c)).GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(demo);
        Assert.All(demo, t => Assert.True(t.GetProperty("demo").GetBoolean()));
        var demoId = demo.First(t => t.GetProperty("supplier").GetString() == "Spaces Utrecht").GetProperty("id").GetInt32();
        // De voorbeelduitgaven van Spaces hangen al aan hun vaste kost, één per maand.
        var spaces = Made(ws, demoId);
        Assert.Equal(spaces.Count, spaces.Select(e => e.RecurringPeriodStart).Distinct().Count());
        Assert.Equal(new BusinessClock().Today.Month, spaces.Count);

        factory.Clock.SetDay(2026, 12, 15);
        Assert.Equal(0, await RunJobAsync(ws));
        Assert.Equal(spaces.Count, Made(ws, demoId).Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/recurring-expenses/{demoId}/book", null)).StatusCode);
        Assert.Contains("voorbeeld", await (await c.PostAsJsonAsync("/api/recurring-expenses", Body(fromExpenseId: spaces[0].Id))).ErrorAsync());

        // Een eigen vaste kost op een voorbeeldproject houdt dat project in leven als de voorbeelddata weggaat.
        var demoProject = WorkflowKit.InWorkspace(factory, ws, db => db.Projects.First(p => p.IsDemo).Id);
        var own = (await CreateAsync(c, Body(supplier: "Eigen", start: "2027-01-01", projectId: demoProject))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/workspace/demo-data")).StatusCode);
        var left = Assert.Single((await OverviewAsync(c)).GetProperty("items").EnumerateArray());
        Assert.Equal(own, left.GetProperty("id").GetInt32());
        Assert.True(WorkflowKit.InWorkspace(factory, ws, db => db.Projects.Any(p => p.Id == demoProject)));
        Assert.Equal(0, WorkflowKit.InWorkspace(factory, ws, db => db.Expenses.Count(e => e.RecurringExpenseId == demoId)));

        // In de export, en weg met de werkruimte.
        var export = JsonDocument.Parse(await (await c.GetAsync("/api/workspace/export")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Eigen", Assert.Single(export.GetProperty("recurringExpenses").EnumerateArray()).GetProperty("supplier").GetString());
        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "geheim123" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(delete)).StatusCode);
        Assert.Equal(0, WorkflowKit.InWorkspace(factory, ws, db => db.RecurringExpenses.IgnoreQueryFilters().Count(r => r.WorkspaceId == ws)));
    }

    [Theory]
    [InlineData("2026-09-24", true)]   // een week ervoor
    [InlineData("2026-09-23", false)]
    [InlineData("2026-11-15", true)]   // 45 dagen erna
    [InlineData("2026-11-16", false)]
    public void A_bank_payment_belongs_to_a_period_from_a_week_before_to_45_days_after(string bankDate, bool fits)
    {
        var p = RecurringPeriods.Period(D(2026, 10, 1), "maand", RecurringPeriodRules.InAdvance, 0);
        Assert.Equal(fits, RecurringExpenseService.BankFits(DateTime.Parse(bankDate), p));
    }
}

// De migratie voor vaste kosten voegt een tabel en drie kolommen toe: bestaande uitgaven blijven zoals ze waren.
public class RecurringExpenseMigrationTests : IDisposable
{
    const string Before = "20261015101744_CashflowPrognose";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-migratie-vaste-kosten-{Guid.NewGuid():N}.db");

    AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path}").Options, new Tenant { WorkspaceId = 1 });

    [Fact]
    public void Existing_expenses_survive_without_a_subscription()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        using (var con = new SqliteConnection($"Data Source={path}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 'Oud bedrijf', 'team');
                INSERT INTO Expenses (Id, WorkspaceId, IsDemo, Date, Supplier, Category, Amount, VatAmount, ReverseCharge, CreatedAt, Investment, PassengerCar)
                    VALUES (1, 1, 0, '2026-09-01 00:00:00', 'Adobe', 'software', '60.5', '10.5', 0, '2026-09-01 00:00:00', 0, 0);
                """;
            cmd.ExecuteNonQuery();
        }

        using (var db = Db())
        {
            db.GetService<IMigrator>().Migrate();
            var e = db.Expenses.Single();
            Assert.Equal((60.5m, 10.5m, (int?)null, (DateTime?)null, false), (e.Amount, e.VatAmount, e.RecurringExpenseId, e.RecurringPeriodStart, e.NeedsReview));
            Assert.Empty(db.RecurringExpenses);
            var t = new RecurringExpense { Supplier = "Adobe", Amount = 60.5m, StartDate = new DateTime(2026, 9, 1) };
            RecurringPeriods.Schedule(t, t.StartDate);
            db.RecurringExpenses.Add(t);
            db.SaveChanges();
            Assert.Equal(1, db.RecurringExpenses.Single().WorkspaceId);
        }
    }

    public void Dispose()
    {
        TestDatabase.Delete(path);
    }
}
