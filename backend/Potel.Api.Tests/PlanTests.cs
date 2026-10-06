using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Potel.Api.Data;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// Wat een abonnement toestaat: hoeveel gebruikers, en wat er na een verlopen proef nog kan.
public class PlanTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task Zzp_workspace_cannot_add_extra_users()
    {
        var c = await RegisterAsync(factory, "zzp-limiet@example.com");
        await SetPlanAsync(factory, await WorkspaceIdAsync(c), "zzp");
        Assert.Equal(1, (await c.GetFromJsonAsync<JsonElement>("/api/workspace")).GetProperty("maxUsers").GetInt32());
        var res = await c.PostAsJsonAsync("/api/users", new { name = "U", email = "zzp-u@example.com", role = "beheerder", active = true, password = "geheim123" });
        Assert.Equal(HttpStatusCode.PaymentRequired, res.StatusCode);
        Assert.Contains("1 gebruiker", await res.ErrorAsync());
    }

    [Fact]
    public async Task Trial_and_team_allow_five_users_counting_disabled_ones()
    {
        var c = await RegisterAsync(factory, "team-limiet@example.com");
        for (var i = 0; i < 3; i++) await CreateUserAsync(c, $"team-u{i}@example.com");
        await CreateUserAsync(c, "team-uit@example.com", active: false);
        var sixth = await c.PostAsJsonAsync("/api/users", new { name = "U", email = "team-u9@example.com", role = "medewerker", active = true, password = "geheim123" });
        Assert.Equal(HttpStatusCode.PaymentRequired, sixth.StatusCode);
        Assert.Contains("5 gebruikers", await sixth.ErrorAsync());

        await SetPlanAsync(factory, await WorkspaceIdAsync(c), "team");
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/users", new { name = "U", email = "team-u9@example.com", role = "medewerker", active = true, password = "geheim123" })).StatusCode);
    }

    [Fact]
    public void Frontend_plans_show_the_same_user_limits()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "frontend", "src", "lib", "plans.ts"))) dir = dir.Parent;
        if (dir is null) return; // alleen de backend uitgecheckt
        var plans = File.ReadAllText(Path.Combine(dir.FullName, "frontend", "src", "lib", "plans.ts"));
        foreach (var plan in Plans.All)
        {
            var m = Regex.Match(plans, $@"id:\s*'{plan}'[^}}]*?maxUsers:\s*(\d+)", RegexOptions.Singleline);
            Assert.True(m.Success, $"plans.ts mist maxUsers voor {plan}");
            Assert.Equal(Plans.MaxUsers(plan), int.Parse(m.Groups[1].Value));
        }
    }

    [Fact]
    public async Task Expired_trial_cannot_insert_demo_data_or_other_writes()
    {
        var c = await RegisterAsync(factory, "demo-verlopen@example.com");
        await ExpireTrialAsync(factory, await WorkspaceIdAsync(c));
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/customers", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsync("/api/workspace/demo-data", null)).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsync("/api/Workspace/Demo-Data/", null)).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/users", new { name = "U", email = "verlopen-u@example.com", role = "medewerker", active = true, password = "geheim123" })).StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<List<JsonElement>>("/api/customers"))!);
    }

    [Fact]
    public async Task Expired_trial_can_still_handle_accounts_and_leave()
    {
        var c = await RegisterAsync(factory, "vertrek@example.com");
        var colleague = await CreateUserAsync(c, "vertrek-collega@example.com");
        await ExpireTrialAsync(factory, await WorkspaceIdAsync(c));

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/workspace/onboarded", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/users/{colleague}", new { name = "Oud", email = "vertrek-collega@example.com", role = "medewerker", active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/users/{colleague}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync("/api/auth/password", new { current = "geheim123", @new = "nieuwgeheim1" })).StatusCode);
        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "nieuwgeheim1" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(delete)).StatusCode);
    }
}
