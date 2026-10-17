using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Notifications;
using Potel.Api.Workflows;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

static class AccountantKit
{
    public const string Password = "boekhouder1";

    public static IEnumerable<OutgoingEmail> InviteMails(AccountMailFactory f, string to) =>
        f.Mail.Sent.Where(m => m.To == to && m.Subject.Contains("toegang als boekhouder"));

    public static string LatestInviteToken(AccountMailFactory f, string email) => AccountMailKit.TokenFrom(InviteMails(f, email).Last(), "/uitnodiging");

    public static Task<HttpResponseMessage> TryInviteAsync(HttpClient admin, string email, bool vat = false, bool expenses = false, string? password = null) =>
        admin.PostAsJsonAsync("/api/users", new { name = $"Boekhouder {email.Split('@')[0]}", email, role = "boekhouder", active = true, password, canFileVat = vat, canCompleteExpenses = expenses });

    public static async Task<int> InviteAsync(HttpClient admin, string email, bool vat = false, bool expenses = false)
    {
        var res = await TryInviteAsync(admin, email, vat, expenses);
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await res.JsonAsync()).GetProperty("id").GetInt32();
    }

    public static Task<HttpResponseMessage> AcceptAsync(WebApplicationFactory<Program> f, string token, string? password = Password, HttpClient? client = null) =>
        (client ?? Client(f)).PostAsJsonAsync("/api/auth/invite/accept", new { token, password });

    // Uitnodigen en aannemen met een nieuw wachtwoord; je bent daarna ingelogd in die werkruimte.
    public static async Task<HttpClient> NewAsync(AccountMailFactory f, HttpClient admin, string email, bool vat = false, bool expenses = false)
    {
        await InviteAsync(admin, email, vat, expenses);
        var c = Client(f);
        var res = await AcceptAsync(f, LatestInviteToken(f, email), client: c);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Assert.True((await res.JsonAsync()).GetProperty("signedIn").GetBoolean());
        return c;
    }

    // Een tweede werkruimte nodigt hetzelfde adres uit; dat account bestaat al, dus geen wachtwoord en niet meteen ingelogd.
    public static async Task JoinAsync(AccountMailFactory f, HttpClient admin, string email, bool vat = false, bool expenses = false)
    {
        await InviteAsync(admin, email, vat, expenses);
        var res = await AcceptAsync(f, LatestInviteToken(f, email), password: null);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Assert.False((await res.JsonAsync()).GetProperty("signedIn").GetBoolean());
    }

    public static async Task<HttpResponseMessage> SwitchAsync(HttpClient c, int workspaceId) =>
        await c.PostAsJsonAsync("/api/auth/switch", new { workspaceId });

    public static async Task<List<int>> InvoiceIdsAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/invoices")).EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();

    public static List<string> Activities(WebApplicationFactory<Program> f, int workspaceId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        db.Tenant.WorkspaceId = workspaceId;
        return db.Activities.OrderBy(a => a.Id).Select(a => a.Text).ToList();
    }

    public static User UserRow(WebApplicationFactory<Program> f, int id)
    {
        using var scope = f.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDb>().Users.IgnoreQueryFilters().AsNoTracking().Single(u => u.Id == id);
    }
}

// De rol boekhouder: per eindpunt wat wel en niet mag, de extra rechten, het abonnement, uitnodigen, wisselen tussen werkruimtes
// en het activiteitenlog bij een export.
public class AccountantTests(TwoFactorFactory f) : IClassFixture<TwoFactorFactory>
{
    static string Mail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    // Alles wat een boekhouder mag bekijken. Elk ander GET-eindpunt geeft hem 403.
    static readonly HashSet<string> Reads =
    [
        "GET /api/auth/me", "GET /api/auth/two-factor", "GET /api/auth/workspaces", "GET /api/workspace", "GET /api/settings",
        "GET /api/invoices", "GET /api/invoices/{id:int}", "GET /api/invoices/{id:int}/hours", "GET /api/invoices/{id:int}/hours.csv",
        "GET /api/quotes", "GET /api/quotes/{id:int}",
        "GET /api/expenses", "GET /api/expenses/{id:int}", "GET /api/expenses/{id:int}/receipt",
        "GET /api/recurring-expenses", "GET /api/recurring-expenses/{id:int}",
        "GET /api/bank", "GET /api/trips", "GET /api/trips/{id:int}", "GET /api/investments",
        "GET /api/tax/vat-return", "GET /api/tax/annual", "GET /api/tax/annual.csv", "GET /api/tax/reserve", "GET /api/tax/reserve/settings",
        "GET /api/customers", "GET /api/customers/{id:int}",
        "GET /api/notifications", "GET /api/notifications/summary", "GET /api/notifications/settings",
    ];

    // Wijzigingen aan je eigen account: die mogen altijd.
    static readonly HashSet<string> Personal =
    [
        "PUT /api/auth/password", "POST /api/auth/two-factor/setup", "POST /api/auth/two-factor/enable", "POST /api/auth/two-factor/disable",
        "POST /api/auth/two-factor/recovery-codes", "POST /api/auth/verify-email/resend", "POST /api/auth/switch",
        "POST /api/notifications/{id:int}/read", "POST /api/notifications/read", "PUT /api/notifications/settings",
    ];

    // Alleen met een recht dat de beheerder gaf.
    static readonly HashSet<string> WithPermission =
    [
        "PUT /api/tax/vat-return/filed", "PUT /api/expenses/{id:int}/category", "PUT /api/expenses/{id:int}/receipt",
    ];

    static List<(string Key, string Method, string Path)> Routes(WebApplicationFactory<Program> factory)
    {
        var list = new List<(string, string, string)>();
        foreach (var e in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var raw = "/" + (e.RoutePattern.RawText ?? "").Trim('/');
            if (!raw.StartsWith("/api/", StringComparison.Ordinal) || e.Metadata.GetMetadata<IAllowAnonymous>() is not null) continue;
            var path = "/" + string.Join('/', e.RoutePattern.PathSegments.Select(s => string.Concat(s.Parts.Select(p => p switch
            {
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternLiteralPart l => l.Content,
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternSeparatorPart sep => sep.Content,
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternParameterPart { IsCatchAll: true } => "iets",
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternParameterPart q => q.ParameterPolicies.Any(x => x.Content == "int") ? "1" : "iets",
                _ => "",
            }))));
            foreach (var method in e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                list.Add(($"{method} {raw}", method, path));
        }
        return list;
    }

    static async Task<HttpStatusCode> SendAsync(HttpClient c, string method, string path)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT" or "PATCH" or "DELETE") req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return (await c.SendAsync(req)).StatusCode;
    }

    // De belangrijkste bewaking: elk eindpunt onder /api, ook wat later bijkomt. Een boekhouder zonder extra rechten krijgt 403 op elke
    // wijziging behalve zijn eigen account, en op elk GET-eindpunt dat niet in de lijst hierboven staat.
    [Fact]
    public async Task Every_endpoint_refuses_an_accountant_except_the_explicitly_allowed_ones()
    {
        var admin = await RegisterAsync(f, Mail("bh-loop-baas"));
        var acc = await AccountantKit.NewAsync(f, admin, Mail("bh-loop"));
        var routes = Routes(f);
        Assert.True(routes.Count > 150, $"maar {routes.Count} eindpunten gevonden");
        Assert.Empty(Reads.Concat(Personal).Concat(WithPermission).Where(key => !routes.Any(r => r.Key == key)));

        var wrong = new List<string>();
        foreach (var (key, method, path) in routes)
        {
            var status = await SendAsync(acc, method, path);
            var allowed = Reads.Contains(key) || Personal.Contains(key);
            if (allowed && status == HttpStatusCode.Forbidden) wrong.Add($"{key} gaf 403 maar hoort open te zijn");
            if (!allowed && status != HttpStatusCode.Forbidden) wrong.Add($"{key} gaf {(int)status} in plaats van 403");
        }
        Assert.Empty(wrong);
        // Na de hele ronde is de boekhouder nog gewoon ingelogd, en er is niets gewijzigd.
        Assert.Equal(HttpStatusCode.OK, (await acc.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task With_both_permissions_only_the_three_permission_endpoints_open_up()
    {
        var admin = await RegisterAsync(f, Mail("bh-rechten-baas"));
        var acc = await AccountantKit.NewAsync(f, admin, Mail("bh-rechten"), vat: true, expenses: true);
        var wrong = new List<string>();
        foreach (var (key, method, path) in Routes(f))
        {
            if (method == "GET" || Personal.Contains(key)) continue;
            var status = await SendAsync(acc, method, path);
            var allowed = WithPermission.Contains(key);
            if (allowed && status == HttpStatusCode.Forbidden) wrong.Add($"{key} gaf 403");
            if (!allowed && status != HttpStatusCode.Forbidden) wrong.Add($"{key} gaf {(int)status} in plaats van 403");
        }
        Assert.Empty(wrong);
    }

    [Fact]
    public async Task Marking_a_vat_return_as_filed_needs_the_permission()
    {
        var admin = await RegisterAsync(f, Mail("bh-btw-baas"));
        var email = Mail("bh-btw");
        var userId = await AccountantKit.InviteAsync(admin, email);
        var acc = Client(f);
        Assert.Equal(HttpStatusCode.OK, (await AccountantKit.AcceptAsync(f, AccountantKit.LatestInviteToken(f, email), client: acc)).StatusCode);
        var last = DateTime.Today.AddMonths(-3);
        var body = new { year = last.Year, quarter = (last.Month - 1) / 3 + 1, filed = true };

        var refused = await acc.PutAsJsonAsync("/api/tax/vat-return/filed", body);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(Accountants.Forbidden, await refused.ErrorAsync());

        // De beheerder geeft het recht; de sessie van de boekhouder kan het meteen, zonder opnieuw in te loggen.
        var name = AccountantKit.UserRow(f, userId).Name;
        (await admin.PutAsJsonAsync($"/api/users/{userId}", new { name, email, role = "boekhouder", active = true, canFileVat = true })).EnsureSuccessStatusCode();
        var ok = await acc.PutAsJsonAsync("/api/tax/vat-return/filed", body);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.NotNull((await ok.JsonAsync()).GetProperty("filedAt").GetString());
        // Kosten aanvullen mag hij nog steeds niet.
        var expense = (await ExpenseKit.CreateAsync(admin, ExpenseKit.Body())).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await acc.PutAsJsonAsync($"/api/expenses/{expense}/category", new { category = "software" })).StatusCode);

        // Recht weer weg: meteen dicht.
        (await admin.PutAsJsonAsync($"/api/users/{userId}", new { name, email, role = "boekhouder", active = true, canFileVat = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await acc.PutAsJsonAsync("/api/tax/vat-return/filed", new { body.year, body.quarter, filed = false })).StatusCode);
    }

    [Fact]
    public async Task Completing_expenses_means_adding_a_receipt_or_a_category_and_nothing_more()
    {
        var admin = await RegisterAsync(f, Mail("bh-kosten-baas"));
        var acc = await AccountantKit.NewAsync(f, admin, Mail("bh-kosten"), expenses: true);
        var id = (await ExpenseKit.CreateAsync(admin, ExpenseKit.Body(category: "overig"))).GetProperty("id").GetInt32();

        var cat = await acc.PutAsJsonAsync($"/api/expenses/{id}/category", new { category = "software" });
        Assert.Equal(HttpStatusCode.OK, cat.StatusCode);
        Assert.Equal("software", (await cat.JsonAsync()).GetProperty("category").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await acc.PutAsJsonAsync($"/api/expenses/{id}/category", new { category = "iets" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await ExpenseKit.UploadAsync(acc, id, ExpenseKit.Png)).StatusCode);
        // Een bestaand bonnetje vervangen of weghalen mag alleen een beheerder.
        Assert.Equal(HttpStatusCode.Forbidden, (await ExpenseKit.UploadAsync(acc, id, ExpenseKit.Pdf, "ander.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await acc.DeleteAsync($"/api/expenses/{id}/receipt")).StatusCode);
        // De uitgave zelf wijzigen, verwijderen of een nieuwe maken kan niet.
        Assert.Equal(HttpStatusCode.Forbidden, (await acc.PutAsJsonAsync($"/api/expenses/{id}", ExpenseKit.Body(amount: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await acc.DeleteAsync($"/api/expenses/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await acc.PostAsJsonAsync("/api/expenses", ExpenseKit.Body())).StatusCode);
        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/expenses/{id}");
        Assert.Equal(121m, after.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task An_accountant_reads_the_books_but_not_the_rest()
    {
        var admin = await InvoiceKit.ReadyAsync(f, "bh-lezen");
        var customer = await InvoiceKit.CustomerAsync(admin);
        var invoice = await InvoiceKit.InvoiceAsync(admin, customer, "concept", 2, 100);
        var acc = await AccountantKit.NewAsync(f, admin, Mail("bh-lezen"));

        Assert.Contains(invoice, await AccountantKit.InvoiceIdsAsync(acc));
        Assert.Equal(HttpStatusCode.OK, (await acc.GetAsync($"/api/invoices/{invoice}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await acc.GetAsync("/api/bank")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await acc.GetAsync("/api/tax/reserve")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await acc.GetAsync($"/api/customers/{customer}")).StatusCode);
        // Van een klant alleen de factuurgegevens: je eigen notities ziet de boekhouder nergens, ook niet in een factuur.
        f.WithDb(db => db.Customers.IgnoreQueryFilters().Where(c => c.Id == customer).ExecuteUpdate(x => x.SetProperty(c => c.Notes, "Lastige klant, eerst vooruit laten betalen")));
        Assert.Equal("Lastige klant, eerst vooruit laten betalen", (await admin.GetFromJsonAsync<JsonElement>($"/api/customers/{customer}")).GetProperty("notes").GetString());
        foreach (var url in new[] { $"/api/customers/{customer}", "/api/customers", $"/api/invoices/{invoice}", "/api/invoices" })
            Assert.DoesNotContain("Lastige klant", await acc.GetStringAsync(url));
        Assert.False((await acc.GetFromJsonAsync<JsonElement>($"/api/customers/{customer}")).TryGetProperty("notes", out _));
        // Geen uren, projecten, leads, planning, werkstromen, gebruikers, geheimen of het dashboard.
        foreach (var url in new[] { "/api/time", "/api/projects", "/api/leads", "/api/appointments", "/api/workflows", "/api/users", "/api/settings/mollie",
                     "/api/github/settings", "/api/timer", "/api/dashboard", "/api/search?q=kl", "/api/insights", "/api/customers/1/portal", "/api/workspace/export",
                     "/api/calendar-feed", "/api/modules" })
            Assert.Equal(HttpStatusCode.Forbidden, (await acc.GetAsync(url)).StatusCode);
        // Niets versturen, wijzigen of verwijderen.
        Assert.Equal(HttpStatusCode.Forbidden, (await InvoiceKit.SetStatusAsync(acc, invoice, "verzonden")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await acc.DeleteAsync($"/api/invoices/{invoice}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await acc.PutAsJsonAsync("/api/settings", InvoiceKit.Company(name: "Overgenomen"))).StatusCode);
        Assert.Equal("Mijn Bedrijf", (await admin.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("companyName").GetString());
    }

    [Fact]
    public async Task Accountants_do_not_count_towards_the_plan_but_have_their_own_limit()
    {
        var adminEmail = Mail("bh-plan-baas");
        var admin = await RegisterAsync(f, adminEmail);
        await SetPlanAsync(f, await WorkspaceIdAsync(admin), "zzp");
        // ZZP heeft ruimte voor één gebruiker: dat is de eigenaar zelf.
        var employee = await admin.PostAsJsonAsync("/api/users", new { name = "Mw", email = Mail("bh-plan-mw"), role = "medewerker", active = true, password = "geheim123" });
        Assert.Equal(HttpStatusCode.PaymentRequired, employee.StatusCode);
        for (var i = 0; i < Plans.MaxAccountants; i++) await AccountantKit.InviteAsync(admin, Mail($"bh-plan-{i}"));
        var tooMany = await AccountantKit.TryInviteAsync(admin, Mail("bh-plan-extra"));
        Assert.Equal(HttpStatusCode.PaymentRequired, tooMany.StatusCode);
        Assert.Contains($"hooguit {Plans.MaxAccountants} boekhouders", await tooMany.ErrorAsync());
        // Nog steeds geen plek voor een medewerker: boekhouders maken geen ruimte en nemen er ook geen in.
        Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.PostAsJsonAsync("/api/users", new { name = "Mw", email = Mail("bh-plan-mw2"), role = "medewerker", active = true, password = "geheim123" })).StatusCode);
    }

    [Fact]
    public async Task An_invited_accountant_chooses_their_own_password_and_the_admin_never_can()
    {
        var admin = await RegisterAsync(f, Mail("bh-uitnodigen-baas"));
        var email = Mail("bh-uitnodigen");
        Assert.Equal(HttpStatusCode.BadRequest, (await AccountantKit.TryInviteAsync(admin, email, password: "geheim123")).StatusCode);
        var id = await AccountantKit.InviteAsync(admin, email);
        var mail = AccountantKit.InviteMails(f, email).Single();
        Assert.Contains("boekhouder", mail.Body);
        Assert.DoesNotContain("geheim", mail.Body);

        // Zolang de uitnodiging niet is aangenomen, kan niemand inloggen, ook niet met een leeg wachtwoord.
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLoginAsync(f, email, "")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLoginAsync(f, email, AccountantKit.Password)).Status);
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/users");
        var row = list.EnumerateArray().Single(u => u.GetProperty("id").GetInt32() == id);
        Assert.True(row.GetProperty("invitePending").GetBoolean());
        Assert.Equal("boekhouder", row.GetProperty("role").GetString());

        // Wachtwoord, adres en rol van een boekhouder wijzigt een beheerder niet.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/users/{id}", new { name = "B", email, role = "boekhouder", active = true, password = "overgenomen1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/users/{id}", new { name = "B", email = Mail("bh-ander"), role = "boekhouder", active = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/users/{id}", new { name = "B", email, role = "beheerder", active = true })).StatusCode);
        // Een medewerker kan ook geen boekhouder worden; die nodig je uit.
        var employee = await CreateUserAsync(admin, Mail("bh-uitnodigen-mw"));
        var employeeEmail = AccountantKit.UserRow(f, employee).Email;
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/users/{employee}", new { name = "Mw", email = employeeEmail, role = "boekhouder", active = true })).StatusCode);

        var info = await Client(f).PostAsJsonAsync("/api/auth/invite", new { token = AccountantKit.LatestInviteToken(f, email) });
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        Assert.False((await info.JsonAsync()).GetProperty("existingAccount").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await AccountantKit.AcceptAsync(f, AccountantKit.LatestInviteToken(f, email), "kort")).StatusCode);
        var accepted = await AccountantKit.AcceptAsync(f, AccountantKit.LatestInviteToken(f, email));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        // De link werkt maar één keer.
        Assert.Equal(HttpStatusCode.BadRequest, (await AccountantKit.AcceptAsync(f, AccountantKit.LatestInviteToken(f, email))).StatusCode);
        var acc = await LoginAsync(f, email, AccountantKit.Password);
        var me = await acc.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("boekhouder", me.GetProperty("role").GetString());
        Assert.True(me.GetProperty("emailVerified").GetBoolean());

        // Op de pagina Gebruikers: wanneer de boekhouder het laatst inlogde.
        row = (await admin.GetFromJsonAsync<JsonElement>("/api/users")).EnumerateArray().Single(u => u.GetProperty("id").GetInt32() == id);
        Assert.False(row.GetProperty("invitePending").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("lastLoginAt").ValueKind);
    }

    [Fact]
    public async Task An_address_that_is_a_normal_account_cannot_become_an_accountant_and_the_other_way_round()
    {
        var admin = await RegisterAsync(f, Mail("bh-adres-baas"));
        var otherOwner = Mail("bh-adres-ander");
        await RegisterAsync(f, otherOwner);
        var refused = await AccountantKit.TryInviteAsync(admin, otherOwner);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(AuthEndpoints.EmailUnavailable, await refused.ErrorAsync());

        var email = Mail("bh-adres");
        await AccountantKit.NewAsync(f, admin, email);
        Assert.Equal(HttpStatusCode.Conflict, (await TryRegisterAsync(f, email)).Status);
        var other = await RegisterAsync(f, Mail("bh-adres-derde"));
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsJsonAsync("/api/users", new { name = "X", email, role = "medewerker", active = true, password = "geheim123" })).StatusCode);
        // Twee keer in dezelfde werkruimte ook niet.
        Assert.Equal(HttpStatusCode.Conflict, (await AccountantKit.TryInviteAsync(admin, email)).StatusCode);
    }

    [Fact]
    public async Task An_accountant_switches_between_clients_without_ever_mixing_their_data()
    {
        var adminA = await InvoiceKit.ReadyAsync(f, "bh-wissel-a");
        var adminB = await InvoiceKit.ReadyAsync(f, "bh-wissel-b");
        var adminC = await InvoiceKit.ReadyAsync(f, "bh-wissel-c");
        var wsA = await WorkspaceIdAsync(adminA);
        var wsB = await WorkspaceIdAsync(adminB);
        var wsC = await WorkspaceIdAsync(adminC);
        var invoiceA = await InvoiceKit.InvoiceAsync(adminA, await InvoiceKit.CustomerAsync(adminA, "Klant A"), "concept", 1, 100);
        var invoiceB = await InvoiceKit.InvoiceAsync(adminB, await InvoiceKit.CustomerAsync(adminB, "Klant B"), "concept", 1, 200);
        var email = Mail("bh-wissel");
        var acc = await AccountantKit.NewAsync(f, adminA, email);
        await AccountantKit.JoinAsync(f, adminB, email);
        // Werkruimte C nodigt uit, maar de uitnodiging is nog niet aangenomen.
        await AccountantKit.InviteAsync(adminC, email);

        var choices = (await acc.GetFromJsonAsync<JsonElement>("/api/auth/workspaces")).EnumerateArray().ToList();
        Assert.Equal([wsA, wsB], choices.Select(c => c.GetProperty("workspaceId").GetInt32()).Order().ToList());
        Assert.True(choices.Single(c => c.GetProperty("workspaceId").GetInt32() == wsA).GetProperty("current").GetBoolean());
        Assert.Equal([invoiceA], await AccountantKit.InvoiceIdsAsync(acc));

        var switched = await AccountantKit.SwitchAsync(acc, wsB);
        Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
        // Een nieuwe inlogcookie voor de rij in B.
        Assert.Contains(switched.Headers.GetValues("Set-Cookie"), c => c.StartsWith("potel.auth=", StringComparison.Ordinal));
        Assert.Equal(wsB, (await switched.JsonAsync()).GetProperty("workspaceId").GetInt32());
        Assert.Equal(wsB, await WorkspaceIdAsync(acc));
        Assert.Equal([invoiceB], await AccountantKit.InvoiceIdsAsync(acc));
        Assert.Equal(HttpStatusCode.NotFound, (await acc.GetAsync($"/api/invoices/{invoiceA}")).StatusCode);
        // Niet naar een werkruimte met een open uitnodiging, of waar je geen boekhouder bent.
        Assert.Equal(HttpStatusCode.NotFound, (await AccountantKit.SwitchAsync(acc, wsC)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AccountantKit.SwitchAsync(acc, 1)).StatusCode);
        Assert.Equal(wsB, await WorkspaceIdAsync(acc));

        // Inloggen: je komt binnen waar je het laatst was.
        var again = await LoginAsync(f, email, AccountantKit.Password);
        Assert.Equal(wsB, await WorkspaceIdAsync(again));
    }

    [Fact]
    public async Task Removing_or_disabling_an_accountant_ends_their_access_immediately()
    {
        var adminA = await RegisterAsync(f, Mail("bh-weg-a"));
        var adminB = await RegisterAsync(f, Mail("bh-weg-b"));
        var wsA = await WorkspaceIdAsync(adminA);
        var wsB = await WorkspaceIdAsync(adminB);
        var email = Mail("bh-weg");
        var inA = await AccountantKit.NewAsync(f, adminA, email);
        await AccountantKit.JoinAsync(f, adminB, email);
        var inB = await LoginAsync(f, email, AccountantKit.Password);
        if (await WorkspaceIdAsync(inB) != wsB) (await AccountantKit.SwitchAsync(inB, wsB)).EnsureSuccessStatusCode();
        var rowB = (await adminB.GetFromJsonAsync<JsonElement>("/api/users")).EnumerateArray().Single(u => u.GetProperty("email").GetString() == email);
        var idB = rowB.GetProperty("id").GetInt32();

        // Uitschakelen in B: de sessie in B stopt meteen, A blijft werken en kan niet meer naar B.
        (await adminB.PutAsJsonAsync($"/api/users/{idB}", new { name = "B", email, role = "boekhouder", active = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await inB.GetAsync("/api/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await inA.GetAsync("/api/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AccountantKit.SwitchAsync(inA, wsB)).StatusCode);
        Assert.DoesNotContain((await inA.GetFromJsonAsync<JsonElement>("/api/auth/workspaces")).EnumerateArray(), c => c.GetProperty("workspaceId").GetInt32() == wsB);

        // Weer aan, wisselen, en dan verwijderen: weer meteen dicht.
        (await adminB.PutAsJsonAsync($"/api/users/{idB}", new { name = "B", email, role = "boekhouder", active = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await AccountantKit.SwitchAsync(inA, wsB)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await adminB.DeleteAsync($"/api/users/{idB}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await inA.GetAsync("/api/invoices")).StatusCode);
        // Inloggen kan nog, in A.
        var back = await LoginAsync(f, email, AccountantKit.Password);
        Assert.Equal(wsA, await WorkspaceIdAsync(back));
        Assert.Single((await back.GetFromJsonAsync<JsonElement>("/api/auth/workspaces")).EnumerateArray());
    }

    [Fact]
    public async Task Password_and_two_step_login_are_the_same_in_every_workspace()
    {
        var adminA = await RegisterAsync(f, Mail("bh-sync-a"));
        var adminB = await RegisterAsync(f, Mail("bh-sync-b"));
        var wsB = await WorkspaceIdAsync(adminB);
        var email = Mail("bh-sync");
        var acc = await AccountantKit.NewAsync(f, adminA, email);
        await AccountantKit.JoinAsync(f, adminB, email);
        var otherDevice = await LoginAsync(f, email, AccountantKit.Password);

        // Een nieuw wachtwoord: overal, en andere sessies stoppen.
        Assert.Equal(HttpStatusCode.NoContent, (await acc.PutAsJsonAsync("/api/auth/password", new { current = AccountantKit.Password, @new = "nieuwgeheim2" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherDevice.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLoginAsync(f, email, AccountantKit.Password)).Status);
        var c = await LoginAsync(f, email, "nieuwgeheim2");
        Assert.Equal(HttpStatusCode.OK, (await AccountantKit.SwitchAsync(c, wsB)).StatusCode);

        // Inloggen in twee stappen aanzetten in B geldt ook voor A, met dezelfde herstelcodes.
        var enabled = await TwoFactorKit.EnableAsync(f, c, "nieuwgeheim2");
        var rows = f.WithDb(db => db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Email == email).ToList());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, u => Assert.True(u.TwoFactorEnabled));
        Assert.Single(rows.Select(u => u.TwoFactorSecret).Distinct());
        var fresh = Client(f);
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await TwoFactorKit.PasswordStepAsync(fresh, email, "nieuwgeheim2")));
        Assert.Equal(HttpStatusCode.OK, (await TwoFactorKit.CodeStepAsync(fresh, enabled.RecoveryCodes[0])).StatusCode);
        // Die herstelcode is nu in alle werkruimtes gebruikt.
        var other = Client(f);
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await TwoFactorKit.PasswordStepAsync(other, email, "nieuwgeheim2")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await TwoFactorKit.CodeStepAsync(other, enabled.RecoveryCodes[0])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TwoFactorKit.CodeStepAsync(other, enabled.RecoveryCodes[1])).StatusCode);
        // Na het inloggen wisselen vraagt geen nieuwe code.
        var ws = await WorkspaceIdAsync(other);
        var target = ws == wsB ? await WorkspaceIdAsync(adminA) : wsB;
        Assert.Equal(HttpStatusCode.OK, (await AccountantKit.SwitchAsync(other, target)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/invoices")).StatusCode);
    }

    [Fact]
    public async Task A_workspace_can_require_two_step_login_for_accountants_too()
    {
        var adminA = await RegisterAsync(f, Mail("bh-2fa-a"));
        var bossB = Mail("bh-2fa-b");
        var adminB = await RegisterAsync(f, bossB);
        var wsA = await WorkspaceIdAsync(adminA);
        var wsB = await WorkspaceIdAsync(adminB);
        var email = Mail("bh-2fa");
        var acc = await AccountantKit.NewAsync(f, adminA, email);
        await AccountantKit.JoinAsync(f, adminB, email);

        // Voor boekhouders alleen als het ook voor beheerders verplicht is.
        await TwoFactorKit.EnableAsync(f, adminB);
        var only = await (await adminB.PutAsJsonAsync("/api/workspace/two-factor", new { requireForAdmins = false, requireForAccountants = true })).JsonAsync();
        Assert.False(only.GetProperty("requireTwoFactorForAccountants").GetBoolean());
        var both = await (await adminB.PutAsJsonAsync("/api/workspace/two-factor", new { requireForAdmins = true, requireForAccountants = true })).JsonAsync();
        Assert.True(both.GetProperty("requireTwoFactorForAccountants").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await AccountantKit.SwitchAsync(acc, wsB)).StatusCode);
        var blocked = await acc.GetAsync("/api/invoices");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.True((await blocked.JsonAsync()).GetProperty("twoFactorSetupRequired").GetBoolean());
        Assert.True((await acc.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("twoFactorSetupRequired").GetBoolean());
        // Terug naar A kan wel: daar is het niet verplicht.
        Assert.Equal(HttpStatusCode.OK, (await AccountantKit.SwitchAsync(acc, wsA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await acc.GetAsync("/api/invoices")).StatusCode);
        // Uit voor beheerders betekent ook uit voor boekhouders.
        var off = await (await adminB.PutAsJsonAsync("/api/workspace/two-factor", new { requireForAdmins = false })).JsonAsync();
        Assert.False(off.GetProperty("requireTwoFactorForAccountants").GetBoolean());
    }

    [Fact]
    public async Task Every_export_by_an_accountant_lands_in_the_activity_log()
    {
        var admin = await InvoiceKit.ReadyAsync(f, "bh-export");
        var ws = await WorkspaceIdAsync(admin);
        var customer = await InvoiceKit.CustomerAsync(admin);
        var project = await InvoiceKit.ProjectAsync(admin, customer, 90, "Site");
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/time", new { projectId = project, date = "2026-10-01", minutes = 60, description = "Bouwen", billable = true })).StatusCode);
        var invoice = (await (await admin.PostAsJsonAsync($"/api/projects/{project}/invoice", new { detailed = false })).JsonAsync()).GetProperty("id").GetInt32();
        var expense = (await ExpenseKit.CreateAsync(admin, ExpenseKit.Body(supplier: "Bol"))).GetProperty("id").GetInt32();
        (await ExpenseKit.UploadAsync(admin, expense, ExpenseKit.Png)).EnsureSuccessStatusCode();
        // Een beheerder die exporteert, komt er niet in.
        (await admin.GetAsync("/api/tax/annual.csv?year=2026")).EnsureSuccessStatusCode();
        Assert.DoesNotContain(AccountantKit.Activities(f, ws), a => a.Contains("gedownload"));

        var acc = await AccountantKit.NewAsync(f, admin, Mail("bh-export"));
        (await acc.GetAsync("/api/tax/annual.csv?year=2026")).EnsureSuccessStatusCode();
        (await acc.GetAsync($"/api/expenses/{expense}/receipt")).EnsureSuccessStatusCode();
        (await acc.GetAsync($"/api/invoices/{invoice}/hours.csv")).EnsureSuccessStatusCode();
        // Wat niet lukt, is ook geen export.
        Assert.Equal(HttpStatusCode.NotFound, (await acc.GetAsync("/api/expenses/999999/receipt")).StatusCode);

        var log = AccountantKit.Activities(f, ws).Where(a => a.Contains("gedownload")).ToList();
        Assert.Equal(3, log.Count);
        Assert.Contains(log, a => a.Contains("(boekhouder) heeft het jaaroverzicht 2026 als CSV gedownload"));
        Assert.Contains(log, a => a.Contains("heeft het bonnetje bij Bol van 12-05-2026 gedownload"));
        Assert.Contains(log, a => a.Contains("heeft de urenspecificatie van factuur (concept) als CSV gedownload"));
    }

    [Fact]
    public async Task An_accountant_only_gets_the_vat_deadline_and_only_with_that_permission()
    {
        var plain = new User { Role = Roles.Accountant };
        var vat = new User { Role = Roles.Accountant, CanFileVat = true };
        Assert.Empty(NotificationKinds.For(plain));
        Assert.Equal([NotificationKinds.Vat], NotificationKinds.For(vat).Select(k => k.Id).ToList());
        Assert.False(NotificationKinds.Allowed(NotificationKinds.Workflow, plain));
        // Een boekhouder kan geen platformbeheerder worden.
        var admin = await RegisterAsync(f, Mail("bh-melding-baas"));
        var email = Mail("bh-melding");
        await AccountantKit.NewAsync(f, admin, email);
        var owner = await LoginAsync(f, "admin@potel.nl", "welkom123");
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/platform/admins", new { email })).StatusCode);
    }

    [Fact]
    public async Task A_read_only_workspace_still_lets_the_accountant_read_and_export()
    {
        var admin = await RegisterAsync(f, Mail("bh-alleen-lezen"));
        var acc = await AccountantKit.NewAsync(f, admin, Mail("bh-alleen-lezen-acc"), vat: true);
        await ExpireTrialAsync(f, await WorkspaceIdAsync(admin));
        Assert.Equal(HttpStatusCode.OK, (await acc.GetAsync("/api/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await acc.GetAsync("/api/tax/annual.csv")).StatusCode);
        var last = DateTime.Today.AddMonths(-3);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await acc.PutAsJsonAsync("/api/tax/vat-return/filed", new { year = last.Year, quarter = (last.Month - 1) / 3 + 1, filed = true })).StatusCode);
    }
}
