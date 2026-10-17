using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Potel.Api.Data;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// De snelle actie "Factureer open uren van ..." in het zoekvenster: alle open uren van één klant op één conceptfactuur,
// alleen voor beheerders, alleen in de eigen werkruimte en niet als de werkruimte alleen-lezen is.
public class CustomerHoursInvoiceTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    static Task<HttpResponseMessage> InvoiceHoursAsync(HttpClient c, int customerId) =>
        c.PostAsync($"/api/customers/{customerId}/invoice-hours", null);

    static async Task<int> UnbilledAsync(HttpClient c, int projectId) =>
        (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}")).GetProperty("minutesUnbilled").GetInt32();

    [Fact]
    public async Task All_open_hours_of_a_customer_land_on_one_concept_with_a_line_per_project()
    {
        var c = await ReadyAsync(factory, "klanturen");
        var customerId = await CustomerAsync(c, "Webshop");
        var otherCustomer = await CustomerAsync(c, "Ander");
        var site = await ProjectAsync(c, customerId, 100, "Website");
        var app = await ProjectAsync(c, customerId, 80, "App");
        var fixedRes = await c.PostAsJsonAsync("/api/projects", new { name = "Logo", customerId, status = "actief", billing = Billing.Fixed, fixedPrice = 500, color = "#000000" });
        Assert.Equal(HttpStatusCode.Created, fixedRes.StatusCode);
        var fixedId = (await Json(fixedRes)).GetProperty("id").GetInt32();
        var elsewhere = await ProjectAsync(c, otherCustomer, 90, "Elders");
        await TimeAsync(c, site, 90, "2026-10-02");
        await TimeAsync(c, site, 30, "2026-10-05");
        await TimeAsync(c, app, 60, "2026-09-28");
        await TimeAsync(c, fixedId, 120, "2026-10-01");
        await TimeAsync(c, elsewhere, 45, "2026-10-01");
        // Niet-factureerbare uren gaan niet mee.
        (await c.PostAsJsonAsync("/api/time", new { projectId = site, date = "2026-10-03", minutes = 15, description = "Intern", billable = false })).EnsureSuccessStatusCode();

        var res = await InvoiceHoursAsync(c, customerId);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await Json(res);
        Assert.Equal(180, body.GetProperty("minutes").GetInt32());
        Assert.Equal(2, body.GetProperty("projects").GetInt32());

        var inv = await GetInvoiceAsync(c, body.GetProperty("id").GetInt32());
        Assert.Equal("concept", inv.GetProperty("status").GetString());
        Assert.Equal(customerId, inv.GetProperty("customerId").GetInt32());
        var lines = inv.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        // Op volgorde van de projectnaam, elk tegen zijn eigen uurtarief.
        Assert.Contains("App", lines[0].GetProperty("description").GetString());
        Assert.Equal(1m, lines[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(80m, lines[0].GetProperty("unitPrice").GetDecimal());
        Assert.Contains("Website", lines[1].GetProperty("description").GetString());
        Assert.Equal(2m, lines[1].GetProperty("quantity").GetDecimal());
        Assert.Equal(100m, lines[1].GetProperty("unitPrice").GetDecimal());
        Assert.StartsWith("2026-09-28", inv.GetProperty("deliveryFrom").GetString());
        Assert.StartsWith("2026-10-05", inv.GetProperty("deliveryTo").GetString());

        Assert.Equal(0, await UnbilledAsync(c, site));
        Assert.Equal(0, await UnbilledAsync(c, app));
        Assert.Equal(45, await UnbilledAsync(c, elsewhere));

        // Nog een keer: niets meer open, dus geen lege factuur erbij.
        var again = await InvoiceHoursAsync(c, customerId);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("Er staan geen open uren voor Webshop B.V.", await again.ErrorAsync());
        Assert.Single((await c.GetFromJsonAsync<List<JsonElement>>("/api/invoices"))!);
    }

    [Fact]
    public async Task Hours_already_on_another_concept_stay_there()
    {
        var c = await ReadyAsync(factory, "klanturen-deel");
        var customerId = await CustomerAsync(c);
        var first = await ProjectAsync(c, customerId, 95, "Eerste");
        var second = await ProjectAsync(c, customerId, 95, "Tweede");
        await TimeAsync(c, first, 60, "2026-10-01");
        await TimeAsync(c, second, 120, "2026-10-01");
        var manual = await InvoiceAsync(c, customerId, "concept", 1, 100, description: "Handmatig");
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/invoices/{manual}/hours", new { projectId = first, detailed = false })).StatusCode);

        var res = await InvoiceHoursAsync(c, customerId);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal(120, (await Json(res)).GetProperty("minutes").GetInt32());
        Assert.Equal(1m, HourQuantity(await GetInvoiceAsync(c, manual)));
    }

    [Fact]
    public async Task Two_quick_actions_at_once_bill_the_hours_only_once()
    {
        var c = await ReadyAsync(factory, "klanturen-tegelijk");
        var customerId = await CustomerAsync(c);
        var project = await ProjectAsync(c, customerId, 95);
        await TimeAsync(c, project, 60, "2026-10-01", count: 3);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => InvoiceHoursAsync(c, customerId)));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        var invoices = (await c.GetFromJsonAsync<List<JsonElement>>("/api/invoices"))!;
        Assert.Equal(3m, HourQuantity(await GetInvoiceAsync(c, Assert.Single(invoices).GetProperty("id").GetInt32())));
    }

    [Fact]
    public async Task Only_admins_of_the_workspace_itself_and_not_when_read_only()
    {
        var email = $"klanturen-rol-{Guid.NewGuid():N}@example.com";
        var owner = await TestApi.RegisterAsync(factory, email);
        (await owner.PutAsJsonAsync("/api/settings", Company())).EnsureSuccessStatusCode();
        var customerId = await CustomerAsync(owner);
        var project = await ProjectAsync(owner, customerId, 95);
        await TimeAsync(owner, project, 60, "2026-10-01");

        // Een medewerker ziet de actie niet en de server weigert hem ook.
        var employeeEmail = $"klanturen-mw-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(owner, employeeEmail, Roles.Employee);
        var employee = await TestApi.LoginAsync(factory, employeeEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await InvoiceHoursAsync(employee, customerId)).StatusCode);

        // Een beheerder van een andere werkruimte vindt deze klant niet.
        var stranger = await ReadyAsync(factory, "klanturen-vreemd");
        Assert.Equal(HttpStatusCode.NotFound, (await InvoiceHoursAsync(stranger, customerId)).StatusCode);

        // Alleen-lezen: niets wijzigen, dus ook geen factuur maken.
        await TestApi.ExpireTrialAsync(factory, await TestApi.WorkspaceIdAsync(owner));
        Assert.Equal(HttpStatusCode.PaymentRequired, (await InvoiceHoursAsync(owner, customerId)).StatusCode);

        Assert.Equal(60, await UnbilledAsync(owner, project));
        Assert.Empty((await owner.GetFromJsonAsync<List<JsonElement>>("/api/invoices"))!);
    }
}
