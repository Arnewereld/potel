using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Potel.Api.Tests;

public class WorkspaceTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    async Task<HttpClient> RegisterAsync(string email, bool demo = false)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var res = await client.PostAsJsonAsync("/api/auth/register", new { company = $"Bedrijf {email}", name = "Eigenaar", email, password = "geheim123", demoData = demo });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return client;
    }

    [Fact]
    public async Task New_workspace_starts_empty_with_a_trial_and_its_own_settings()
    {
        var client = await RegisterAsync("nieuw@example.com");
        var ws = await client.GetFromJsonAsync<JsonElement>("/api/workspace");
        Assert.Equal("proef", ws.GetProperty("plan").GetString());
        Assert.Equal(30, ws.GetProperty("trialDaysLeft").GetInt32());
        Assert.False(ws.GetProperty("onboarded").GetBoolean());
        Assert.Empty((await client.GetFromJsonAsync<List<JsonElement>>("/api/customers"))!);
        Assert.Equal("Bedrijf nieuw@example.com", (await client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("companyName").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/workspace/demo-data", null)).StatusCode);
        Assert.NotEmpty((await client.GetFromJsonAsync<List<JsonElement>>("/api/projects"))!);
        // De bedrijfsgegevens blijven die van de nieuwe klant.
        Assert.Equal("Bedrijf nieuw@example.com", (await client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("companyName").GetString());
    }

    [Fact]
    public async Task Workspaces_cannot_see_or_touch_each_others_data()
    {
        var a = await RegisterAsync("a@example.com");
        var b = await RegisterAsync("b@example.com");

        var customer = await (await a.PostAsJsonAsync("/api/customers", new { name = "Geheime klant" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = customer.GetProperty("id").GetInt32();

        Assert.Empty((await b.GetFromJsonAsync<List<JsonElement>>("/api/customers"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/customers/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsJsonAsync($"/api/customers/{id}", new { name = "Gekaapt" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/customers/{id}")).StatusCode);
        var invoice = await b.PostAsJsonAsync("/api/invoices", new
        {
            customerId = id, issueDate = "2026-10-01", dueDate = "2026-10-15", status = "concept",
            lines = new[] { new { description = "x", quantity = 1, unit = "stuk", unitPrice = 1, vatRate = 21 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invoice.StatusCode);
        Assert.DoesNotContain("Geheime klant", await (await b.GetAsync("/api/search?q=geheim")).Content.ReadAsStringAsync());

        // Een meegestuurde werkruimte wordt genegeerd.
        await b.PostAsJsonAsync("/api/customers", new { name = "Klant van B", workspaceId = 1 });
        Assert.DoesNotContain((await a.GetFromJsonAsync<List<JsonElement>>("/api/customers"))!, c => c.GetProperty("name").GetString() == "Klant van B");
        Assert.Equal("Geheime klant", (await a.GetFromJsonAsync<JsonElement>($"/api/customers/{id}")).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Email_is_unique_across_workspaces_and_deleting_removes_everything()
    {
        var client = await RegisterAsync("weg@example.com", demo: true);
        var again = await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new { company = "X", name = "Y", email = "weg@example.com", password = "geheim123", demoData = false });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var wrong = new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "fout" }) };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(wrong)).StatusCode);
        var right = new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "geheim123" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(right)).StatusCode);

        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "weg@example.com", password = "geheim123" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Only_platform_admins_can_see_all_workspaces_and_change_plans()
    {
        var customer = await RegisterAsync("klant@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/platform/workspaces")).StatusCode);
        var wsId = (await customer.GetFromJsonAsync<JsonElement>("/api/workspace")).GetProperty("id").GetInt32();

        var owner = await factory.LoginAsync();
        var all = await owner.GetFromJsonAsync<List<JsonElement>>("/api/platform/workspaces");
        Assert.Contains(all!, w => w.GetProperty("owner").GetString() == "klant@example.com");

        (await owner.PutAsJsonAsync($"/api/platform/workspaces/{wsId}/plan", new { plan = "zzp" })).EnsureSuccessStatusCode();
        Assert.Equal("zzp", (await customer.GetFromJsonAsync<JsonElement>("/api/workspace")).GetProperty("plan").GetString());
    }

    [Fact]
    public async Task Expired_trial_is_read_only_until_a_plan_is_chosen()
    {
        var customer = await RegisterAsync("verlopen@example.com");
        var wsId = (await customer.GetFromJsonAsync<JsonElement>("/api/workspace")).GetProperty("id").GetInt32();
        var owner = await factory.LoginAsync();
        await owner.PutAsJsonAsync($"/api/platform/workspaces/{wsId}/plan", new { plan = "proef", trialEndsAt = "2020-01-01" });

        Assert.Equal(HttpStatusCode.PaymentRequired, (await customer.PostAsJsonAsync("/api/customers", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/workspace/export")).StatusCode);

        await owner.PutAsJsonAsync($"/api/platform/workspaces/{wsId}/plan", new { plan = "zzp" });
        Assert.Equal(HttpStatusCode.Created, (await customer.PostAsJsonAsync("/api/customers", new { name = "X" })).StatusCode);
    }
}
