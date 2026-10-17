using System.Net;
using System.Net.Http.Json;

namespace Potel.Api.Tests;

public class AuthTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task Api_requires_login()
    {
        var res = await factory.CreateClient().GetAsync("/api/customers");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var res = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "admin@potel.nl", password = "fout" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Admin_can_log_in_and_use_the_api()
    {
        var client = await factory.LoginAsync();
        var me = await client.GetFromJsonAsync<Dictionary<string, object>>("/api/auth/me");
        Assert.Equal("beheerder", me!["role"].ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/customers")).StatusCode);
    }

    [Fact]
    public async Task Employee_cannot_manage_users_and_disabled_user_loses_access()
    {
        var admin = await factory.LoginAsync();
        var created = await admin.PostAsJsonAsync("/api/users", new { name = "Piet", email = "piet@potel.nl", role = "medewerker", active = true, password = "geheim123" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var user = await created.Content.ReadFromJsonAsync<Dictionary<string, object>>();

        var piet = await factory.LoginAsync("piet@potel.nl", "geheim123");
        Assert.Equal(HttpStatusCode.Forbidden, (await piet.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await piet.GetAsync("/api/leads")).StatusCode);

        await admin.PutAsJsonAsync($"/api/users/{user!["id"]}", new { name = "Piet", email = "piet@potel.nl", role = "medewerker", active = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await piet.GetAsync("/api/leads")).StatusCode);
    }

    [Fact]
    public async Task Last_admin_cannot_be_demoted()
    {
        var admin = await factory.LoginAsync();
        var users = await admin.GetFromJsonAsync<List<Dictionary<string, object>>>("/api/users");
        var me = users!.First(u => u["email"].ToString() == "admin@potel.nl");
        var res = await admin.PutAsJsonAsync($"/api/users/{me["id"]}", new { name = "Beheerder", email = "admin@potel.nl", role = "medewerker", active = true });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
