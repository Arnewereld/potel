using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// De factuureisen: verstuurd is vast, nummers bij versturen zonder gaten, gegevens vastgelegd, verplichte velden en de juiste btw-regeling.
public class InvoiceRulesTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    static object Body(int customerId, string status = "concept", string vatRegime = "normaal", decimal price = 100) => new
    {
        customerId, issueDate = "2026-10-01", dueDate = "2026-10-15", deliveryFrom = "2026-09-30", status, vatRegime,
        lines = new[] { new { description = "Werk", quantity = 1, unit = "stuk", unitPrice = price, vatRate = 21 } },
    };

    [Fact]
    public async Task A_sent_invoice_is_frozen_and_its_status_only_moves_forward()
    {
        var c = await ReadyAsync(factory, "frozen");
        var customerId = await CustomerAsync(c);
        var id = await InvoiceAsync(c, customerId, "verzonden", 1, 100);

        var put = await c.PutAsJsonAsync($"/api/invoices/{id}", Body(customerId, "verzonden", price: 1));
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Contains("creditnota", await put.ErrorAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/invoices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SetStatusAsync(c, id, "concept")).StatusCode);
        Assert.Equal(100m, (await GetInvoiceAsync(c, id)).GetProperty("lines")[0].GetProperty("unitPrice").GetDecimal());

        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, id, "verlopen")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, id, "betaald")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SetStatusAsync(c, id, "verzonden")).StatusCode);
        Assert.Equal("betaald", (await GetInvoiceAsync(c, id)).GetProperty("status").GetString());

        // Een concept mag wel weg.
        var concept = await InvoiceAsync(c, customerId, "concept", 1, 100);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/invoices/{concept}")).StatusCode);
    }

    [Fact]
    public async Task Numbers_are_given_when_sending_per_workspace_and_year_without_gaps()
    {
        var a = await ReadyAsync(factory, "numbers-a");
        var b = await ReadyAsync(factory, "numbers-b");
        var customerA = await CustomerAsync(a);
        var customerB = await CustomerAsync(b);

        var first = await InvoiceAsync(a, customerA, "concept", 1, 100, issueDate: "2026-10-01");
        var deleted = await InvoiceAsync(a, customerA, "concept", 1, 100, issueDate: "2026-10-01");
        var second = await InvoiceAsync(a, customerA, "concept", 1, 100, issueDate: "2026-10-02");
        var lastYear = await InvoiceAsync(a, customerA, "concept", 1, 100, issueDate: "2025-12-20");
        Assert.Equal(JsonValueKind.Null, (await GetInvoiceAsync(a, first)).GetProperty("number").ValueKind);
        // Een concept geeft geen nummer uit, dus weggooien laat geen gat achter.
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/invoices/{deleted}")).StatusCode);

        Assert.Equal("2026-0001", (await Json(await SetStatusAsync(a, second, "verzonden"))).GetProperty("number").GetString());
        Assert.Equal("2026-0002", (await Json(await SetStatusAsync(a, first, "verzonden"))).GetProperty("number").GetString());
        Assert.Equal("2025-0001", (await Json(await SetStatusAsync(a, lastYear, "verzonden"))).GetProperty("number").GetString());
        Assert.Equal("2026-0003", (await a.GetFromJsonAsync<JsonElement>("/api/invoices/next-number?date=2026-11-01")).GetProperty("number").GetString());

        // Een andere werkruimte heeft zijn eigen reeks.
        var other = await InvoiceAsync(b, customerB, "verzonden", 1, 100, issueDate: "2026-10-05");
        Assert.Equal("2026-0001", (await GetInvoiceAsync(b, other)).GetProperty("number").GetString());

        // Het nummer staat vast: meesturen bij aanmaken of opslaan doet niets.
        var custom = await a.PostAsJsonAsync("/api/invoices", new
        {
            customerId = customerA, number = "9999", issueDate = "2026-10-01", dueDate = "2026-10-15", status = "concept",
            lines = new[] { new { description = "Werk", quantity = 1, unit = "stuk", unitPrice = 1, vatRate = 21 } },
        });
        Assert.Equal(JsonValueKind.Null, (await Json(custom)).GetProperty("number").ValueKind);
    }

    [Fact]
    public async Task Sent_invoices_keep_the_details_of_both_parties_from_the_moment_of_sending()
    {
        var c = await ReadyAsync(factory, "snapshot");
        var customerId = await CustomerAsync(c, "Fietsplein", vatNumber: "NL812345678B01");
        var id = await InvoiceAsync(c, customerId, "concept", 1, 100);
        Assert.Equal(JsonValueKind.Null, (await GetInvoiceAsync(c, id)).GetProperty("seller").ValueKind);
        await SetStatusAsync(c, id, "verzonden");

        (await c.PutAsJsonAsync("/api/settings", Company(name: "Nieuwe Naam", address: "Ander adres 9", kvk: "87654321"))).EnsureSuccessStatusCode();
        (await c.PutAsJsonAsync($"/api/customers/{customerId}", new { name = "Fietsplein", company = "Verhuisd B.V.", address = "Nieuwe weg 2", city = "Zwolle" })).EnsureSuccessStatusCode();

        var inv = await GetInvoiceAsync(c, id);
        var seller = inv.GetProperty("seller");
        var buyer = inv.GetProperty("buyer");
        Assert.Equal("Mijn Bedrijf", seller.GetProperty("name").GetString());
        Assert.Equal("Keizersgracht 1", seller.GetProperty("address").GetString());
        Assert.Equal("1015 AA Amsterdam", seller.GetProperty("city").GetString());
        Assert.Equal("12345678", seller.GetProperty("kvk").GetString());
        Assert.Equal("NL001234567B01", seller.GetProperty("vatNumber").GetString());
        Assert.Equal("NL00 BANK 0123 4567 89", seller.GetProperty("iban").GetString());
        Assert.Equal("Fietsplein B.V.", buyer.GetProperty("name").GetString());
        Assert.Equal("Fietsplein", buyer.GetProperty("contact").GetString());
        Assert.Equal("Dorpsstraat 1", buyer.GetProperty("address").GetString());
        Assert.Equal("NL812345678B01", buyer.GetProperty("vatNumber").GetString());
        Assert.Equal("Nederland", buyer.GetProperty("country").GetString());
        Assert.NotEqual(JsonValueKind.Null, inv.GetProperty("sentAt").ValueKind);
    }

    [Fact]
    public async Task Sending_needs_the_required_details_and_says_which_are_missing()
    {
        var c = await TestApi.RegisterAsync(factory, $"missing-{Guid.NewGuid():N}@example.com");
        var customerId = await CustomerAsync(c, address: null);
        var id = await InvoiceAsync(c, customerId, "concept", 1, 100);

        var res = await SetStatusAsync(c, id, "verzonden");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await Json(res);
        var missing = body.GetProperty("missing").EnumerateArray().Select(m => m.GetString()).ToList();
        Assert.Equal(new[] { "je adres", "je postcode en plaats", "je KvK-nummer", "je btw-id", "het adres van de klant" }, missing);
        Assert.StartsWith("Vul eerst je adres, je postcode en plaats, je KvK-nummer, je btw-id en het adres van de klant in.", body.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, (await GetInvoiceAsync(c, id)).GetProperty("number").ValueKind);
        // Ook meteen verstuurd aanmaken lukt dan niet, en er blijft geen half concept achter.
        Assert.Equal(HttpStatusCode.BadRequest, (await PostInvoiceAsync(c, customerId, "verzonden", 1, 100)).StatusCode);
        Assert.Single((await c.GetFromJsonAsync<List<JsonElement>>("/api/invoices"))!);

        // Zonder leverdatum ook niet.
        (await c.PutAsJsonAsync("/api/settings", Company(btw: "NL12345678B01"))).EnsureSuccessStatusCode();
        await c.PutAsJsonAsync($"/api/customers/{customerId}", new { name = "Klant", address = "Dorpsstraat 1", city = "Utrecht" });
        var inv = await GetInvoiceAsync(c, id);
        var noDelivery = await c.PutAsJsonAsync($"/api/invoices/{id}", new
        {
            customerId, issueDate = inv.GetProperty("issueDate").GetString(), dueDate = inv.GetProperty("dueDate").GetString(), status = "verzonden",
            lines = new[] { new { description = "Werk", quantity = 1, unit = "stuk", unitPrice = 100, vatRate = 21 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, noDelivery.StatusCode);
        Assert.Contains("de leverdatum of periode", await noDelivery.ErrorAsync());

        // Een Nederlands btw-id moet er ook als een btw-id uitzien.
        var wrongVat = await SetStatusAsync(c, id, "verzonden");
        Assert.Contains("btw-id klopt niet", await wrongVat.ErrorAsync());
        (await c.PutAsJsonAsync("/api/settings", Company(btw: "nl 001234567 b01"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, id, "verzonden")).StatusCode);
    }

    [Fact]
    public async Task Reverse_charge_only_for_a_customer_in_another_eu_country_with_a_vat_number()
    {
        var c = await ReadyAsync(factory, "verlegd");
        var dutch = await CustomerAsync(c, "Nederlands", vatNumber: "NL812345678B01");
        var germanNoVat = await CustomerAsync(c, "Duits zonder", country: "Duitsland");
        var german = await CustomerAsync(c, "Duits", country: "Germany", vatNumber: "DE123456789");

        var nl = await c.PostAsJsonAsync("/api/invoices", Body(dutch, vatRegime: "verlegd"));
        Assert.Equal(HttpStatusCode.BadRequest, nl.StatusCode);
        Assert.Contains("ander EU-land", await nl.ErrorAsync());
        Assert.Contains("btw-nummer van de klant", await (await c.PostAsJsonAsync("/api/invoices", Body(germanNoVat, vatRegime: "verlegd"))).ErrorAsync());

        Assert.Equal("Duitsland", (await c.GetFromJsonAsync<JsonElement>($"/api/customers/{german}")).GetProperty("country").GetString());
        var res = await c.PostAsJsonAsync("/api/invoices", Body(german, "verzonden", "verlegd"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var inv = await Json(res);
        Assert.Equal(0m, inv.GetProperty("lines")[0].GetProperty("vatRate").GetDecimal());
        Assert.Equal(0m, inv.GetProperty("totals").GetProperty("vat").GetDecimal());
        Assert.Equal("NL001234567B01", inv.GetProperty("seller").GetProperty("vatNumber").GetString());
        Assert.Equal("DE123456789", inv.GetProperty("buyer").GetProperty("vatNumber").GetString());
        Assert.Equal("Duitsland", inv.GetProperty("buyer").GetProperty("country").GetString());

        // Verhuist de klant later naar Nederland, dan kan een nieuw concept met btw verlegd niet meer verstuurd worden.
        var concept = await Json(await c.PostAsJsonAsync("/api/invoices", Body(german, vatRegime: "verlegd")));
        await c.PutAsJsonAsync($"/api/customers/{german}", new { name = "Duits", address = "Dorpsstraat 1", city = "Utrecht", country = "Nederland", vatNumber = "DE123456789" });
        Assert.Equal(HttpStatusCode.BadRequest, (await SetStatusAsync(c, concept.GetProperty("id").GetInt32(), "verzonden")).StatusCode);
    }

    [Fact]
    public async Task Kor_and_outside_the_eu_put_no_vat_on_the_invoice()
    {
        var c = await ReadyAsync(factory, "kor", vatRegime: "kor");
        var dutch = await CustomerAsync(c);
        var american = await CustomerAsync(c, "Amerikaans", country: "Verenigde Staten");
        var german = await CustomerAsync(c, "Duits", country: "Duitsland", vatNumber: "DE123456789");

        var kor = await Json(await c.PostAsJsonAsync("/api/invoices", Body(dutch, "verzonden", "kor")));
        Assert.Equal(0m, kor.GetProperty("lines")[0].GetProperty("vatRate").GetDecimal());
        Assert.Equal(100m, Total(kor));
        Assert.Empty(kor.GetProperty("totals").GetProperty("vatGroups").EnumerateArray().Where(g => g.GetProperty("vat").GetDecimal() != 0));

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/invoices", Body(german, vatRegime: "buiten-eu"))).StatusCode);
        var outside = await c.PostAsJsonAsync("/api/invoices", Body(american, "verzonden", "buiten-eu"));
        Assert.Equal(HttpStatusCode.Created, outside.StatusCode);
        Assert.Equal(100m, Total(await Json(outside)));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/invoices", Body(dutch, vatRegime: "onbekend"))).StatusCode);
    }

    [Fact]
    public async Task New_invoices_from_hours_take_the_vat_regime_from_the_workspace_and_the_customer()
    {
        var c = await ReadyAsync(factory, "regime-default", vatRegime: "kor");
        async Task<string> RegimeFor(int customerId)
        {
            var projectId = await ProjectAsync(c, customerId, 95, $"P{customerId}");
            await TimeAsync(c, projectId, 60, "2026-10-01");
            var res = await c.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { detailed = false });
            var inv = await GetInvoiceAsync(c, (await Json(res)).GetProperty("id").GetInt32());
            Assert.Equal(VatOf(inv), inv.GetProperty("vatRegime").GetString() == "normaal" ? 21m : 0m);
            return inv.GetProperty("vatRegime").GetString()!;
        }
        static decimal VatOf(JsonElement inv) => inv.GetProperty("lines")[0].GetProperty("vatRate").GetDecimal();

        Assert.Equal("kor", await RegimeFor(await CustomerAsync(c)));
        Assert.Equal("verlegd", await RegimeFor(await CustomerAsync(c, "Belg", country: "België", vatNumber: "BE0123456789")));
        Assert.Equal("buiten-eu", await RegimeFor(await CustomerAsync(c, "Zwitser", country: "Zwitserland")));
        (await c.PutAsJsonAsync("/api/settings", Company("normaal"))).EnsureSuccessStatusCode();
        Assert.Equal("normaal", await RegimeFor(await CustomerAsync(c, "Ander")));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/settings", Company("verlegd"))).StatusCode);
    }

    // De editor stuurt de factuur terug zoals hij binnenkwam, met totalen, momentopname en creditgegevens erbij.
    // Die velden negeert de server; alleen wat in een concept mag veranderen telt.
    [Fact]
    public async Task Saving_the_invoice_as_the_editor_sends_it_back_works()
    {
        var c = await ReadyAsync(factory, "roundtrip");
        var customerId = await CustomerAsync(c);
        var id = await InvoiceAsync(c, customerId, "verzonden", 1, 100);
        var credit = await Json(await c.PostAsync($"/api/invoices/{id}/credit", null));
        var creditId = credit.GetProperty("id").GetInt32();

        var json = System.Text.Json.Nodes.JsonNode.Parse(credit.GetRawText())!.AsObject();
        json["notes"] = "Korting achteraf";
        json["number"] = "2026-9999";
        json["creditForInvoiceId"] = null;
        json["seller"] = System.Text.Json.Nodes.JsonNode.Parse("""{"name":"Iemand anders"}""");
        var put = await c.PutAsync($"/api/invoices/{creditId}", new StringContent(json.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var saved = await Json(put);
        Assert.Equal("Korting achteraf", saved.GetProperty("notes").GetString());
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("number").ValueKind);
        Assert.Equal(JsonValueKind.Null, saved.GetProperty("seller").ValueKind);
        Assert.Equal(id, saved.GetProperty("creditForInvoiceId").GetInt32());
        Assert.Equal(-121m, Total(saved));
    }
}
