using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// PlatformAdmins noemt adressen die nog geen account hebben, zoals vlak na een nieuwe installatie.
public class OpenPlatformSlotFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("PlatformAdmins:1", "jij@jouwdomein.nl");
        builder.UseSetting("PlatformAdmins:2", "tweede@jouwdomein.nl");
        builder.UseSetting("PlatformAdmins:3", "derde@jouwdomein.nl");
    }
}

// Platformrechten horen bij een gebruiker in de database, niet bij een e-mailadres dat iedereen kan aanmaken.
public class PlatformAdminTests(OpenPlatformSlotFactory factory) : IClassFixture<OpenPlatformSlotFactory>
{
    static async Task<bool> HasPlatformAccessAsync(HttpClient c) =>
        (await c.GetAsync("/api/platform/workspaces")).StatusCode == HttpStatusCode.OK
        || (await c.GetFromJsonAsync<JsonElement>("/api/workspace")).GetProperty("platformAdmin").GetBoolean();

    [Fact]
    public async Task Registering_a_listed_platform_address_gives_no_platform_access()
    {
        var victim = await RegisterAsync(factory, "slachtoffer-p1@example.com");
        var victimWs = await WorkspaceIdAsync(victim);
        var attacker = await RegisterAsync(factory, "jij@jouwdomein.nl");
        var attackerWs = await WorkspaceIdAsync(attacker);

        Assert.Equal(HttpStatusCode.Forbidden, (await attacker.GetAsync("/api/platform/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await attacker.PutAsJsonAsync($"/api/platform/workspaces/{attackerWs}/plan", new { plan = "team" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await attacker.PutAsJsonAsync($"/api/platform/workspaces/{victimWs}/plan", new { plan = "proef", trialEndsAt = "2020-01-01" })).StatusCode);
        Assert.False((await attacker.GetFromJsonAsync<JsonElement>("/api/workspace")).GetProperty("platformAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.Created, (await victim.PostAsJsonAsync("/api/customers", new { name = "Klant" })).StatusCode);
    }

    [Fact]
    public async Task Renaming_yourself_to_a_listed_platform_address_gives_no_platform_access()
    {
        var mallory = await RegisterAsync(factory, "mallory-p2@example.com");
        var put = await mallory.PutAsJsonAsync($"/api/users/{await MyIdAsync(mallory)}", new { name = "Mallory", email = "tweede@jouwdomein.nl", role = "beheerder", active = true });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.False(await HasPlatformAccessAsync(mallory));
        var relogged = await LoginAsync(factory, "tweede@jouwdomein.nl");
        Assert.False(await HasPlatformAccessAsync(relogged));
    }

    [Fact]
    public async Task Creating_a_user_with_a_listed_platform_address_gives_no_platform_access()
    {
        var mallory = await RegisterAsync(factory, "mallory-p3@example.com");
        await CreateUserAsync(mallory, "derde@jouwdomein.nl", role: "beheerder");
        var sock = await LoginAsync(factory, "derde@jouwdomein.nl");
        Assert.False(await HasPlatformAccessAsync(sock));
    }

    [Fact]
    public async Task Owner_keeps_platform_access_after_changing_email_and_the_freed_address_gives_nothing()
    {
        using var f = new PortalFactory();
        var owner = await f.LoginAsync();
        Assert.True(await HasPlatformAccessAsync(owner));
        var ownerId = await MyIdAsync(owner);
        (await owner.PutAsJsonAsync($"/api/users/{ownerId}", new { name = "Beheerder", email = "nieuw-adres@example.com", role = "beheerder", active = true })).EnsureSuccessStatusCode();

        // Dezelfde sessie blijft werken en houdt de rechten; ze hangen aan de gebruiker, niet aan het adres.
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/platform/workspaces")).StatusCode);
        var attacker = await RegisterAsync(f, "admin@potel.nl", "aanvaller1");
        Assert.False(await HasPlatformAccessAsync(attacker));
        Assert.True(await HasPlatformAccessAsync(await LoginAsync(f, "nieuw-adres@example.com", "welkom123")));
    }

    [Fact]
    public async Task Platform_admin_can_appoint_and_remove_others_but_nobody_else_can()
    {
        var helper = await RegisterAsync(factory, "collega-platform@example.com");
        var helperId = await MyIdAsync(helper);
        Assert.Equal(HttpStatusCode.Forbidden, (await helper.GetAsync("/api/platform/admins")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await helper.PostAsJsonAsync("/api/platform/admins", new { email = "collega-platform@example.com" })).StatusCode);

        var owner = await factory.LoginAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync("/api/platform/admins", new { email = "bestaat-niet@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/platform/admins", new { email = "collega-platform@example.com" })).StatusCode);
        Assert.True(await HasPlatformAccessAsync(helper));

        var ownerId = await MyIdAsync(owner);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.DeleteAsync($"/api/platform/admins/{ownerId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"/api/platform/admins/{helperId}")).StatusCode);
        Assert.False(await HasPlatformAccessAsync(helper));
    }
}

// Opnieuw opstarten op dezelfde database, zoals na een update of herstart van de container.
public class RestartTests
{
    sealed class SharedDbFactory(string dbPath, params (string Key, string Value)[] settings) : PortalFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("DatabasePath", dbPath);
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        }
    }

    static async Task WithSharedDb(Func<string, Task> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"potel-restart-{Guid.NewGuid():N}.db");
        try { await test(path); }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public Task A_restart_does_not_hand_platform_rights_to_whoever_registered_a_listed_address() => WithSharedDb(async path =>
    {
        (string, string) slot = ("PlatformAdmins:1", "jij@jouwdomein.nl");
        using (var first = new SharedDbFactory(path, slot))
            await RegisterAsync(first, "jij@jouwdomein.nl");

        // Er is al een platformbeheerder (admin@potel.nl), dus de instellingen wijzen niemand meer aan.
        using var second = new SharedDbFactory(path, slot);
        var attacker = await LoginAsync(second, "jij@jouwdomein.nl");
        Assert.Equal(HttpStatusCode.Forbidden, (await attacker.GetAsync("/api/platform/workspaces")).StatusCode);
    });

    [Fact]
    public Task Without_any_platform_admin_a_restart_appoints_only_the_first_listed_account() => WithSharedDb(async path =>
    {
        // Geen standaardaccount, zoals in productie: de eigenaar meldt zich zelf aan.
        var settings = new[] { ("Admin:Email", ""), ("PlatformAdmins:0", "eigenaar@example.com"), ("PlatformAdmins:1", "tweede@example.com") };
        using (var first = new SharedDbFactory(path, settings))
        {
            var owner = await RegisterAsync(first, "eigenaar@example.com");
            await RegisterAsync(first, "tweede@example.com");
            // Aanmelden alleen geeft nog geen rechten.
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/platform/workspaces")).StatusCode);
        }

        using var second = new SharedDbFactory(path, settings);
        Assert.Equal(HttpStatusCode.OK, (await (await LoginAsync(second, "eigenaar@example.com")).GetAsync("/api/platform/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await LoginAsync(second, "tweede@example.com")).GetAsync("/api/platform/workspaces")).StatusCode);
    });
}
