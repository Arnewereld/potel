using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Workflows;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

public class QuoteFactory : MailFactory
{
    public const string ClientIp = "203.0.113.7";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimit:PublicInvoicePerMinute", "10000");
        // De testserver kent geen IP-adres; doe alsof elk verzoek van hetzelfde adres komt.
        builder.ConfigureTestServices(s => s.AddSingleton<IStartupFilter>(new FixedIp()));
    }

    sealed class FixedIp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, n) =>
            {
                http.Connection.RemoteIpAddress ??= IPAddress.Parse(ClientIp);
                return n(http);
            });
            next(app);
        };
    }
}

// Offertes: een eigen nummerreeks bij versturen, vast na versturen, en een veilige link waarop de klant één keer accepteert
// of afwijst. Project en factuur maak je daarna zelf.
public class QuoteTests(QuoteFactory factory) : IClassFixture<QuoteFactory>
{
    static readonly Regex TokenInLink = new(@"https://potel\.test/offerte#token=([A-Za-z0-9_-]+)");

    record Setup(HttpClient Client, int Workspace, int Customer, string AdminEmail, string CustomerEmail);

    async Task<Setup> ReadyAsync(string prefix)
    {
        factory.Clock.SetDay(2026, 10, 12);
        var admin = $"{prefix}-{Guid.NewGuid():N}@example.com";
        var c = await TestApi.RegisterAsync(factory, admin);
        (await c.PutAsJsonAsync("/api/settings", Company())).EnsureSuccessStatusCode();
        var email = $"klant-{Guid.NewGuid():N}@example.com";
        var res = await c.PostAsJsonAsync("/api/customers", new { name = "Sanne de Vries", company = "Bakkerij De Vries", email, address = "Dorpsstraat 1", city = "1234 AB Utrecht" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return new Setup(c, await TestApi.WorkspaceIdAsync(c), (await res.JsonAsync()).GetProperty("id").GetInt32(), admin, email);
    }

    static object Body(int? customerId = null, int? leadId = null, decimal? deposit = null, string issue = "2026-10-12", string valid = "2026-11-11",
        string vatRegime = "normaal", object[]? lines = null, string subject = "Nieuwe webshop") => new
    {
        customerId, leadId, issueDate = issue, validUntil = valid, vatRegime, subject, intro = "Zoals besproken.", terms = "Prijzen zonder btw.", depositPercent = deposit,
        lines = lines ?? [new { description = "Webshop bouwen", quantity = 20, unit = "uur", unitPrice = 100, vatRate = 21 }, new { description = "Hosting", quantity = 12, unit = "maand", unitPrice = 25, vatRate = 21 }],
    };

    static async Task<int> CreateAsync(HttpClient c, object body)
    {
        var res = await c.PostAsJsonAsync("/api/quotes", body);
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await res.JsonAsync()).GetProperty("id").GetInt32();
    }

    static async Task<JsonElement> SendAsync(HttpClient c, int id)
    {
        var res = await c.PostAsync($"/api/quotes/{id}/send", null);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await res.JsonAsync();
    }

    static Task<JsonElement> GetAsync(HttpClient c, int id) => c.GetFromJsonAsync<JsonElement>($"/api/quotes/{id}");

    static async Task<string> LinkAsync(HttpClient c, int id)
    {
        var res = await c.PostAsync($"/api/quotes/{id}/link", null);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var path = (await res.JsonAsync()).GetProperty("path").GetString()!;
        Assert.StartsWith("/offerte#token=", path);
        return path["/offerte#token=".Length..];
    }

    async Task<(int Id, string Token)> SentWithLinkAsync(Setup s, decimal? deposit = null)
    {
        var id = await CreateAsync(s.Client, Body(s.Customer, deposit: deposit));
        await SendAsync(s.Client, id);
        return (id, await LinkAsync(s.Client, id));
    }

    // Openbare verzoeken: zonder cookie, net als je klant.
    HttpClient Anonymous() => factory.CreateClient();
    Task<HttpResponseMessage> ViewAsync(string? token) => Anonymous().PostAsJsonAsync("/api/public/quote", new { token });
    Task<HttpResponseMessage> AcceptAsync(string? token, string? name = "Sanne de Vries", bool agree = true) =>
        Anonymous().PostAsJsonAsync("/api/public/quote/accept", new { token, name, agree });
    Task<HttpResponseMessage> RejectAsync(string? token, string? reason = null) => Anonymous().PostAsJsonAsync("/api/public/quote/reject", new { token, reason });

    T In<T>(int ws, Func<AppDb, T> action) => WorkflowKit.InWorkspace(factory, ws, action);

    // Meldingen over offertes; de bevestigingsmail bij aanmelden telt niet mee.
    List<OutgoingEmail> Notices(string email) => factory.Mail.To(email).Where(m => m.Subject.StartsWith("Offerte")).ToList();

    [Fact]
    public async Task Numbers_are_given_on_send_in_their_own_series()
    {
        var s = await ReadyAsync("offerte-nummer");
        Assert.Equal("OFF-2026-001", (await s.Client.GetFromJsonAsync<JsonElement>("/api/quotes/next-number")).GetProperty("number").GetString());
        var first = await CreateAsync(s.Client, Body(s.Customer));
        var second = await CreateAsync(s.Client, Body(s.Customer));
        var draft = await GetAsync(s.Client, first);
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("number").ValueKind);
        Assert.Equal("concept", draft.GetProperty("state").GetString());

        // De volgorde van versturen telt, niet die van aanmaken.
        Assert.Equal("OFF-2026-001", (await SendAsync(s.Client, second)).GetProperty("number").GetString());
        Assert.Equal("OFF-2026-002", (await SendAsync(s.Client, first)).GetProperty("number").GetString());
        // Een voorbeeldofferte met een gewoon nummer telt niet mee.
        In(s.Workspace, db =>
        {
            db.Quotes.Add(new Quote { IsDemo = true, Number = "OFF-2026-050", CustomerId = s.Customer, Status = QuoteStatus.Sent });
            return db.SaveChanges();
        });
        Assert.Equal("OFF-2026-003", (await s.Client.GetFromJsonAsync<JsonElement>("/api/quotes/next-number")).GetProperty("number").GetString());

        // Facturen houden hun eigen reeks.
        var invoice = await GetInvoiceAsync(s.Client, await InvoiceAsync(s.Client, s.Customer, "verzonden", 1, 100));
        Assert.Equal("2026-0001", invoice.GetProperty("number").GetString());

        // Een andere werkruimte begint gewoon bij 001.
        var other = await ReadyAsync("offerte-nummer-ander");
        Assert.Equal("OFF-2026-001", (await SendAsync(other.Client, await CreateAsync(other.Client, Body(other.Customer)))).GetProperty("number").GetString());

        // Op de offertedatum van vandaag, met evenveel dagen geldigheid als in het concept.
        var late = await CreateAsync(s.Client, Body(s.Customer, issue: "2026-10-01", valid: "2026-10-15"));
        var sent = await SendAsync(s.Client, late);
        Assert.Equal("2026-10-12T00:00:00", sent.GetProperty("issueDate").GetString());
        Assert.Equal("2026-10-26T00:00:00", sent.GetProperty("validUntil").GetString());
    }

    [Fact]
    public async Task Quotes_stay_inside_their_own_workspace()
    {
        var a = await ReadyAsync("offerte-a");
        var b = await ReadyAsync("offerte-b");
        var (id, token) = await SentWithLinkAsync(a);
        var draft = await CreateAsync(a.Client, Body(a.Customer));
        var lead = (await (await a.Client.PostAsJsonAsync("/api/leads", new { name = "Lead van A" })).JsonAsync()).GetProperty("id").GetInt32();

        Assert.Empty((await b.Client.GetFromJsonAsync<List<JsonElement>>("/api/quotes"))!);
        foreach (var quote in new[] { id, draft })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/quotes/{quote}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PutAsJsonAsync($"/api/quotes/{quote}", Body(b.Customer))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Client.DeleteAsync($"/api/quotes/{quote}")).StatusCode);
            foreach (var action in new[] { "send", "withdraw", "duplicate", "link", "links/revoke" })
                Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsync($"/api/quotes/{quote}/{action}", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/quotes/{quote}/links")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Client.GetAsync($"/api/quotes/{quote}/mail-preview")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsJsonAsync($"/api/quotes/{quote}/mail", new { subject = "x", body = "{link}" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsJsonAsync($"/api/quotes/{quote}/project", new { billing = "vast" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsJsonAsync($"/api/quotes/{quote}/invoice", new { deposit = false })).StatusCode);
        }
        // Een klant of lead uit een andere werkruimte kies je niet.
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Client.PostAsJsonAsync("/api/quotes", Body(a.Customer))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Client.PostAsJsonAsync("/api/quotes", Body(leadId: lead))).StatusCode);
        // Ook zoeken vindt de offerte niet.
        Assert.Empty((await b.Client.GetFromJsonAsync<List<JsonElement>>("/api/search?q=OFF-2026"))!);
        Assert.Single((await a.Client.GetFromJsonAsync<List<JsonElement>>("/api/search?q=OFF-2026"))!, h => h.GetProperty("type").GetString() == "offerte");

        // Akkoord via de link landt in werkruimte A, ook als de klant toevallig bij B is ingelogd.
        Assert.Equal(HttpStatusCode.OK, (await b.Client.PostAsJsonAsync("/api/public/quote/accept", new { token, name = "Sanne de Vries", agree = true })).StatusCode);
        Assert.Equal("geaccepteerd", (await GetAsync(a.Client, id)).GetProperty("state").GetString());
        Assert.DoesNotContain(In(b.Workspace, db => db.Activities.Select(x => x.Text).ToList()), t => t.Contains("OFF-2026-001"));
    }

    [Fact]
    public async Task A_sent_quote_is_frozen_and_keeps_what_was_on_it()
    {
        var s = await ReadyAsync("offerte-vast");
        var (id, token) = await SentWithLinkAsync(s);

        var put = await s.Client.PutAsJsonAsync($"/api/quotes/{id}", Body(s.Customer, lines: [new { description = "Goedkoper", quantity = 1, unit = "stuk", unitPrice = 1, vatRate = 21 }]));
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal(QuoteEndpoints.FrozenError, await put.ErrorAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.DeleteAsync($"/api/quotes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync($"/api/quotes/{id}/send", null)).StatusCode);

        // Andere bedrijfsgegevens of een andere klantnaam veranderen de verstuurde offerte niet.
        (await s.Client.PutAsJsonAsync("/api/settings", Company(name: "Nieuwe Naam"))).EnsureSuccessStatusCode();
        (await s.Client.PutAsJsonAsync($"/api/customers/{s.Customer}", new { name = "Iemand Anders", company = "Andere Zaak", address = "Elders 2", city = "9999 ZZ Groningen" })).EnsureSuccessStatusCode();
        var quote = await GetAsync(s.Client, id);
        Assert.Equal("Mijn Bedrijf", quote.GetProperty("seller").GetProperty("name").GetString());
        Assert.Equal("Bakkerij De Vries", quote.GetProperty("buyer").GetProperty("name").GetString());
        Assert.Equal(2, quote.GetProperty("lines").GetArrayLength());
        Assert.Equal(2783m, quote.GetProperty("totals").GetProperty("total").GetDecimal());

        var view = (await (await ViewAsync(token)).JsonAsync()).GetProperty("quote");
        Assert.Equal("Mijn Bedrijf", view.GetProperty("seller").GetProperty("name").GetString());
        Assert.Equal("Bakkerij De Vries", view.GetProperty("buyer").GetProperty("name").GetString());
        Assert.Equal(2300m, view.GetProperty("totals").GetProperty("subtotal").GetDecimal());
        // Wat de klant niet hoeft te zien, staat er niet in.
        Assert.False(view.TryGetProperty("decidedIp", out _));
        Assert.False(view.GetProperty("buyer").TryGetProperty("email", out _));

        // Een klant met een offerte verwijder je niet.
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.DeleteAsync($"/api/customers/{s.Customer}")).StatusCode);
    }

    [Fact]
    public async Task A_new_version_replaces_the_old_one_when_it_is_sent()
    {
        var s = await ReadyAsync("offerte-versie");
        var (v1, token) = await SentWithLinkAsync(s);
        var res = await s.Client.PostAsync($"/api/quotes/{v1}/duplicate", null);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var v2 = await res.JsonAsync();
        var v2Id = v2.GetProperty("id").GetInt32();
        Assert.Equal(2, v2.GetProperty("version").GetInt32());
        Assert.Equal(v1, v2.GetProperty("previousVersionId").GetInt32());
        Assert.Equal("OFF-2026-001", v2.GetProperty("previousNumber").GetString());
        Assert.Equal("concept", v2.GetProperty("status").GetString());
        Assert.Equal(v2Id, (await GetAsync(s.Client, v1)).GetProperty("newerVersionId").GetInt32());
        // Nog een versie van versie 1 kan niet; die maak je vanuit versie 2.
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync($"/api/quotes/{v1}/duplicate", null)).StatusCode);

        // Zolang versie 2 een concept is, kan de klant versie 1 nog accepteren; na versturen niet meer.
        Assert.True((await (await ViewAsync(token)).JsonAsync()).GetProperty("canDecide").GetBoolean());
        (await s.Client.PutAsJsonAsync($"/api/quotes/{v2Id}", Body(s.Customer, lines: [new { description = "Webshop bouwen", quantity = 18, unit = "uur", unitPrice = 100, vatRate = 21 }]))).EnsureSuccessStatusCode();
        Assert.Equal("OFF-2026-002", (await SendAsync(s.Client, v2Id)).GetProperty("number").GetString());
        Assert.Equal("ingetrokken", (await GetAsync(s.Client, v1)).GetProperty("state").GetString());

        var view = await (await ViewAsync(token)).JsonAsync();
        Assert.False(view.GetProperty("canDecide").GetBoolean());
        Assert.Equal("ingetrokken", view.GetProperty("quote").GetProperty("state").GetString());
        Assert.Equal("OFF-2026-002", view.GetProperty("quote").GetProperty("newerNumber").GetString());
        var accept = await AcceptAsync(token);
        Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
        Assert.Contains("ingetrokken", await accept.ErrorAsync());
    }

    [Fact]
    public async Task A_wrong_revoked_or_expired_link_gets_the_same_404()
    {
        var s = await ReadyAsync("offerte-link");
        var (id, token) = await SentWithLinkAsync(s);
        var (_, revoked) = await SentWithLinkAsync(s);
        var revokedId = (await s.Client.GetFromJsonAsync<List<JsonElement>>("/api/quotes"))!.First(q => q.GetProperty("id").GetInt32() != id).GetProperty("id").GetInt32();
        Assert.Equal(1, (await (await s.Client.PostAsync($"/api/quotes/{revokedId}/links/revoke", null)).JsonAsync()).GetProperty("revoked").GetInt32());
        // Een concept of voorbeeldofferte krijgt geen link, en een link die er toch is (rechtstreeks in de database) werkt niet.
        var draft = await CreateAsync(s.Client, Body(s.Customer));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsync($"/api/quotes/{draft}/link", null)).StatusCode);
        const string draftToken = "conceptconceptconceptconceptconceptconcept1";
        const string demoToken = "voorbeeldvoorbeeldvoorbeeldvoorbeeldvoorbe1";
        In(s.Workspace, db =>
        {
            var demo = new Quote { IsDemo = true, Number = "VOORBEELD-OFF-001", CustomerId = s.Customer, Status = QuoteStatus.Sent, ValidUntil = new DateTime(2026, 12, 1) };
            db.Quotes.Add(demo);
            db.SaveChanges();
            db.QuoteLinks.Add(new QuoteLink { QuoteId = draft, TokenHash = Potel.Api.Invoicing.InvoiceMailer.Hash(draftToken), ExpiresAt = new DateTime(2030, 1, 1) });
            db.QuoteLinks.Add(new QuoteLink { QuoteId = demo.Id, TokenHash = Potel.Api.Invoicing.InvoiceMailer.Hash(demoToken), ExpiresAt = new DateTime(2030, 1, 1) });
            return db.SaveChanges();
        });

        async Task AllGone(string? t)
        {
            foreach (var res in new[] { await ViewAsync(t), await AcceptAsync(t), await RejectAsync(t, "Nee") })
            {
                Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
                Assert.Equal(QuoteEndpoints.LinkGone, await res.ErrorAsync());
                Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
            }
        }
        await AllGone(null);
        await AllGone("");
        await AllGone("verkeerd");
        await AllGone(token + "x");
        await AllGone(new string('a', 500));
        await AllGone(revoked);
        await AllGone(draftToken);
        await AllGone(demoToken);

        // Na de geldigheid werkt de link nog een jaar om terug te kijken, maar akkoord geven kan niet meer. Daarna is hij weg.
        factory.Clock.SetDay(2026, 12, 1);
        var view = await ViewAsync(token);
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        Assert.Equal("verlopen", (await view.JsonAsync()).GetProperty("quote").GetProperty("state").GetString());
        factory.Clock.SetDay(2027, 11, 13);
        await AllGone(token);
        factory.Clock.SetDay(2026, 10, 12);
        Assert.Equal(HttpStatusCode.OK, (await ViewAsync(token)).StatusCode);
        Assert.Equal(1, (await s.Client.GetFromJsonAsync<JsonElement>($"/api/quotes/{id}/links")).GetProperty("active").GetInt32());
    }

    [Fact]
    public async Task The_client_accepts_once_and_we_keep_name_time_and_ip()
    {
        var s = await ReadyAsync("offerte-akkoord");
        var (id, token) = await SentWithLinkAsync(s);
        var view = await (await ViewAsync(token)).JsonAsync();
        Assert.True(view.GetProperty("canDecide").GetBoolean());
        Assert.Equal("verstuurd", view.GetProperty("quote").GetProperty("state").GetString());
        Assert.Equal(1, (await s.Client.GetFromJsonAsync<JsonElement>($"/api/quotes/{id}/links")).GetProperty("opens").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(token, agree: false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(token, name: " ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await AcceptAsync(token, name: new string('x', 120))).StatusCode);
        Assert.Equal("verstuurd", (await GetAsync(s.Client, id)).GetProperty("state").GetString());

        factory.Clock.Value = new DateTime(2026, 10, 13, 9, 30, 0, DateTimeKind.Utc);
        var ok = await AcceptAsync(token, name: "  Sanne de Vries ");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var quote = await GetAsync(s.Client, id);
        Assert.Equal("geaccepteerd", quote.GetProperty("state").GetString());
        Assert.Equal("Sanne de Vries", quote.GetProperty("decidedName").GetString());
        Assert.Equal(QuoteFactory.ClientIp, quote.GetProperty("decidedIp").GetString());
        Assert.Equal(new DateTime(2026, 10, 13, 9, 30, 0), quote.GetProperty("decidedAt").GetDateTime());

        // Eén keer: nog een keer accepteren of alsnog afwijzen kan niet.
        var again = await AcceptAsync(token, name: "Iemand Anders");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("al geaccepteerd", await again.ErrorAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await RejectAsync(token)).StatusCode);
        Assert.Equal("Sanne de Vries", (await GetAsync(s.Client, id)).GetProperty("decidedName").GetString());
        var after = await (await ViewAsync(token)).JsonAsync();
        Assert.False(after.GetProperty("canDecide").GetBoolean());
        Assert.Equal("Sanne de Vries", after.GetProperty("quote").GetProperty("decidedName").GetString());

        // Intrekken kan dan ook niet meer, en er is vanzelf geen project of factuur gemaakt.
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync($"/api/quotes/{id}/withdraw", null)).StatusCode);
        Assert.Equal(0, In(s.Workspace, db => db.Projects.Count() + db.Invoices.Count()));
        Assert.Contains(In(s.Workspace, db => db.Activities.Select(a => a.Text).ToList()), t => t == "Offerte OFF-2026-001 geaccepteerd door Sanne de Vries");

        // De beheerder krijgt een mail, met een link naar de offerte.
        Assert.True(await WorkflowKit.WaitUntil(() => Notices(s.AdminEmail).Count == 1, TimeSpan.FromSeconds(5)));
        var mail = Assert.Single(Notices(s.AdminEmail));
        Assert.Equal("Offerte OFF-2026-001 is geaccepteerd", mail.Subject);
        Assert.Contains("Sanne de Vries", mail.Body);
        Assert.Contains($"https://potel.test/offertes/{id}", mail.Body);
        Assert.Empty(factory.Mail.To(s.CustomerEmail));

        // En een melding in de bel, met dezelfde link.
        var bell = (await s.Client.GetFromJsonAsync<JsonElement>("/api/notifications")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal("offerte", bell.GetProperty("kind").GetString());
        Assert.Equal("Bakkerij De Vries heeft offerte OFF-2026-001 geaccepteerd.", bell.GetProperty("text").GetString());
        Assert.Equal($"/offertes/{id}", bell.GetProperty("link").GetString());
    }

    [Fact]
    public async Task Two_accepts_at_the_same_time_count_once()
    {
        var s = await ReadyAsync("offerte-tegelijk");
        var (id, token) = await SentWithLinkAsync(s);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => SafeSend(() => AcceptAsync(token, name: $"Persoon {i}"))));
        Assert.Equal(1, results.Count(r => r?.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, In(s.Workspace, db => db.Activities.Count(a => a.Text.StartsWith("Offerte OFF-2026-001 geaccepteerd"))));
        Assert.Equal("geaccepteerd", (await GetAsync(s.Client, id)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task No_decision_after_the_validity_date_or_after_withdrawal()
    {
        var s = await ReadyAsync("offerte-geldig");
        var (lastDay, lastDayToken) = await SentWithLinkAsync(s);
        var (expired, expiredToken) = await SentWithLinkAsync(s);
        var (withdrawn, withdrawnToken) = await SentWithLinkAsync(s);

        // Tot en met de geldigheidsdatum mag het nog.
        factory.Clock.SetDay(2026, 11, 11);
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(lastDayToken)).StatusCode);
        Assert.Equal("geaccepteerd", (await GetAsync(s.Client, lastDay)).GetProperty("state").GetString());

        factory.Clock.SetDay(2026, 11, 12);
        Assert.Equal("verlopen", (await GetAsync(s.Client, expired)).GetProperty("state").GetString());
        foreach (var res in new[] { await AcceptAsync(expiredToken), await RejectAsync(expiredToken) })
        {
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Contains("geldig tot en met 11 november 2026", await res.ErrorAsync());
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync($"/api/quotes/{expired}/mail", new { subject = "Offerte", body = "{link}" })).StatusCode);

        factory.Clock.SetDay(2026, 10, 20);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsync($"/api/quotes/{withdrawn}/withdraw", null)).StatusCode);
        foreach (var res in new[] { await AcceptAsync(withdrawnToken), await RejectAsync(withdrawnToken) })
        {
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Contains("ingetrokken", await res.ErrorAsync());
        }
        var view = await (await ViewAsync(withdrawnToken)).JsonAsync();
        Assert.Equal("ingetrokken", view.GetProperty("quote").GetProperty("state").GetString());
        Assert.False(view.GetProperty("canDecide").GetBoolean());
        Assert.Equal("ingetrokken", (await GetAsync(s.Client, withdrawn)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Rejecting_keeps_the_reason_and_tells_the_admin()
    {
        var s = await ReadyAsync("offerte-afwijzen");
        var (id, token) = await SentWithLinkAsync(s);
        Assert.Equal(HttpStatusCode.BadRequest, (await RejectAsync(token, new string('x', QuoteEndpoints.MaxReasonLength + 1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RejectAsync(token, "Te duur voor ons.\nMisschien volgend jaar.")).StatusCode);
        var quote = await GetAsync(s.Client, id);
        Assert.Equal("afgewezen", quote.GetProperty("state").GetString());
        Assert.Equal("Te duur voor ons.\nMisschien volgend jaar.", quote.GetProperty("rejectReason").GetString());
        Assert.Equal(QuoteFactory.ClientIp, quote.GetProperty("decidedIp").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await AcceptAsync(token)).StatusCode);
        Assert.True(await WorkflowKit.WaitUntil(() => Notices(s.AdminEmail).Count == 1, TimeSpan.FromSeconds(5)));
        var mail = Assert.Single(Notices(s.AdminEmail));
        Assert.Equal("Offerte OFF-2026-001 is afgewezen", mail.Subject);
        Assert.Contains("Te duur voor ons.", mail.Body);
    }

    [Fact]
    public async Task Without_a_mail_server_the_decision_still_counts()
    {
        var s = await ReadyAsync("offerte-geen-mail");
        var (id, token) = await SentWithLinkAsync(s);
        factory.Mail.Configured = false;
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(token)).StatusCode);
        }
        finally
        {
            factory.Mail.Configured = true;
        }
        Assert.Equal("geaccepteerd", (await GetAsync(s.Client, id)).GetProperty("state").GetString());
        await Task.Delay(200);
        Assert.Empty(Notices(s.AdminEmail));
        // Het dashboard laat het zien.
        var recent = (await s.Client.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("quotes").GetProperty("recent");
        Assert.Equal("OFF-2026-001", Assert.Single(recent.EnumerateArray()).GetProperty("number").GetString());
    }

    [Fact]
    public async Task Accepting_a_quote_for_a_lead_makes_a_customer_and_starts_the_workflow()
    {
        var s = await ReadyAsync("offerte-lead");
        var lead = (await (await s.Client.PostAsJsonAsync("/api/leads", new { name = "Daan Mulder", company = "Mulder Retail", email = "daan@example.com", status = "contact", value = 5000 })).JsonAsync()).GetProperty("id").GetInt32();
        var graph = WorkflowKit.GraphJson(
            [WorkflowKit.Node("t", "trigger.quoteAccepted"), WorkflowKit.Node("a", "action.task", new() { ["title"] = "Kick-off {{quote.number}} met {{customer.company}} ({{quote.subtotal}}, {{quote.acceptedBy}})" })],
            [WorkflowKit.Edge("t", "a")]);
        await WorkflowKit.CreateWorkflowAsync(s.Client, "Na akkoord", graph, active: true);

        // Verlegde btw kan niet voor een lead: daarvoor weten we het btw-nummer nog niet.
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync("/api/quotes", Body(leadId: lead, vatRegime: "verlegd"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync("/api/quotes", new { customerId = s.Customer, leadId = lead, issueDate = "2026-10-12", validUntil = "2026-11-11", vatRegime = "normaal", lines = new[] { new { description = "x", quantity = 1, unit = "stuk", unitPrice = 1, vatRate = 21 } } })).StatusCode);
        var id = await CreateAsync(s.Client, Body(leadId: lead));
        var sent = await SendAsync(s.Client, id);
        Assert.Equal("Mulder Retail", sent.GetProperty("buyer").GetProperty("name").GetString());
        Assert.Equal("Daan Mulder", sent.GetProperty("buyer").GetProperty("contact").GetString());
        // Een lead met een verstuurde offerte staat op "offerte".
        Assert.Equal("offerte", In(s.Workspace, db => db.Leads.Single(l => l.Id == lead).Status));
        var token = await LinkAsync(s.Client, id);
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(token, name: "Daan Mulder")).StatusCode);

        var quote = await GetAsync(s.Client, id);
        var customerId = quote.GetProperty("customerId").GetInt32();
        var (status, leadCustomer, customerName) = In(s.Workspace, db =>
        {
            var l = db.Leads.Single(x => x.Id == lead);
            return (l.Status, l.CustomerId, db.Customers.Single(c => c.Id == customerId).Company);
        });
        Assert.Equal("gewonnen", status);
        Assert.Equal(customerId, leadCustomer);
        Assert.Equal("Mulder Retail", customerName);
        Assert.True(WorkflowKit.HasAppointment(factory, s.Workspace, "Kick-off OFF-2026-001 met Mulder Retail (2300.00, Daan Mulder)"));

        // Nu kan er een project van gemaakt worden, voor de nieuwe klant.
        var project = await s.Client.PostAsJsonAsync($"/api/quotes/{id}/project", new { billing = "vast" });
        Assert.Equal(HttpStatusCode.Created, project.StatusCode);
        var projectId = (await project.JsonAsync()).GetProperty("id").GetInt32();
        Assert.Equal(customerId, In(s.Workspace, db => db.Projects.Single(p => p.Id == projectId).CustomerId));
    }

    [Fact]
    public async Task After_acceptance_you_make_a_project_and_invoices_yourself()
    {
        var s = await ReadyAsync("offerte-vervolg");
        var (id, token) = await SentWithLinkAsync(s, deposit: 30);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync($"/api/quotes/{id}/project", new { billing = "vast" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync($"/api/quotes/{id}/invoice", new { deposit = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(token)).StatusCode);

        // Vaste prijs: het totaal zonder btw, met de uren uit de offerte als budget.
        var res = await s.Client.PostAsJsonAsync($"/api/quotes/{id}/project", new { billing = "vast" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var projectId = (await res.JsonAsync()).GetProperty("id").GetInt32();
        var project = In(s.Workspace, db => db.Projects.Single(p => p.Id == projectId));
        Assert.Equal((Billing.Fixed, 2300m, 20m, s.Customer, "Nieuwe webshop"), (project.Billing, project.FixedPrice, project.BudgetHours!.Value, project.CustomerId, project.Name));
        Assert.Equal(projectId, (await GetAsync(s.Client, id)).GetProperty("projectId").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/api/quotes/{id}/project", new { billing = "uur" })).StatusCode);

        // De aanbetaling: 30% van het bedrag zonder btw, met de btw van de regels.
        res = await s.Client.PostAsJsonAsync($"/api/quotes/{id}/invoice", new { deposit = true });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var deposit = await GetInvoiceAsync(s.Client, (await res.JsonAsync()).GetProperty("id").GetInt32());
        Assert.Equal("concept", deposit.GetProperty("status").GetString());
        var line = Assert.Single(deposit.GetProperty("lines").EnumerateArray());
        Assert.Equal("Aanbetaling 30% op offerte OFF-2026-001", line.GetProperty("description").GetString());
        Assert.Equal(690m, deposit.GetProperty("totals").GetProperty("subtotal").GetDecimal());
        Assert.Equal(834.90m, Total(deposit));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/api/quotes/{id}/invoice", new { deposit = true })).StatusCode);

        // De hele factuur: de regels van de offerte, min de aanbetaling.
        res = await s.Client.PostAsJsonAsync($"/api/quotes/{id}/invoice", new { deposit = false });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var full = await GetInvoiceAsync(s.Client, (await res.JsonAsync()).GetProperty("id").GetInt32());
        Assert.Equal(3, full.GetProperty("lines").GetArrayLength());
        Assert.StartsWith("Min aanbetaling", full.GetProperty("lines")[2].GetProperty("description").GetString());
        Assert.Equal(1610m, full.GetProperty("totals").GetProperty("subtotal").GetDecimal());
        Assert.Equal(2783m - 834.90m, Total(full));
        Assert.Contains("OFF-2026-001", full.GetProperty("notes").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/api/quotes/{id}/invoice", new { deposit = false })).StatusCode);

        // Per uur: zoveel uur als in het bedrag past, met het uurtarief van de offerte.
        var (hourly, hourlyToken) = await SentWithLinkAsync(s);
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(hourlyToken)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync($"/api/quotes/{hourly}/invoice", new { deposit = true })).StatusCode);
        res = await s.Client.PostAsJsonAsync($"/api/quotes/{hourly}/project", new { billing = "uur", name = "Webshop per uur" });
        var hourlyProject = In(s.Workspace, db => db.Projects.Single(p => p.Name == "Webshop per uur"));
        Assert.Equal((Billing.Hourly, 100m, 23m), (hourlyProject.Billing, hourlyProject.HourlyRate, hourlyProject.BudgetHours!.Value));
    }

    [Fact]
    public async Task Only_an_admin_sends_makes_links_withdraws_and_mails()
    {
        var s = await ReadyAsync("offerte-rollen");
        var (sent, _) = await SentWithLinkAsync(s);
        var email = $"medewerker-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(s.Client, email);
        var worker = await TestApi.LoginAsync(factory, email);

        // Een medewerker kan een concept maken en bekijken, maar niet namens het bedrijf versturen.
        var draft = await CreateAsync(worker, Body(s.Customer));
        Assert.Equal(HttpStatusCode.OK, (await worker.GetAsync($"/api/quotes/{sent}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.PostAsync($"/api/quotes/{draft}/send", null)).StatusCode);
        foreach (var action in new[] { "withdraw", "link" })
            Assert.Equal(HttpStatusCode.Forbidden, (await worker.PostAsync($"/api/quotes/{sent}/{action}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await worker.PostAsJsonAsync($"/api/quotes/{sent}/mail", new { subject = "Offerte", body = "{link}" })).StatusCode);
        Assert.Equal("concept", (await GetAsync(s.Client, draft)).GetProperty("state").GetString());
        Assert.Equal("verstuurd", (await GetAsync(s.Client, sent)).GetProperty("state").GetString());
        Assert.Equal(1, (await s.Client.GetFromJsonAsync<JsonElement>($"/api/quotes/{sent}/links")).GetProperty("active").GetInt32());
        // Een link dichtzetten is een veiligheidsmaatregel; dat mag een medewerker wel, net als bij facturen.
        Assert.Equal(HttpStatusCode.OK, (await worker.PostAsync($"/api/quotes/{sent}/links/revoke", null)).StatusCode);
        Assert.Equal(0, (await s.Client.GetFromJsonAsync<JsonElement>($"/api/quotes/{sent}/links")).GetProperty("active").GetInt32());
    }

    [Fact]
    public async Task A_read_only_workspace_can_still_withdraw_and_close_links()
    {
        var s = await ReadyAsync("offerte-alleen-lezen");
        var (sent, token) = await SentWithLinkAsync(s);
        var (other, otherToken) = await SentWithLinkAsync(s);
        var draft = await CreateAsync(s.Client, Body(s.Customer));
        await TestApi.ExpireTrialAsync(factory, s.Workspace);

        Assert.Equal(HttpStatusCode.PaymentRequired, (await s.Client.PostAsJsonAsync("/api/quotes", Body(s.Customer))).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await s.Client.PutAsJsonAsync($"/api/quotes/{draft}", Body(s.Customer))).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await s.Client.PostAsync($"/api/quotes/{draft}/send", null)).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await s.Client.PostAsync($"/api/quotes/{sent}/duplicate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await s.Client.PostAsync($"/api/quotes/{sent}/link", null)).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await s.Client.DeleteAsync($"/api/quotes/{draft}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.GetAsync($"/api/quotes/{sent}")).StatusCode);

        // Intrekken en links dichtzetten zijn veiligheidsmaatregelen; die mogen altijd.
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsync($"/api/quotes/{sent}/withdraw", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsync($"/api/quotes/{sent}/links/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ViewAsync(token)).StatusCode);
        // Je klant kan een offerte die al bij hem ligt nog gewoon accepteren.
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(otherToken)).StatusCode);
        Assert.Equal("geaccepteerd", (await GetAsync(s.Client, other)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Mailing_a_quote_sends_a_personal_link()
    {
        var s = await ReadyAsync("offerte-mail");
        var id = await CreateAsync(s.Client, Body(s.Customer));
        Assert.Contains("Verstuur de offerte eerst", (await s.Client.GetFromJsonAsync<JsonElement>($"/api/quotes/{id}/mail-preview")).GetProperty("problem").GetString());
        await SendAsync(s.Client, id);
        var preview = await s.Client.GetFromJsonAsync<JsonElement>($"/api/quotes/{id}/mail-preview");
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("problem").ValueKind);
        Assert.Equal(s.CustomerEmail, preview.GetProperty("to").GetString());
        var subject = preview.GetProperty("subject").GetString()!;
        var body = preview.GetProperty("body").GetString()!;
        Assert.Equal("Offerte OFF-2026-001 van Mijn Bedrijf", subject);
        Assert.Contains("{link}", body);
        Assert.Contains("11 november 2026", body);

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync($"/api/quotes/{id}/mail", new { subject, body = "Zonder link" })).StatusCode);
        var res = await s.Client.PostAsJsonAsync($"/api/quotes/{id}/mail", new { subject, body });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var mail = Assert.Single(factory.Mail.To(s.CustomerEmail));
        Assert.Equal("Mijn Bedrijf", mail.FromName);
        Assert.Equal("hallo@mijnbedrijf.nl", mail.ReplyTo);
        var token = TokenInLink.Match(mail.Body).Groups[1].Value;
        Assert.Equal(HttpStatusCode.OK, (await ViewAsync(token)).StatusCode);
        var links = await s.Client.GetFromJsonAsync<JsonElement>($"/api/quotes/{id}/links");
        Assert.Equal(s.CustomerEmail, links.GetProperty("mailedTo").GetString());

        // Gaat het mailen mis, dan gaat de nieuwe link meteen weer dicht.
        factory.Mail.Fail = true;
        try
        {
            Assert.Equal(HttpStatusCode.BadGateway, (await s.Client.PostAsJsonAsync($"/api/quotes/{id}/mail", new { subject, body })).StatusCode);
        }
        finally
        {
            factory.Mail.Fail = false;
        }
        Assert.Equal(1, (await s.Client.GetFromJsonAsync<JsonElement>($"/api/quotes/{id}/links")).GetProperty("active").GetInt32());
    }

    [Fact]
    public async Task The_dashboard_shows_open_quotes_and_the_acceptance_rate()
    {
        var s = await ReadyAsync("offerte-dashboard");
        var empty = (await s.Client.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("quotes");
        Assert.Equal(0, empty.GetProperty("open").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("acceptanceRate").ValueKind);

        var (_, accepted) = await SentWithLinkAsync(s);
        var (_, rejected) = await SentWithLinkAsync(s);
        await SentWithLinkAsync(s);
        await CreateAsync(s.Client, Body(s.Customer));
        var (withdrawn, _) = await SentWithLinkAsync(s);
        await AcceptAsync(accepted);
        await RejectAsync(rejected);
        await s.Client.PostAsync($"/api/quotes/{withdrawn}/withdraw", null);

        var tile = (await s.Client.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("quotes");
        Assert.Equal(1, tile.GetProperty("open").GetInt32());
        Assert.Equal(2300m, tile.GetProperty("openValue").GetDecimal());
        Assert.Equal(50, tile.GetProperty("acceptanceRate").GetInt32());
        Assert.Equal(2, tile.GetProperty("recent").GetArrayLength());

        // Verlopen telt als niet geaccepteerd.
        factory.Clock.SetDay(2026, 11, 20);
        tile = (await s.Client.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("quotes");
        Assert.Equal(0, tile.GetProperty("open").GetInt32());
        Assert.Equal(33, tile.GetProperty("acceptanceRate").GetInt32());
        factory.Clock.SetDay(2026, 10, 12);
    }

    [Fact]
    public async Task Deleting_the_workspace_takes_its_quotes_with_it()
    {
        var s = await ReadyAsync("offerte-weg");
        var other = await ReadyAsync("offerte-weg-ander");
        var (kept, keptToken) = await SentWithLinkAsync(other);

        // Een geaccepteerde offerte met een project en facturen eraan, een openstaande met een link en een concept.
        var (accepted, token) = await SentWithLinkAsync(s, deposit: 50);
        Assert.Equal(HttpStatusCode.OK, (await AcceptAsync(token)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await s.Client.PostAsJsonAsync($"/api/quotes/{accepted}/project", new { billing = "vast" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await s.Client.PostAsJsonAsync($"/api/quotes/{accepted}/invoice", new { deposit = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await s.Client.PostAsJsonAsync($"/api/quotes/{accepted}/invoice", new { deposit = false })).StatusCode);
        await SentWithLinkAsync(s);
        await CreateAsync(s.Client, Body(s.Customer));

        // Eerst nog in de export, met het akkoord erbij maar zonder links.
        var export = JsonDocument.Parse(await (await s.Client.GetAsync("/api/workspace/export")).Content.ReadAsStringAsync()).RootElement;
        var quotes = export.GetProperty("quotes").EnumerateArray().ToList();
        Assert.Equal(3, quotes.Count);
        Assert.Contains(quotes, q => q.GetProperty("decidedName").GetString() == "Sanne de Vries" && q.GetProperty("lines").GetArrayLength() == 2);
        Assert.False(export.TryGetProperty("quoteLinks", out _));

        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "geheim123" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await s.Client.SendAsync(delete)).StatusCode);
        Assert.Equal(0, In(s.Workspace, db => db.Quotes.Count() + db.QuoteLinks.Count()));
        Assert.Equal(0, In(s.Workspace, db => db.QuoteLines.Count(l => !db.Quotes.IgnoreQueryFilters().Any(q => q.Id == l.QuoteId))));
        Assert.Equal(HttpStatusCode.NotFound, (await ViewAsync(token)).StatusCode);

        // De andere werkruimte merkt er niets van.
        Assert.Equal(1, In(other.Workspace, db => db.Quotes.Count(q => q.Id == kept)));
        Assert.Equal(HttpStatusCode.OK, (await ViewAsync(keptToken)).StatusCode);
    }
}

// Voorbeeldoffertes in elke stand, met een eigen nummerreeks, en weg met de rest van de voorbeelddata.
public class QuoteDemoTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task Demo_quotes_show_every_state_and_go_with_the_demo_data()
    {
        var c = await TestApi.RegisterAsync(factory, $"offerte-demo-{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/workspace/demo-data", null)).StatusCode);
        (await c.PutAsJsonAsync("/api/settings", Company())).EnsureSuccessStatusCode();
        var quotes = (await c.GetFromJsonAsync<List<JsonElement>>("/api/quotes"))!;
        Assert.All(quotes, q => Assert.True(q.GetProperty("demo").GetBoolean()));
        Assert.Equal(["afgewezen", "concept", "geaccepteerd", "verlopen", "verstuurd"], quotes.Select(q => q.GetProperty("state").GetString()!).Distinct().Order());
        Assert.All(quotes.Where(q => q.GetProperty("number").ValueKind == JsonValueKind.String), q => Assert.StartsWith("VOORBEELD-OFF-", q.GetProperty("number").GetString()));
        Assert.Equal($"OFF-{new BusinessClock().Today.Year}-001", (await c.GetFromJsonAsync<JsonElement>("/api/quotes/next-number")).GetProperty("number").GetString());

        // Een voorbeeld verstuur, mail of deel je niet echt.
        var draft = quotes.First(q => q.GetProperty("state").GetString() == "concept").GetProperty("id").GetInt32();
        var open = quotes.First(q => q.GetProperty("state").GetString() == "verstuurd").GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/quotes/{draft}/send", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/quotes/{open}/link", null)).StatusCode);

        // Een kopie van een voorbeeld is een gewoon concept en blijft staan; de voorbeeldlead waar hij bij hoort ook.
        var copy = await c.PostAsync($"/api/quotes/{draft}/duplicate", null);
        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        var copyJson = await copy.JsonAsync();
        Assert.False(copyJson.GetProperty("demo").GetBoolean());
        Assert.Equal(1, copyJson.GetProperty("version").GetInt32());
        var dashboard = (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("quotes");
        Assert.True(dashboard.GetProperty("open").GetInt32() >= 1);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/workspace/demo-data")).StatusCode);
        var left = Assert.Single((await c.GetFromJsonAsync<List<JsonElement>>("/api/quotes"))!);
        Assert.Equal(copyJson.GetProperty("id").GetInt32(), left.GetProperty("id").GetInt32());
        var leads = (await c.GetFromJsonAsync<List<JsonElement>>("/api/leads"))!;
        Assert.Equal(left.GetProperty("leadId").GetInt32(), Assert.Single(leads).GetProperty("id").GetInt32());
    }
}

// De migratie voor offertes: bestaande gegevens blijven staan en offertes zijn standaard 30 dagen geldig.
public class QuoteMigrationTests : IDisposable
{
    const string Before = "20261013112618_Urenspecificatie";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-migratie-offertes-{Guid.NewGuid():N}.db");

    AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path}").Options, new Tenant());

    object? Scalar(string sql)
    {
        using var con = new SqliteConnection($"Data Source={path}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    [Fact]
    public void Existing_data_survives_and_quotes_default_to_thirty_days()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        using (var con = new SqliteConnection($"Data Source={path}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, WebhooksSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 0, 'Oud bedrijf', 'team');
                INSERT INTO Customers (Id, WorkspaceId, CreatedAt, Name, Country, IsDemo, RemindersOff) VALUES (1, 1, '2026-01-01 00:00:00', 'Jan', 'Nederland', 0, 0);
                INSERT INTO Leads (Id, WorkspaceId, Name, Value, Status, CreatedAt, IsDemo) VALUES (1, 1, 'Piet', 1000, 'offerte', '2026-01-01 00:00:00', 0);
                INSERT INTO Invoices (Id, WorkspaceId, CustomerId, Number, Status, IssueDate, DueDate, VatRegime, IsDemo, RemindersOff, HourSpecification, HourAmounts) VALUES
                    (1, 1, 1, '2026-0001', 'verzonden', '2026-09-01 00:00:00', '2026-09-15 00:00:00', 'normaal', 0, 0, 0, 0);
                """;
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO Settings (WorkspaceId, CompanyName, DefaultHourlyRate, PaymentTermDays, VatRegime, WeeklyHoursTarget, YearlyHoursTarget, BrandColor) VALUES (1, 'Oud bedrijf', 90, 30, 'normaal', 32, 1225, '#ff6d5a')";
            cmd.ExecuteNonQuery();
        }

        using (var db = Db()) db.GetService<IMigrator>().Migrate();

        Assert.Equal(30L, Scalar("SELECT QuoteValidDays FROM Settings WHERE WorkspaceId = 1"));
        Assert.Equal(30L, Scalar("SELECT PaymentTermDays FROM Settings WHERE WorkspaceId = 1"));
        Assert.Equal("2026-0001", Scalar("SELECT Number FROM Invoices WHERE Id = 1"));
        Assert.Equal("offerte", Scalar("SELECT Status FROM Leads WHERE Id = 1"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM Quotes"));
    }

    public void Dispose()
    {
        TestDatabase.Delete(path);
    }
}
