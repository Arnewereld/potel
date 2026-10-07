using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
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

    // Productie zonder standaardaccount: de eigenaar meldt zich zelf aan. Een herstart geeft dan niemand platformrechten, ook niet
    // wie het adres uit PlatformAdmins als eerste aanmeldde: dat kan iedereen zijn. Alleen de opdracht op de server (platform-admin) geeft ze.
    [Fact]
    public Task A_restart_never_hands_platform_rights_to_a_self_registered_account() => WithSharedDb(async path =>
    {
        var settings = new[] { ("Admin:Email", ""), ("PlatformAdmins:0", "eigenaar@example.com") };
        using (var first = new SharedDbFactory(path, settings))
        {
            await RegisterAsync(first, "klant-van-het-platform@example.com");
            // Een vreemde is de eigenaar voor en meldt zich aan met diens adres.
            var attacker = await RegisterAsync(first, "eigenaar@example.com", "aanvaller1");
            Assert.Equal(HttpStatusCode.Forbidden, (await attacker.GetAsync("/api/platform/workspaces")).StatusCode);
        }

        using var second = new SharedDbFactory(path, settings);
        var again = await LoginAsync(second, "eigenaar@example.com", "aanvaller1");
        Assert.Equal(HttpStatusCode.Forbidden, (await again.GetAsync("/api/platform/workspaces")).StatusCode);
        Assert.False(second.WithDb(db => db.Users.IgnoreQueryFilters().Any(u => u.IsPlatformAdmin)));
    });

    [Fact]
    public Task The_server_command_makes_an_account_platform_admin() => WithSharedDb(async path =>
    {
        var settings = new[] { ("Admin:Email", ""), ("PlatformAdmins:0", "eigenaar@example.com") };
        using var f = new SharedDbFactory(path, settings);
        var owner = await RegisterAsync(f, "eigenaar@example.com");
        var other = await RegisterAsync(f, "tweede@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/platform/workspaces")).StatusCode);

        // Wat "dotnet Potel.Api.dll platform-admin eigenaar@example.com" op de server doet.
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        Assert.False(f.WithDb(db => Seed.MakePlatformAdmin(db, "bestaat-niet@example.com", logger)));
        Assert.True(f.WithDb(db => Seed.MakePlatformAdmin(db, " Eigenaar@Example.com ", logger)));

        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/platform/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync("/api/platform/workspaces")).StatusCode);
    });
}

// Een platformbeheerder uit een andere werkruimte: de beheerder van die werkruimte mag dat account niet overnemen.
public class AppointedPlatformAdminTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    static object Helper(string email = "hulp-platform@example.com", string role = "medewerker", bool active = true, string? password = null, string name = "Hulp") =>
        new { name, email, role, active, password };

    static HttpRequestMessage DeleteWorkspace(string password) =>
        new(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password }) };

    [Fact]
    public async Task A_workspace_admin_cannot_take_over_or_remove_an_appointed_platform_admin()
    {
        var boss = await RegisterAsync(factory, "baas-andere-zaak@example.com");
        var helperId = await CreateUserAsync(boss, "hulp-platform@example.com");
        var owner = await factory.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync("/api/platform/admins", new { email = "hulp-platform@example.com" })).StatusCode);

        // Nieuw wachtwoord, ander adres, uitschakelen of een andere rol: allemaal geweigerd.
        foreach (var change in new[] { Helper(password: "overgenomen1"), Helper(email: "baas-tweede@example.com"), Helper(active: false), Helper(role: "beheerder") })
        {
            var res = await boss.PutAsJsonAsync($"/api/users/{helperId}", change);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            Assert.Contains("platform", await res.ErrorAsync());
        }
        var (_, status) = await TryLoginAsync(factory, "hulp-platform@example.com", "overgenomen1");
        Assert.Equal(HttpStatusCode.Unauthorized, status);

        // Alleen de naam aanpassen mag gewoon.
        Assert.Equal(HttpStatusCode.OK, (await boss.PutAsJsonAsync($"/api/users/{helperId}", Helper(name: "Hulp van de baas"))).StatusCode);

        // Verwijderen, los of met de hele werkruimte, ook niet.
        Assert.Equal(HttpStatusCode.Forbidden, (await boss.DeleteAsync($"/api/users/{helperId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await boss.SendAsync(DeleteWorkspace("geheim123"))).StatusCode);

        // Haalt een platformbeheerder de rechten weg, dan is het weer een gewone gebruiker van die werkruimte.
        Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync($"/api/platform/admins/{helperId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await boss.PutAsJsonAsync($"/api/users/{helperId}", Helper(password: "nieuwgeheim1"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await boss.DeleteAsync($"/api/users/{helperId}")).StatusCode);
    }

    // Is de beheerder van de werkruimte zelf ook platformbeheerder, dan mag hij het account wel aanpassen.
    [Fact]
    public async Task A_workspace_admin_who_is_platform_admin_too_may_change_the_account()
    {
        var boss = await RegisterAsync(factory, "baas-derde-zaak@example.com");
        var helperId = await CreateUserAsync(boss, "hulp-derde@example.com");
        var owner = await factory.LoginAsync();
        (await owner.PostAsJsonAsync("/api/platform/admins", new { email = "hulp-derde@example.com" })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync("/api/platform/admins", new { email = "baas-derde-zaak@example.com" })).EnsureSuccessStatusCode();

        var res = await boss.PutAsJsonAsync($"/api/users/{helperId}", Helper(email: "hulp-derde@example.com", password: "nieuwgeheim1"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await LoginAsync(factory, "hulp-derde@example.com", "nieuwgeheim1");
    }

    // De laatste platformbeheerder kan zijn werkruimte niet weggooien: dan kan niemand het platform nog beheren.
    [Fact]
    public async Task The_last_platform_admin_cannot_delete_their_own_workspace()
    {
        using var own = new PortalFactory();
        var owner = await own.LoginAsync();
        var refused = await owner.SendAsync(DeleteWorkspace("welkom123"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("laatste platformbeheerder", await refused.ErrorAsync());

        // Met een platformbeheerder in een andere werkruimte erbij mag het wel.
        await RegisterAsync(own, "opvolger@example.com");
        (await owner.PostAsJsonAsync("/api/platform/admins", new { email = "opvolger@example.com" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(DeleteWorkspace("welkom123"))).StatusCode);
    }
}
