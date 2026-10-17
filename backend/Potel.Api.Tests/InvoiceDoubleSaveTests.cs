using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Een concept opslaan werkt de regels bij in plaats van ze te vervangen: twee keer hetzelfde opslaan (een dubbelklik,
// of een tweede tabblad met dezelfde versie) laat de uren op de factuur staan.
public class InvoiceDoubleSaveTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    static object[] Lines(JsonElement inv) => inv.GetProperty("lines").EnumerateArray().Select(l => (object)new
    {
        id = l.GetProperty("id").GetInt32(), description = l.GetProperty("description").GetString(),
        quantity = l.GetProperty("quantity").GetDecimal(), unit = l.GetProperty("unit").GetString(),
        unitPrice = l.GetProperty("unitPrice").GetDecimal(), vatRate = l.GetProperty("vatRate").GetDecimal(),
    }).ToArray();

    static int[] LineIds(JsonElement inv) => inv.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("id").GetInt32()).ToArray();

    async Task<(HttpClient C, int ProjectId, int InvoiceId)> ConceptWithHoursAsync(string prefix)
    {
        var c = await ReadyAsync(factory, prefix);
        var customerId = await CustomerAsync(c);
        var projectId = await ProjectAsync(c, customerId, 95);
        await TimeAsync(c, projectId, 120, "2026-10-01");
        var created = await c.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { detailed = false });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (c, projectId, (await Json(created)).GetProperty("id").GetInt32());
    }

    static async Task<int> UnbilledAsync(HttpClient c, int projectId) =>
        (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}")).GetProperty("minutesUnbilled").GetInt32();

    [Fact]
    public async Task Saving_the_same_concept_twice_keeps_its_hours_and_line_ids()
    {
        var (c, projectId, invoiceId) = await ConceptWithHoursAsync("dubbel-opslaan");
        var inv = await GetInvoiceAsync(c, invoiceId);
        var body = WithLines(inv, Lines(inv));

        var first = await c.PutAsJsonAsync($"/api/invoices/{invoiceId}", body);
        var second = await c.PutAsJsonAsync($"/api/invoices/{invoiceId}", body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var after = await GetInvoiceAsync(c, invoiceId);
        Assert.Equal(LineIds(inv), LineIds(after));
        Assert.Equal(2m, HourQuantity(after));
        Assert.Equal(0, await UnbilledAsync(c, projectId));
    }

    [Fact]
    public async Task A_line_that_is_no_longer_on_the_invoice_gives_a_conflict_and_frees_nothing()
    {
        var (c, projectId, invoiceId) = await ConceptWithHoursAsync("verouderd");
        var inv = await GetInvoiceAsync(c, invoiceId);
        var stale = WithLines(inv, new[]
        {
            new { id = 999_999, description = "Uit een ander tabblad", quantity = 1m, unit = "stuk", unitPrice = 10m, vatRate = 21m },
        });
        var res = await c.PutAsJsonAsync($"/api/invoices/{invoiceId}", stale);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("ergens anders opgeslagen", await res.ErrorAsync());
        Assert.Equal(0, await UnbilledAsync(c, projectId));
        Assert.Equal(2m, HourQuantity(await GetInvoiceAsync(c, invoiceId)));
    }

    [Fact]
    public async Task Lines_keep_the_order_they_were_saved_in()
    {
        var c = await ReadyAsync(factory, "volgorde");
        var customerId = await CustomerAsync(c);
        var res = await c.PostAsJsonAsync("/api/invoices", new
        {
            customerId, issueDate = Day(DateTime.Today), dueDate = Day(DateTime.Today.AddDays(14)), status = "concept", vatRegime = "normaal",
            lines = new[]
            {
                new { description = "Eerste", quantity = 1m, unit = "stuk", unitPrice = 10m, vatRate = 21m },
                new { description = "Tweede", quantity = 1m, unit = "stuk", unitPrice = 20m, vatRate = 21m },
            },
        });
        var inv = await Json(res);
        var invoiceId = inv.GetProperty("id").GetInt32();
        var lines = Lines(inv);
        var put = await c.PutAsJsonAsync($"/api/invoices/{invoiceId}", WithLines(inv, new[]
        {
            lines[1],
            new { description = "Nieuw", quantity = 1m, unit = "stuk", unitPrice = 5m, vatRate = 21m },
            lines[0],
        }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var after = await GetInvoiceAsync(c, invoiceId);
        Assert.Equal(new[] { "Tweede", "Nieuw", "Eerste" }, after.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("description").GetString()));
        Assert.Equal(LineIds(inv).Reverse().ToArray(), new[] { LineIds(after)[0], LineIds(after)[2] });
    }
}
