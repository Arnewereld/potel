using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Verstuurde creditnota's tellen mee bij wat een klant nog moet betalen: een helemaal gecrediteerde factuur verloopt niet,
// kan niet meer op betaald, en zijn uren komen weer vrij. Een deel crediteren verlaagt het openstaande bedrag.
public class CreditSettlementTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    async Task<int> CreditAsync(HttpClient c, int invoiceId, decimal? quantity = null)
    {
        var credit = await Json(await c.PostAsync($"/api/invoices/{invoiceId}/credit", null));
        var creditId = credit.GetProperty("id").GetInt32();
        if (quantity is { } q)
        {
            var line = credit.GetProperty("lines")[0];
            var put = await c.PutAsJsonAsync($"/api/invoices/{creditId}", WithLines(credit, new[]
            {
                new { description = line.GetProperty("description").GetString(), quantity = q, unit = line.GetProperty("unit").GetString(), unitPrice = line.GetProperty("unitPrice").GetDecimal(), vatRate = line.GetProperty("vatRate").GetDecimal() },
            }));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }
        var sent = await SetStatusAsync(c, creditId, "verzonden");
        Assert.True(sent.StatusCode == HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        return creditId;
    }

    // De betaaltermijn is al verstreken, zoals bij een factuur van een maand geleden.
    void Backdate(int workspaceId, int invoiceId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        db.Tenant.WorkspaceId = workspaceId;
        var inv = db.Invoices.Single(i => i.Id == invoiceId);
        inv.IssueDate = DateTime.UtcNow.Date.AddDays(-30);
        inv.DueDate = DateTime.UtcNow.Date.AddDays(-16);
        db.SaveChanges();
    }

    static async Task<int> PaidWorkflowAsync(HttpClient c) =>
        await WorkflowKit.CreateWorkflowAsync(c, "Bij betaling", WorkflowKit.GraphJson([WorkflowKit.Node("s", "trigger.paid")], []), active: true);

    int Runs(int workspaceId) => WorkflowKit.InWorkspace(factory, workspaceId, db => db.WorkflowRuns.Count());

    [Fact]
    public async Task A_fully_credited_invoice_does_not_go_overdue_and_cannot_be_marked_paid()
    {
        var c = await ReadyAsync(factory, "helemaal-gecrediteerd");
        var workspaceId = await TestApi.WorkspaceIdAsync(c);
        var customerId = await CustomerAsync(c);
        var invoiceId = await InvoiceAsync(c, customerId, "verzonden", 1, 100);
        var creditId = await CreditAsync(c, invoiceId);
        Backdate(workspaceId, invoiceId);
        await PaidWorkflowAsync(c);

        var dash = await c.GetFromJsonAsync<JsonElement>("/api/dashboard");
        var inv = await GetInvoiceAsync(c, invoiceId);
        Assert.Equal("verzonden", inv.GetProperty("status").GetString());
        Assert.True(inv.GetProperty("fullyCredited").GetBoolean());
        Assert.Equal(0m, inv.GetProperty("openAmount").GetDecimal());
        Assert.Equal(creditId, inv.GetProperty("creditNotes")[0].GetProperty("id").GetInt32());
        Assert.Equal(0, dash.GetProperty("overdue").GetInt32());
        Assert.Equal(0m, dash.GetProperty("outstanding").GetDecimal());

        var paid = await SetStatusAsync(c, invoiceId, "betaald");
        Assert.Equal(HttpStatusCode.Conflict, paid.StatusCode);
        Assert.Equal(InvoiceEndpoints.FullyCreditedError, await paid.ErrorAsync());
        Assert.Equal(0, Runs(workspaceId));

        // De creditnota verrekenen maakt de omzet niet negatief: de factuur zelf telde ook niet mee.
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, creditId, "betaald")).StatusCode);
        Assert.Equal(0m, (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("revenueYear").GetDecimal());
    }

    [Fact]
    public async Task A_partial_credit_lowers_what_is_still_open()
    {
        var c = await ReadyAsync(factory, "deels-gecrediteerd");
        var workspaceId = await TestApi.WorkspaceIdAsync(c);
        var customerId = await CustomerAsync(c);
        var invoiceId = await InvoiceAsync(c, customerId, "verzonden", 1, 1000);
        var creditId = await CreditAsync(c, invoiceId, quantity: -0.5m);

        var inv = await GetInvoiceAsync(c, invoiceId);
        Assert.Equal(605m, inv.GetProperty("openAmount").GetDecimal());
        Assert.Equal(-605m, inv.GetProperty("creditedTotal").GetDecimal());
        Assert.False(inv.GetProperty("fullyCredited").GetBoolean());
        var note = inv.GetProperty("creditNotes")[0];
        Assert.Equal(creditId, note.GetProperty("id").GetInt32());
        Assert.Equal(-605m, note.GetProperty("total").GetDecimal());
        Assert.StartsWith($"{new BusinessClock().Today.Year}-", note.GetProperty("number").GetString());
        Assert.Equal(605m, (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("outstanding").GetDecimal());

        // De creditnota verrekenen verandert niets aan wat er nog openstaat; dat was al afgetrokken.
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, creditId, "betaald")).StatusCode);
        Assert.Equal(605m, (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("outstanding").GetDecimal());

        // Een deels gecrediteerde factuur kan gewoon betaald worden, met de werkstroom "Factuur betaald".
        await PaidWorkflowAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, invoiceId, "betaald")).StatusCode);
        Assert.Equal(1, Runs(workspaceId));
        Assert.Equal(0m, (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("outstanding").GetDecimal());
    }

    async Task<(HttpClient C, int ProjectId, int InvoiceId)> SentHoursInvoiceAsync(string prefix)
    {
        var c = await ReadyAsync(factory, prefix);
        var customerId = await CustomerAsync(c);
        var projectId = await ProjectAsync(c, customerId, 95);
        await TimeAsync(c, projectId, 120, "2026-10-01");
        var invoiceId = (await Json(await c.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { detailed = false }))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, invoiceId, "verzonden")).StatusCode);
        return (c, projectId, invoiceId);
    }

    static Task<JsonElement> ProjectInfoAsync(HttpClient c, int projectId) => c.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}");

    [Fact]
    public async Task The_hours_of_a_fully_credited_invoice_can_be_invoiced_again()
    {
        var (c, projectId, invoiceId) = await SentHoursInvoiceAsync("uren-terug");
        Assert.Equal(190m, (await ProjectInfoAsync(c, projectId)).GetProperty("invoicedValue").GetDecimal());

        await CreditAsync(c, invoiceId);
        var project = await ProjectInfoAsync(c, projectId);
        Assert.Equal(120, project.GetProperty("minutesUnbilled").GetInt32());
        Assert.Equal(0m, project.GetProperty("invoicedValue").GetDecimal());

        var again = await c.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { detailed = false });
        Assert.True(again.StatusCode == HttpStatusCode.Created, await again.Content.ReadAsStringAsync());
        Assert.Equal(2m, HourQuantity(await GetInvoiceAsync(c, (await Json(again)).GetProperty("id").GetInt32())));
    }

    [Fact]
    public async Task A_partial_credit_keeps_the_hours_on_the_invoice()
    {
        var (c, projectId, invoiceId) = await SentHoursInvoiceAsync("uren-blijven");
        await CreditAsync(c, invoiceId, quantity: -1m);
        Assert.Equal(0, (await ProjectInfoAsync(c, projectId)).GetProperty("minutesUnbilled").GetInt32());
    }
}
