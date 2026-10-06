using System.Net;
using System.Net.Http.Json;
using Potel.Api.Data;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Eén absurd grote regel mag de facturenlijst niet breken: zulke aantallen en bedragen worden geweigerd met een melding.
public class InvoiceOverflowTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task A_huge_line_is_refused_and_the_invoice_list_keeps_working()
    {
        var c = await ReadyAsync(factory, "te-groot");
        var customerId = await CustomerAsync(c);

        var posted = await PostInvoiceAsync(c, customerId, "concept", 1_000_000_000_000_000m, 1_000_000_000_000_000m);
        Assert.Equal(HttpStatusCode.BadRequest, posted.StatusCode);
        Assert.Equal(Money.TooLargeError, await posted.ErrorAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await PostInvoiceAsync(c, customerId, "concept", 2_000_000m, 1m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostInvoiceAsync(c, customerId, "concept", 1m, -2_000_000_000m)).StatusCode);

        // Op de grens mag het nog.
        Assert.Equal(HttpStatusCode.Created, (await PostInvoiceAsync(c, customerId, "concept", 1_000_000m, 1_000m)).StatusCode);

        // Ook bij opslaan van een bestaand concept.
        var id = await InvoiceAsync(c, customerId, "concept", 1, 100);
        var inv = await GetInvoiceAsync(c, id);
        var put = await c.PutAsJsonAsync($"/api/invoices/{id}", WithLines(inv, new[]
        {
            new { description = "Heel veel", quantity = 1_000_000_000_000_000m, unit = "stuk", unitPrice = 1_000_000_000_000_000m, vatRate = 21m },
        }));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/dashboard")).StatusCode);
    }

    [Fact]
    public async Task A_project_with_a_huge_rate_is_refused()
    {
        var c = await ReadyAsync(factory, "groot-tarief");
        var customerId = await CustomerAsync(c);
        var res = await c.PostAsJsonAsync("/api/projects", new { name = "Duur", customerId, status = "actief", billing = "uur", hourlyRate = 1_000_000_000_000_000m, color = "#000000" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var fixedPrice = await c.PostAsJsonAsync("/api/projects", new { name = "Vast", customerId, status = "actief", billing = "vast", fixedPrice = 1_000_000_000_000_000m, color = "#000000" });
        Assert.Equal(HttpStatusCode.BadRequest, fixedPrice.StatusCode);
    }
}
