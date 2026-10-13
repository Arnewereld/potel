using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Invoicing;
using Potel.Api.Payments;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Potel met een openbaar adres, zonder Mollie-sleutel van het platform: iDEAL op facturen gebruikt alleen de sleutels van
// werkruimtes. De nagebootste Mollie kent per sleutel zijn eigen betalingen.
public class IdealFactory : PortalFactory
{
    public readonly FakeMollie Mollie = new();
    protected virtual string? BaseUrl => "https://potel.example/";
    protected virtual int PayPerMinute => 10000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("App:BaseUrl", BaseUrl ?? "");
        builder.UseSetting("RateLimit:InvoicePayPerMinute", PayPerMinute.ToString());
        builder.UseSetting("RateLimit:PublicInvoicePerMinute", "10000");
        builder.UseSetting("RateLimit:PortalPerMinute", "10000");
        builder.ConfigureTestServices(s => s.AddHttpClient<MollieClient>().ConfigurePrimaryHttpMessageHandler(() => Mollie));
    }
}

public class NoBaseUrlIdealFactory : IdealFactory
{
    protected override string? BaseUrl => null;
}

public class LocalBaseUrlIdealFactory : IdealFactory
{
    protected override string? BaseUrl => "http://localhost:5080";
}

public class SlowPayIdealFactory : IdealFactory
{
    protected override int PayPerMinute => 2;
}

static class IdealKit
{
    public static string NewKey(string mode = "test") => $"{mode}_{Guid.NewGuid():N}";

    // Een werkruimte met bedrijfsgegevens en een eigen Mollie-sleutel waarbij iDEAL aan staat.
    public static async Task<(HttpClient C, int Ws, string Key)> MerchantAsync(IdealFactory f, string prefix, string[]? methods = null)
    {
        var c = await ReadyAsync(f, prefix);
        var key = NewKey();
        f.Mollie.Accounts[key] = methods ?? ["ideal"];
        (await c.PutAsJsonAsync("/api/settings/mollie", new { key })).EnsureSuccessStatusCode();
        return (c, await TestApi.WorkspaceIdAsync(c), key);
    }

    // Een werkende factuurlink, zoals in de mail.
    public static string Link(PortalFactory f, int ws, int invoiceId)
    {
        var token = InvoiceMailer.NewToken();
        WorkflowKit.InWorkspace(f, ws, db => { db.InvoiceLinks.Add(new InvoiceLink { InvoiceId = invoiceId, TokenHash = InvoiceMailer.Hash(token) }); return db.SaveChanges(); });
        return token;
    }

    public static Task<HttpResponseMessage> PayAsync(PortalFactory f, string token, object? extra = null) =>
        f.CreateClient().PostAsJsonAsync("/api/public/invoice/pay", extra ?? new { token });

    public static async Task<string> CheckoutAsync(PortalFactory f, string token)
    {
        var res = await PayAsync(f, token);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.JsonAsync()).GetProperty("checkoutUrl").GetString()!;
    }

    public static string PaymentId(string checkoutUrl) => checkoutUrl.Split('/').Last();

    public static Task<HttpResponseMessage> WebhookAsync(PortalFactory f, string id) =>
        f.CreateClient().PostAsync("/api/public/invoice/mollie-webhook", new FormUrlEncodedContent([new("id", id)]));

    public static async Task<JsonElement> PageAsync(PortalFactory f, string token) =>
        await (await f.CreateClient().PostAsJsonAsync("/api/public/invoice", new { token })).JsonAsync();

    // De code uit het terugkeeradres dat we Mollie gaven.
    public static string ReturnRef(FakeMollie m, string paymentId)
    {
        var url = m.Payments[paymentId]["redirectUrl"]!.GetValue<string>();
        return url[(url.IndexOf("betaling=", StringComparison.Ordinal) + "betaling=".Length)..];
    }

    public static List<JsonElement> Payments(JsonElement inv) => inv.GetProperty("payments").EnumerateArray().ToList();
}

// iDEAL op facturen via de eigen Mollie van de freelancer: de sleutel, het bedrag (altijd van de server), de webhook die alleen
// op een id vertrouwt na navragen met de sleutel van die werkruimte, en één betaling per Mollie-betaling, ook tegelijk.
public class IdealTests(IdealFactory factory) : IClassFixture<IdealFactory>
{
    FakeMollie Mollie => factory.Mollie;

    int Runs(int ws) => WorkflowKit.InWorkspace(factory, ws, db => db.WorkflowRuns.Count());

    [Fact]
    public async Task The_key_is_stored_encrypted_never_returned_and_can_be_removed_even_when_read_only()
    {
        var c = await ReadyAsync(factory, "mollie-sleutel");
        var ws = await TestApi.WorkspaceIdAsync(c);
        var key = IdealKit.NewKey();

        var none = await c.GetFromJsonAsync<JsonElement>("/api/settings/mollie");
        Assert.Equal(JsonValueKind.Null, none.GetProperty("key").ValueKind);
        Assert.False(none.GetProperty("available").GetBoolean());

        var saved = await c.PutAsJsonAsync("/api/settings/mollie", new { key });
        var text = await saved.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(key, text);
        var dto = JsonDocument.Parse(text).RootElement;
        Assert.Equal("test", dto.GetProperty("key").GetProperty("mode").GetString());
        Assert.Equal(key[^4..], dto.GetProperty("key").GetProperty("last4").GetString());
        Assert.True(dto.GetProperty("available").GetBoolean());
        Assert.DoesNotContain(key, await c.GetStringAsync("/api/settings/mollie"));
        Assert.DoesNotContain(key, await c.GetStringAsync("/api/workspace/export"));
        Assert.DoesNotContain(key[5..], await c.GetStringAsync("/api/workspace/export"));

        // In de database staat hij versleuteld, en alleen met de sleutels van Potel weer te lezen.
        var stored = WorkflowKit.InWorkspace(factory, ws, db => db.MollieAccounts.Single().ProtectedKey);
        Assert.DoesNotContain(key[5..], stored);
        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        Assert.Equal(key, dp.CreateProtector(InvoiceIdeal.ProtectorPurpose).Unprotect(stored));

        var live = IdealKit.NewKey("live");
        var liveDto = await (await c.PutAsJsonAsync("/api/settings/mollie", new { key = live })).JsonAsync();
        Assert.Equal("live", liveDto.GetProperty("key").GetProperty("mode").GetString());
        Assert.Equal(1, WorkflowKit.InWorkspace(factory, ws, db => db.MollieAccounts.Count()));

        foreach (var bad in new[] { "access_abcdefghijklmnopqrstuvwxyz123456", "sk_live_1234567890abcdefghijkl", "test_kort", "test_met spatie 1234567890abcdef", "" })
        {
            var res = await c.PutAsJsonAsync("/api/settings/mollie", new { key = bad });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
        Assert.Contains("organisatietoken", await (await c.PutAsJsonAsync("/api/settings/mollie", new { key = "access_abcdefghijklmnopqrstuvwxyz123456" })).ErrorAsync());

        await TestApi.ExpireTrialAsync(factory, ws);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PutAsJsonAsync("/api/settings/mollie", new { key })).StatusCode);
        var removed = await c.DeleteAsync("/api/settings/mollie");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await removed.JsonAsync()).GetProperty("key").ValueKind);
        Assert.Equal(0, WorkflowKit.InWorkspace(factory, ws, db => db.MollieAccounts.Count()));
    }

    [Fact]
    public async Task Only_an_admin_sets_tests_or_removes_the_key_and_other_workspaces_never_see_it()
    {
        var (admin, ws, key) = await IdealKit.MerchantAsync(factory, "mollie-rol");
        var email = $"mw-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(admin, email);
        var employee = await TestApi.LoginAsync(factory, email);

        // Een medewerker ziet dat er een sleutel is, maar kan er niets mee.
        Assert.Equal("test", (await employee.GetFromJsonAsync<JsonElement>("/api/settings/mollie")).GetProperty("key").GetProperty("mode").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PutAsJsonAsync("/api/settings/mollie", new { key = IdealKit.NewKey() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.DeleteAsync("/api/settings/mollie")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsync("/api/settings/mollie/test", null)).StatusCode);

        var other = await ReadyAsync(factory, "mollie-ander");
        Assert.Equal(JsonValueKind.Null, (await other.GetFromJsonAsync<JsonElement>("/api/settings/mollie")).GetProperty("key").ValueKind);
        (await other.DeleteAsync("/api/settings/mollie")).EnsureSuccessStatusCode();
        Assert.Equal(1, WorkflowKit.InWorkspace(factory, ws, db => db.MollieAccounts.Count()));
        Assert.Equal(key[^4..], (await admin.GetFromJsonAsync<JsonElement>("/api/settings/mollie")).GetProperty("key").GetProperty("last4").GetString());
    }

    [Fact]
    public async Task Testing_the_connection_uses_the_workspace_key_and_says_whether_ideal_is_on()
    {
        var (c, _, key) = await IdealKit.MerchantAsync(factory, "mollie-test");
        var ok = await (await c.PostAsync("/api/settings/mollie/test", null)).JsonAsync();
        Assert.True(ok.GetProperty("ok").GetBoolean());
        Assert.Contains("iDEAL staat aan", ok.GetProperty("message").GetString());
        Assert.Equal(key, Mollie.Requests.Last(r => r.Path == "/v2/methods").Key);

        Mollie.Accounts[key] = ["creditcard"];
        var off = await (await c.PostAsync("/api/settings/mollie/test", null)).JsonAsync();
        Assert.False(off.GetProperty("ok").GetBoolean());
        Assert.Contains("nog niet aan", off.GetProperty("message").GetString());

        // Een sleutel die Mollie niet kent.
        var unknown = IdealKit.NewKey();
        (await c.PutAsJsonAsync("/api/settings/mollie", new { key = unknown })).EnsureSuccessStatusCode();
        var bad = await (await c.PostAsync("/api/settings/mollie/test", null)).JsonAsync();
        Assert.False(bad.GetProperty("ok").GetBoolean());
        Assert.Contains("herkent deze sleutel niet", bad.GetProperty("message").GetString());
        Assert.DoesNotContain(Mollie.Requests, r => r.Path == "/v2/methods" && r.Key == FakeMollie.Key);
    }

    [Fact]
    public async Task Paying_asks_mollie_for_exactly_the_open_amount_and_reuses_a_running_payment()
    {
        var (c, ws, key) = await IdealKit.MerchantAsync(factory, "ideal-bedrag");
        var id = await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 1, 100);
        var number = (await GetInvoiceAsync(c, id)).GetProperty("number").GetString()!;
        await PaymentKit.PaidAsync(c, id, 21);
        var token = IdealKit.Link(factory, ws, id);
        Assert.True((await IdealKit.PageAsync(factory, token)).GetProperty("canPay").GetBoolean());

        // Een bedrag uit de browser telt niet: Mollie krijgt wat er volgens de server openstaat.
        var res = await IdealKit.PayAsync(factory, token, new { token, amount = 1, invoiceId = 999 });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var url = (await res.JsonAsync()).GetProperty("checkoutUrl").GetString()!;
        Assert.StartsWith("https://www.mollie.com/", url);
        var pid = IdealKit.PaymentId(url);
        var sent = Mollie.Requests.Last(r => r.Method == "POST" && r.Path == "/v2/payments");
        Assert.Equal(key, sent.Key);
        var body = sent.Body!;
        Assert.Equal("100.00", body["amount"]!["value"]!.GetValue<string>());
        Assert.Equal("EUR", body["amount"]!["currency"]!.GetValue<string>());
        Assert.Equal("ideal", body["method"]!.GetValue<string>());
        Assert.Contains(number, body["description"]!.GetValue<string>());
        Assert.StartsWith("https://potel.example/factuur?betaling=", body["redirectUrl"]!.GetValue<string>());
        Assert.DoesNotContain(token, body["redirectUrl"]!.GetValue<string>());
        Assert.Equal("https://potel.example/api/public/invoice/mollie-webhook", body["webhookUrl"]!.GetValue<string>());
        Assert.Equal(id.ToString(), body["metadata"]!["invoiceId"]!.GetValue<string>());

        // Nog een keer klikken: dezelfde betaalpagina, geen tweede betaling bij Mollie.
        var created = Mollie.Created.Count;
        Assert.Equal(url, await IdealKit.CheckoutAsync(factory, token));
        Assert.Equal(created, Mollie.Created.Count);

        // Is er intussen iets betaald, dan een nieuwe betaling voor het nieuwe bedrag.
        await PaymentKit.PaidAsync(c, id, 50);
        var second = await IdealKit.CheckoutAsync(factory, token);
        Assert.NotEqual(url, second);
        Assert.Equal("50.00", Mollie.Payments[IdealKit.PaymentId(second)]["amount"]!["value"]!.GetValue<string>());

        // Een verlopen betaling wordt niet hergebruikt.
        Mollie.SetStatus(IdealKit.PaymentId(second), "expired");
        var third = await IdealKit.CheckoutAsync(factory, token);
        Assert.NotEqual(second, third);
        Assert.Equal("expired", WorkflowKit.InWorkspace(factory, ws, db => db.OnlinePayments.Single(p => p.MollieId == IdealKit.PaymentId(second)).Status));
        Assert.Equal(pid, WorkflowKit.InWorkspace(factory, ws, db => db.OnlinePayments.OrderBy(p => p.Id).First().MollieId));
    }

    [Fact]
    public async Task Drafts_credit_notes_demo_paid_fully_credited_invoices_and_workspaces_without_a_key_cannot_be_paid_online()
    {
        var (c, ws, _) = await IdealKit.MerchantAsync(factory, "ideal-nooit");
        var customer = await CustomerAsync(c);

        var paid = await InvoiceAsync(c, customer, "verzonden", 1, 100);
        await PaymentKit.PaidAsync(c, paid, 121);
        var paidToken = IdealKit.Link(factory, ws, paid);
        Assert.False((await IdealKit.PageAsync(factory, paidToken)).GetProperty("canPay").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await IdealKit.PayAsync(factory, paidToken)).StatusCode);

        var credited = await InvoiceAsync(c, customer, "verzonden", 1, 100);
        var credit = (await (await c.PostAsync($"/api/invoices/{credited}/credit", null)).JsonAsync()).GetProperty("id").GetInt32();
        (await SetStatusAsync(c, credit, "verzonden")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await IdealKit.PayAsync(factory, IdealKit.Link(factory, ws, credited))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await IdealKit.PayAsync(factory, IdealKit.Link(factory, ws, credit))).StatusCode);

        var draft = await InvoiceAsync(c, customer, "concept", 1, 100);
        Assert.Equal(HttpStatusCode.NotFound, (await IdealKit.PayAsync(factory, IdealKit.Link(factory, ws, draft))).StatusCode);

        var demo = await InvoiceAsync(c, customer, "verzonden", 1, 100);
        WorkflowKit.InWorkspace(factory, ws, db => { db.Invoices.Single(i => i.Id == demo).IsDemo = true; return db.SaveChanges(); });
        Assert.Equal(HttpStatusCode.NotFound, (await IdealKit.PayAsync(factory, IdealKit.Link(factory, ws, demo))).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await IdealKit.PayAsync(factory, "verzonnen-token")).StatusCode);

        // Zonder eigen sleutel geen knop en geen betaling.
        var plain = await ReadyAsync(factory, "ideal-zonder");
        var plainWs = await TestApi.WorkspaceIdAsync(plain);
        var open = await InvoiceAsync(plain, await CustomerAsync(plain), "verzonden", 1, 100);
        var openToken = IdealKit.Link(factory, plainWs, open);
        Assert.False((await IdealKit.PageAsync(factory, openToken)).GetProperty("canPay").GetBoolean());
        var refused = await IdealKit.PayAsync(factory, openToken);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(InvoiceIdeal.NotOffered, await refused.ErrorAsync());
        Assert.Empty(Mollie.Requests.Where(r => r.Path == "/v2/payments" && r.Body?["metadata"]?["invoiceId"]?.GetValue<string>() is { } i
            && new[] { paid, credited, credit, draft, demo, open }.Select(x => x.ToString()).Contains(i)));
    }

    [Fact]
    public async Task The_webhook_records_a_paid_payment_once_and_starts_the_paid_workflow_once()
    {
        var (c, ws, key) = await IdealKit.MerchantAsync(factory, "ideal-webhook");
        await WorkflowKit.CreateWorkflowAsync(c, "Bij betaling", WorkflowKit.GraphJson([WorkflowKit.Node("s", "trigger.paid")], []), active: true);
        var id = await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 1, 100);
        var token = IdealKit.Link(factory, ws, id);
        var pid = IdealKit.PaymentId(await IdealKit.CheckoutAsync(factory, token));
        var code = IdealKit.ReturnRef(Mollie, pid);

        // Terug van Mollie voordat er betaald is: nog niet binnen.
        var waiting = await (await factory.CreateClient().PostAsJsonAsync("/api/public/invoice/payment-status", new { @ref = code })).JsonAsync();
        Assert.False(waiting.GetProperty("paid").GetBoolean());
        Assert.Equal("open", waiting.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.OK, (await IdealKit.WebhookAsync(factory, pid)).StatusCode);
        Assert.Equal("verzonden", (await GetInvoiceAsync(c, id)).GetProperty("status").GetString());

        Mollie.Pay(pid);
        Assert.Equal(HttpStatusCode.OK, (await IdealKit.WebhookAsync(factory, pid)).StatusCode);
        var inv = await GetInvoiceAsync(c, id);
        Assert.Equal("betaald", inv.GetProperty("status").GetString());
        var p = Assert.Single(IdealKit.Payments(inv));
        Assert.Equal(121m, p.GetProperty("amount").GetDecimal());
        Assert.Equal("ideal", p.GetProperty("method").GetString());
        Assert.Equal(pid, p.GetProperty("molliePaymentId").GetString());
        Assert.Equal(1, Runs(ws));
        // Navragen gebeurde met de sleutel van deze werkruimte, nooit met die van het platform.
        Assert.All(Mollie.Requests.Where(r => r.Path == $"/v2/payments/{pid}"), r => Assert.Equal(key, r.Key));

        // Mollie meldt het nog eens: niets dubbel.
        Assert.Equal(HttpStatusCode.OK, (await IdealKit.WebhookAsync(factory, pid)).StatusCode);
        Assert.Single(IdealKit.Payments(await GetInvoiceAsync(c, id)));
        Assert.Equal(1, Runs(ws));

        var done = await (await factory.CreateClient().PostAsJsonAsync("/api/public/invoice/payment-status", new { @ref = code })).JsonAsync();
        Assert.True(done.GetProperty("paid").GetBoolean());
        Assert.True(done.GetProperty("invoicePaid").GetBoolean());
        Assert.Equal(121m, done.GetProperty("amount").GetDecimal());
        Assert.True(done.GetProperty("test").GetBoolean());

        // Haalt een beheerder de betaling weg, dan zet een nieuwe melding van Mollie hem er niet opnieuw op.
        await c.DeleteAsync($"/api/invoices/{id}/payments/{p.GetProperty("id").GetInt32()}");
        Assert.Equal(HttpStatusCode.OK, (await IdealKit.WebhookAsync(factory, pid)).StatusCode);
        Assert.Empty(IdealKit.Payments(await GetInvoiceAsync(c, id)));
    }

    [Fact]
    public async Task Webhooks_that_arrive_at_the_same_time_record_the_payment_once()
    {
        var (c, ws, _) = await IdealKit.MerchantAsync(factory, "ideal-tegelijk");
        await WorkflowKit.CreateWorkflowAsync(c, "Bij betaling", WorkflowKit.GraphJson([WorkflowKit.Node("s", "trigger.paid")], []), active: true);
        var id = await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 2, 50);
        var pid = IdealKit.PaymentId(await IdealKit.CheckoutAsync(factory, IdealKit.Link(factory, ws, id)));
        Mollie.Pay(pid);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => IdealKit.WebhookAsync(factory, pid)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, WorkflowKit.InWorkspace(factory, ws, db => db.InvoicePayments.Count(x => x.MolliePaymentId == pid)));
        Assert.Equal("betaald", (await GetInvoiceAsync(c, id)).GetProperty("status").GetString());
        Assert.Equal(1, Runs(ws));
    }

    [Fact]
    public async Task Clicking_pay_twice_at_the_same_time_makes_one_mollie_payment()
    {
        var (c, ws, key) = await IdealKit.MerchantAsync(factory, "ideal-dubbelklik");
        var id = await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 1, 100);
        var token = IdealKit.Link(factory, ws, id);
        Mollie.Delay = TimeSpan.FromMilliseconds(300);
        try
        {
            var before = Mollie.Requests.Count(r => r.Method == "POST" && r.Path == "/v2/payments" && r.Key == key);
            var urls = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => IdealKit.CheckoutAsync(factory, token)));
            Assert.Single(urls.Distinct());
            Assert.Equal(before + 1, Mollie.Requests.Count(r => r.Method == "POST" && r.Path == "/v2/payments" && r.Key == key));
        }
        finally { Mollie.Delay = TimeSpan.Zero; }
    }

    [Fact]
    public async Task Clicking_pay_again_after_paying_records_the_payment_instead_of_making_a_second_one()
    {
        var (c, ws, key) = await IdealKit.MerchantAsync(factory, "ideal-nog-eens");
        var id = await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 1, 100);
        var token = IdealKit.Link(factory, ws, id);
        var pid = IdealKit.PaymentId(await IdealKit.CheckoutAsync(factory, token));
        var posts = Mollie.Requests.Count(r => r.Method == "POST" && r.Path == "/v2/payments" && r.Key == key);

        // Bezig bij de bank: even wachten, geen tweede betaling.
        Mollie.SetStatus(pid, "pending");
        var busy = await IdealKit.PayAsync(factory, token);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal(InvoiceIdeal.Busy, await busy.ErrorAsync());

        // Betaald, maar de webhook is nog onderweg: meteen vastleggen.
        Mollie.Pay(pid);
        var paid = await IdealKit.PayAsync(factory, token);
        Assert.Equal(HttpStatusCode.Conflict, paid.StatusCode);
        Assert.Equal(InvoiceIdeal.JustPaid, await paid.ErrorAsync());
        Assert.Equal("betaald", (await GetInvoiceAsync(c, id)).GetProperty("status").GetString());
        Assert.Equal(posts, Mollie.Requests.Count(r => r.Method == "POST" && r.Path == "/v2/payments" && r.Key == key));

        // Daarna nog eens: er staat niets meer open.
        Assert.Equal(InvoiceIdeal.NotOffered, await (await IdealKit.PayAsync(factory, token)).ErrorAsync());
        Assert.Equal(HttpStatusCode.OK, (await IdealKit.WebhookAsync(factory, pid)).StatusCode);
        Assert.Single(IdealKit.Payments(await GetInvoiceAsync(c, id)));
    }

    [Fact]
    public async Task Unknown_forged_or_foreign_payment_ids_do_nothing()
    {
        var (a, wsA, _) = await IdealKit.MerchantAsync(factory, "ideal-vreemd-a");
        var (_, wsB, keyB) = await IdealKit.MerchantAsync(factory, "ideal-vreemd-b");
        var id = await InvoiceAsync(a, await CustomerAsync(a), "verzonden", 1, 100);
        var pid = IdealKit.PaymentId(await IdealKit.CheckoutAsync(factory, IdealKit.Link(factory, wsA, id)));
        Mollie.Pay(pid);

        // Ids die we niet kennen: 200 en niets doen, ook niet bij Mollie navragen.
        var asked = Mollie.Requests.Count;
        foreach (var bogus in new[] { "tr_bestaatniet", "nep", "tr_", "tr_<script>", "" })
            Assert.Equal(HttpStatusCode.OK, (await IdealKit.WebhookAsync(factory, bogus)).StatusCode);
        Assert.Equal(asked, Mollie.Requests.Count);
        // Een betaling van het abonnement op Potel (sleutel van het platform) is hier ook onbekend.
        Mollie.Payments["tr_platform1"] = new System.Text.Json.Nodes.JsonObject { ["id"] = "tr_platform1", ["status"] = "paid", ["amount"] = new System.Text.Json.Nodes.JsonObject { ["currency"] = "EUR", ["value"] = "121.00" } };
        Assert.Equal(HttpStatusCode.OK, (await IdealKit.WebhookAsync(factory, "tr_platform1")).StatusCode);
        Assert.Equal(asked, Mollie.Requests.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().PostAsync("/api/public/invoice/mollie-webhook", new StringContent($"id={pid}&x={new string('a', 5000)}", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"))).StatusCode);

        // Zet werkruimte A de sleutel van een ander Mollie-account, dan kent Mollie de betaling met die sleutel niet:
        // niets vastleggen, ook al is hij betaald.
        (await a.PutAsJsonAsync("/api/settings/mollie", new { key = keyB })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await IdealKit.WebhookAsync(factory, pid)).StatusCode);
        Assert.Equal("verzonden", (await GetInvoiceAsync(a, id)).GetProperty("status").GetString());
        Assert.Equal(0, WorkflowKit.InWorkspace(factory, wsA, db => db.InvoicePayments.Count()));
        Assert.Equal(0, WorkflowKit.InWorkspace(factory, wsB, db => db.InvoicePayments.Count() + db.OnlinePayments.Count()));
        Assert.Equal(keyB, Mollie.Requests.Last(r => r.Path == $"/v2/payments/{pid}").Key);
    }

    [Fact]
    public async Task When_mollie_is_down_the_webhook_answers_503_so_mollie_tries_again()
    {
        var (c, ws, _) = await IdealKit.MerchantAsync(factory, "ideal-storing");
        var id = await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 1, 100);
        var pid = IdealKit.PaymentId(await IdealKit.CheckoutAsync(factory, IdealKit.Link(factory, ws, id)));
        Mollie.Pay(pid);
        Mollie.Down = true;
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await IdealKit.WebhookAsync(factory, pid)).StatusCode);
            Assert.Equal("verzonden", (await GetInvoiceAsync(c, id)).GetProperty("status").GetString());
        }
        finally { Mollie.Down = false; }
        Assert.Equal(HttpStatusCode.OK, (await IdealKit.WebhookAsync(factory, pid)).StatusCode);
        Assert.Equal("betaald", (await GetInvoiceAsync(c, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Money_that_arrives_after_a_manual_payment_is_still_recorded_with_a_warning()
    {
        var (c, ws, _) = await IdealKit.MerchantAsync(factory, "ideal-te-veel");
        var id = await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 1, 100);
        var pid = IdealKit.PaymentId(await IdealKit.CheckoutAsync(factory, IdealKit.Link(factory, ws, id)));
        await PaymentKit.PaidAsync(c, id, 21);
        Mollie.Pay(pid);
        Assert.Equal(HttpStatusCode.OK, (await IdealKit.WebhookAsync(factory, pid)).StatusCode);
        var inv = await GetInvoiceAsync(c, id);
        Assert.Equal("betaald", inv.GetProperty("status").GetString());
        Assert.Equal(142m, inv.GetProperty("paidTotal").GetDecimal());
        Assert.True(WorkflowKit.InWorkspace(factory, ws, db => db.Activities.Any(a => a.Text.Contains("te veel betaald"))));
    }

    [Fact]
    public async Task A_read_only_workspace_still_receives_the_money_its_customers_pay()
    {
        var (c, ws, _) = await IdealKit.MerchantAsync(factory, "ideal-alleen-lezen");
        var id = await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 1, 100);
        var token = IdealKit.Link(factory, ws, id);
        await TestApi.ExpireTrialAsync(factory, ws);
        var pid = IdealKit.PaymentId(await IdealKit.CheckoutAsync(factory, token));
        Mollie.Pay(pid);
        Assert.Equal(HttpStatusCode.OK, (await IdealKit.WebhookAsync(factory, pid)).StatusCode);
        Assert.Equal("betaald", (await GetInvoiceAsync(c, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unknown_return_codes_show_nothing()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/api/public/invoice/payment-status", new { @ref = "verzonnen" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(InvoiceIdealEndpoints.UnknownPayment, await res.ErrorAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().PostAsJsonAsync("/api/public/invoice/payment-status", new { @ref = (string?)null })).StatusCode);
    }

    [Fact]
    public async Task The_mail_and_the_portal_offer_ideal_only_when_it_can_be_used()
    {
        var (c, ws, _) = await IdealKit.MerchantAsync(factory, "ideal-mail");
        var customer = await CustomerAsync(c);
        var id = await InvoiceAsync(c, customer, "verzonden", 1, 100);
        var mail = await c.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}/mail-preview?step=0");
        Assert.Contains(MailTemplates.IdealSentence, mail.GetProperty("body").GetString());

        // Het klantportaal: een knop bij een factuur met een openstaand bedrag, niet bij een betaalde.
        (await c.PutAsJsonAsync($"/api/customers/{customer}/portal", new { showHours = true, showBudget = true, showInvoices = true, showAmounts = true })).EnsureSuccessStatusCode();
        var paid = await InvoiceAsync(c, customer, "verzonden", 1, 10);
        await PaymentKit.PaidAsync(c, paid, 12.10m);
        var path = (await (await c.PostAsJsonAsync($"/api/customers/{customer}/portal/link", new { expiresInDays = (int?)null })).JsonAsync()).GetProperty("path").GetString()!;
        var portal = await (await factory.CreateClient().PostAsJsonAsync("/api/public/portal", new { token = path["/portaal#token=".Length..] })).JsonAsync();
        var rows = portal.GetProperty("invoices").EnumerateArray().ToList();
        var numberOpen = (await GetInvoiceAsync(c, id)).GetProperty("number").GetString();
        var numberPaid = (await GetInvoiceAsync(c, paid)).GetProperty("number").GetString();
        Assert.True(rows.Single(r => r.GetProperty("number").GetString() == numberOpen).GetProperty("canPay").GetBoolean());
        Assert.False(rows.Single(r => r.GetProperty("number").GetString() == numberPaid).GetProperty("canPay").GetBoolean());
        // Met het token van de factuurlink uit het portaal kan de klant betalen.
        var link = rows.Single(r => r.GetProperty("number").GetString() == numberOpen).GetProperty("link").GetString()!;
        Assert.StartsWith("https://www.mollie.com/", await IdealKit.CheckoutAsync(factory, link["/factuur#token=".Length..]));

        // Zonder sleutel: geen zin over iDEAL en geen knop.
        (await c.DeleteAsync("/api/settings/mollie")).EnsureSuccessStatusCode();
        Assert.DoesNotContain("iDEAL", (await c.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}/mail-preview?step=0")).GetProperty("body").GetString());
        var after = await (await factory.CreateClient().PostAsJsonAsync("/api/public/portal", new { token = path["/portaal#token=".Length..] })).JsonAsync();
        Assert.All(after.GetProperty("invoices").EnumerateArray(), r => Assert.False(r.GetProperty("canPay").GetBoolean()));
    }
}

// Zonder openbaar adres (of met een adres op deze computer) kan Mollie ons niet bereiken: geen knop, en Instellingen zegt waarom.
public class IdealWithoutBaseUrlTests(NoBaseUrlIdealFactory factory) : IClassFixture<NoBaseUrlIdealFactory>
{
    [Fact]
    public async Task Without_a_public_address_the_button_is_hidden_and_settings_explain_why()
    {
        var (c, ws, _) = await IdealKit.MerchantAsync(factory, "ideal-geen-adres");
        var settings = await c.GetFromJsonAsync<JsonElement>("/api/settings/mollie");
        Assert.False(settings.GetProperty("available").GetBoolean());
        Assert.Equal(InvoiceIdeal.NoBaseUrl, settings.GetProperty("problem").GetString());
        var id = await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 1, 100);
        var token = IdealKit.Link(factory, ws, id);
        Assert.False((await IdealKit.PageAsync(factory, token)).GetProperty("canPay").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await IdealKit.PayAsync(factory, token)).StatusCode);
        Assert.DoesNotContain(factory.Mollie.Requests, r => r.Path == "/v2/payments");
    }
}

public class IdealLocalBaseUrlTests(LocalBaseUrlIdealFactory factory) : IClassFixture<LocalBaseUrlIdealFactory>
{
    [Fact]
    public async Task An_address_on_this_computer_is_not_enough_for_mollie()
    {
        var (c, _, _) = await IdealKit.MerchantAsync(factory, "ideal-lokaal");
        var settings = await c.GetFromJsonAsync<JsonElement>("/api/settings/mollie");
        Assert.False(settings.GetProperty("available").GetBoolean());
        Assert.Equal(InvoiceIdeal.LocalBaseUrl, settings.GetProperty("problem").GetString());
    }
}

// Elke klik op Betaal nu maakt een betaling bij Mollie, dus per IP-adres krap begrensd.
public class IdealRateLimitTests(SlowPayIdealFactory factory) : IClassFixture<SlowPayIdealFactory>
{
    [Fact]
    public async Task Paying_is_rate_limited_per_ip_address()
    {
        var (c, ws, _) = await IdealKit.MerchantAsync(factory, "ideal-grens");
        var token = IdealKit.Link(factory, ws, await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 1, 100));
        Assert.Equal(HttpStatusCode.OK, (await IdealKit.PayAsync(factory, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await IdealKit.PayAsync(factory, token)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await IdealKit.PayAsync(factory, token)).StatusCode);
    }
}
