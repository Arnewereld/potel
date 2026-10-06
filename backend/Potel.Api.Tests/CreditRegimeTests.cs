using System.Net;
using System.Net.Http.Json;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Een creditnota corrigeert een factuur zoals die verstuurd is. Is de klant inmiddels verhuisd of zijn btw-nummer kwijt,
// dan blijft de creditnota bij de btw-regeling en de klantgegevens van die factuur.
public class CreditRegimeTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    async Task<(HttpClient C, int CustomerId, int InvoiceId)> ReverseChargeInvoiceAsync(string prefix)
    {
        var c = await ReadyAsync(factory, prefix);
        var customerId = await CustomerAsync(c, "Kunde", country: "Duitsland", vatNumber: "DE123456789");
        var invoiceId = await InvoiceAsync(c, customerId, "verzonden", 1, 100, vatRate: 0, vatRegime: "verlegd");
        return (c, customerId, invoiceId);
    }

    static async Task MoveToNetherlandsAsync(HttpClient c, int customerId, string? vatNumber = "NL812345678B01") =>
        (await c.PutAsJsonAsync($"/api/customers/{customerId}", new
        {
            name = "Kunde", company = "Kunde B.V.", address = "Dorpsstraat 1", city = "1234 AB Utrecht", country = "Nederland", vatNumber,
        })).EnsureSuccessStatusCode();

    [Fact]
    public async Task A_reverse_charge_invoice_can_be_credited_after_the_customer_moved()
    {
        var (c, customerId, invoiceId) = await ReverseChargeInvoiceAsync("credit-verhuisd");
        await MoveToNetherlandsAsync(c, customerId);

        var credit = await c.PostAsync($"/api/invoices/{invoiceId}/credit", null);
        Assert.Equal(HttpStatusCode.Created, credit.StatusCode);
        var draft = await Json(credit);
        var creditId = draft.GetProperty("id").GetInt32();

        // Maar een deel crediteren: opslaan en versturen lukken allebei.
        var put = await c.PutAsJsonAsync($"/api/invoices/{creditId}", WithLines(draft, new[] { new { description = "Deel terug", quantity = -0.5m, unit = "stuk", unitPrice = 100m, vatRate = 0m } }));
        Assert.True(put.StatusCode == HttpStatusCode.OK, "opslaan creditnota: " + await put.Content.ReadAsStringAsync());
        var send = await SetStatusAsync(c, creditId, "verzonden");
        Assert.True(send.StatusCode == HttpStatusCode.OK, "versturen creditnota: " + await send.Content.ReadAsStringAsync());

        // Op de creditnota staan de klantgegevens van de factuur: in Duitsland, met het Duitse btw-nummer.
        var sent = await GetInvoiceAsync(c, creditId);
        Assert.Equal("verlegd", sent.GetProperty("vatRegime").GetString());
        Assert.Equal("Duitsland", sent.GetProperty("buyer").GetProperty("country").GetString());
        Assert.Equal("DE123456789", sent.GetProperty("buyer").GetProperty("vatNumber").GetString());
    }

    [Fact]
    public async Task A_reverse_charge_invoice_can_be_credited_after_the_customer_lost_its_vat_number()
    {
        var (c, customerId, invoiceId) = await ReverseChargeInvoiceAsync("credit-zonder-btw");
        (await c.PutAsJsonAsync($"/api/customers/{customerId}", new
        {
            name = "Kunde", company = "Kunde B.V.", address = "Dorpsstraat 1", city = "1234 AB Utrecht", country = "Duitsland", vatNumber = (string?)null,
        })).EnsureSuccessStatusCode();

        var creditId = (await Json(await c.PostAsync($"/api/invoices/{invoiceId}/credit", null))).GetProperty("id").GetInt32();
        var send = await SetStatusAsync(c, creditId, "verzonden");
        Assert.True(send.StatusCode == HttpStatusCode.OK, await send.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_new_invoice_still_checks_the_regime_against_the_customer()
    {
        var (c, customerId, _) = await ReverseChargeInvoiceAsync("nieuw-verlegd");
        await MoveToNetherlandsAsync(c, customerId);
        var res = await PostInvoiceAsync(c, customerId, "concept", 1, 100, vatRate: 0, vatRegime: "verlegd");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
