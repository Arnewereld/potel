using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Potel.Api.Data;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Voorbeelddata uit de welkomstwizard: voorbeeldfacturen hebben een eigen nummerreeks en alles is in één keer weg te halen.
public class DemoDataTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    async Task<HttpClient> WithDemoDataAsync(string prefix)
    {
        var c = await TestApi.RegisterAsync(factory, $"{prefix}-{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/workspace/demo-data", null)).StatusCode);
        (await c.PutAsJsonAsync("/api/settings", Company())).EnsureSuccessStatusCode();
        return c;
    }

    static async Task<List<JsonElement>> ListAsync(HttpClient c, string url) => (await c.GetFromJsonAsync<List<JsonElement>>(url))!;

    [Fact]
    public async Task Demo_invoices_can_be_deleted_and_do_not_take_real_invoice_numbers()
    {
        var c = await WithDemoDataAsync("demo-nummers");
        var demo = await ListAsync(c, "/api/invoices");
        Assert.NotEmpty(demo);
        Assert.All(demo, i => Assert.True(i.GetProperty("demo").GetBoolean()));
        Assert.All(demo.Where(i => i.GetProperty("number").ValueKind == JsonValueKind.String),
            i => Assert.StartsWith("VOORBEELD-", i.GetProperty("number").GetString()));
        Assert.Contains(demo, i => i.GetProperty("status").GetString() != "concept");

        // De eerste echte factuur krijgt gewoon nummer 1 van dit jaar, ook met de voorbeeldfacturen er nog naast.
        var customerId = await CustomerAsync(c, "Echte klant");
        var real = await GetInvoiceAsync(c, await InvoiceAsync(c, customerId, "verzonden", 1, 100));
        Assert.Equal($"{new BusinessClock().Today.Year}-0001", real.GetProperty("number").GetString());
        Assert.False(real.GetProperty("demo").GetBoolean());

        // Ook verstuurde en betaalde voorbeeldfacturen mogen weg; een echte verstuurde factuur niet.
        foreach (var inv in demo)
            Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/invoices/{inv.GetProperty("id").GetInt32()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/invoices/{real.GetProperty("id").GetInt32()}")).StatusCode);
    }

    [Fact]
    public async Task A_demo_invoice_cannot_be_credited()
    {
        var c = await WithDemoDataAsync("demo-credit");
        var sent = (await ListAsync(c, "/api/invoices")).First(i => i.GetProperty("status").GetString() != "concept");
        var res = await c.PostAsync($"/api/invoices/{sent.GetProperty("id").GetInt32()}/credit", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("voorbeeldfactuur", await res.ErrorAsync());
    }

    [Fact]
    public async Task Removing_demo_data_keeps_what_you_added_yourself()
    {
        var c = await WithDemoDataAsync("demo-weg");
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/workspace")).GetProperty("demoData").GetBoolean());

        // Eigen spullen: een klant met een factuur, en eigen uren op een voorbeeldproject.
        var customerId = await CustomerAsync(c, "Eigen klant");
        var ownInvoice = await InvoiceAsync(c, customerId, "verzonden", 1, 100);
        var demoProject = (await ListAsync(c, "/api/projects")).First().GetProperty("id").GetInt32();
        await TimeAsync(c, demoProject, 30, Day(DateTime.Today));
        var lead = await c.PostAsJsonAsync("/api/leads", new { name = "Eigen lead" });
        Assert.Equal(HttpStatusCode.Created, lead.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/workspace/demo-data")).StatusCode);

        var invoices = await ListAsync(c, "/api/invoices");
        Assert.Equal(new[] { ownInvoice }, invoices.Select(i => i.GetProperty("id").GetInt32()));
        var customers = await ListAsync(c, "/api/customers");
        Assert.Contains(customers, x => x.GetProperty("id").GetInt32() == customerId);
        // Het voorbeeldproject met eigen uren blijft, met zijn klant; de andere voorbeeldprojecten zijn weg.
        var projects = await ListAsync(c, "/api/projects");
        Assert.Equal(new[] { demoProject }, projects.Select(p => p.GetProperty("id").GetInt32()));
        Assert.Equal(30, (await ListAsync(c, $"/api/time?projectId={demoProject}")).Sum(t => t.GetProperty("minutes").GetInt32()));
        Assert.Equal(new[] { "Eigen lead" }, (await ListAsync(c, "/api/leads")).Select(l => l.GetProperty("name").GetString()));
        Assert.Empty(await ListAsync(c, "/api/workflows"));
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/workspace")).GetProperty("demoData").GetBoolean());
    }
}
