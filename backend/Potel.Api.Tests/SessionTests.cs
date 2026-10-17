using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// Rol, status en wachtwoord worden bij elk verzoek tegen de database gecontroleerd, niet alleen bij het inloggen.
public class SessionTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    static HttpRequestMessage DeleteWorkspace(string password) =>
        new(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password }) };

    [Fact]
    public async Task Demoted_admin_loses_admin_rights_immediately()
    {
        var a = await RegisterAsync(factory, "degradatie-a@example.com");
        var bId = await CreateUserAsync(a, "degradatie-b@example.com", role: "beheerder");
        var b = await LoginAsync(factory, "degradatie-b@example.com");
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/users")).StatusCode);

        (await a.PutAsJsonAsync($"/api/users/{bId}", new { name = "B", email = "degradatie-b@example.com", role = "medewerker", active = true })).EnsureSuccessStatusCode();

        // De oude cookie van B geeft geen beheerrechten meer.
        Assert.NotEqual(HttpStatusCode.OK, (await b.GetAsync("/api/users")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await b.GetAsync("/api/workspace/export")).StatusCode);
        Assert.False((await b.SendAsync(DeleteWorkspace("geheim123"))).IsSuccessStatusCode);
        Assert.False((await b.PutAsJsonAsync($"/api/users/{bId}", new { name = "B", email = "degradatie-b@example.com", role = "beheerder", active = true })).IsSuccessStatusCode);

        // A en de werkruimte zijn er nog, en B is echt medewerker gebleven.
        var users = await a.GetFromJsonAsync<List<JsonElement>>("/api/users");
        Assert.Equal("medewerker", users!.Single(u => u.GetProperty("id").GetInt32() == bId).GetProperty("role").GetString());

        // Opnieuw inloggen werkt, ook in dezelfde browser met de oude cookie, en dan met de nieuwe rol.
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsJsonAsync("/api/auth/login", new { email = "degradatie-b@example.com", password = "geheim123" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/leads")).StatusCode);
        var again = await LoginAsync(factory, "degradatie-b@example.com");
        Assert.Equal("medewerker", (await again.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await again.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/api/leads")).StatusCode);
    }

    [Fact]
    public async Task Password_change_ends_other_sessions_but_keeps_this_one()
    {
        var admin = await RegisterAsync(factory, "wachtwoord-a@example.com");
        await CreateUserAsync(admin, "sas@example.com");
        var stolen = await LoginAsync(factory, "sas@example.com");
        var mine = await LoginAsync(factory, "sas@example.com");

        Assert.Equal(HttpStatusCode.NoContent, (await mine.PutAsJsonAsync("/api/auth/password", new { current = "geheim123", @new = "nieuwgeheim456" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await stolen.GetAsync("/api/leads")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mine.GetAsync("/api/leads")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLoginAsync(factory, "sas@example.com")).Status);
        await LoginAsync(factory, "sas@example.com", "nieuwgeheim456");
    }

    [Fact]
    public async Task New_password_or_email_set_by_an_admin_ends_that_users_sessions()
    {
        var admin = await RegisterAsync(factory, "reset-a@example.com");
        var id = await CreateUserAsync(admin, "reset-b@example.com");
        var b = await LoginAsync(factory, "reset-b@example.com");
        (await admin.PutAsJsonAsync($"/api/users/{id}", new { name = "B", email = "reset-b@example.com", role = "medewerker", active = true, password = "andersgeheim1" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/api/leads")).StatusCode);

        var b2 = await LoginAsync(factory, "reset-b@example.com", "andersgeheim1");
        (await admin.PutAsJsonAsync($"/api/users/{id}", new { name = "B", email = "reset-b2@example.com", role = "medewerker", active = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await b2.GetAsync("/api/leads")).StatusCode);

        // Alleen de naam wijzigen laat de sessie met rust.
        var b3 = await LoginAsync(factory, "reset-b2@example.com", "andersgeheim1");
        (await admin.PutAsJsonAsync($"/api/users/{id}", new { name = "Bea", email = "reset-b2@example.com", role = "medewerker", active = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await b3.GetAsync("/api/leads")).StatusCode);
    }

    [Fact]
    public async Task Admin_who_changes_own_email_or_role_stays_logged_in()
    {
        var admin = await RegisterAsync(factory, "zelf-a@example.com");
        await CreateUserAsync(admin, "zelf-b@example.com", role: "beheerder");
        var me = await MyIdAsync(admin);
        (await admin.PutAsJsonAsync($"/api/users/{me}", new { name = "Ik", email = "zelf-nieuw@example.com", role = "beheerder", active = true })).EnsureSuccessStatusCode();
        Assert.Equal("zelf-nieuw@example.com", (await admin.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("email").GetString());

        (await admin.PutAsJsonAsync($"/api/users/{me}", new { name = "Ik", email = "zelf-nieuw@example.com", role = "medewerker", active = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/leads")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Cookie_with_an_outdated_role_is_refused_even_without_a_stamp_change()
    {
        // Een sessie van voor deze versie: lege stempel, en de rol is daarna buiten de API om gewijzigd.
        var admin = await RegisterAsync(factory, "oud-a@example.com");
        var id = await CreateUserAsync(admin, "oud-b@example.com", role: "beheerder");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            db.Tenant.WorkspaceId = await WorkspaceIdAsync(admin);
            db.Users.Find(id)!.SecurityStamp = "";
            await db.SaveChangesAsync();
        }
        var b = await LoginAsync(factory, "oud-b@example.com");
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/users")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            db.Tenant.WorkspaceId = await WorkspaceIdAsync(admin);
            db.Users.Find(id)!.Role = Roles.Employee;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/api/users")).StatusCode);
    }
}
