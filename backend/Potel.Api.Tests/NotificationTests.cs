using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Notifications;
using Potel.Api.Workflows;
using static Potel.Api.Tests.InvoiceKit;
using static Potel.Api.Tests.WorkflowKit;

namespace Potel.Api.Tests;

public class NotificationFactory : PortalFactory
{
    public MovableClock Clock { get; } = new(DateTime.UtcNow);
    public TestMailServer Mail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("App:BaseUrl", "https://potel.test");
        builder.UseSetting("Invoicing:StartHour", "0");
        builder.UseSetting("Invoicing:CheckEveryMinutes", "0");
        builder.UseSetting("Workflows:EmailsPerDayTrial", "50");
        builder.UseSetting("Workflows:EmailsPerDayAllTrials", "100000");
        builder.UseSetting("Notifications:MailsPerUserPerDay", "3");
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<BusinessClock>(Clock);
            s.AddSingleton<IEmailSender>(Mail);
        });
    }
}

// Meldingen: per gebeurtenis voor de juiste mensen, één keer per gebeurtenis, alleen in de eigen werkruimte, en per e-mail
// alleen voor wie dat wil, binnen de limieten.
public class NotificationTests(NotificationFactory factory) : IClassFixture<NotificationFactory>
{
    record Team(HttpClient Owner, int Ws, int OwnerId, string OwnerEmail, HttpClient Admin, int AdminId, string AdminEmail,
        HttpClient Employee, int EmployeeId, string EmployeeEmail);

    static string Mail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    // Een werkruimte met de eigenaar, nog een beheerder en een medewerker, allemaal met een bevestigd adres.
    async Task<Team> TeamAsync(string prefix, bool ready = false)
    {
        factory.Clock.Value = DateTime.UtcNow;
        var ownerEmail = Mail(prefix);
        var owner = await TestApi.RegisterAsync(factory, ownerEmail);
        if (ready) (await owner.PutAsJsonAsync("/api/settings", Company())).EnsureSuccessStatusCode();
        var adminEmail = Mail($"{prefix}-beheer");
        var adminId = await TestApi.CreateUserAsync(owner, adminEmail, Roles.Admin);
        TestApi.MarkVerified(factory, adminEmail);
        var employeeEmail = Mail($"{prefix}-mw");
        var employeeId = await TestApi.CreateUserAsync(owner, employeeEmail, Roles.Employee);
        TestApi.MarkVerified(factory, employeeEmail);
        return new Team(owner, await TestApi.WorkspaceIdAsync(owner), await TestApi.MyIdAsync(owner), ownerEmail,
            await TestApi.LoginAsync(factory, adminEmail), adminId, adminEmail, await TestApi.LoginAsync(factory, employeeEmail), employeeId, employeeEmail);
    }

    static async Task<List<JsonElement>> ItemsAsync(HttpClient c, string query = "") =>
        (await c.GetFromJsonAsync<JsonElement>($"/api/notifications{query}")).GetProperty("items").EnumerateArray().ToList();

    static async Task<int> UnreadAsync(HttpClient c) => (await c.GetFromJsonAsync<JsonElement>("/api/notifications/summary")).GetProperty("unread").GetInt32();

    static string Kind(JsonElement n) => n.GetProperty("kind").GetString()!;
    static string Text(JsonElement n) => n.GetProperty("text").GetString()!;

    List<Notification> Rows(int ws, int userId) => InWorkspace(factory, ws, db => db.Notifications.Where(n => n.UserId == userId).OrderBy(n => n.Id).ToList());

    async Task<T> WithNotifierAsync<T>(int ws, Func<Notifier, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = ws;
        return await action(scope.ServiceProvider.GetRequiredService<Notifier>());
    }

    Task<int> NotifyAsync(int ws, string kind, string key, string text = "Er is iets gebeurd.", int? except = null) =>
        WithNotifierAsync(ws, n => n.ToWorkspaceAsync(new NotificationEvent(kind, key, $"Melding {key}", text, "/facturen/1"), except));

    Task MailRoundAsync() => factory.Services.GetRequiredService<NotificationMailer>().RunOnceAsync();

    // Een tijdstip vandaag (Nederlandse tijd) op de klok van de test.
    void At(int day, int hour) =>
        factory.Clock.Value = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, day, hour, 0, 0, DateTimeKind.Unspecified), BusinessClock.Zone);

    static async Task PutSettingsAsync(HttpClient c, bool digest, params (string Kind, bool InApp, bool Email)[] kinds)
    {
        var res = await c.PutAsJsonAsync("/api/notifications/settings", new { digest, kinds = kinds.Select(k => new { id = k.Kind, inApp = k.InApp, email = k.Email }) });
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    List<OutgoingEmail> MailsTo(string email, string? subject = null) =>
        factory.Mail.To(email).Where(m => subject is null ? m.Subject.StartsWith("Melding") || m.Subject.StartsWith("Je overzicht") : m.Subject.StartsWith(subject)).ToList();

    [Fact]
    public async Task A_payment_notifies_the_other_admins_but_not_who_recorded_it_or_employees()
    {
        var t = await TeamAsync("betaling", ready: true);
        var customer = await CustomerAsync(t.Owner, "Bakkerij");
        var invoice = await InvoiceAsync(t.Owner, customer, "verzonden", 1, 100);

        await PaymentKit.PaidAsync(t.Owner, invoice, 50);
        await PaymentKit.PaidAsync(t.Owner, invoice, 71);

        var items = await ItemsAsync(t.Admin);
        Assert.Equal(2, items.Count);
        Assert.All(items, n => Assert.Equal(NotificationKinds.Payment, Kind(n)));
        Assert.All(items, n => Assert.Equal($"/facturen/{invoice}", n.GetProperty("link").GetString()));
        // Nieuwste eerst: eerst de volledige betaling, dan de deelbetaling met wat er nog openstond.
        Assert.Contains("is betaald", Text(items[0]));
        Assert.Contains("71,00", Text(items[0]));
        Assert.Contains("met de hand vastgelegd", Text(items[0]));
        Assert.Contains("50,00", Text(items[1]));
        Assert.Contains("nog", Text(items[1]));
        Assert.Equal(2, await UnreadAsync(t.Admin));

        // Wie de betaling vastlegde, wist het al; een medewerker krijgt geen meldingen over geld.
        Assert.Empty(await ItemsAsync(t.Owner));
        Assert.Empty(await ItemsAsync(t.Employee));
        Assert.Empty(Rows(t.Ws, t.EmployeeId));
    }

    [Fact]
    public async Task The_same_event_gives_one_notification_per_user_even_at_the_same_time()
    {
        var t = await TeamAsync("dubbel");
        Assert.Equal(2, await NotifyAsync(t.Ws, NotificationKinds.Payment, "betaling:9001"));
        Assert.Equal(0, await NotifyAsync(t.Ws, NotificationKinds.Payment, "betaling:9001"));
        var together = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => NotifyAsync(t.Ws, NotificationKinds.Payment, "betaling:9002"))));
        Assert.Equal(2, together.Sum());
        Assert.Equal(2, Rows(t.Ws, t.OwnerId).Count);
        Assert.Equal(2, Rows(t.Ws, t.AdminId).Count);
    }

    [Fact]
    public async Task An_overdue_invoice_notifies_admins_once()
    {
        var t = await TeamAsync("te-laat", ready: true);
        var customer = await CustomerAsync(t.Owner, "Laatbetaler");
        var invoice = await InvoiceAsync(t.Owner, customer, "verzonden", 1, 100);
        factory.Clock.Value = DateTime.UtcNow.AddDays(20);

        // Het overzicht zet hem op te laat; nog een keer kijken geeft geen tweede melding.
        (await t.Employee.GetAsync("/api/invoices")).EnsureSuccessStatusCode();
        (await t.Owner.GetAsync("/api/invoices")).EnsureSuccessStatusCode();
        var mine = Assert.Single(await ItemsAsync(t.Owner));
        Assert.Equal(NotificationKinds.Overdue, Kind(mine));
        Assert.Contains("over de vervaldatum", Text(mine));
        Assert.Contains("Laatbetaler", Text(mine));
        Assert.Single(await ItemsAsync(t.Admin));
        Assert.Empty(Rows(t.Ws, t.EmployeeId));
        Assert.Equal("te-laat:" + invoice, Rows(t.Ws, t.OwnerId).Single().Key);
    }

    [Fact]
    public async Task Recurring_invoices_and_fixed_costs_made_by_the_planner_notify_admins()
    {
        var t = await TeamAsync("planner", ready: true);
        var customer = await CustomerAsync(t.Owner, "Vaste klant");
        var start = DateTime.UtcNow.AddMonths(1);
        var first = new DateTime(start.Year, start.Month, 1);
        var template = await (await t.Owner.PostAsJsonAsync("/api/recurring-invoices", new
        {
            name = "Hosting", customerId = customer, vatRegime = "normaal", interval = "maand", periodRule = "vooraf", action = "concept",
            startDate = Day(first), lines = new[] { new { description = "Hosting {maand}", quantity = 1, unit = "maand", unitPrice = 50, vatRate = 21 } },
        })).JsonAsync();
        var expense = await (await t.Owner.PostAsJsonAsync("/api/recurring-expenses", new
        {
            supplier = "Adobe", description = "Creative Cloud", category = "software", amount = 60.50m, vatRate = 21, interval = "maand", action = "concept",
            startDate = Day(first),
        })).JsonAsync();
        var remind = await (await t.Owner.PostAsJsonAsync("/api/recurring-expenses", new
        {
            supplier = "Huurbaas", description = "Kantoor", category = "kantoor", amount = 400m, vatRate = 21, interval = "maand", action = "herinneren",
            startDate = Day(first),
        })).JsonAsync();
        Assert.True(template.GetProperty("id").GetInt32() > 0 && expense.GetProperty("id").GetInt32() > 0 && remind.GetProperty("id").GetInt32() > 0);
        Assert.Empty(await ItemsAsync(t.Owner));

        // Een maand later doet de planner zijn werk.
        factory.Clock.Value = first.AddDays(2).AddHours(10);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = t.Ws;
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<Invoicing.RecurringInvoiceService>().RunDueAsync());
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<Invoicing.RecurringExpenseService>().RunDueAsync());
        }

        var items = await ItemsAsync(t.Owner);
        var invoice = Assert.Single(items, n => Kind(n) == NotificationKinds.Recurring);
        Assert.Contains("conceptfactuur voor Vaste klant B.V.", Text(invoice));
        Assert.StartsWith("/facturen/", invoice.GetProperty("link").GetString());
        var costs = items.Where(n => Kind(n) == NotificationKinds.FixedCost).Select(Text).ToList();
        Assert.Equal(2, costs.Count);
        Assert.Contains(costs, x => x.Contains("Adobe") && x.Contains("als concept"));
        Assert.Contains(costs, x => x.Contains("Huurbaas") && x.Contains("aan de beurt"));
        Assert.Equal(3, (await ItemsAsync(t.Admin)).Count);
        Assert.Empty(Rows(t.Ws, t.EmployeeId));
    }

    [Fact]
    public async Task Creating_a_recurring_invoice_that_is_already_due_does_not_notify_who_made_it()
    {
        var t = await TeamAsync("meteen", ready: true);
        var customer = await CustomerAsync(t.Owner, "Directe klant");
        var res = await t.Owner.PostAsJsonAsync("/api/recurring-invoices", new
        {
            name = "Onderhoud", customerId = customer, vatRegime = "normaal", interval = "maand", periodRule = "vooraf", action = "concept",
            startDate = Day(DateTime.UtcNow.AddDays(-3)), lines = new[] { new { description = "Onderhoud", quantity = 1, unit = "maand", unitPrice = 80, vatRate = 21 } },
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Empty(await ItemsAsync(t.Owner));
        Assert.Single(await ItemsAsync(t.Admin), n => Kind(n) == NotificationKinds.Recurring);
    }

    [Fact]
    public async Task A_client_opening_the_portal_notifies_admins_the_first_time_and_after_a_quiet_week()
    {
        var t = await TeamAsync("portaal", ready: true);
        var customer = await CustomerAsync(t.Owner, "Kijker");
        var res = await t.Owner.PostAsJsonAsync($"/api/customers/{customer}/portal/link", new { expiresInDays = (int?)null });
        var token = (await res.JsonAsync()).GetProperty("path").GetString()!["/portaal#token=".Length..];
        Task<HttpResponseMessage> Open(HttpClient? c = null) => (c ?? factory.CreateClient()).PostAsJsonAsync("/api/public/portal", new { token });

        // Iemand van de werkruimte zelf die even kijkt, telt niet.
        (await Open(t.Owner)).EnsureSuccessStatusCode();
        Assert.Empty(await ItemsAsync(t.Admin));
        // Nu de klant: wie de link al opende, telt niet als de klant zelf.
        InWorkspace(factory, t.Ws, db => db.PortalLinks.Where(l => l.CustomerId == customer).ExecuteUpdate(x => x.SetProperty(l => l.LastOpenedAt, (DateTime?)null)));
        (await Open()).EnsureSuccessStatusCode();
        (await Open()).EnsureSuccessStatusCode();
        var first = Assert.Single(await ItemsAsync(t.Owner));
        Assert.Equal(NotificationKinds.Portal, Kind(first));
        Assert.Contains("voor het eerst", Text(first));
        Assert.Equal($"/klanten/{customer}", first.GetProperty("link").GetString());

        // Drie dagen later is niet bijzonder; na een stille week wel.
        factory.Clock.Value = DateTime.UtcNow.AddDays(3);
        (await Open()).EnsureSuccessStatusCode();
        Assert.Single(await ItemsAsync(t.Owner));
        factory.Clock.Value = DateTime.UtcNow.AddDays(11);
        (await Open()).EnsureSuccessStatusCode();
        var items = await ItemsAsync(t.Owner);
        Assert.Equal(2, items.Count);
        Assert.Contains("weer geopend", Text(items[0]));
        Assert.Empty(Rows(t.Ws, t.EmployeeId));
    }

    [Fact]
    public async Task A_failed_backup_notifies_only_platform_admins_once_a_day()
    {
        var t = await TeamAsync("back-up");
        var result = new BackupResult(BackupOutcome.Failed, Error: "Schijf vol.");
        Assert.True(await WithNotifierAsync(t.Ws, n => n.BackupFailedAsync(result)) >= 1);
        Assert.Equal(0, await WithNotifierAsync(t.Ws, n => n.BackupFailedAsync(result)));

        var platform = await TestApi.LoginAsync(factory, "admin@potel.nl", "welkom123");
        var item = Assert.Single(await ItemsAsync(platform), n => Kind(n) == NotificationKinds.Backup);
        Assert.Contains("mislukt", Text(item));
        Assert.Equal("/platform", item.GetProperty("link").GetString());
        Assert.Empty(await ItemsAsync(t.Owner));
        Assert.Empty(await ItemsAsync(t.Admin));
    }

    [Fact]
    public async Task The_vat_deadline_gives_a_notification_a_week_before_and_on_the_last_day()
    {
        var t = await TeamAsync("btw", ready: true);
        var kor = await ReadyAsync(factory, "btw-kor", vatRegime: VatRegimes.Kor);
        var korWs = await TestApi.WorkspaceIdAsync(kor);
        var filed = await ReadyAsync(factory, "btw-gedaan");
        var filedWs = await TestApi.WorkspaceIdAsync(filed);
        InWorkspace(factory, filedWs, db => { db.VatFilings.Add(new VatFiling { Year = 2026, Quarter = 3 }); return db.SaveChanges(); });

        async Task CheckAsync(int ws)
        {
            using var scope = factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = ws;
            await scope.ServiceProvider.GetServices<Invoicing.IWorkspaceJob>().OfType<VatDeadlineJob>().Single().CheckAsync();
        }

        // Het derde kwartaal moet uiterlijk 31 oktober binnen zijn.
        factory.Clock.SetDay(2026, 10, 23);
        await CheckAsync(t.Ws);
        Assert.Empty(await ItemsAsync(t.Owner));
        factory.Clock.SetDay(2026, 10, 24);
        foreach (var ws in new[] { t.Ws, korWs, filedWs }) await CheckAsync(ws);
        factory.Clock.SetDay(2026, 10, 25);
        await CheckAsync(t.Ws);
        var week = Assert.Single(await ItemsAsync(t.Owner));
        Assert.Equal(NotificationKinds.Vat, Kind(week));
        Assert.Contains("over 7 dagen", Text(week));
        Assert.Contains("Q3 2026", Text(week));
        Assert.Equal("/btw-aangifte", week.GetProperty("link").GetString());

        factory.Clock.SetDay(2026, 10, 31);
        await CheckAsync(t.Ws);
        await CheckAsync(t.Ws);
        var items = await ItemsAsync(t.Owner);
        Assert.Equal(2, items.Count);
        Assert.Contains("Vandaag is de laatste dag", Text(items[0]));
        Assert.Equal(2, (await ItemsAsync(t.Admin)).Count);
        Assert.Empty(Rows(t.Ws, t.EmployeeId));
        // Met de KOR doe je geen aangifte, en een aangifte die al gedaan is, hoeft niet meer.
        Assert.Empty(await ItemsAsync(kor));
        Assert.Empty(await ItemsAsync(filed));
    }

    [Fact]
    public async Task A_failed_workflow_notifies_admins_and_later_also_whoever_started_it()
    {
        var t = await TeamAsync("werkstroom");
        var graph = GraphJson([Node("s", "trigger.manual"), Node("w", "action.webhook", new() { ["url"] = "http://127.0.0.1/" })], [Edge("s", "w")]);
        var id = await CreateWorkflowAsync(t.Owner, "Doorsturen", graph);

        // De medewerker start hem zelf en ziet de fout meteen; de beheerders krijgen een melding.
        var run = await RunAsync(t.Employee, id);
        Assert.Equal("fout", run.Status);
        var item = Assert.Single(await ItemsAsync(t.Owner));
        Assert.Equal(NotificationKinds.Workflow, Kind(item));
        Assert.Contains("Doorsturen", Text(item));
        Assert.Equal($"/werkstromen/{id}", item.GetProperty("link").GetString());
        Assert.Single(await ItemsAsync(t.Admin));
        Assert.Empty(await ItemsAsync(t.Employee));

        // Een run die hij startte en die later vastloopt (na het wachten): dan krijgt hij er wel een.
        var waiting = InWorkspace(factory, t.Ws, db =>
        {
            var r = new WorkflowRun { WorkflowId = id, Trigger = "Handmatig gestart", Status = "wachtend", ResumeAt = DateTime.UtcNow, StartedByUserId = t.EmployeeId };
            db.WorkflowRuns.Add(r);
            db.SaveChanges();
            return r.Id;
        });
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = t.Ws;
            await scope.ServiceProvider.GetRequiredService<WorkflowEngine>().FailAsync(waiting);
        }
        var mine = Assert.Single(await ItemsAsync(t.Employee));
        Assert.Contains("Doorsturen", Text(mine));
        Assert.Equal(2, (await ItemsAsync(t.Owner)).Count);
    }

    [Fact]
    public async Task Notifications_stay_with_their_own_user_and_workspace()
    {
        var t = await TeamAsync("eigen");
        var other = await TestApi.RegisterAsync(factory, Mail("buurman"));
        await NotifyAsync(t.Ws, NotificationKinds.Payment, "betaling:7001", except: t.AdminId);
        var id = Rows(t.Ws, t.OwnerId).Single().Id;

        Assert.Empty(await ItemsAsync(other));
        Assert.Equal(0, await UnreadAsync(other));
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/notifications/{id}/read", null)).StatusCode);
        // Ook een collega in dezelfde werkruimte kan andermans melding niet als gelezen markeren.
        Assert.Equal(HttpStatusCode.NotFound, (await t.Admin.PostAsync($"/api/notifications/{id}/read", null)).StatusCode);
        (await other.PostAsync("/api/notifications/read", null)).EnsureSuccessStatusCode();
        (await t.Admin.PostAsync("/api/notifications/read", null)).EnsureSuccessStatusCode();
        Assert.Equal(1, await UnreadAsync(t.Owner));
        Assert.Null(Rows(t.Ws, t.OwnerId).Single().ReadAt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/notifications")).StatusCode);
    }

    [Fact]
    public async Task The_bell_counts_unread_notifications_and_marks_them_read()
    {
        var t = await TeamAsync("bel");
        for (var i = 1; i <= 4; i++) await NotifyAsync(t.Ws, NotificationKinds.Payment, $"betaling:{8000 + i}", $"Betaling {i}", except: t.AdminId);
        await NotifyAsync(t.Ws, NotificationKinds.Vat, "btw:2026-3:week", "Btw", except: t.AdminId);
        var ids = Rows(t.Ws, t.OwnerId).Select(n => n.Id).ToList();

        var summary = await t.Owner.GetFromJsonAsync<JsonElement>("/api/notifications/summary");
        Assert.Equal(5, summary.GetProperty("unread").GetInt32());
        Assert.Equal(ids.Max(), summary.GetProperty("latest").GetInt32());
        Assert.Empty(summary.GetProperty("fresh").EnumerateArray());
        // Wat er nieuw is sinds de vorige keer, voor een melding in beeld.
        var fresh = (await t.Owner.GetFromJsonAsync<JsonElement>($"/api/notifications/summary?after={ids[2]}")).GetProperty("fresh").EnumerateArray().ToList();
        Assert.Equal([ids[4], ids[3]], fresh.Select(n => n.GetProperty("id").GetInt32()));

        // Per pagina, nieuwste eerst, en per soort.
        var page = await t.Owner.GetFromJsonAsync<JsonElement>("/api/notifications?limit=2");
        Assert.True(page.GetProperty("more").GetBoolean());
        Assert.Equal([ids[4], ids[3]], page.GetProperty("items").EnumerateArray().Select(n => n.GetProperty("id").GetInt32()));
        Assert.Equal([ids[2], ids[1]], (await ItemsAsync(t.Owner, $"?limit=2&before={ids[3]}")).Select(n => n.GetProperty("id").GetInt32()));
        Assert.Single(await ItemsAsync(t.Owner, "?kind=btw"));

        var read = await (await t.Owner.PostAsync($"/api/notifications/{ids[0]}/read", null)).JsonAsync();
        Assert.Equal(4, read.GetProperty("unread").GetInt32());
        Assert.True((await ItemsAsync(t.Owner)).Single(n => n.GetProperty("id").GetInt32() == ids[0]).GetProperty("read").GetBoolean());
        Assert.Equal(4, (await ItemsAsync(t.Owner, "?unread=true")).Count);
        Assert.Equal(1, (await (await t.Owner.PostAsync("/api/notifications/read?kind=betaling", null)).JsonAsync()).GetProperty("unread").GetInt32());
        Assert.Equal(0, (await (await t.Owner.PostAsync("/api/notifications/read", null)).JsonAsync()).GetProperty("unread").GetInt32());
        Assert.Equal(5, (await ItemsAsync(t.Owner)).Count);
    }

    [Fact]
    public async Task Someone_who_is_no_longer_admin_no_longer_sees_or_gets_money_notifications()
    {
        var t = await TeamAsync("rol");
        await NotifyAsync(t.Ws, NotificationKinds.Payment, "betaling:6001");
        Assert.Single(await ItemsAsync(t.Admin));

        (await t.Owner.PutAsJsonAsync($"/api/users/{t.AdminId}", new { name = "Oud-beheerder", email = t.AdminEmail, role = Roles.Employee, active = true })).EnsureSuccessStatusCode();
        var demoted = await TestApi.LoginAsync(factory, t.AdminEmail);
        Assert.Empty(await ItemsAsync(demoted));
        Assert.Equal(0, await UnreadAsync(demoted));
        Assert.Equal(1, await NotifyAsync(t.Ws, NotificationKinds.Payment, "betaling:6002"));
        Assert.Single(Rows(t.Ws, t.AdminId));

        // In zijn instellingen staan alleen nog de soorten voor iedereen.
        var settings = await demoted.GetFromJsonAsync<JsonElement>("/api/notifications/settings");
        Assert.Equal([NotificationKinds.Workflow], settings.GetProperty("kinds").EnumerateArray().Select(k => k.GetProperty("id").GetString()));
        var refused = await demoted.PutAsJsonAsync("/api/notifications/settings", new { digest = false, kinds = new[] { new { id = NotificationKinds.Payment, inApp = true, email = true } } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var platform = await t.Owner.PutAsJsonAsync("/api/notifications/settings", new { digest = false, kinds = new[] { new { id = NotificationKinds.Backup, inApp = true, email = true } } });
        Assert.Equal(HttpStatusCode.BadRequest, platform.StatusCode);
    }

    [Fact]
    public async Task Notifications_can_be_read_in_a_read_only_workspace()
    {
        var t = await TeamAsync("alleen-lezen");
        await NotifyAsync(t.Ws, NotificationKinds.Payment, "betaling:5001", except: t.AdminId);
        await TestApi.ExpireTrialAsync(factory, t.Ws);

        var item = Assert.Single(await ItemsAsync(t.Owner));
        Assert.Equal(HttpStatusCode.OK, (await t.Owner.PostAsync($"/api/notifications/{item.GetProperty("id").GetInt32()}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t.Owner.PostAsync("/api/notifications/read", null)).StatusCode);
        await PutSettingsAsync(t.Owner, false, (NotificationKinds.Vat, false, false));
        Assert.Equal(HttpStatusCode.PaymentRequired, (await t.Owner.PostAsJsonAsync("/api/customers", new { name = "Nieuw" })).StatusCode);
    }

    [Fact]
    public async Task Settings_turn_a_kind_off_in_the_app_or_on_by_mail()
    {
        var t = await TeamAsync("instellingen");
        var settings = await t.Owner.GetFromJsonAsync<JsonElement>("/api/notifications/settings");
        Assert.True(settings.GetProperty("emailAvailable").GetBoolean());
        var kinds = settings.GetProperty("kinds").EnumerateArray().ToDictionary(k => k.GetProperty("id").GetString()!);
        Assert.DoesNotContain(NotificationKinds.Backup, kinds.Keys);
        // Wat al per e-mail kwam (een besluit over een offerte), blijft standaard per e-mail komen.
        Assert.True(kinds[NotificationKinds.Quote].GetProperty("email").GetBoolean());
        Assert.False(kinds[NotificationKinds.Payment].GetProperty("email").GetBoolean());
        Assert.All(kinds.Values, k => Assert.True(k.GetProperty("inApp").GetBoolean()));

        await PutSettingsAsync(t.Owner, false, (NotificationKinds.Payment, false, true), (NotificationKinds.Vat, false, false));
        At(2, 10);
        await NotifyAsync(t.Ws, NotificationKinds.Payment, "betaling:4001", "Er is betaald.", except: t.AdminId);
        await NotifyAsync(t.Ws, NotificationKinds.Vat, "btw:2026-3:week", except: t.AdminId);

        // Niet in de bel, wel per e-mail; de btw helemaal niet.
        Assert.Empty(await ItemsAsync(t.Owner));
        Assert.Single(Rows(t.Ws, t.OwnerId));
        await MailRoundAsync();
        Assert.True(await WaitUntil(() => MailsTo(t.OwnerEmail).Count == 1, TimeSpan.FromSeconds(5)));
        var mail = MailsTo(t.OwnerEmail).Single();
        Assert.Equal("Melding betaling:4001", mail.Subject);
        Assert.Contains("Er is betaald.", mail.Body);
        Assert.Contains("https://potel.test/facturen/1", mail.Body);
        Assert.Contains("Instellingen, Meldingen", mail.Body);
        Assert.Equal(NotificationMail.Sent, Rows(t.Ws, t.OwnerId).Single().Mail);
        await MailRoundAsync();
        await Task.Delay(200);
        Assert.Single(MailsTo(t.OwnerEmail));

        var after = await t.Owner.GetFromJsonAsync<JsonElement>("/api/notifications/settings");
        var payment = after.GetProperty("kinds").EnumerateArray().Single(k => k.GetProperty("id").GetString() == NotificationKinds.Payment);
        Assert.False(payment.GetProperty("inApp").GetBoolean());
        Assert.True(payment.GetProperty("email").GetBoolean());
    }

    [Fact]
    public async Task With_the_daily_overview_mails_wait_for_one_mail_in_the_morning()
    {
        var t = await TeamAsync("overzicht");
        await PutSettingsAsync(t.Owner, true, (NotificationKinds.Payment, true, true), (NotificationKinds.Vat, true, true));
        At(3, 6);
        await NotifyAsync(t.Ws, NotificationKinds.Payment, "betaling:3001", "Bakkerij betaalde.", except: t.AdminId);
        await NotifyAsync(t.Ws, NotificationKinds.Vat, "btw:2026-3:week", "De btw moet binnen.", except: t.AdminId);
        await NotifyAsync(t.Ws, NotificationKinds.Payment, "betaling:3002", "Al gelezen in de app.", except: t.AdminId);
        var read = Rows(t.Ws, t.OwnerId).Single(n => n.Key == "betaling:3002").Id;
        (await t.Owner.PostAsync($"/api/notifications/{read}/read", null)).EnsureSuccessStatusCode();

        // Voor acht uur gaat er niets weg.
        await MailRoundAsync();
        await Task.Delay(200);
        Assert.Empty(MailsTo(t.OwnerEmail));

        At(3, 9);
        await MailRoundAsync();
        Assert.True(await WaitUntil(() => MailsTo(t.OwnerEmail).Count == 1, TimeSpan.FromSeconds(5)));
        var digest = MailsTo(t.OwnerEmail).Single();
        Assert.Equal("Je overzicht van vandaag: 2 meldingen", digest.Subject);
        Assert.Contains("Bakkerij betaalde.", digest.Body);
        Assert.Contains("De btw moet binnen.", digest.Body);
        Assert.DoesNotContain("Al gelezen in de app.", digest.Body);
        Assert.Contains("https://potel.test/meldingen", digest.Body);

        // Eén overzicht per dag: wat er vandaag nog bijkomt, komt morgen.
        await NotifyAsync(t.Ws, NotificationKinds.Payment, "betaling:3003", "Nog een betaling.", except: t.AdminId);
        await MailRoundAsync();
        await Task.Delay(200);
        Assert.Single(MailsTo(t.OwnerEmail));
        At(4, 8);
        await MailRoundAsync();
        Assert.True(await WaitUntil(() => MailsTo(t.OwnerEmail).Count == 2, TimeSpan.FromSeconds(5)));
        Assert.Equal("Je overzicht van vandaag: 1 melding", MailsTo(t.OwnerEmail)[1].Subject);
        Assert.Contains("Nog een betaling.", MailsTo(t.OwnerEmail)[1].Body);
    }

    [Fact]
    public async Task Mail_stays_within_the_limit_per_user_and_only_goes_to_confirmed_addresses()
    {
        var t = await TeamAsync("limiet");
        await PutSettingsAsync(t.Owner, false, (NotificationKinds.Payment, true, true));
        await PutSettingsAsync(t.Admin, false, (NotificationKinds.Payment, true, true));
        InWorkspace(factory, t.Ws, db => db.Users.Where(u => u.Id == t.AdminId).ExecuteUpdate(x => x.SetProperty(u => u.EmailVerifiedAt, (DateTime?)null)));

        // Voor acht uur: drie losse mails, de rest wacht op het overzicht.
        At(5, 7);
        for (var i = 1; i <= 5; i++) await NotifyAsync(t.Ws, NotificationKinds.Payment, $"betaling:{2000 + i}", $"Betaling nummer {i}.");
        await MailRoundAsync();
        Assert.True(await WaitUntil(() => MailsTo(t.OwnerEmail).Count == 3, TimeSpan.FromSeconds(5)));
        At(5, 9);
        await MailRoundAsync();
        Assert.True(await WaitUntil(() => MailsTo(t.OwnerEmail).Count == 4, TimeSpan.FromSeconds(5)));
        var overflow = MailsTo(t.OwnerEmail, "Je overzicht").Single();
        Assert.Contains("zoveel meldingen per e-mail", overflow.Body);
        Assert.Contains("Betaling nummer 4.", overflow.Body);
        Assert.Contains("Betaling nummer 5.", overflow.Body);
        Assert.All(Rows(t.Ws, t.OwnerId), n => Assert.Equal(NotificationMail.Sent, n.Mail));

        // Een beheerder zonder bevestigd adres krijgt ze alleen in de app.
        Assert.Empty(MailsTo(t.AdminEmail));
        Assert.All(Rows(t.Ws, t.AdminId), n => Assert.Equal(NotificationMail.Skipped, n.Mail));
        Assert.Equal(5, await UnreadAsync(t.Admin));
    }

    [Fact]
    public async Task Without_a_mail_server_nothing_is_mailed_and_mail_settings_stay_as_they_were()
    {
        var t = await TeamAsync("zonder-mail");
        factory.Mail.Configured = false;
        try
        {
            var settings = await t.Owner.GetFromJsonAsync<JsonElement>("/api/notifications/settings");
            Assert.False(settings.GetProperty("emailAvailable").GetBoolean());
            await PutSettingsAsync(t.Owner, true, (NotificationKinds.Payment, true, true), (NotificationKinds.Quote, true, false));
            var s = InWorkspace(factory, t.Ws, db => db.NotificationSettings.Single(x => x.UserId == t.OwnerId));
            Assert.Equal(NotificationKinds.Quote, s.EmailOn);
            Assert.False(s.Digest);

            await NotifyAsync(t.Ws, NotificationKinds.Quote, "offerte:1001", except: t.AdminId);
            Assert.Null(Rows(t.Ws, t.OwnerId).Single().Mail);
            Assert.Single(await ItemsAsync(t.Owner));
        }
        finally
        {
            factory.Mail.Configured = true;
        }
        await MailRoundAsync();
        await Task.Delay(200);
        Assert.Empty(MailsTo(t.OwnerEmail));
    }

    [Fact]
    public async Task Read_notifications_are_cleaned_up_after_90_days()
    {
        var t = await TeamAsync("opruimen");
        var now = DateTime.UtcNow;
        factory.Clock.Value = now;
        InWorkspace(factory, t.Ws, db =>
        {
            db.Notifications.AddRange(
                new Notification { UserId = t.OwnerId, Kind = NotificationKinds.Payment, Key = "oud-gelezen", Title = "a", Text = "a", CreatedAt = now.AddDays(-120), ReadAt = now.AddDays(-100) },
                new Notification { UserId = t.OwnerId, Kind = NotificationKinds.Payment, Key = "recent-gelezen", Title = "b", Text = "b", CreatedAt = now.AddDays(-120), ReadAt = now.AddDays(-10) },
                new Notification { UserId = t.OwnerId, Kind = NotificationKinds.Payment, Key = "oud-ongelezen", Title = "c", Text = "c", CreatedAt = now.AddDays(-200) },
                new Notification { UserId = t.OwnerId, Kind = NotificationKinds.Payment, Key = "oud-alleen-mail", Title = "d", Text = "d", CreatedAt = now.AddDays(-100), InApp = false, Mail = NotificationMail.Sent });
            return db.SaveChanges();
        });
        using (var scope = factory.Services.CreateScope())
            await factory.Services.GetRequiredService<NotificationMailer>().CleanupAsync(scope.ServiceProvider.GetRequiredService<AppDb>());
        Assert.Equal(["recent-gelezen", "oud-ongelezen"], Rows(t.Ws, t.OwnerId).Select(n => n.Key));
    }

    [Fact]
    public async Task Removing_a_user_removes_their_notifications_and_settings()
    {
        var t = await TeamAsync("weg");
        await PutSettingsAsync(t.Admin, true);
        await NotifyAsync(t.Ws, NotificationKinds.Payment, "betaling:1201");
        Assert.Single(Rows(t.Ws, t.AdminId));
        (await t.Owner.DeleteAsync($"/api/users/{t.AdminId}")).EnsureSuccessStatusCode();
        Assert.Empty(Rows(t.Ws, t.AdminId));
        Assert.False(InWorkspace(factory, t.Ws, db => db.NotificationSettings.Any(s => s.UserId == t.AdminId)));
        Assert.Single(Rows(t.Ws, t.OwnerId));
    }
}

// De dagelijkse back-up mislukt (de map is een bestand): de planner voor back-ups maakt een melding voor de platformbeheerders.
public class BrokenBackupFactory : PortalFactory
{
    public string Blocker { get; } = Path.Combine(Path.GetTempPath(), $"potel-geen-map-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        File.WriteAllText(Blocker, "geen map");
        builder.UseSetting("Backup:Path", Blocker);
        builder.UseSetting("Backup:Daily", "true");
        builder.UseSetting("Backup:StartupDelaySeconds", "0");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(Blocker); } catch (IOException) { }
    }
}

public class BackupNotificationTests(BrokenBackupFactory factory) : IClassFixture<BrokenBackupFactory>
{
    [Fact]
    public async Task A_failing_daily_backup_notifies_the_platform_admins()
    {
        var platform = await TestApi.LoginAsync(factory, "admin@potel.nl", "welkom123");
        var owner = await TestApi.RegisterAsync(factory, $"geen-platform-{Guid.NewGuid():N}@example.com");
        Assert.True(await WaitUntil(async () => (await platform.GetFromJsonAsync<JsonElement>("/api/notifications/summary")).GetProperty("unread").GetInt32() > 0,
            TimeSpan.FromSeconds(30)));
        var item = (await platform.GetFromJsonAsync<JsonElement>("/api/notifications")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(NotificationKinds.Backup, item.GetProperty("kind").GetString());
        Assert.Contains("mislukt", item.GetProperty("text").GetString());
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>("/api/notifications")).GetProperty("items").EnumerateArray());
    }

    static async Task<bool> WaitUntil(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return true;
            await Task.Delay(100);
        }
        return await condition();
    }
}

// Het dagelijkse overzicht via de echte SmtpEmailSender, tegen een nepmailserver.
public class NotificationSmtpFactory : SmtpMailFactory
{
    public MovableClock Clock { get; } = new(DateTime.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("App:BaseUrl", "https://potel.test");
        builder.ConfigureTestServices(s => s.AddSingleton<BusinessClock>(Clock));
    }
}

public class NotificationSmtpTests(NotificationSmtpFactory factory) : IClassFixture<NotificationSmtpFactory>
{
    [Fact]
    public async Task The_daily_overview_goes_out_through_the_mail_server()
    {
        var email = $"smtp-overzicht-{Guid.NewGuid():N}@example.com";
        var owner = await TestApi.RegisterAsync(factory, email);
        var ws = await TestApi.WorkspaceIdAsync(owner);
        var settings = await owner.PutAsJsonAsync("/api/notifications/settings", new { digest = true, kinds = new[] { new { id = NotificationKinds.Vat, inApp = true, email = true } } });
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);

        factory.Clock.Value = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, 2, 6, 0, 0), BusinessClock.Zone);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = ws;
            var notifier = scope.ServiceProvider.GetRequiredService<Notifier>();
            await notifier.ToWorkspaceAsync(new NotificationEvent(NotificationKinds.Vat, "btw:2026-3:week", "Btw", "Je btw-aangifte moet over 7 dagen binnen zijn.", "/btw-aangifte"));
            await notifier.ToWorkspaceAsync(new NotificationEvent(NotificationKinds.Vat, "btw:2026-3:laatste-dag", "Btw", "Vandaag is de laatste dag.", "/btw-aangifte"));
        }
        factory.Clock.Value = factory.Clock.Value.AddHours(3);
        await factory.Services.GetRequiredService<NotificationMailer>().RunOnceAsync();

        Assert.True(await WaitUntil(() => factory.Smtp.Messages.Any(m => m.Contains($"RCPT TO:<{email}>") && m.Contains("Je overzicht van vandaag")), TimeSpan.FromSeconds(10)));
        var raw = factory.Smtp.Messages.Single(m => m.Contains($"RCPT TO:<{email}>") && m.Contains("Je overzicht van vandaag"));
        Assert.Contains("Subject: Je overzicht van vandaag: 2 meldingen", raw);
    }
}
