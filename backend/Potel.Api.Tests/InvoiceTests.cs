using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Potel.Api.Tests;

public class InvoiceTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    static async Task<JsonElement> Json(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    async Task<(HttpClient Client, int CustomerId, int InvoiceId)> CreateAsync(object? extra = null)
    {
        var client = await factory.LoginAsync();
        var customer = await Json(await client.PostAsJsonAsync("/api/customers", new { name = "Klant", vatNumber = "DE123456789" }));
        var customerId = customer.GetProperty("id").GetInt32();
        var res = await client.PostAsJsonAsync("/api/invoices", new
        {
            customerId, issueDate = "2026-10-01", dueDate = "2026-10-15", status = "concept", reference = "PO-1", reverseCharge = true,
            lines = new[] { new { description = "Bouwen", quantity = 10, unit = "uur", unitPrice = 100, vatRate = 21 } },
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (client, customerId, (await Json(res)).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Reverse_charge_sets_vat_to_zero_and_keeps_unit_and_reference()
    {
        var (client, _, id) = await CreateAsync();
        var inv = await client.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}");
        Assert.Equal("PO-1", inv.GetProperty("reference").GetString());
        Assert.Equal("uur", inv.GetProperty("lines")[0].GetProperty("unit").GetString());
        Assert.Equal(0m, inv.GetProperty("lines")[0].GetProperty("vatRate").GetDecimal());
    }

    [Fact]
    public async Task Marking_paid_records_the_date_and_credit_note_negates_lines()
    {
        var (client, _, id) = await CreateAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/invoices/{id}/credit", null)).StatusCode);

        var paid = await Json(await client.PostAsJsonAsync($"/api/invoices/{id}/status", new { status = "betaald", paidAt = "2026-10-10" }));
        Assert.StartsWith("2026-10-10", paid.GetProperty("paidAt").GetString());

        var credit = await Json(await client.PostAsync($"/api/invoices/{id}/credit", null));
        Assert.Equal("concept", credit.GetProperty("status").GetString());
        Assert.Equal(-10m, credit.GetProperty("lines")[0].GetProperty("quantity").GetDecimal());

        var copy = await Json(await client.PostAsync($"/api/invoices/{id}/duplicate", null));
        Assert.NotEqual(paid.GetProperty("number").GetString(), copy.GetProperty("number").GetString());
        Assert.Equal(10m, copy.GetProperty("lines")[0].GetProperty("quantity").GetDecimal());
    }

    [Fact]
    public async Task Open_hours_can_be_added_to_a_draft_of_the_same_customer()
    {
        var (client, customerId, id) = await CreateAsync();
        var project = await Json(await client.PostAsJsonAsync("/api/projects", new { name = "App", customerId, status = "actief", billing = "uur", hourlyRate = 80, color = "#000" }));
        var projectId = project.GetProperty("id").GetInt32();
        await client.PostAsJsonAsync("/api/time", new { projectId, date = "2026-10-02", minutes = 120, description = "Werk", billable = true });

        var res = await client.PostAsJsonAsync($"/api/invoices/{id}/hours", new { projectId, detailed = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var lines = (await Json(res)).GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Contains(lines.EnumerateArray(), l => l.GetProperty("quantity").GetDecimal() == 2m && l.GetProperty("unitPrice").GetDecimal() == 80m);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}")).GetProperty("minutesUnbilled").GetInt32());
    }
}
