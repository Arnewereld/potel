using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Potel.Api.Tests;

public class ProjectTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task Open_hours_become_an_invoice_and_are_released_when_it_is_deleted()
    {
        var client = await factory.LoginAsync();
        var customer = await (await client.PostAsJsonAsync("/api/customers", new { name = "Testklant" })).Content.ReadFromJsonAsync<JsonElement>();
        var created = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "API bouwen", customerId = customer.GetProperty("id").GetInt32(), status = "actief", billing = "uur", hourlyRate = 100, color = "#4ea5ff",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var projectId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        await client.PostAsJsonAsync("/api/time", new { projectId, date = "2026-10-05", minutes = 90, description = "Endpoints", billable = true });
        await client.PostAsJsonAsync("/api/time", new { projectId, date = "2026-10-06", minutes = 60, description = "Tests", billable = true });
        await client.PostAsJsonAsync("/api/time", new { projectId, date = "2026-10-06", minutes = 30, description = "Koffie", billable = false });

        var project = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}");
        Assert.Equal(150, project.GetProperty("minutesUnbilled").GetInt32());
        Assert.Equal(250m, project.GetProperty("unbilledValue").GetDecimal());

        var res = await client.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { detailed = false });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var invoiceId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var invoice = await client.GetFromJsonAsync<JsonElement>($"/api/invoices/{invoiceId}");
        var line = invoice.GetProperty("lines")[0];
        Assert.Equal(2.5m, line.GetProperty("quantity").GetDecimal());
        Assert.Equal(100m, line.GetProperty("unitPrice").GetDecimal());

        // Gefactureerde uren liggen vast en tellen niet meer als open.
        var entries = await client.GetFromJsonAsync<List<JsonElement>>($"/api/time?projectId={projectId}");
        var locked = entries!.First(e => e.GetProperty("invoiceId").ValueKind == JsonValueKind.Number);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/time/{locked.GetProperty("id").GetInt32()}")).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}")).GetProperty("minutesUnbilled").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { detailed = false })).StatusCode);

        await client.DeleteAsync($"/api/invoices/{invoiceId}");
        Assert.Equal(150, (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}")).GetProperty("minutesUnbilled").GetInt32());
    }

    [Fact]
    public async Task Dashboard_reports_hours_and_vat()
    {
        var client = await factory.LoginAsync();
        var dash = await client.GetFromJsonAsync<JsonElement>("/api/dashboard");
        Assert.Equal(1225, dash.GetProperty("hours").GetProperty("yearTarget").GetInt32());
        Assert.Equal(7, dash.GetProperty("hours").GetProperty("byDay").GetArrayLength());
        Assert.True(dash.GetProperty("vat").GetProperty("amount").GetDecimal() >= 0);
    }
}
