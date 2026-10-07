using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// E-mailadressen zijn uniek over alle werkruimtes (je logt in met alleen je adres), maar een werkruimte
// mag daar niet uit kunnen afleiden wie er elders een account heeft.
public class EmailOracleTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task A_taken_address_gives_the_same_message_wherever_it_is_used()
    {
        await RegisterAsync(factory, "elders@victimcorp.nl");
        var a = await RegisterAsync(factory, "orakel-a@example.com");
        await CreateUserAsync(a, "collega-a@example.com");

        var elsewhere = await a.PostAsJsonAsync("/api/users", new { name = "X", email = "elders@victimcorp.nl", role = "medewerker", active = false, password = "geheim123" });
        var ownWorkspace = await a.PostAsJsonAsync("/api/users", new { name = "X", email = "collega-a@example.com", role = "medewerker", active = false, password = "geheim123" });
        var me = await MyIdAsync(a);
        var rename = await a.PutAsJsonAsync($"/api/users/{me}", new { name = "X", email = "elders@victimcorp.nl", role = "beheerder", active = true });
        var (_, signup) = await TryRegisterAsync(factory, "elders@victimcorp.nl");
        var signupMessage = await (await TestApi.Client(factory).PostAsJsonAsync("/api/auth/register",
            new { company = "X", name = "Y", email = "elders@victimcorp.nl", password = "geheim123", demoData = false, kvk = "12345678", acceptTerms = true, businessUse = true })).ErrorAsync();

        Assert.Equal(ownWorkspace.StatusCode, elsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, signup);
        var messages = new[] { await elsewhere.ErrorAsync(), await ownWorkspace.ErrorAsync(), await rename.ErrorAsync(), signupMessage };
        Assert.Single(messages.Distinct());
        Assert.DoesNotContain("werkruimte", messages[0]);
        Assert.Equal("orakel-a@example.com", (await a.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("email").GetString());
    }

    [Fact]
    public async Task Disabling_yourself_is_refused_before_the_address_is_looked_up()
    {
        await RegisterAsync(factory, "slachtoffer@example.com");
        var attacker = await RegisterAsync(factory, "probe@example.com");
        var me = await MyIdAsync(attacker);
        var taken = await attacker.PutAsJsonAsync($"/api/users/{me}", new { name = "x", email = "slachtoffer@example.com", role = "beheerder", active = false });
        var free = await attacker.PutAsJsonAsync($"/api/users/{me}", new { name = "x", email = "niemand-xyz@example.com", role = "beheerder", active = false });
        Assert.Equal(free.StatusCode, taken.StatusCode);
        Assert.Equal(await free.ErrorAsync(), await taken.ErrorAsync());
        Assert.Equal("probe@example.com", (await attacker.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("email").GetString());
    }
}

// Elke test een eigen app, want aanmelden telt mee voor dezelfde limiet.
public class AccountRateLimitTests
{
    [Fact]
    public async Task Adding_users_is_rate_limited()
    {
        using var factory = new StrictRateLimitFactory();
        var a = await RegisterAsync(factory, "enum-a@example.com");
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
            statuses.Add((await a.PostAsJsonAsync("/api/users", new { name = "X", email = $"probe{i}@target.nl", role = "medewerker", active = false, password = "geheim123" })).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task Changing_users_is_rate_limited()
    {
        using var factory = new StrictRateLimitFactory();
        var a = await RegisterAsync(factory, "enum-b@example.com");
        var me = await MyIdAsync(a);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
            statuses.Add((await a.PutAsJsonAsync($"/api/users/{me}", new { name = "X", email = $"probe-put{i}@target.nl", role = "beheerder", active = false })).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task Registering_is_rate_limited()
    {
        using var factory = new StrictRateLimitFactory();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++) statuses.Add((await TryRegisterAsync(factory, $"aanmelden{i}@example.com")).Status);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
