using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

public class PortalFactory2 : PortalFactory
{
    public MovableClock Clock { get; } = new(new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimit:PortalPerMinute", "10000");
        builder.UseSetting("RateLimit:PublicInvoicePerMinute", "10000");
        builder.ConfigureTestServices(s => s.AddSingleton<BusinessClock>(Clock));
    }
}

// Een strenge limiet op het klantportaal, om de begrenzing zelf te testen.
public class StrictPortalFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimit:PortalPerMinute", "3");
    }
}

// Het klantportaal: een geheime link per klant die alleen de gegevens van die klant laat zien, zoals de beheerder dat instelt.
public class PortalTests(PortalFactory2 factory) : IClassFixture<PortalFactory2>
{
    record Setup(HttpClient Admin, int Workspace, int Customer, int Other, int Project, string Invoice, string OtherInvoice, int InvoiceId);

    const string SecretNote = "Interne notitie: betaalt altijd laat";
    const string SecretDescription = "Marge op dit project is dun";

    async Task<Setup> SetupAsync(string prefix)
    {
        factory.Clock.SetDay(2026, 10, 12);
        var c = await ReadyAsync(factory, prefix);
        var ws = await TestApi.WorkspaceIdAsync(c);
        var customer = await CustomerAsync(c, "Bakkerij");
        var other = await CustomerAsync(c, "Concurrent");
        // De klant zoals hij is, met een bedrijfsnaam, een mailadres en een interne notitie erbij.
        var cust = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await (await c.GetAsync($"/api/customers/{customer}")).Content.ReadAsStringAsync())!
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        cust["company"] = "Bakkerij B.V.";
        cust["email"] = "inkoop@bakkerij.example";
        cust["notes"] = SecretNote;
        (await c.PutAsJsonAsync($"/api/customers/{customer}", cust)).EnsureSuccessStatusCode();

        async Task<int> Project(int customerId, string name, string status = "actief") =>
            (await (await c.PostAsJsonAsync("/api/projects", new
            {
                name, customerId, status, billing = "uur", hourlyRate = 123.45m, budgetHours = 40, color = "#000000", description = SecretDescription,
            })).JsonAsync()).GetProperty("id").GetInt32();
        var project = await Project(customer, "Webshop");
        var done = await Project(customer, "Oude site", "afgerond");
        var foreign = await Project(other, "Geheim project");

        async Task Time(int projectId, string date, int minutes, string description, bool billable = true) =>
            Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/time", new { projectId, date, minutes, description, billable })).StatusCode);
        await Time(project, "2026-10-08", 90, "Checkout bouwen");
        await Time(project, "2026-09-29", 120, "Betaalprovider koppelen");
        await Time(project, "2026-10-09", 60, "Intern overleg zonder factuur", billable: false);
        await Time(done, "2026-10-07", 30, "Afronding oude site"); // recent, maar het project is klaar
        await Time(foreign, "2026-10-08", 45, "Werk voor de concurrent");
        await ExpenseKit.CreateAsync(c, ExpenseKit.Body(supplier: "Geheime leverancier", customerId: customer));

        var invoiceId = await InvoiceAsync(c, customer, "verzonden", 2, 100, description: "Webshop oktober");
        await InvoiceAsync(c, customer, "concept", 1, 999, description: "Nog niet verstuurd concept");
        var otherInvoice = await InvoiceAsync(c, other, "verzonden", 1, 50, description: "Factuur concurrent");
        var number = (await GetInvoiceAsync(c, invoiceId)).GetProperty("number").GetString()!;
        var otherNumber = (await GetInvoiceAsync(c, otherInvoice)).GetProperty("number").GetString()!;
        return new Setup(c, ws, customer, other, project, number, otherNumber, invoiceId);
    }

    static async Task<string> LinkAsync(HttpClient c, int customer, int? days = null)
    {
        var res = await c.PostAsJsonAsync($"/api/customers/{customer}/portal/link", new { expiresInDays = days });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var path = (await res.JsonAsync()).GetProperty("path").GetString()!;
        Assert.StartsWith("/portaal#token=", path);
        return path["/portaal#token=".Length..];
    }

    Task<HttpResponseMessage> OpenAsync(string token) => factory.CreateClient().PostAsJsonAsync("/api/public/portal", new { token });

    async Task<(JsonElement Json, string Body)> PortalAsync(string token)
    {
        var res = await OpenAsync(token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        return (JsonDocument.Parse(body).RootElement, body);
    }

    static Task SettingsAsync(HttpClient c, int customer, bool hours = true, bool budget = true, bool invoices = true, bool amounts = false, string? note = null) =>
        c.PutAsJsonAsync($"/api/customers/{customer}/portal", new { showHours = hours, showBudget = budget, showInvoices = invoices, showAmounts = amounts, note })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    static string InvoiceToken(JsonElement portal, string number) =>
        portal.GetProperty("invoices").EnumerateArray().Single(i => i.GetProperty("number").GetString() == number).GetProperty("link").GetString()!["/factuur#token=".Length..];

    async Task AssertGoneAsync(string token)
    {
        var res = await OpenAsync(token);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(CustomerPortalEndpoints.LinkGone, await res.ErrorAsync());
    }

    [Fact]
    public async Task The_portal_shows_only_this_customer_and_stores_only_a_hash()
    {
        var s = await SetupAsync("portaal-afscherming");
        var token = await LinkAsync(s.Admin, s.Customer);
        Assert.True(token.Length >= 43, "Het token heeft minstens 32 willekeurige bytes");
        var stored = Assert.Single(WorkflowKit.InWorkspace(factory, s.Workspace, db => db.PortalLinks.Where(l => l.CustomerId == s.Customer).ToList()));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), stored.TokenHash);
        // De beheerder ziet het token daarna nergens meer terug.
        Assert.DoesNotContain(token, await (await s.Admin.GetAsync($"/api/customers/{s.Customer}/portal")).Content.ReadAsStringAsync());
        Assert.DoesNotContain(token, await (await s.Admin.GetAsync("/api/dashboard")).Content.ReadAsStringAsync());
        var export = await (await s.Admin.GetAsync("/api/workspace/export")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(token, export);
        Assert.DoesNotContain(stored.TokenHash, export);

        var res = await OpenAsync(token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("noindex", res.Headers.GetValues("X-Robots-Tag").Single());
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
        var body = await res.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        Assert.Equal("Mijn Bedrijf", json.GetProperty("company").GetProperty("name").GetString());
        Assert.Equal("Bakkerij B.V.", json.GetProperty("customer").GetProperty("name").GetString());
        var project = Assert.Single(json.GetProperty("projects").EnumerateArray());
        Assert.Equal("Webshop", project.GetProperty("name").GetString());
        // Alleen factureerbare uren tellen: 90 + 120 minuten, niet het interne overleg.
        Assert.Equal(210, project.GetProperty("minutes").GetInt32());
        Assert.Equal(40, project.GetProperty("budgetHours").GetDecimal());
        var weeks = project.GetProperty("weeks").EnumerateArray().ToList();
        Assert.Equal(CustomerPortalEndpoints.BurnWeeks, weeks.Count);
        Assert.Equal(0, weeks[^1].GetProperty("minutes").GetInt32()); // deze week, vanaf maandag 12 oktober
        Assert.Equal(90, weeks[^2].GetProperty("minutes").GetInt32()); // week van 5 oktober (de 8e)
        Assert.Equal(120, weeks[^3].GetProperty("minutes").GetInt32()); // week van 28 september
        Assert.Contains("Checkout bouwen", body);
        Assert.Contains(s.Invoice, body);

        // Niets van een andere klant, geen interne notities, geen niet-factureerbare uren, geen concepten of kosten.
        foreach (var secret in new[]
                 {
                     "Concurrent", "Geheim project", "Werk voor de concurrent", s.OtherInvoice, "Intern overleg", SecretNote, SecretDescription,
                     "Geheime leverancier", "inkoop@bakkerij.example", "Nog niet verstuurd", "Oude site", "Afronding", "123.45", "hallo@mijnbedrijf.nl", "Keizersgracht",
                     "NL001234567B01", "customerId", "workspaceId", "projectId", "hourlyRate\":1",
                 })
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        Assert.All(json.GetProperty("invoices").EnumerateArray(), i => Assert.NotEqual("concept", i.GetProperty("status").GetString()));

        // Geopend: dat ziet de beheerder bij de klant.
        var status = await (await s.Admin.GetAsync($"/api/customers/{s.Customer}/portal")).JsonAsync();
        Assert.Equal(1, status.GetProperty("link").GetProperty("opens").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("lastOpenedAt").ValueKind);

        // De pagina zelf hoort ook niet in zoekmachines.
        var page = await factory.CreateClient().GetAsync("/portaal");
        Assert.Contains("noindex", page.Headers.GetValues("X-Robots-Tag").Single());

        // Een andere werkruimte kan niets met deze klant, en ziet ook ingelogd in het portaal alleen deze klant.
        var other = await ReadyAsync(factory, "portaal-ander");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/customers/{s.Customer}/portal")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/customers/{s.Customer}/portal", new { showHours = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/customers/{s.Customer}/portal/link", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/customers/{s.Customer}/portal/revoke", null)).StatusCode);
        var asOther = await (await other.PostAsJsonAsync("/api/public/portal", new { token })).Content.ReadAsStringAsync();
        Assert.Contains("Bakkerij B.V.", asOther);
        Assert.DoesNotContain("Concurrent", asOther);
        Assert.Equal(1, WorkflowKit.InWorkspace(factory, s.Workspace, db => db.PortalLinks.Count(l => l.CustomerId == s.Customer && l.RevokedAt == null)));
    }

    [Fact]
    public async Task What_the_customer_sees_follows_the_settings()
    {
        var s = await SetupAsync("portaal-instellingen");
        var token = await LinkAsync(s.Admin, s.Customer);

        // Standaard: geen bedragen bij uren en projecten.
        var (json, body) = await PortalAsync(token);
        var project = json.GetProperty("projects")[0];
        Assert.Equal(JsonValueKind.Null, project.GetProperty("hourlyRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, project.GetProperty("value").ValueKind);
        Assert.All(json.GetProperty("entries").EnumerateArray(), e => Assert.Equal(JsonValueKind.Null, e.GetProperty("amount").ValueKind));
        Assert.False(json.GetProperty("visible").GetProperty("amounts").GetBoolean());
        Assert.DoesNotContain("123.45", body);

        await SettingsAsync(s.Admin, s.Customer, amounts: true, note: "Deze maand bouw ik de checkout.");
        (json, _) = await PortalAsync(token);
        project = json.GetProperty("projects")[0];
        Assert.Equal(123.45m, project.GetProperty("hourlyRate").GetDecimal());
        Assert.Equal(432.08m, project.GetProperty("value").GetDecimal()); // 3,5 uur × 123,45
        var entry = json.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("description").GetString() == "Checkout bouwen");
        Assert.Equal(185.18m, entry.GetProperty("amount").GetDecimal()); // 1,5 uur × 123,45
        Assert.Equal("Deze maand bouw ik de checkout.", json.GetProperty("note").GetString());
        // De instellingen gaan mee in de export van de werkruimte.
        Assert.Contains("Deze maand bouw ik de checkout.", await (await s.Admin.GetAsync("/api/workspace/export")).Content.ReadAsStringAsync());

        // Zonder uren: geen boekingen en geen uren per week, wel nog hoeveel van het budget op is.
        await SettingsAsync(s.Admin, s.Customer, hours: false);
        (json, body) = await PortalAsync(token);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("entries").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("projects")[0].GetProperty("weeks").ValueKind);
        Assert.Equal(210, json.GetProperty("projects")[0].GetProperty("minutes").GetInt32());
        Assert.DoesNotContain("Checkout bouwen", body);

        // Zonder uren en budget: alleen de projectnaam.
        var invoiceToken = InvoiceToken(json, s.Invoice);
        await SettingsAsync(s.Admin, s.Customer, hours: false, budget: false);
        (json, _) = await PortalAsync(token);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("projects")[0].GetProperty("minutes").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("projects")[0].GetProperty("budgetHours").ValueKind);

        // Zonder facturen: geen facturen, en een factuurlink uit het portaal werkt dan ook niet meer.
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/api/public/invoice", new { token = invoiceToken })).StatusCode);
        await SettingsAsync(s.Admin, s.Customer, invoices: false);
        (json, body) = await PortalAsync(token);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("invoices").ValueKind);
        Assert.DoesNotContain(s.Invoice, body);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().PostAsJsonAsync("/api/public/invoice", new { token = invoiceToken })).StatusCode);

        var tooLong = await s.Admin.PutAsJsonAsync($"/api/customers/{s.Customer}/portal", new { showHours = true, note = new string('x', 1001) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Wrong_revoked_and_expired_links_all_get_the_same_answer()
    {
        var s = await SetupAsync("portaal-intrekken");
        var token = await LinkAsync(s.Admin, s.Customer);
        await PortalAsync(token);

        var wrong = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');
        foreach (var t in new[] { wrong, "", "x", new string('a', 500), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))) })
            await AssertGoneAsync(t);

        // Een nieuwe link maakt de oude ongeldig.
        var second = await LinkAsync(s.Admin, s.Customer);
        await AssertGoneAsync(token);
        var (json, _) = await PortalAsync(second);
        var invoiceToken = InvoiceToken(json, s.Invoice);

        // Op de factuur telt de link uit het portaal mee als open link, maar alleen zolang het portaal werkt.
        async Task<int> ActiveInvoiceLinks() => (await (await s.Admin.GetAsync($"/api/invoices/{s.InvoiceId}/mails")).JsonAsync()).GetProperty("activeLinks").GetInt32();
        Assert.Equal(1, await ActiveInvoiceLinks());

        // Intrekken: het portaal en de factuurlinks eruit werken niet meer.
        var revoked = await (await s.Admin.PostAsync($"/api/customers/{s.Customer}/portal/revoke", null)).JsonAsync();
        Assert.Equal(1, revoked.GetProperty("revoked").GetInt32());
        Assert.Equal(JsonValueKind.Null, revoked.GetProperty("portal").GetProperty("link").ValueKind);
        await AssertGoneAsync(second);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().PostAsJsonAsync("/api/public/invoice", new { token = invoiceToken })).StatusCode);
        Assert.Equal(0, await ActiveInvoiceLinks());

        // Een link met een einddatum werkt tot die datum, daarna niet meer; de factuurlinks ook niet.
        var limited = await LinkAsync(s.Admin, s.Customer, days: 30);
        (json, _) = await PortalAsync(limited);
        Assert.NotEqual(JsonValueKind.Null, json.GetProperty("expiresAt").ValueKind);
        invoiceToken = InvoiceToken(json, s.Invoice);
        factory.Clock.Value = factory.Clock.Value.AddDays(31);
        await AssertGoneAsync(limited);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().PostAsJsonAsync("/api/public/invoice", new { token = invoiceToken })).StatusCode);
        var status = await (await s.Admin.GetAsync($"/api/customers/{s.Customer}/portal")).JsonAsync();
        Assert.True(status.GetProperty("link").GetProperty("expired").GetBoolean());

        foreach (var days in new[] { 0, -1, 3651 })
            Assert.Equal(HttpStatusCode.BadRequest, (await s.Admin.PostAsJsonAsync($"/api/customers/{s.Customer}/portal/link", new { expiresInDays = days })).StatusCode);
        // Een factuurtoken opent het portaal niet.
        await AssertGoneAsync(invoiceToken);
    }

    [Fact]
    public async Task Invoice_links_in_the_portal_open_only_that_invoice_and_stop_when_the_freelancer_revokes_them()
    {
        var s = await SetupAsync("portaal-factuur");
        var token = await LinkAsync(s.Admin, s.Customer);
        var (json, _) = await PortalAsync(token);
        var invoice = json.GetProperty("invoices").EnumerateArray().Single(i => i.GetProperty("number").GetString() == s.Invoice);
        Assert.Equal("verzonden", invoice.GetProperty("status").GetString());
        Assert.Equal(242m, invoice.GetProperty("openAmount").GetDecimal());
        var invoiceToken = InvoiceToken(json, s.Invoice);
        Assert.True(invoiceToken.Length >= 43);

        var res = await factory.CreateClient().PostAsJsonAsync("/api/public/invoice", new { token = invoiceToken });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(s.Invoice, (await res.JsonAsync()).GetProperty("invoice").GetProperty("number").GetString());

        // Elk bezoek geeft dezelfde link, zonder extra links in de database.
        var (again, _) = await PortalAsync(token);
        Assert.Equal(invoiceToken, InvoiceToken(again, s.Invoice));
        Assert.Equal(1, WorkflowKit.InWorkspace(factory, s.Workspace, db => db.InvoiceLinks.Count(l => l.InvoiceId == s.InvoiceId)));

        // Links van de factuur ingetrokken: het portaal toont de factuur nog, maar zonder link.
        (await s.Admin.PostAsync($"/api/invoices/{s.InvoiceId}/links/revoke", null)).EnsureSuccessStatusCode();
        (again, _) = await PortalAsync(token);
        var shown = again.GetProperty("invoices").EnumerateArray().Single(i => i.GetProperty("number").GetString() == s.Invoice);
        Assert.Equal(JsonValueKind.Null, shown.GetProperty("link").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().PostAsJsonAsync("/api/public/invoice", new { token = invoiceToken })).StatusCode);

        // Verlopen gaat vanzelf, en de status volgt de vervaldatum.
        factory.Clock.Value = factory.Clock.Value.AddDays(20);
        (again, _) = await PortalAsync(token);
        Assert.Equal("verlopen", again.GetProperty("invoices").EnumerateArray().Single(i => i.GetProperty("number").GetString() == s.Invoice).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Only_admins_manage_the_portal_and_a_read_only_workspace_can_still_revoke()
    {
        var s = await SetupAsync("portaal-rollen");
        var employeeEmail = $"medewerker-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(s.Admin, employeeEmail);
        var employee = await TestApi.LoginAsync(factory, employeeEmail);
        Assert.Equal(HttpStatusCode.OK, (await employee.GetAsync($"/api/customers/{s.Customer}/portal")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PutAsJsonAsync($"/api/customers/{s.Customer}/portal", new { showHours = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync($"/api/customers/{s.Customer}/portal/link", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsync($"/api/customers/{s.Customer}/portal/revoke", null)).StatusCode);

        var token = await LinkAsync(s.Admin, s.Customer);
        await TestApi.ExpireTrialAsync(factory, s.Workspace);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await s.Admin.PostAsJsonAsync($"/api/customers/{s.Customer}/portal/link", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await s.Admin.PutAsJsonAsync($"/api/customers/{s.Customer}/portal", new { showHours = true })).StatusCode);
        // Het portaal blijft te bekijken, en intrekken mag altijd.
        await PortalAsync(token);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/api/customers/{s.Customer}/portal/revoke", null)).StatusCode);
        await AssertGoneAsync(token);
    }

    [Fact]
    public async Task Deleting_the_customer_or_the_workspace_takes_the_portal_with_it()
    {
        factory.Clock.SetDay(2026, 10, 12);
        var email = $"portaal-weg-{Guid.NewGuid():N}@example.com";
        var c = await TestApi.RegisterAsync(factory, email);
        var customer = await TestApi.CreateCustomerAsync(c, "Losse klant");
        var token = await LinkAsync(c, customer);
        await SettingsAsync(c, customer, note: "Hallo");
        await PortalAsync(token);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/customers/{customer}")).StatusCode);
        await AssertGoneAsync(token);

        var second = await TestApi.CreateCustomerAsync(c, "Tweede klant");
        var secondToken = await LinkAsync(c, second);
        await SettingsAsync(c, second);
        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "geheim123" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(delete)).StatusCode);
        await AssertGoneAsync(secondToken);
    }
}

public class PortalRateLimitTests(StrictPortalFactory factory) : IClassFixture<StrictPortalFactory>
{
    [Fact]
    public async Task Guessing_portal_links_is_limited_per_address()
    {
        var anonymous = factory.CreateClient();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsJsonAsync("/api/public/portal", new { token = $"geraden-{i}" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await anonymous.PostAsJsonAsync("/api/public/portal", new { token = "nog-een" })).StatusCode);
    }
}
