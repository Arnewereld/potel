using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Payments;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// Een nagebootste Mollie-API: klanten, betalingen en abonnementen in het geheugen, met hulpjes om een betaling te laten slagen.
public class FakeMollie : HttpMessageHandler
{
    public const string Key = "test_potelsleutel1234567890";
    public readonly ConcurrentDictionary<string, JsonObject> Customers = new();
    public readonly ConcurrentDictionary<string, JsonObject> Payments = new();
    public readonly ConcurrentDictionary<string, JsonObject> Subscriptions = new();
    readonly ConcurrentDictionary<string, string> idempotency = new();
    public readonly ConcurrentQueue<(string Method, string Path, JsonObject? Body)> Requests = new();
    public string CheckoutHost = "www.mollie.com";
    public bool Down;
    int next;

    string Next(string prefix) => $"{prefix}t{Interlocked.Increment(ref next)}";

    static HttpResponseMessage Reply(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/hal+json") };

    static HttpResponseMessage Error(HttpStatusCode status, string detail) =>
        Reply(status, new JsonObject { ["status"] = (int)status, ["title"] = status.ToString(), ["detail"] = detail });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Assert.Equal("api.mollie.com", req.RequestUri!.Host);
        if (req.Headers.Authorization?.ToString() != $"Bearer {Key}") return Error(HttpStatusCode.Unauthorized, "Missing authentication, or failed to authenticate");
        if (Down) return Error(HttpStatusCode.ServiceUnavailable, "Even niet");
        var body = req.Content is null ? null : JsonNode.Parse(await req.Content.ReadAsStringAsync(ct)) as JsonObject;
        var path = req.RequestUri.AbsolutePath;
        Assert.StartsWith("/v2/", path);
        Requests.Enqueue((req.Method.Method, path, body));
        var parts = path["/v2/".Length..].Split('/');
        var method = req.Method.Method;

        switch (parts)
        {
            case ["customers"] when method == "POST":
            {
                var c = (JsonObject)body!.DeepClone();
                c["resource"] = "customer";
                c["id"] = Next("cst_");
                Customers[c["id"]!.GetValue<string>()] = c;
                return Reply(HttpStatusCode.Created, c);
            }
            case ["payments"] when method == "POST":
            {
                var p = (JsonObject)body!.DeepClone();
                var id = Next("tr_");
                p["resource"] = "payment";
                p["id"] = id;
                p["status"] = "open";
                p["createdAt"] = DateTimeOffset.UtcNow.ToString("O");
                p["_links"] = new JsonObject { ["checkout"] = new JsonObject { ["href"] = $"https://{CheckoutHost}/checkout/select-issuer/ideal/{id}" } };
                Payments[id] = p;
                return Reply(HttpStatusCode.Created, p);
            }
            case ["payments", var id] when method == "GET":
                return Payments.TryGetValue(id, out var found) ? Reply(HttpStatusCode.OK, found.DeepClone()) : Error(HttpStatusCode.NotFound, "No payment exists with token " + id);
            case ["customers", var customer, "subscriptions"] when method == "POST":
            {
                var key = req.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null;
                if (key is not null && idempotency.TryGetValue(key, out var existing)) return Reply(HttpStatusCode.Created, Subscriptions[existing].DeepClone());
                if (!Customers.ContainsKey(customer)) return Error(HttpStatusCode.NotFound, "No customer");
                var s = (JsonObject)body!.DeepClone();
                var id = Next("sub_");
                s["resource"] = "subscription";
                s["id"] = id;
                s["status"] = "active";
                s["customerId"] = customer;
                Subscriptions[id] = s;
                if (key is not null) idempotency[key] = id;
                return Reply(HttpStatusCode.Created, s);
            }
            case ["customers", var customer, "subscriptions", var id] when method == "GET":
                return Subscriptions.TryGetValue(id, out var sub) && sub["customerId"]!.GetValue<string>() == customer
                    ? Reply(HttpStatusCode.OK, sub.DeepClone()) : Error(HttpStatusCode.NotFound, "No subscription");
            case ["customers", var customer, "subscriptions", var id] when method == "DELETE":
            {
                if (!Subscriptions.TryGetValue(id, out var s) || s["customerId"]!.GetValue<string>() != customer) return Error(HttpStatusCode.NotFound, "No subscription");
                if (s["status"]!.GetValue<string>() == "canceled") return Error(HttpStatusCode.UnprocessableEntity, "The subscription has been canceled");
                s["status"] = "canceled";
                return Reply(HttpStatusCode.OK, s.DeepClone());
            }
        }
        return Error(HttpStatusCode.NotFound, "Onbekend in de nagebootste Mollie: " + method + " " + path);
    }

    public void Pay(string id)
    {
        Payments[id]["status"] = "paid";
        Payments[id]["paidAt"] = DateTimeOffset.UtcNow.ToString("O");
        Payments[id]["mandateId"] = "mdt_t1";
    }

    public void SetStatus(string id, string status) => Payments[id]["status"] = status;

    // Een incasso die Mollie zelf aanmaakt voor een abonnement.
    public string Recurring(string subscriptionId, string status = "paid", string? amount = null)
    {
        var sub = Subscriptions[subscriptionId];
        var id = Next("tr_");
        Payments[id] = new JsonObject
        {
            ["resource"] = "payment", ["id"] = id, ["status"] = status, ["sequenceType"] = "recurring", ["method"] = "directdebit",
            ["customerId"] = sub["customerId"]!.GetValue<string>(), ["subscriptionId"] = subscriptionId,
            ["amount"] = amount is null ? sub["amount"]!.DeepClone() : new JsonObject { ["currency"] = "EUR", ["value"] = amount },
            ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["paidAt"] = status == "paid" ? DateTimeOffset.UtcNow.ToString("O") : null,
        };
        return id;
    }

    public JsonObject? LastBody(string method, string path) => Requests.LastOrDefault(r => r.Method == method && r.Path == path).Body;
}

// Potel met een (test)sleutel van Mollie en een openbaar adres, en de nagebootste Mollie erachter.
public class MollieFactory : PortalFactory
{
    public readonly FakeMollie Mollie = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Mollie:ApiKey", FakeMollie.Key);
        builder.UseSetting("App:BaseUrl", "https://potel.example/");
        builder.ConfigureTestServices(s => s.AddHttpClient<MollieClient>().ConfigurePrimaryHttpMessageHandler(() => Mollie));
    }
}

static class MollieKit
{
    // Een werkruimte met adres, klaar om te betalen.
    public static async Task<HttpClient> PayerAsync(MollieFactory f, string prefix)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@example.com";
        var c = await RegisterAsync(f, email);
        (await c.PutAsJsonAsync("/api/settings", InvoiceKit.Company(name: $"Klant {prefix} B.V.", address: "Stationsplein 9"))).EnsureSuccessStatusCode();
        return c;
    }

    public static async Task<string> CheckoutAsync(MollieFactory f, HttpClient c, string plan = "zzp")
    {
        var res = await c.PostAsJsonAsync("/api/billing/checkout", new { plan });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var url = (await res.JsonAsync()).GetProperty("checkoutUrl").GetString()!;
        Assert.StartsWith("https://www.mollie.com/checkout/", url);
        return url[(url.LastIndexOf('/') + 1)..];
    }

    public static Task<HttpResponseMessage> WebhookAsync(MollieFactory f, string id) =>
        f.CreateClient().PostAsync("/api/mollie/webhook", new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = id }));

    public static async Task<string> PaidAsync(MollieFactory f, HttpClient c, string plan = "zzp")
    {
        var id = await CheckoutAsync(f, c, plan);
        f.Mollie.Pay(id);
        Assert.Equal(HttpStatusCode.OK, (await WebhookAsync(f, id)).StatusCode);
        return id;
    }

    public static Workspace Ws(MollieFactory f, int id) => f.WithDb(db => db.Workspaces.AsNoTracking().Single(w => w.Id == id));

    public static void Update(MollieFactory f, int id, Action<Workspace> change)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var w = db.Workspaces.Single(x => x.Id == id);
        change(w);
        db.SaveChanges();
    }

    public static async Task<List<JsonElement>> OwnerInvoicesAsync(MollieFactory f, string reference)
    {
        var owner = await f.LoginAsync();
        return (await owner.GetFromJsonAsync<List<JsonElement>>("/api/invoices"))!
            .Where(i => i.GetProperty("reference").ValueKind == JsonValueKind.String && i.GetProperty("reference").GetString() == reference).ToList();
    }

    public static List<string> Activity(MollieFactory f, int workspaceId) =>
        f.WithDb(db => db.Activities.IgnoreQueryFilters().Where(a => a.WorkspaceId == workspaceId).OrderBy(a => a.Id).Select(a => a.Text).ToList());
}

// Zonder sleutel van Mollie verandert er niets: geen betaalknoppen en geen betaaladressen.
public class MollieDisabledTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task Without_an_api_key_there_is_no_online_payment()
    {
        var c = await RegisterAsync(factory, "zonder-mollie@example.com");
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/workspace")).GetProperty("onlinePayment").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/billing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/billing/checkout", new { plan = "zzp" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/api/billing/cancel", null)).StatusCode);
        var webhook = await factory.CreateClient().PostAsync("/api/mollie/webhook", new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = "tr_abc" }));
        Assert.Equal(HttpStatusCode.NotFound, webhook.StatusCode);
    }
}

public class MollieTests(MollieFactory factory) : IClassFixture<MollieFactory>
{
    FakeMollie Mollie => factory.Mollie;

    [Fact]
    public async Task Checkout_creates_a_customer_and_a_first_ideal_payment_including_vat()
    {
        var c = await MollieKit.PayerAsync(factory, "start");
        var ws = await WorkspaceIdAsync(c);
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/workspace")).GetProperty("onlinePayment").GetBoolean());
        var billing = await c.GetFromJsonAsync<JsonElement>("/api/billing");
        var zzp = billing.GetProperty("plans").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "zzp");
        Assert.Equal(12m, zzp.GetProperty("net").GetDecimal());
        Assert.Equal(14.52m, zzp.GetProperty("gross").GetDecimal());

        var id = await MollieKit.CheckoutAsync(factory, c);
        var p = Mollie.Payments[id];
        Assert.Equal("14.52", p["amount"]!["value"]!.GetValue<string>());
        Assert.Equal("EUR", p["amount"]!["currency"]!.GetValue<string>());
        Assert.Equal("ideal", p["method"]!.GetValue<string>());
        Assert.Equal("first", p["sequenceType"]!.GetValue<string>());
        Assert.Equal("https://potel.example/instellingen?tab=abonnement&betaling=terug", p["redirectUrl"]!.GetValue<string>());
        Assert.Equal("https://potel.example/api/mollie/webhook", p["webhookUrl"]!.GetValue<string>());
        Assert.Equal(ws.ToString(), p["metadata"]!["workspaceId"]!.GetValue<string>());
        var customer = p["customerId"]!.GetValue<string>();
        Assert.Equal("Klant start B.V.", Mollie.Customers[customer]["name"]!.GetValue<string>());
        Assert.Equal(customer, MollieKit.Ws(factory, ws).MollieCustomerId);

        // Nog een keer, nu voor Team: dezelfde klant bij Mollie, en het bedrag van Team.
        var team = await MollieKit.CheckoutAsync(factory, c, "team");
        Assert.Equal(customer, Mollie.Payments[team]["customerId"]!.GetValue<string>());
        Assert.Equal("35.09", Mollie.Payments[team]["amount"]!["value"]!.GetValue<string>());
        Assert.Single(Mollie.Customers.Values, x => x["metadata"]!["workspaceId"]!.GetValue<int>() == ws);
        // Er is nog niets veranderd zolang er niet betaald is.
        Assert.Equal("proef", MollieKit.Ws(factory, ws).Plan);
    }

    [Fact]
    public async Task Checkout_needs_an_admin_a_paid_plan_and_an_address()
    {
        var c = await RegisterAsync(factory, $"geen-adres-{Guid.NewGuid():N}@example.com");
        var res = await c.PostAsJsonAsync("/api/billing/checkout", new { plan = "zzp" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("adres", await res.ErrorAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/billing/checkout", new { plan = "proef" })).StatusCode);

        var payer = await MollieKit.PayerAsync(factory, "medewerker");
        var email = $"mw-{Guid.NewGuid():N}@example.com";
        await CreateUserAsync(payer, email);
        var employee = await LoginAsync(factory, email);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync("/api/billing/checkout", new { plan = "team" })).StatusCode);
        // ZZP is voor één gebruiker; met twee kan dat niet.
        var zzp = await payer.PostAsJsonAsync("/api/billing/checkout", new { plan = "zzp" });
        Assert.Equal(HttpStatusCode.BadRequest, zzp.StatusCode);
        Assert.Contains("gebruiker", await zzp.ErrorAsync());
    }

    [Fact]
    public async Task An_expired_trial_can_still_pay()
    {
        var c = await MollieKit.PayerAsync(factory, "verlopen");
        var ws = await WorkspaceIdAsync(c);
        await ExpireTrialAsync(factory, ws);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/customers", new { name = "X" })).StatusCode);
        var id = await MollieKit.CheckoutAsync(factory, c);
        Mollie.Pay(id);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/billing/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/customers", new { name = "Weer aan het werk" })).StatusCode);
    }

    [Fact]
    public async Task Paid_first_payment_starts_the_subscription_sets_the_plan_and_invoices_from_the_owner()
    {
        var c = await MollieKit.PayerAsync(factory, "betaald");
        var ws = await WorkspaceIdAsync(c);
        var id = await MollieKit.PaidAsync(factory, c);

        var w = MollieKit.Ws(factory, ws);
        Assert.Equal("zzp", w.Plan);
        Assert.Null(w.TrialEndsAt);
        Assert.NotNull(w.MollieSubscriptionId);
        Assert.InRange(w.PaidUntil!.Value, DateTime.UtcNow.AddDays(27), DateTime.UtcNow.AddDays(32));

        var sub = Mollie.Subscriptions[w.MollieSubscriptionId!];
        Assert.Equal(w.MollieCustomerId, sub["customerId"]!.GetValue<string>());
        Assert.Equal("14.52", sub["amount"]!["value"]!.GetValue<string>());
        Assert.Equal("1 month", sub["interval"]!.GetValue<string>());
        Assert.Equal("https://potel.example/api/mollie/webhook", sub["webhookUrl"]!.GetValue<string>());
        Assert.Equal("zzp", sub["metadata"]!["plan"]!.GetValue<string>());
        var start = DateOnly.Parse(sub["startDate"]!.GetValue<string>());
        Assert.InRange(start, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(27)), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(32)));

        var me = await c.GetFromJsonAsync<JsonElement>("/api/workspace");
        Assert.True(me.GetProperty("subscriptionActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, me.GetProperty("readOnly").ValueKind);

        // De eigenaar van het platform heeft een verstuurde en betaalde factuur, met de gegevens van de klant.
        var invoice = Assert.Single(await MollieKit.OwnerInvoicesAsync(factory, id));
        Assert.Equal("betaald", invoice.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, invoice.GetProperty("number").ValueKind);
        Assert.Equal("normaal", invoice.GetProperty("vatRegime").GetString());
        Assert.Equal(14.52m, invoice.GetProperty("totals").GetProperty("total").GetDecimal());
        var line = Assert.Single(invoice.GetProperty("lines").EnumerateArray());
        Assert.Equal(12m, line.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(21m, line.GetProperty("vatRate").GetDecimal());
        Assert.Contains("ZZP", line.GetProperty("description").GetString());
        var buyer = invoice.GetProperty("buyer");
        Assert.Equal("Klant betaald B.V.", buyer.GetProperty("name").GetString());
        Assert.Equal("Stationsplein 9", buyer.GetProperty("address").GetString());
        Assert.Equal("NL001234567B01", buyer.GetProperty("vatNumber").GetString());
        Assert.Equal(invoice.GetProperty("customerId").GetInt32(), MollieKit.Ws(factory, ws).BillingCustomerId);
        // De klant ziet in zijn eigen werkruimte geen facturen van de eigenaar.
        Assert.Empty((await c.GetFromJsonAsync<List<JsonElement>>("/api/invoices"))!);
    }

    [Fact]
    public async Task A_repeated_webhook_changes_nothing_twice()
    {
        var c = await MollieKit.PayerAsync(factory, "dubbel");
        var ws = await WorkspaceIdAsync(c);
        var id = await MollieKit.PaidAsync(factory, c);
        var before = MollieKit.Ws(factory, ws);

        var replays = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => MollieKit.WebhookAsync(factory, id)));
        Assert.All(replays, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var after = MollieKit.Ws(factory, ws);
        Assert.Equal(before.PaidUntil, after.PaidUntil);
        Assert.Equal(before.MollieSubscriptionId, after.MollieSubscriptionId);
        Assert.Single(Mollie.Subscriptions.Values, s => s["customerId"]!.GetValue<string>() == after.MollieCustomerId);
        Assert.Single(Mollie.Requests, r => r.Method == "POST" && r.Path == $"/v2/customers/{after.MollieCustomerId}/subscriptions");
        Assert.Single(MollieKit.Activity(factory, ws), t => t.Contains("betaald met iDEAL"));
        Assert.Single(await MollieKit.OwnerInvoicesAsync(factory, id));
    }

    [Fact]
    public async Task Unknown_or_forged_ids_do_nothing()
    {
        var c = await MollieKit.PayerAsync(factory, "vals");
        var ws = await WorkspaceIdAsync(c);
        var id = await MollieKit.CheckoutAsync(factory, c);

        Assert.Equal(HttpStatusCode.OK, (await MollieKit.WebhookAsync(factory, "tr_bestaatniet")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MollieKit.WebhookAsync(factory, "../../customers")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().PostAsJsonAsync("/api/mollie/webhook", new { id, status = "paid" })).StatusCode);
        var big = new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = id, ["vulling"] = new string('x', 10_000) });
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().PostAsync("/api/mollie/webhook", big)).StatusCode);
        // Een webhook voor een echte betaling die nog niet betaald is: de status komt van Mollie, niet uit het verzoek.
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = id, ["status"] = "paid" });
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsync("/api/mollie/webhook", form)).StatusCode);

        var w = MollieKit.Ws(factory, ws);
        Assert.Equal("proef", w.Plan);
        Assert.Null(w.MollieSubscriptionId);
        Assert.Empty(Mollie.Subscriptions.Values.Where(s => s["customerId"]!.GetValue<string>() == w.MollieCustomerId));
        Assert.Empty(await MollieKit.OwnerInvoicesAsync(factory, id));
    }

    [Fact]
    public async Task A_payment_with_another_workspace_or_amount_is_ignored()
    {
        var a = await MollieKit.PayerAsync(factory, "metadata-a");
        var b = await MollieKit.PayerAsync(factory, "metadata-b");
        var wsA = await WorkspaceIdAsync(a);
        var wsB = await WorkspaceIdAsync(b);
        var id = await MollieKit.CheckoutAsync(factory, a);
        Mollie.Payments[id]["metadata"]!["workspaceId"] = wsB.ToString();
        Mollie.Pay(id);
        Assert.Equal(HttpStatusCode.OK, (await MollieKit.WebhookAsync(factory, id)).StatusCode);
        Assert.Equal("proef", MollieKit.Ws(factory, wsA).Plan);
        Assert.Equal("proef", MollieKit.Ws(factory, wsB).Plan);

        var cheap = await MollieKit.CheckoutAsync(factory, a, "team");
        Mollie.Payments[cheap]["amount"]!["value"] = "0.01";
        Mollie.Pay(cheap);
        Assert.Equal(HttpStatusCode.OK, (await MollieKit.WebhookAsync(factory, cheap)).StatusCode);
        Assert.Equal("proef", MollieKit.Ws(factory, wsA).Plan);
    }

    [Fact]
    public async Task A_failed_payment_changes_nothing_and_says_so()
    {
        var c = await MollieKit.PayerAsync(factory, "mislukt");
        var ws = await WorkspaceIdAsync(c);
        var id = await MollieKit.CheckoutAsync(factory, c);
        Mollie.SetStatus(id, "failed");
        Assert.Equal(HttpStatusCode.OK, (await MollieKit.WebhookAsync(factory, id)).StatusCode);

        var w = MollieKit.Ws(factory, ws);
        Assert.Equal("proef", w.Plan);
        Assert.NotNull(w.TrialEndsAt);
        Assert.Null(w.MollieSubscriptionId);
        Assert.Contains(MollieKit.Activity(factory, ws), t => t.Contains("niet gelukt"));
        var billing = await c.GetFromJsonAsync<JsonElement>("/api/billing");
        Assert.Equal("failed", billing.GetProperty("lastPayment").GetProperty("status").GetString());
        Assert.Empty(await MollieKit.OwnerInvoicesAsync(factory, id));
    }

    [Fact]
    public async Task Coming_back_from_checkout_fetches_the_status_without_waiting_for_the_webhook()
    {
        var c = await MollieKit.PayerAsync(factory, "terug");
        var ws = await WorkspaceIdAsync(c);
        var id = await MollieKit.CheckoutAsync(factory, c, "team");
        Mollie.Pay(id);
        var res = await c.PostAsync("/api/billing/sync", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.JsonAsync();
        Assert.Equal("paid", body.GetProperty("lastPayment").GetProperty("status").GetString());
        Assert.Equal("team", body.GetProperty("plan").GetString());
        Assert.True(body.GetProperty("subscriptionActive").GetBoolean());
        Assert.Equal("team", MollieKit.Ws(factory, ws).Plan);
        Assert.Single(await MollieKit.OwnerInvoicesAsync(factory, id));
    }

    [Fact]
    public async Task Cancelling_stops_the_subscription_and_the_workspace_becomes_read_only_after_the_paid_period()
    {
        var c = await MollieKit.PayerAsync(factory, "opzeggen");
        var ws = await WorkspaceIdAsync(c);
        await MollieKit.PaidAsync(factory, c);
        var sub = MollieKit.Ws(factory, ws).MollieSubscriptionId!;

        var res = await c.PostAsync("/api/billing/cancel", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("canceled", Mollie.Subscriptions[sub]["status"]!.GetValue<string>());
        var w = MollieKit.Ws(factory, ws);
        Assert.Null(w.MollieSubscriptionId);
        Assert.NotNull(w.SubscriptionCanceledAt);
        Assert.Equal("zzp", w.Plan);
        // Tot het einde van de betaalde maand kun je gewoon werken.
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/customers", new { name = "Nog even" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/billing/cancel", null)).StatusCode);

        // Daarna is de werkruimte alleen-lezen, maar opnieuw betalen kan.
        MollieKit.Update(factory, ws, x => x.PaidUntil = DateTime.UtcNow.AddMinutes(-1));
        var blocked = await c.PostAsJsonAsync("/api/customers", new { name = "Te laat" });
        Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);
        Assert.Equal(Plans.SubscriptionEndedError, await blocked.ErrorAsync());
        Assert.Equal(Plans.SubscriptionEndedError, (await c.GetFromJsonAsync<JsonElement>("/api/workspace")).GetProperty("readOnly").GetString());
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/billing/checkout", new { plan = "zzp" })).StatusCode);
    }

    [Fact]
    public async Task Monthly_direct_debits_extend_the_period_and_a_missed_one_locks_after_a_grace_period()
    {
        var c = await MollieKit.PayerAsync(factory, "incasso");
        var ws = await WorkspaceIdAsync(c);
        await MollieKit.PaidAsync(factory, c);
        var first = MollieKit.Ws(factory, ws);

        var recurring = Mollie.Recurring(first.MollieSubscriptionId!);
        Assert.Equal(HttpStatusCode.OK, (await MollieKit.WebhookAsync(factory, recurring)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MollieKit.WebhookAsync(factory, recurring)).StatusCode);
        var second = MollieKit.Ws(factory, ws);
        Assert.Equal(first.PaidUntil!.Value.AddMonths(1), second.PaidUntil);
        var invoice = Assert.Single(await MollieKit.OwnerInvoicesAsync(factory, recurring));
        Assert.Equal("betaald", invoice.GetProperty("status").GetString());
        Assert.Equal(first.BillingCustomerId, invoice.GetProperty("customerId").GetInt32());

        // Een incasso die niet lukt: nog even werken, en na de wachttijd alleen-lezen.
        var failed = Mollie.Recurring(first.MollieSubscriptionId!, status: "failed");
        Assert.Equal(HttpStatusCode.OK, (await MollieKit.WebhookAsync(factory, failed)).StatusCode);
        Assert.Contains(MollieKit.Activity(factory, ws), t => t.Contains("incasso") && t.Contains("niet gelukt"));
        Assert.NotNull(MollieKit.Ws(factory, ws).MollieSubscriptionId);
        MollieKit.Update(factory, ws, x => x.PaidUntil = DateTime.UtcNow.AddDays(-3));
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/customers", new { name = "Wachttijd" })).StatusCode);
        MollieKit.Update(factory, ws, x => x.PaidUntil = DateTime.UtcNow.AddDays(-Plans.PaymentGraceDays - 1));
        var blocked = await c.PostAsJsonAsync("/api/customers", new { name = "Te laat" });
        Assert.Equal(HttpStatusCode.PaymentRequired, blocked.StatusCode);
        Assert.Equal(Plans.PaymentMissingError, await blocked.ErrorAsync());
    }

    [Fact]
    public async Task When_mollie_stops_the_subscription_after_a_failed_debit_it_is_no_longer_active()
    {
        var c = await MollieKit.PayerAsync(factory, "gestopt");
        var ws = await WorkspaceIdAsync(c);
        await MollieKit.PaidAsync(factory, c);
        var sub = MollieKit.Ws(factory, ws).MollieSubscriptionId!;
        Mollie.Subscriptions[sub]["status"] = "canceled";
        var failed = Mollie.Recurring(sub, status: "failed");
        Assert.Equal(HttpStatusCode.OK, (await MollieKit.WebhookAsync(factory, failed)).StatusCode);
        var w = MollieKit.Ws(factory, ws);
        Assert.Null(w.MollieSubscriptionId);
        Assert.NotNull(w.SubscriptionCanceledAt);
    }

    [Fact]
    public async Task Switching_plans_replaces_the_running_subscription()
    {
        var c = await MollieKit.PayerAsync(factory, "overstap");
        var ws = await WorkspaceIdAsync(c);
        await MollieKit.PaidAsync(factory, c);
        var old = MollieKit.Ws(factory, ws).MollieSubscriptionId!;
        var again = await c.PostAsJsonAsync("/api/billing/checkout", new { plan = "zzp" });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        await MollieKit.PaidAsync(factory, c, "team");
        var w = MollieKit.Ws(factory, ws);
        Assert.Equal("team", w.Plan);
        Assert.NotEqual(old, w.MollieSubscriptionId);
        Assert.Equal("canceled", Mollie.Subscriptions[old]["status"]!.GetValue<string>());
        Assert.Equal("35.09", Mollie.Subscriptions[w.MollieSubscriptionId!]["amount"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Workspaces_only_see_and_change_their_own_subscription()
    {
        var a = await MollieKit.PayerAsync(factory, "eigen-a");
        var b = await MollieKit.PayerAsync(factory, "eigen-b");
        var wsA = await WorkspaceIdAsync(a);
        var wsB = await WorkspaceIdAsync(b);
        await MollieKit.PaidAsync(factory, a);

        var billingB = await b.GetFromJsonAsync<JsonElement>("/api/billing");
        Assert.Equal(JsonValueKind.Null, billingB.GetProperty("lastPayment").ValueKind);
        Assert.False(billingB.GetProperty("subscriptionActive").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsync("/api/billing/cancel", null)).StatusCode);
        Assert.NotNull(MollieKit.Ws(factory, wsA).MollieSubscriptionId);
        Assert.Equal("proef", MollieKit.Ws(factory, wsB).Plan);
        Assert.Null(MollieKit.Ws(factory, wsB).MollieCustomerId);
        // Een incasso van het abonnement van A komt bij A terecht, niet bij B.
        var recurring = Mollie.Recurring(MollieKit.Ws(factory, wsA).MollieSubscriptionId!);
        await MollieKit.WebhookAsync(factory, recurring);
        Assert.Equal(wsA, factory.WithDb(db => db.MolliePayments.IgnoreQueryFilters().Single(p => p.Id == recurring).WorkspaceId));
        Assert.Null(MollieKit.Ws(factory, wsB).PaidUntil);
    }

    [Fact]
    public async Task A_direct_debit_for_an_unknown_customer_is_ignored()
    {
        var c = await MollieKit.PayerAsync(factory, "vreemd");
        var ws = await WorkspaceIdAsync(c);
        await MollieKit.PaidAsync(factory, c);
        var sub = MollieKit.Ws(factory, ws).MollieSubscriptionId!;
        var recurring = Mollie.Recurring(sub);
        Mollie.Payments[recurring]["customerId"] = "cst_onbekend";
        Assert.Equal(HttpStatusCode.OK, (await MollieKit.WebhookAsync(factory, recurring)).StatusCode);
        Assert.False(factory.WithDb(db => db.MolliePayments.IgnoreQueryFilters().Any(p => p.Id == recurring)));
    }

    [Fact]
    public async Task When_mollie_is_down_the_webhook_asks_for_a_retry()
    {
        var c = await MollieKit.PayerAsync(factory, "storing");
        var id = await MollieKit.CheckoutAsync(factory, c);
        Mollie.Pay(id);
        Mollie.Down = true;
        try { Assert.Equal(HttpStatusCode.ServiceUnavailable, (await MollieKit.WebhookAsync(factory, id)).StatusCode); }
        finally { Mollie.Down = false; }
        Assert.Equal(HttpStatusCode.OK, (await MollieKit.WebhookAsync(factory, id)).StatusCode);
        Assert.Equal("zzp", MollieKit.Ws(factory, await WorkspaceIdAsync(c)).Plan);
    }

    [Fact]
    public async Task Deleting_the_workspace_cancels_the_subscription_first()
    {
        var c = await MollieKit.PayerAsync(factory, "weg");
        var ws = await WorkspaceIdAsync(c);
        await MollieKit.PaidAsync(factory, c);
        var sub = MollieKit.Ws(factory, ws).MollieSubscriptionId!;
        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "geheim123" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(delete)).StatusCode);
        Assert.Equal("canceled", Mollie.Subscriptions[sub]["status"]!.GetValue<string>());
        Assert.False(factory.WithDb(db => db.MolliePayments.IgnoreQueryFilters().Any(p => p.WorkspaceId == ws)));
    }
}

// Een antwoord van Mollie met een betaalpagina buiten mollie.com stuurt niemand door.
public class MollieCheckoutHostTests(MollieFactory factory) : IClassFixture<MollieFactory>
{
    [Fact]
    public async Task A_checkout_link_outside_mollie_is_refused()
    {
        factory.Mollie.CheckoutHost = "mollie.com.evil.example";
        var c = await MollieKit.PayerAsync(factory, "omleiding");
        var res = await c.PostAsJsonAsync("/api/billing/checkout", new { plan = "zzp" });
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.False((await res.Content.ReadAsStringAsync()).Contains("evil"));
    }
}

// Bestaande werkruimtes waarvan jij het abonnement zelf omzette, blijven na de update gewoon werken.
public class MollieMigrationTests : IDisposable
{
    const string Before = "20261007111920_ZakelijkAanmeldenEnVoorwaarden";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-migratie-{Guid.NewGuid():N}.db");

    AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path}").Options, new Tenant());

    [Fact]
    public void Manually_upgraded_workspaces_stay_writable()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        using (var con = new SqliteConnection($"Data Source={path}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, WebhooksSent, Name, Plan) VALUES (1, '2025-01-01 00:00:00', 0, 0, 'Betaalt per mail', 'team');
                """;
            cmd.ExecuteNonQuery();
        }
        using (var db = Db()) db.Database.Migrate();
        using (var db = Db())
        {
            var w = db.Workspaces.Single();
            Assert.Equal("team", w.Plan);
            Assert.Null(w.PaidUntil);
            Assert.Null(w.MollieSubscriptionId);
            Assert.False(w.ReadOnly(DateTime.UtcNow));
            Assert.Empty(db.MolliePayments.IgnoreQueryFilters());
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }
}
