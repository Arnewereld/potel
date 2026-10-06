using System.Net;
using System.Net.Http.Json;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Dubbelklikken of twee tabbladen: gelijktijdige verzoeken mogen uren nooit twee keer factureren en nooit hetzelfde nummer geven.
public class InvoiceRaceTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task Two_requests_adding_the_same_hours_bill_them_only_once()
    {
        var c = await ReadyAsync(factory, "race-hours");
        var customerId = await CustomerAsync(c);
        decimal worst = 0;
        for (var round = 0; round < 8 && worst == 0; round++)
        {
            var projectId = await ProjectAsync(c, customerId, 95, $"P{round}");
            await TimeAsync(c, projectId, 90, "2026-10-01", count: 20); // 30 uur
            var invoiceId = await InvoiceAsync(c, customerId, "concept", 1, 100);
            await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
                SafeSend(() => c.PostAsJsonAsync($"/api/invoices/{invoiceId}/hours", new { projectId, detailed = false }))));
            var billed = HourQuantity(await GetInvoiceAsync(c, invoiceId));
            if (billed != 30m) worst = billed;
        }
        Assert.True(worst == 0, $"30 open uren zijn als {worst} uur gefactureerd");
    }

    [Fact]
    public async Task Parallel_invoices_from_a_project_bill_each_hour_only_once()
    {
        var c = await ReadyAsync(factory, "race-project");
        var customerId = await CustomerAsync(c);
        string? problem = null;
        for (var round = 0; round < 15 && problem is null; round++)
        {
            var projectId = await ProjectAsync(c, customerId, 95, $"P{round}");
            await TimeAsync(c, projectId, 90, "2026-10-01", count: 20); // 30 uur
            var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
                SafeSend(() => c.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { detailed = false }))));
            if (results.Any(r => r is null || r.StatusCode == HttpStatusCode.InternalServerError)) { problem = "een verzoek gaf een serverfout"; break; }
            var created = results.Where(r => r!.StatusCode == HttpStatusCode.Created).ToList();
            decimal billed = 0;
            foreach (var r in created) billed += HourQuantity(await GetInvoiceAsync(c, (await Json(r!)).GetProperty("id").GetInt32()));
            if (billed != 30m) problem = $"{created.Count} facturen gemaakt met samen {billed} uur voor 30 open uren";
        }
        Assert.True(problem is null, problem);
    }

    [Fact]
    public async Task Parallel_copies_never_fail_with_a_server_error()
    {
        var c = await ReadyAsync(factory, "race-copy");
        var customerId = await CustomerAsync(c);
        var invoiceId = await InvoiceAsync(c, customerId, "verzonden", 1, 100);
        var failures = 0;
        for (var round = 0; round < 5; round++)
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                SafeSend(() => c.PostAsync($"/api/invoices/{invoiceId}/duplicate", null))));
            failures += results.Count(r => r is null || r.StatusCode != HttpStatusCode.Created);
        }
        Assert.True(failures == 0, $"{failures} van 20 gelijktijdige kopieën mislukten");
    }

    [Fact]
    public async Task Invoices_sent_at_the_same_time_get_consecutive_numbers_without_errors()
    {
        var c = await ReadyAsync(factory, "race-number");
        var customerId = await CustomerAsync(c);
        var year = DateTime.Today.Year;

        // Meteen verstuurd aanmaken, en concepten tegelijk versturen.
        var direct = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SafeSend(() => PostInvoiceAsync(c, customerId, "verzonden", 1, 100))));
        var concepts = new List<int>();
        for (var i = 0; i < 6; i++) concepts.Add(await InvoiceAsync(c, customerId, "concept", 1, 100));
        var sent = await Task.WhenAll(concepts.Select(id => SafeSend(() => SetStatusAsync(c, id, "verzonden"))));

        Assert.All(direct, r => Assert.Equal(HttpStatusCode.Created, r?.StatusCode));
        Assert.All(sent, r => Assert.Equal(HttpStatusCode.OK, r?.StatusCode));
        var numbers = (await c.GetFromJsonAsync<List<System.Text.Json.JsonElement>>("/api/invoices"))!
            .Select(i => i.GetProperty("number").GetString()).OrderBy(n => n).ToList();
        Assert.Equal(Enumerable.Range(1, 10).Select(n => $"{year}-{n:0000}"), numbers);
    }
}
