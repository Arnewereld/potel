using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Vrijgesteld van btw (art. 11 Wet OB), bijvoorbeeld zorg of onderwijs: een eigen regeling, zonder btw op de factuur.
public class VatRegimeTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task An_exempt_invoice_can_be_made_and_sent_without_vat()
    {
        var c = await ReadyAsync(factory, "vrijgesteld");
        var customerId = await CustomerAsync(c);
        var res = await PostInvoiceAsync(c, customerId, "concept", 1, 100, vatRate: 21, vatRegime: "vrijgesteld");
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var inv = await Json(res);
        Assert.Equal("vrijgesteld", inv.GetProperty("vatRegime").GetString());
        // Zonder btw: elke regel op 0%, en het totaal is het bedrag zelf.
        Assert.Equal(0m, inv.GetProperty("lines")[0].GetProperty("vatRate").GetDecimal());
        Assert.Equal(100m, Total(inv));

        var sent = await SetStatusAsync(c, inv.GetProperty("id").GetInt32(), "verzonden");
        Assert.True(sent.StatusCode == HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Exempt_can_be_the_workspace_default_for_new_invoices()
    {
        var c = await ReadyAsync(factory, "vrijgesteld-standaard", vatRegime: "vrijgesteld");
        Assert.Equal("vrijgesteld", (await c.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("vatRegime").GetString());

        // Een factuur uit de uren van een project krijgt de standaardregeling.
        var customerId = await CustomerAsync(c);
        var projectId = await ProjectAsync(c, customerId, 80);
        await TimeAsync(c, projectId, 60, "2026-10-01");
        var created = await c.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { detailed = false });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var inv = await GetInvoiceAsync(c, (await Json(created)).GetProperty("id").GetInt32());
        Assert.Equal("vrijgesteld", inv.GetProperty("vatRegime").GetString());
        Assert.Equal(80m, Total(inv));

        // Verleggen hangt van de klant af en kan geen standaard zijn.
        var refused = await c.PutAsJsonAsync("/api/settings", Company(vatRegime: "verlegd"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("vrijgesteld", await refused.ErrorAsync());
    }
}

// Met de KOR doe je geen btw-aangifte: het dashboard noemt dan geen aangiftedatum.
public class KorDashboardTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task With_the_kor_the_dashboard_shows_no_vat_return_deadline()
    {
        var kor = await ReadyAsync(factory, "kor-dashboard", vatRegime: "kor");
        var vat = (await kor.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("vat");
        Assert.True(vat.GetProperty("kor").GetBoolean());
        Assert.Equal(JsonValueKind.Null, vat.GetProperty("dueDate").ValueKind);

        var normal = await ReadyAsync(factory, "normaal-dashboard");
        var normalVat = (await normal.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("vat");
        Assert.False(normalVat.GetProperty("kor").GetBoolean());
        Assert.Equal(JsonValueKind.String, normalVat.GetProperty("dueDate").ValueKind);
    }
}
