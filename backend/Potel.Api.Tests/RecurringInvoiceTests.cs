using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Invoicing;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Het rekenwerk van terugkerende facturen: perioden aan het eind van de maand, schrikkeljaren, kwartalen, jaren en de teksten.
public class RecurringPeriodTests
{
    static DateTime D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void Monthly_periods_from_the_31st_follow_the_end_of_each_month_without_drifting()
    {
        var start = D(2026, 1, 31);
        Assert.Equal(D(2026, 2, 28), RecurringPeriods.Start(start, "maand", 1));
        Assert.Equal(D(2026, 3, 31), RecurringPeriods.Start(start, "maand", 2));
        Assert.Equal(D(2026, 4, 30), RecurringPeriods.Start(start, "maand", 3));
        Assert.Equal(D(2026, 5, 31), RecurringPeriods.Start(start, "maand", 4));
        var first = RecurringPeriods.Period(start, "maand", "vooraf", 0);
        Assert.Equal((D(2026, 1, 31), D(2026, 2, 27), D(2026, 1, 31)), (first.From, first.To, first.RunDate));
        var second = RecurringPeriods.Period(start, "maand", "vooraf", 1);
        Assert.Equal((D(2026, 2, 28), D(2026, 3, 30)), (second.From, second.To));
    }

    [Fact]
    public void Leap_years_get_the_29th_of_february()
    {
        Assert.Equal(D(2028, 2, 29), RecurringPeriods.Start(D(2028, 1, 31), "maand", 1));
        Assert.Equal(D(2028, 2, 29), RecurringPeriods.Start(D(2027, 11, 30), "kwartaal", 1));
        // Een jaarfactuur vanaf 29 februari valt in gewone jaren op 28 februari en in schrikkeljaren weer op de 29e.
        var start = D(2028, 2, 29);
        Assert.Equal(D(2029, 2, 28), RecurringPeriods.Start(start, "jaar", 1));
        Assert.Equal(D(2032, 2, 29), RecurringPeriods.Start(start, "jaar", 4));
        Assert.Equal(D(2029, 2, 27), RecurringPeriods.Period(start, "jaar", "vooraf", 0).To);
    }

    [Fact]
    public void Quarters_and_years_cover_the_whole_period()
    {
        var q = RecurringPeriods.Period(D(2026, 10, 1), "kwartaal", "vooraf", 0);
        Assert.Equal((D(2026, 10, 1), D(2026, 12, 31)), (q.From, q.To));
        Assert.Equal("Q4 2026", RecurringPeriods.Label(q.From, q.To));
        var q2 = RecurringPeriods.Period(D(2026, 11, 30), "kwartaal", "vooraf", 1);
        Assert.Equal((D(2027, 2, 28), D(2027, 5, 29)), (q2.From, q2.To));
        var y = RecurringPeriods.Period(D(2026, 1, 1), "jaar", "vooraf", 0);
        Assert.Equal((D(2026, 1, 1), D(2026, 12, 31)), (y.From, y.To));
        Assert.Equal("2026", RecurringPeriods.Label(y.From, y.To));
    }

    [Fact]
    public void In_arrears_runs_the_day_after_the_period()
    {
        var p = RecurringPeriods.Period(D(2026, 10, 1), "maand", "achteraf", 0);
        Assert.Equal((D(2026, 10, 1), D(2026, 10, 31), D(2026, 11, 1)), (p.From, p.To, p.RunDate));
        var q = RecurringPeriods.Period(D(2026, 12, 1), "kwartaal", "achteraf", 0);
        Assert.Equal(D(2027, 3, 1), q.RunDate);
    }

    [Theory]
    [InlineData("2026-01-31", "maand")]
    [InlineData("2028-02-29", "jaar")]
    [InlineData("2026-11-30", "kwartaal")]
    [InlineData("2026-03-15", "maand")]
    public void Index_of_a_period_start_round_trips(string start, string interval)
    {
        var s = DateTime.Parse(start);
        for (var i = 0; i < 60; i++) Assert.Equal(i, RecurringPeriods.IndexOf(s, interval, RecurringPeriods.Start(s, interval, i)));
    }

    [Theory]
    [InlineData("2026-10-01", "2026-10-31", "oktober 2026")]
    [InlineData("2026-10-15", "2026-11-14", "15 oktober t/m 14 november 2026")]
    [InlineData("2026-12-15", "2027-01-14", "15 december 2026 t/m 14 januari 2027")]
    [InlineData("2026-12-01", "2027-02-28", "december 2026 t/m februari 2027")]
    [InlineData("2026-02-01", "2026-04-30", "februari t/m april 2026")]
    [InlineData("2027-01-01", "2027-12-31", "2027")]
    public void Periods_have_plain_dutch_labels(string from, string to, string label) =>
        Assert.Equal(label, RecurringPeriods.Label(DateTime.Parse(from), DateTime.Parse(to)));

    [Fact]
    public void Placeholders_are_filled_in_dutch_and_follow_capitals()
    {
        var from = D(2026, 10, 1);
        var to = D(2026, 10, 31);
        Assert.Equal("Hosting oktober 2026", RecurringPeriods.Fill("Hosting {maand} {jaar}", from, to));
        Assert.Equal("Oktober: onderhoud", RecurringPeriods.Fill("{Maand}: onderhoud", from, to));
        Assert.Equal("Retainer Q4 (oktober 2026)", RecurringPeriods.Fill("Retainer {kwartaal} ({periode})", from, to));
        Assert.Equal("Oktober 2026", RecurringPeriods.Fill("{Periode}", from, to));
        // Onbekende woorden tussen accolades blijven staan.
        Assert.Equal("Iets met {dag}", RecurringPeriods.Fill("Iets met {dag}", from, to));
        Assert.Equal("Q1 2027", RecurringPeriods.Fill("{kwartaal} {jaar}", D(2027, 2, 1), D(2027, 4, 30)));
    }
}

// Een klok die een test kan verzetten, om maanden te laten verstrijken of een storing na te doen.
public class MovableClock(DateTime utcNow) : BusinessClock
{
    public DateTime Value { get; set; } = utcNow;
    public override DateTime UtcNow => Value;

    // Een dag om 10:00 Nederlandse tijd.
    public void SetDay(int year, int month, int day) =>
        Value = TimeZoneInfo.ConvertTimeToUtc(new DateTime(year, month, day, 10, 0, 0, DateTimeKind.Unspecified), Zone);
}

public class RecurringFactory : PortalFactory
{
    public MovableClock Clock { get; } = new(new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Invoicing:MaxCatchUpPeriods", "3");
        builder.UseSetting("Invoicing:StartHour", "0");
        builder.UseSetting("Invoicing:CheckEveryMinutes", "0");
        builder.ConfigureTestServices(s => s.AddSingleton<BusinessClock>(Clock));
    }
}

// Terugkerende facturen: één factuur per periode, ook na een storing en met twee rondes tegelijk, alleen in de eigen werkruimte.
public class RecurringInvoiceTests(RecurringFactory factory) : IClassFixture<RecurringFactory>
{
    static object Template(int customerId, string start, string interval = "maand", string rule = "vooraf", string? end = null,
        string description = "Hosting {maand} {jaar}", decimal price = 50, string action = "concept", string vatRegime = "normaal") => new
    {
        name = "Hosting", customerId, vatRegime, interval, periodRule = rule, action, startDate = start, endDate = end,
        notes = "Periode {periode}", lines = new[] { new { description, quantity = 1, unit = "maand", unitPrice = price, vatRate = 21 } },
    };

    async Task<(HttpClient Client, int Workspace, int Customer)> ReadyAsync(string prefix)
    {
        factory.Clock.SetDay(2026, 10, 12);
        var c = await InvoiceKit.ReadyAsync(factory, prefix);
        return (c, await TestApi.WorkspaceIdAsync(c), await CustomerAsync(c, "Klant"));
    }

    static async Task<JsonElement> CreateAsync(HttpClient c, object template)
    {
        var res = await c.PostAsJsonAsync("/api/recurring-invoices", template);
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await res.JsonAsync();
    }

    async Task<int> RunJobAsync(int workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = workspaceId;
        return await scope.ServiceProvider.GetRequiredService<RecurringInvoiceService>().RunDueAsync();
    }

    List<Invoice> Made(int workspaceId, int templateId) => WorkflowKit.InWorkspace(factory, workspaceId, db =>
        db.Invoices.Include(i => i.Lines).Where(i => i.RecurringInvoiceId == templateId).OrderBy(i => i.RecurringPeriodStart).ToList());

    static DateTime D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public async Task A_due_period_becomes_one_concept_invoice_with_the_period_filled_in()
    {
        var (c, ws, customer) = await ReadyAsync("terug-eerste");
        var t = await CreateAsync(c, Template(customer, "2026-10-01"));
        var id = t.GetProperty("id").GetInt32();

        var made = Made(ws, id);
        var inv = Assert.Single(made);
        Assert.Equal("concept", inv.Status);
        Assert.Null(inv.Number);
        Assert.Equal((D(2026, 10, 1), D(2026, 10, 31)), (inv.DeliveryFrom, inv.DeliveryTo));
        Assert.Equal("Hosting oktober 2026", inv.Lines.Single().Description);
        Assert.Equal("Periode oktober 2026", inv.Notes);
        Assert.Equal(D(2026, 10, 12), inv.IssueDate);
        Assert.Equal(D(2026, 10, 26), inv.DueDate);

        var after = await c.GetFromJsonAsync<JsonElement>($"/api/recurring-invoices/{id}");
        Assert.Equal("2026-11-01T00:00:00", after.GetProperty("nextRunDate").GetString());
        Assert.Equal(1, after.GetProperty("invoiceCount").GetInt32());
        Assert.True(after.GetProperty("locked").GetBoolean());
        // De factuur wijst terug naar de terugkerende factuur.
        var json = await GetInvoiceAsync(c, inv.Id);
        Assert.Equal(id, json.GetProperty("recurringInvoiceId").GetInt32());

        // Nog een keer draaien op dezelfde dag doet niets.
        Assert.Equal(0, await RunJobAsync(ws));
        Assert.Single(Made(ws, id));
        Assert.Contains(WorkflowKit.InWorkspace(factory, ws, db => db.Activities.Select(a => a.Text).ToList()),
            text => text.Contains("klaargezet door terugkerende factuur \"Hosting\""));
    }

    [Fact]
    public async Task After_downtime_each_missed_period_is_made_once_and_restarts_change_nothing()
    {
        var (c, ws, customer) = await ReadyAsync("terug-storing");
        var id = (await CreateAsync(c, Template(customer, "2026-11-01"))).GetProperty("id").GetInt32();
        Assert.Empty(Made(ws, id));

        // De server lag stil van oktober tot half januari.
        factory.Clock.SetDay(2027, 1, 15);
        Assert.Equal(3, await RunJobAsync(ws));
        var made = Made(ws, id);
        Assert.Equal([D(2026, 11, 1), D(2026, 12, 1), D(2027, 1, 1)], made.Select(i => i.RecurringPeriodStart!.Value));
        Assert.Equal(["Hosting november 2026", "Hosting december 2026", "Hosting januari 2027"], made.Select(i => i.Lines.Single().Description));

        // Een herstart (een nieuwe ronde) maakt niets dubbel.
        Assert.Equal(0, await RunJobAsync(ws));
        Assert.Equal(3, Made(ws, id).Count);
        Assert.Equal(3, WorkflowKit.InWorkspace(factory, ws, db => db.Activities.Count(a => a.Text.Contains("door terugkerende factuur"))));
    }

    [Fact]
    public async Task A_long_outage_only_catches_up_the_latest_periods_and_logs_the_rest()
    {
        var (c, ws, customer) = await ReadyAsync("terug-plafond");
        var id = (await CreateAsync(c, Template(customer, "2026-11-01"))).GetProperty("id").GetInt32();
        factory.Clock.SetDay(2027, 6, 10);
        Assert.Equal(3, await RunJobAsync(ws));
        Assert.Equal([D(2027, 4, 1), D(2027, 5, 1), D(2027, 6, 1)], Made(ws, id).Select(i => i.RecurringPeriodStart!.Value));
        var log = WorkflowKit.InWorkspace(factory, ws, db => db.Activities.Select(a => a.Text).ToList());
        Assert.Contains(log, l => l.Contains("5 gemiste perioden (november 2026 t/m maart 2027) zijn overgeslagen"));
        Assert.Equal(0, await RunJobAsync(ws));
    }

    [Fact]
    public async Task Concurrent_runs_make_each_period_exactly_once()
    {
        var (c, ws, customer) = await ReadyAsync("terug-tegelijk");
        var id = (await CreateAsync(c, Template(customer, "2026-11-01", rule: "achteraf"))).GetProperty("id").GetInt32();
        factory.Clock.SetDay(2027, 1, 2);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => RunJobAsync(ws))));
        Assert.Equal(2, results.Sum());
        Assert.Equal([D(2026, 11, 1), D(2026, 12, 1)], Made(ws, id).Select(i => i.RecurringPeriodStart!.Value));

        // Handmatig "Nu aanmaken" tegelijk: nooit twee facturen voor dezelfde periode.
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SafeSend(() => c.PostAsync($"/api/recurring-invoices/{id}/run", null))));
        var created = responses.Count(r => r?.StatusCode == HttpStatusCode.Created);
        Assert.True(created >= 1);
        Assert.All(responses, r => Assert.True(r?.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict));
        var periods = Made(ws, id).Select(i => i.RecurringPeriodStart!.Value).ToList();
        Assert.Equal(periods.Distinct().Count(), periods.Count);
        Assert.Equal(2 + created, periods.Count);
    }

    [Fact]
    public async Task The_database_refuses_a_second_invoice_for_the_same_period()
    {
        var (c, ws, customer) = await ReadyAsync("terug-index");
        var id = (await CreateAsync(c, Template(customer, "2026-10-01"))).GetProperty("id").GetInt32();
        Assert.Throws<DbUpdateException>(() => WorkflowKit.InWorkspace(factory, ws, db =>
        {
            db.Invoices.Add(new Invoice { CustomerId = customer, RecurringInvoiceId = id, RecurringPeriodStart = D(2026, 10, 1), Lines = [new InvoiceLine { Description = "Dubbel" }] });
            return db.SaveChanges();
        }));
    }

    [Fact]
    public async Task Paused_templates_make_nothing_and_resuming_skips_the_paused_periods()
    {
        var (c, ws, customer) = await ReadyAsync("terug-pauze");
        var id = (await CreateAsync(c, Template(customer, "2026-11-01"))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/recurring-invoices/{id}/pause", null)).StatusCode);
        factory.Clock.SetDay(2027, 1, 1);
        Assert.Equal(0, await RunJobAsync(ws));
        Assert.Empty(Made(ws, id));

        var resumed = await (await c.PostAsync($"/api/recurring-invoices/{id}/resume", null)).JsonAsync();
        // November en december vielen in de pauze; januari is vandaag aan de beurt en komt er meteen.
        Assert.Equal([D(2027, 1, 1)], Made(ws, id).Select(i => i.RecurringPeriodStart!.Value));
        Assert.Equal("2027-02-01T00:00:00", resumed.GetProperty("nextRunDate").GetString());
        Assert.Contains(WorkflowKit.InWorkspace(factory, ws, db => db.Activities.Select(a => a.Text).ToList()),
            l => l.Contains("2 perioden uit de pauze (november t/m december 2026) zijn overgeslagen"));
    }

    [Fact]
    public async Task The_planner_never_makes_an_invoice_for_a_paused_template_but_run_now_still_can()
    {
        var (c, ws, customer) = await ReadyAsync("terug-pauze-race");
        var id = (await CreateAsync(c, Template(customer, "2026-11-01"))).GetProperty("id").GetInt32();
        await c.PostAsync($"/api/recurring-invoices/{id}/pause", null);
        factory.Clock.SetDay(2026, 11, 2);
        // Ook als de planner hem net voor het pauzeren had opgepakt, maakt hij niets meer.
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = ws;
            var result = await scope.ServiceProvider.GetRequiredService<RecurringInvoiceService>().CreateAsync(id, D(2026, 11, 1), manual: false);
            Assert.Equal(RecurringOutcome.Paused, result.Outcome);
        }
        Assert.Empty(Made(ws, id));
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsync($"/api/recurring-invoices/{id}/run", null)).StatusCode);
        Assert.Single(Made(ws, id));
    }

    [Fact]
    public async Task The_end_date_stops_the_template_and_run_now_makes_the_next_period()
    {
        var (c, ws, customer) = await ReadyAsync("terug-einde");
        var id = (await CreateAsync(c, Template(customer, "2026-10-01", interval: "kwartaal", end: "2027-01-01"))).GetProperty("id").GetInt32();
        Assert.Single(Made(ws, id));
        // Nu aanmaken maakt Q1 2027 al, ook al is die nog niet aan de beurt.
        var run = await c.PostAsync($"/api/recurring-invoices/{id}/run", null);
        Assert.Equal(HttpStatusCode.Created, run.StatusCode);
        Assert.Equal([D(2026, 10, 1), D(2027, 1, 1)], Made(ws, id).Select(i => i.RecurringPeriodStart!.Value));
        Assert.Equal("Hosting januari 2027", Made(ws, id)[1].Lines.Single().Description);
        var t = await c.GetFromJsonAsync<JsonElement>($"/api/recurring-invoices/{id}");
        Assert.Equal("afgelopen", t.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, t.GetProperty("nextRunDate").ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/recurring-invoices/{id}/run", null)).StatusCode);
        factory.Clock.SetDay(2027, 9, 1);
        Assert.Equal(0, await RunJobAsync(ws));
    }

    [Fact]
    public async Task Demo_templates_and_demo_customers_never_make_invoices()
    {
        factory.Clock.SetDay(2026, 10, 12);
        var c = await TestApi.RegisterAsync(factory, $"terug-demo-{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/workspace/demo-data", null)).StatusCode);
        (await c.PutAsJsonAsync("/api/settings", Company())).EnsureSuccessStatusCode();
        var ws = await TestApi.WorkspaceIdAsync(c);
        var demo = Assert.Single((await c.GetFromJsonAsync<List<JsonElement>>("/api/recurring-invoices"))!);
        Assert.True(demo.GetProperty("demo").GetBoolean());
        var demoId = demo.GetProperty("id").GetInt32();

        factory.Clock.SetDay(2026, 12, 15);
        Assert.Equal(0, await RunJobAsync(ws));
        Assert.Empty(Made(ws, demoId));
        var run = await c.PostAsync($"/api/recurring-invoices/{demoId}/run", null);
        Assert.Equal(HttpStatusCode.BadRequest, run.StatusCode);
        Assert.Contains("voorbeeld", await run.ErrorAsync());

        // Een voorbeeldklant kan geen terugkerende factuur krijgen.
        var demoCustomer = (await c.GetFromJsonAsync<List<JsonElement>>("/api/customers"))!.First().GetProperty("id").GetInt32();
        var post = await c.PostAsJsonAsync("/api/recurring-invoices", Template(demoCustomer, "2026-12-01"));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
        Assert.Contains("voorbeeldklant", await post.ErrorAsync());

        // Voorbeelddata weghalen haalt ook het voorbeeld weg.
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/workspace/demo-data")).StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<List<JsonElement>>("/api/recurring-invoices"))!);
    }

    [Fact]
    public async Task Another_workspace_cannot_see_change_run_or_use_your_templates()
    {
        var (a, wsA, customerA) = await ReadyAsync("terug-a");
        var (b, wsB, customerB) = await ReadyAsync("terug-b");
        var id = (await CreateAsync(a, Template(customerA, "2026-11-01"))).GetProperty("id").GetInt32();

        Assert.Empty((await b.GetFromJsonAsync<List<JsonElement>>("/api/recurring-invoices"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/recurring-invoices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsJsonAsync($"/api/recurring-invoices/{id}", Template(customerB, "2026-11-01"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/recurring-invoices/{id}/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/recurring-invoices/{id}/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/recurring-invoices/{id}")).StatusCode);
        // Een klant van een andere werkruimte kan niet, ook niet via de voorbeeldberekening.
        var foreign = await b.PostAsJsonAsync("/api/recurring-invoices", Template(customerA, "2026-11-01"));
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Contains("eigen werkruimte", await foreign.ErrorAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PutAsJsonAsync($"/api/recurring-invoices/{id}", Template(customerB, "2026-11-01"))).StatusCode);

        // De planner van B doet niets met de factuur van A.
        factory.Clock.SetDay(2026, 11, 2);
        Assert.Equal(0, await RunJobAsync(wsB));
        Assert.Empty(Made(wsA, id));
        Assert.Equal(1, await RunJobAsync(wsA));
        Assert.Empty(WorkflowKit.InWorkspace(factory, wsB, db => db.Invoices.ToList()));
    }

    [Fact]
    public async Task Employees_can_view_but_only_admins_change_templates()
    {
        var (admin, _, customer) = await ReadyAsync("terug-rol");
        var id = (await CreateAsync(admin, Template(customer, "2026-11-01"))).GetProperty("id").GetInt32();
        var email = $"medewerker-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(admin, email);
        var employee = await TestApi.LoginAsync(factory, email);

        Assert.Single((await employee.GetFromJsonAsync<List<JsonElement>>("/api/recurring-invoices"))!);
        Assert.Equal(HttpStatusCode.OK, (await employee.GetAsync($"/api/recurring-invoices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync("/api/recurring-invoices", Template(customer, "2026-11-01"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PutAsJsonAsync($"/api/recurring-invoices/{id}", Template(customer, "2026-11-01"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsync($"/api/recurring-invoices/{id}/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsync($"/api/recurring-invoices/{id}/resume", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsync($"/api/recurring-invoices/{id}/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.DeleteAsync($"/api/recurring-invoices/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_read_only_workspace_cannot_change_templates_and_the_planner_makes_nothing()
    {
        var (c, ws, customer) = await ReadyAsync("terug-alleen-lezen");
        var id = (await CreateAsync(c, Template(customer, "2026-11-01"))).GetProperty("id").GetInt32();
        await TestApi.ExpireTrialAsync(factory, ws);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/recurring-invoices", Template(customer, "2026-11-01"))).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsync($"/api/recurring-invoices/{id}/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/recurring-invoices/{id}")).StatusCode);

        factory.Clock.SetDay(2026, 11, 3);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = ws;
            foreach (var job in scope.ServiceProvider.GetServices<IWorkspaceJob>()) await job.RunAsync(CancellationToken.None);
        }
        Assert.Empty(Made(ws, id));
    }

    [Fact]
    public async Task After_the_first_invoice_customer_start_and_interval_are_locked_but_lines_can_change()
    {
        var (c, ws, customer) = await ReadyAsync("terug-vast");
        var other = await CustomerAsync(c, "Ander");
        var id = (await CreateAsync(c, Template(customer, "2026-10-01"))).GetProperty("id").GetInt32();

        foreach (var changed in new[] { Template(other, "2026-10-01"), Template(customer, "2026-09-01"), Template(customer, "2026-10-01", interval: "kwartaal") })
        {
            var res = await c.PutAsJsonAsync($"/api/recurring-invoices/{id}", changed);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Equal(Potel.Api.Endpoints.RecurringInvoiceEndpoints.LockedError, await res.ErrorAsync());
        }
        var ok = await c.PutAsJsonAsync($"/api/recurring-invoices/{id}", Template(customer, "2026-10-01", rule: "achteraf", description: "Onderhoud {Periode}", price: 75));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        // November achteraf: op 1 december.
        Assert.Equal("2026-12-01T00:00:00", (await ok.JsonAsync()).GetProperty("nextRunDate").GetString());
        factory.Clock.SetDay(2026, 12, 1);
        Assert.Equal(1, await RunJobAsync(ws));
        var last = Made(ws, id).Last();
        Assert.Equal("Onderhoud November 2026", last.Lines.Single().Description);
        Assert.Equal(75, last.Lines.Single().UnitPrice);
    }

    [Fact]
    public async Task Templates_are_validated_like_invoices()
    {
        var (c, _, customer) = await ReadyAsync("terug-check");
        async Task<string> Refused(object body)
        {
            var res = await c.PostAsJsonAsync("/api/recurring-invoices", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return await res.ErrorAsync();
        }
        Assert.Contains("meer dan 3 facturen", await Refused(Template(customer, "2024-01-01")));
        Assert.Contains("Btw verlegd", await Refused(Template(customer, "2026-11-01", vatRegime: "verlegd")));
        Assert.Contains("einddatum", await Refused(Template(customer, "2026-11-01", end: "2026-10-01")));
        Assert.Contains("omschrijving", await Refused(Template(customer, "2026-11-01", description: " ")));
        Assert.Contains("Direct versturen", await Refused(Template(customer, "2026-11-01", action: "versturen")));
        Assert.Contains("eigen werkruimte", await Refused(Template(999_999, "2026-11-01")));
    }

    [Fact]
    public async Task Deleting_a_template_keeps_its_invoices_and_a_customer_with_templates_stays()
    {
        var (c, ws, customer) = await ReadyAsync("terug-weg");
        var id = (await CreateAsync(c, Template(customer, "2026-10-01"))).GetProperty("id").GetInt32();
        var invoiceId = Assert.Single(Made(ws, id)).Id;
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/customers/{customer}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/recurring-invoices/{id}")).StatusCode);
        var inv = await GetInvoiceAsync(c, invoiceId);
        Assert.Equal(JsonValueKind.Null, inv.GetProperty("recurringInvoiceId").ValueKind);
    }

    [Fact]
    public async Task The_background_planner_makes_due_invoices()
    {
        var (c, ws, customer) = await ReadyAsync("terug-planner");
        var id = (await CreateAsync(c, Template(customer, "2026-11-01"))).GetProperty("id").GetInt32();
        factory.Clock.SetDay(2026, 11, 1);
        var (reached, _) = await WorkflowKit.RunSchedulerUntil(factory, () => Made(ws, id).Count == 1, TimeSpan.FromSeconds(15));
        Assert.True(reached, "De planner maakte de factuur niet");
    }
}
