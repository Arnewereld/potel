using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Uren op een factuur: per boeking evenveel als in één regel, vrij zodra ze van een concept af gaan, en per project goed geteld.
public class InvoiceHoursTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    static decimal Subtotal(JsonElement invoice) => invoice.GetProperty("totals").GetProperty("subtotal").GetDecimal();

    [Theory]
    [InlineData(20, 95)]    // 3 x 0,33 = 0,99 uur i.p.v. 1,00
    [InlineData(10, 95)]    // 3 x 0,17 = 0,51 uur i.p.v. 0,50
    [InlineData(20, 87.5)]  // 0,33 x 87,50 = 28,875: per regel afgerond scheelt het een cent
    public async Task Detailed_hour_invoice_bills_the_same_as_the_summary(int minutes, decimal rate)
    {
        var c = await ReadyAsync(factory, $"detail{minutes}");
        var customerId = await CustomerAsync(c);
        var detailedProject = await ProjectAsync(c, customerId, rate, "Per boeking");
        var summaryProject = await ProjectAsync(c, customerId, rate, "Totaal");
        await TimeAsync(c, detailedProject, minutes, "2026-10-01", count: 3);
        await TimeAsync(c, summaryProject, minutes, "2026-10-01", count: 3);
        var promised = (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{detailedProject}")).GetProperty("unbilledValue").GetDecimal();

        async Task<JsonElement> InvoiceOf(int projectId, bool detailed)
        {
            var res = await c.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { detailed });
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            return await GetInvoiceAsync(c, (await Json(res)).GetProperty("id").GetInt32());
        }
        var detailedInvoice = await InvoiceOf(detailedProject, true);
        var summaryInvoice = await InvoiceOf(summaryProject, false);

        Assert.Equal(HourQuantity(summaryInvoice), HourQuantity(detailedInvoice));
        Assert.Equal(promised, Subtotal(summaryInvoice));
        Assert.Equal(promised, Subtotal(detailedInvoice));
        Assert.Equal(Total(summaryInvoice), Total(detailedInvoice));
    }

    [Fact]
    public async Task Removing_hour_lines_from_a_concept_releases_those_hours()
    {
        var c = await ReadyAsync(factory, "unlink");
        var customerId = await CustomerAsync(c);
        var projectId = await ProjectAsync(c, customerId, 95);
        await TimeAsync(c, projectId, 120, "2026-10-01");
        var invoiceId = await InvoiceAsync(c, customerId, "concept", 1, 100, description: "Handmatig");
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/invoices/{invoiceId}/hours", new { projectId, detailed = false })).StatusCode);
        var inv = await GetInvoiceAsync(c, invoiceId);

        var put = await c.PutAsJsonAsync($"/api/invoices/{invoiceId}",
            WithLines(inv, new[] { new { description = "Handmatig", quantity = 1, unit = "stuk", unitPrice = 100, vatRate = 21 } }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        Assert.Equal(0m, HourQuantity(await GetInvoiceAsync(c, invoiceId)));
        var project = await c.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}");
        Assert.Equal(120, project.GetProperty("minutesUnbilled").GetInt32());
        var entry = (await c.GetFromJsonAsync<List<JsonElement>>($"/api/time?projectId={projectId}"))!.Single();
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("invoiceId").ValueKind);
        // En ze kunnen weer gewoon gewijzigd en gefactureerd worden.
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/time/{entry.GetProperty("id").GetInt32()}",
            new { projectId, date = "2026-10-01", minutes = 150, description = "Werk", billable = true })).StatusCode);
    }

    [Fact]
    public async Task Only_the_hours_of_the_removed_line_come_free_and_edits_keep_the_others_linked()
    {
        var c = await ReadyAsync(factory, "unlink-one");
        var customerId = await CustomerAsync(c);
        var a = await ProjectAsync(c, customerId, 95, "A");
        var b = await ProjectAsync(c, customerId, 80, "B");
        await TimeAsync(c, a, 60, "2026-10-01");
        await TimeAsync(c, b, 30, "2026-10-02");
        var invoiceId = await InvoiceAsync(c, customerId, "concept", 1, 0, description: "-");
        await c.PostAsJsonAsync($"/api/invoices/{invoiceId}/hours", new { projectId = a, detailed = false });
        await c.PostAsJsonAsync($"/api/invoices/{invoiceId}/hours", new { projectId = b, detailed = false });
        var inv = await GetInvoiceAsync(c, invoiceId);
        var lineA = inv.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("unitPrice").GetDecimal() == 95m);

        // Regel A blijft (met een andere omschrijving), regel B gaat eraf.
        var put = await c.PutAsJsonAsync($"/api/invoices/{invoiceId}", WithLines(inv, new[]
        {
            new { id = lineA.GetProperty("id").GetInt32(), description = "Project A, oktober", quantity = 1m, unit = "uur", unitPrice = 95m, vatRate = 21m },
        }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{a}")).GetProperty("minutesUnbilled").GetInt32());
        Assert.Equal(30, (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{b}")).GetProperty("minutesUnbilled").GetInt32());
        Assert.Equal(95m, (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{a}")).GetProperty("invoicedValue").GetDecimal());

        // Bij nog een keer opslaan blijven de uren van A aan hun (opnieuw opgeslagen) regel hangen.
        inv = await GetInvoiceAsync(c, invoiceId);
        var line = inv.GetProperty("lines")[0];
        await c.PutAsJsonAsync($"/api/invoices/{invoiceId}", WithLines(inv, new[]
        {
            new { id = line.GetProperty("id").GetInt32(), description = "Project A", quantity = 1m, unit = "uur", unitPrice = 95m, vatRate = 21m },
            new { id = 0, description = "Extra", quantity = 1m, unit = "stuk", unitPrice = 10m, vatRate = 21m },
        }));
        Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{a}")).GetProperty("minutesUnbilled").GetInt32());
        inv = await GetInvoiceAsync(c, invoiceId);
        await c.PutAsJsonAsync($"/api/invoices/{invoiceId}", WithLines(inv, new[]
        {
            new { id = inv.GetProperty("lines")[1].GetProperty("id").GetInt32(), description = "Extra", quantity = 1m, unit = "stuk", unitPrice = 10m, vatRate = 21m },
        }));
        Assert.Equal(60, (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{a}")).GetProperty("minutesUnbilled").GetInt32());
    }

    [Fact]
    public async Task Delivery_period_is_filled_from_the_dates_of_the_hours()
    {
        var c = await ReadyAsync(factory, "delivery");
        var customerId = await CustomerAsync(c);
        var projectId = await ProjectAsync(c, customerId, 95);
        await TimeAsync(c, projectId, 60, "2026-09-20");
        await TimeAsync(c, projectId, 60, "2026-09-03");
        var res = await c.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { detailed = false });
        var inv = await GetInvoiceAsync(c, (await Json(res)).GetProperty("id").GetInt32());
        Assert.StartsWith("2026-09-03", inv.GetProperty("deliveryFrom").GetString());
        Assert.StartsWith("2026-09-20", inv.GetProperty("deliveryTo").GetString());

        // Ook als je uren aan een bestaand concept toevoegt.
        var other = await ProjectAsync(c, customerId, 95, "Ander");
        await TimeAsync(c, other, 30, "2026-08-31");
        var manual = await InvoiceAsync(c, customerId, "concept", 1, 0, description: "-");
        var added = await Json(await c.PostAsJsonAsync($"/api/invoices/{manual}/hours", new { projectId = other, detailed = false }));
        Assert.StartsWith("2026-08-31", added.GetProperty("deliveryFrom").GetString());
        Assert.StartsWith("2026-08-31", added.GetProperty("deliveryTo").GetString());
    }

    [Fact]
    public async Task Project_invoiced_amount_counts_only_its_own_hours()
    {
        var c = await ReadyAsync(factory, "project-invoiced");
        var customerId = await CustomerAsync(c);
        var a = await ProjectAsync(c, customerId, 95, "A");
        var b = await ProjectAsync(c, customerId, 95, "B");
        await TimeAsync(c, a, 600, "2026-10-01");
        await TimeAsync(c, b, 300, "2026-10-01");
        var invoiceId = await InvoiceAsync(c, customerId, "concept", 1, 0, description: "-");
        await c.PostAsJsonAsync($"/api/invoices/{invoiceId}/hours", new { projectId = a, detailed = false });
        await c.PostAsJsonAsync($"/api/invoices/{invoiceId}/hours", new { projectId = b, detailed = true });

        var invA = (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{a}")).GetProperty("invoicedValue").GetDecimal();
        var invB = (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{b}")).GetProperty("invoicedValue").GetDecimal();
        Assert.True(invA == 950m && invB == 475m, $"Project A toont {invA} (verwacht 950), project B toont {invB} (verwacht 475)");
    }
}
