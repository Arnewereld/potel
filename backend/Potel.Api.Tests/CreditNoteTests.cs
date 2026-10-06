using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Een creditnota hangt vast aan de factuur die hij corrigeert, verwijst naar diens nummer en datum en kan niet dubbel.
public class CreditNoteTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    async Task<JsonElement> CreditAsync(HttpClient c, int invoiceId)
    {
        var res = await c.PostAsync($"/api/invoices/{invoiceId}/credit", null);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await Json(res);
    }

    [Fact]
    public async Task Credit_note_is_never_overdue()
    {
        var c = await ReadyAsync(factory, "credit-overdue");
        var wsId = await TestApi.WorkspaceIdAsync(c);
        var customerId = await CustomerAsync(c);
        var invoiceId = await InvoiceAsync(c, customerId, "verzonden", 1, 100);
        var creditId = (await CreditAsync(c, invoiceId)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, creditId, "verzonden")).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            db.Tenant.WorkspaceId = wsId;
            var credit = db.Invoices.Single(i => i.Id == creditId);
            credit.IssueDate = DateTime.UtcNow.Date.AddDays(-20);
            credit.DueDate = DateTime.UtcNow.Date.AddDays(-6);
            db.SaveChanges();
        }
        var dash = await c.GetFromJsonAsync<JsonElement>("/api/dashboard");
        var status = (await GetInvoiceAsync(c, creditId)).GetProperty("status").GetString();
        Assert.True(dash.GetProperty("overdue").GetInt32() == 0 && status != "verlopen",
            $"overdue = {dash.GetProperty("overdue").GetInt32()}, status creditnota = {status}");
    }

    [Fact]
    public async Task Credit_note_links_to_the_original_and_refers_to_its_number_and_date()
    {
        var c = await ReadyAsync(factory, "credit-link");
        var customerId = await CustomerAsync(c);
        var invoiceId = await InvoiceAsync(c, customerId, "verzonden", 2, 50, issueDate: "2026-09-15");
        var original = await GetInvoiceAsync(c, invoiceId);
        var credit = await CreditAsync(c, invoiceId);

        Assert.Equal(invoiceId, credit.GetProperty("creditForInvoiceId").GetInt32());
        Assert.Equal(original.GetProperty("number").GetString(), credit.GetProperty("creditForNumber").GetString());
        Assert.StartsWith("2026-09-15", credit.GetProperty("creditForIssueDate").GetString());
        Assert.True(credit.GetProperty("isCredit").GetBoolean());
        // De verwijzing staat in een vaste regel op de creditnota, niet in de opmerking.
        Assert.True(credit.GetProperty("notes").ValueKind == JsonValueKind.Null || !credit.GetProperty("notes").GetString()!.Contains("Creditnota voor"));

        // Versturen geeft hem een eigen nummer in dezelfde reeks; klant en btw-regeling blijven die van de factuur.
        var otherCustomer = await CustomerAsync(c, "Ander");
        var creditId = credit.GetProperty("id").GetInt32();
        var put = await c.PutAsJsonAsync($"/api/invoices/{creditId}", new
        {
            customerId = otherCustomer, issueDate = "2026-09-20", dueDate = "2026-09-20",
            status = "concept", vatRegime = "kor", deliveryFrom = credit.GetProperty("deliveryFrom").GetString(),
            lines = new[] { new { description = "Vooraf", quantity = -2, unit = "stuk", unitPrice = 50, vatRate = 21 } },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var saved = await Json(put);
        Assert.Equal(customerId, saved.GetProperty("customerId").GetInt32());
        Assert.Equal("normaal", saved.GetProperty("vatRegime").GetString());
        var sent = await Json(await SetStatusAsync(c, creditId, "verzonden"));
        Assert.Equal("2026-0001", original.GetProperty("number").GetString());
        Assert.Equal("2026-0002", sent.GetProperty("number").GetString());
        Assert.Equal(-121m, Total(sent));
    }

    [Fact]
    public async Task Only_sent_or_paid_invoices_can_be_credited_and_never_twice_for_the_full_amount()
    {
        var c = await ReadyAsync(factory, "credit-twice");
        var customerId = await CustomerAsync(c);
        var concept = await InvoiceAsync(c, customerId, "concept", 1, 100);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/invoices/{concept}/credit", null)).StatusCode);

        var paid = await InvoiceAsync(c, customerId, "betaald", 1, 100);
        var credit = await CreditAsync(c, paid);
        // Een creditnota van een creditnota kan niet, en een tweede volledige creditnota ook niet: niet als concept en niet verstuurd.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync($"/api/invoices/{credit.GetProperty("id").GetInt32()}/credit", null)).StatusCode);
        var twice = await c.PostAsync($"/api/invoices/{paid}/credit", null);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Contains("al voor het volledige bedrag gecrediteerd", await twice.ErrorAsync());
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, credit.GetProperty("id").GetInt32(), "verzonden")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/invoices/{paid}/credit", null)).StatusCode);
    }

    [Fact]
    public async Task Partial_credits_together_never_exceed_the_original()
    {
        var c = await ReadyAsync(factory, "credit-partial");
        var customerId = await CustomerAsync(c);
        var invoiceId = await InvoiceAsync(c, customerId, "verzonden", 10, 100);   // 1.210,00
        async Task<int> PartialAsync(decimal quantity)
        {
            var credit = await CreditAsync(c, invoiceId);
            var id = credit.GetProperty("id").GetInt32();
            var put = await c.PutAsJsonAsync($"/api/invoices/{id}", WithLines(credit, new[] { new { description = "Korting", quantity, unit = "stuk", unitPrice = 100, vatRate = 21 } }));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            return id;
        }
        var first = await PartialAsync(-4);
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, first, "verzonden")).StatusCode);

        // Er is nog 6 x 100 over; een tweede creditnota begint vol en moet eerst kleiner.
        var second = (await CreditAsync(c, invoiceId)).GetProperty("id").GetInt32();
        var tooMuch = await SetStatusAsync(c, second, "verzonden");
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
        Assert.Contains("meer dan factuur", await tooMuch.ErrorAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/invoices/{second}")).StatusCode);
        var rest = await PartialAsync(-6);
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, rest, "verzonden")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/invoices/{invoiceId}/credit", null)).StatusCode);
    }

    [Fact]
    public async Task A_negative_invoice_without_original_cannot_be_sent()
    {
        var c = await ReadyAsync(factory, "credit-loose");
        var customerId = await CustomerAsync(c);
        var id = await InvoiceAsync(c, customerId, "concept", -1, 100);
        var res = await SetStatusAsync(c, id, "verzonden");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Creditnota maken", await res.ErrorAsync());
    }

    [Fact]
    public async Task A_workspace_with_credit_notes_can_still_be_deleted()
    {
        var email = $"credit-delete-{Guid.NewGuid():N}@example.com";
        var c = await TestApi.RegisterAsync(factory, email);
        (await c.PutAsJsonAsync("/api/settings", Company())).EnsureSuccessStatusCode();
        var customerId = await CustomerAsync(c);
        var invoiceId = await InvoiceAsync(c, customerId, "verzonden", 1, 100);
        var creditId = (await CreditAsync(c, invoiceId)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, creditId, "verzonden")).StatusCode);

        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "geheim123" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(delete)).StatusCode);
    }
}
