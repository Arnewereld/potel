using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;

namespace Potel.Api.Tests;

// Gedeelde hulpjes voor tests met meerdere werkruimtes.
static class TestApi
{
    public static HttpClient Client(WebApplicationFactory<Program> f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    public static async Task<(HttpClient Client, HttpStatusCode Status)> TryRegisterAsync(WebApplicationFactory<Program> f, string email, string password = "geheim123")
    {
        var client = Client(f);
        var res = await client.PostAsJsonAsync("/api/auth/register", new { company = $"Bedrijf {email}", name = "Eigenaar", email, password, demoData = false, kvk = "12345678", acceptTerms = true, businessUse = true });
        return (client, res.StatusCode);
    }

    // Een nieuwe werkruimte waarvan de beheerder zijn adres al bevestigde, zoals na een klik op de link in de bevestigingsmail.
    public static async Task<HttpClient> RegisterAsync(WebApplicationFactory<Program> f, string email, string password = "geheim123", bool verified = true)
    {
        var (client, status) = await TryRegisterAsync(f, email, password);
        Assert.Equal(HttpStatusCode.Created, status);
        if (verified) MarkVerified(f, email);
        return client;
    }

    public static void MarkVerified(WebApplicationFactory<Program> f, string email)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var user = db.Users.IgnoreQueryFilters().Single(u => u.Email == email);
        user.EmailVerifiedAt = DateTime.UtcNow;
        db.Tenant.WorkspaceId = user.WorkspaceId;
        db.SaveChanges();
    }

    public static async Task<(HttpClient Client, HttpStatusCode Status)> TryLoginAsync(WebApplicationFactory<Program> f, string email, string password = "geheim123")
    {
        var client = Client(f);
        var res = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        return (client, res.StatusCode);
    }

    public static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> f, string email, string password = "geheim123")
    {
        var (client, status) = await TryLoginAsync(f, email, password);
        Assert.Equal(HttpStatusCode.OK, status);
        return client;
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<string> ErrorAsync(this HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    public static async Task<int> WorkspaceIdAsync(HttpClient c) => (await c.GetFromJsonAsync<JsonElement>("/api/workspace")).GetProperty("id").GetInt32();

    public static async Task<int> MyIdAsync(HttpClient c) => (await c.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("id").GetInt32();

    public static async Task<int> CreateUserAsync(HttpClient admin, string email, string role = "medewerker", bool active = true)
    {
        var res = await admin.PostAsJsonAsync("/api/users", new { name = email.Split('@')[0], email, role, active, password = "geheim123" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.JsonAsync()).GetProperty("id").GetInt32();
    }

    public static async Task<int> CreateCustomerAsync(HttpClient c, string name = "Klant") =>
        (await (await c.PostAsJsonAsync("/api/customers", new { name })).JsonAsync()).GetProperty("id").GetInt32();

    // Zet het abonnement van een werkruimte om via de platformeigenaar uit de ontwikkelinstellingen.
    public static async Task SetPlanAsync(WebApplicationFactory<Program> f, int workspaceId, string plan, string? trialEndsAt = null)
    {
        var owner = await LoginAsync(f, "admin@potel.nl", "welkom123");
        (await owner.PutAsJsonAsync($"/api/platform/workspaces/{workspaceId}/plan", new { plan, trialEndsAt })).EnsureSuccessStatusCode();
    }

    public static Task ExpireTrialAsync(WebApplicationFactory<Program> f, int workspaceId) => SetPlanAsync(f, workspaceId, "proef", "2020-01-01");
}

// Een PortalFactory met een strenge limiet, om de begrenzing zelf te testen.
public class StrictRateLimitFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimit:AuthPerMinute", "3");
    }
}
