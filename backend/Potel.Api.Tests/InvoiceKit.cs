using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Potel.Api.Tests;

// Hulpjes voor factuurtests: een eigen werkruimte met complete bedrijfsgegevens, klanten, projecten, uren en facturen.
static class InvoiceKit
{
    public static async Task<JsonElement> Json(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    public static string Day(DateTime d) => d.ToString("yyyy-MM-dd");

    public static object Company(string vatRegime = "normaal", string name = "Mijn Bedrijf", string address = "Keizersgracht 1", string? kvk = "12345678", string? btw = "NL001234567B01") => new
    {
        companyName = name, ownerName = "Eigenaar", address, city = "1015 AA Amsterdam", email = "hallo@mijnbedrijf.nl", kvk, btw,
        iban = "NL00 BANK 0123 4567 89", defaultHourlyRate = 95, paymentTermDays = 14, weeklyHoursTarget = 32, yearlyHoursTarget = 1225,
        brandColor = "#ff6d5a", vatRegime,
    };

    // Een nieuwe werkruimte waarvan de bedrijfsgegevens compleet zijn, zodat facturen verstuurd kunnen worden.
    public static async Task<HttpClient> ReadyAsync(WebApplicationFactory<Program> f, string prefix, string vatRegime = "normaal")
    {
        var c = await TestApi.RegisterAsync(f, $"{prefix}-{Guid.NewGuid():N}@example.com");
        (await c.PutAsJsonAsync("/api/settings", Company(vatRegime))).EnsureSuccessStatusCode();
        return c;
    }

    public static async Task<int> CustomerAsync(HttpClient c, string name = "Klant", string country = "Nederland", string? vatNumber = null, string? address = "Dorpsstraat 1")
    {
        var res = await c.PostAsJsonAsync("/api/customers", new { name, company = $"{name} B.V.", address, city = "1234 AB Utrecht", country, vatNumber });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await Json(res)).GetProperty("id").GetInt32();
    }

    public static async Task<int> ProjectAsync(HttpClient c, int customerId, decimal rate, string name = "Project")
    {
        var res = await c.PostAsJsonAsync("/api/projects", new { name, customerId, status = "actief", billing = "uur", hourlyRate = rate, color = "#000000" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await Json(res)).GetProperty("id").GetInt32();
    }

    public static async Task TimeAsync(HttpClient c, int projectId, int minutes, string date, int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            var res = await c.PostAsJsonAsync("/api/time", new { projectId, date, minutes, description = $"Werk {i}", billable = true });
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        }
    }

    public static Task<HttpResponseMessage> PostInvoiceAsync(HttpClient c, int customerId, string status, decimal quantity, decimal unitPrice,
        decimal vatRate = 21, string description = "Vooraf", string vatRegime = "normaal", string? issueDate = null)
    {
        var issue = issueDate is null ? DateTime.Today : DateTime.Parse(issueDate);
        return c.PostAsJsonAsync("/api/invoices", new
        {
            customerId, issueDate = Day(issue), dueDate = Day(issue.AddDays(14)), status, vatRegime,
            lines = new[] { new { description, quantity, unit = "stuk", unitPrice, vatRate } },
        });
    }

    public static async Task<int> InvoiceAsync(HttpClient c, int customerId, string status, decimal quantity, decimal unitPrice,
        decimal vatRate = 21, string description = "Vooraf", string vatRegime = "normaal", string? issueDate = null)
    {
        var res = await PostInvoiceAsync(c, customerId, status, quantity, unitPrice, vatRate, description, vatRegime, issueDate);
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await Json(res)).GetProperty("id").GetInt32();
    }

    public static Task<JsonElement> GetInvoiceAsync(HttpClient c, int id) => c.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}");

    public static Task<HttpResponseMessage> SetStatusAsync(HttpClient c, int id, string status) =>
        c.PostAsJsonAsync($"/api/invoices/{id}/status", new { status });

    public static decimal HourQuantity(JsonElement invoice) =>
        invoice.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("unit").GetString() == "uur").Sum(l => l.GetProperty("quantity").GetDecimal());

    public static decimal Total(JsonElement invoice) => invoice.GetProperty("totals").GetProperty("total").GetDecimal();

    // Gelijktijdige verzoeken: een verzoek dat crasht telt als mislukt in plaats van de test te breken.
    public static async Task<HttpResponseMessage?> SafeSend(Func<Task<HttpResponseMessage>> send)
    {
        try { return await send(); } catch { return null; }
    }

    // De factuur terugsturen zoals hij is, met andere regels.
    public static object WithLines(JsonElement inv, object lines) => new
    {
        customerId = inv.GetProperty("customerId").GetInt32(), issueDate = inv.GetProperty("issueDate").GetString(),
        dueDate = inv.GetProperty("dueDate").GetString(), status = inv.GetProperty("status").GetString(),
        vatRegime = inv.GetProperty("vatRegime").GetString(), deliveryFrom = inv.GetProperty("deliveryFrom").GetString(),
        deliveryTo = inv.GetProperty("deliveryTo").ValueKind == JsonValueKind.Null ? null : inv.GetProperty("deliveryTo").GetString(),
        lines,
    };
}
