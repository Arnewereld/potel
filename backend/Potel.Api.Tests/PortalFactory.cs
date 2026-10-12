using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;

namespace Potel.Api.Tests;

// Testhosts houden appsettings.json niet in de gaten. xUnit start vrijwel alle testklassen tegelijk, dus tientallen hosts
// (bij een gewone volledige run tot 77), en elke bestandswachter kost een inotify-instantie. Linux geeft er standaard 128 per
// gebruiker; onder belasting of met een tweede proces erbij raakten ze op en startte een willekeurige host niet meer.
static class TestHostSetup
{
#pragma warning disable CA2255 // Hoort zo: dit moet gebeuren voordat de eerste host start.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void WithoutConfigWatchers() => Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
}

// Start de echte API met een eigen, tijdelijke database per testklasse.
public class PortalFactory : WebApplicationFactory<Program>
{
    readonly string dbPath = Path.Combine(Path.GetTempPath(), $"potel-test-{Guid.NewGuid():N}.db");

    public string DatabasePath => dbPath;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("DatabasePath", dbPath);
        builder.UseSetting("Workflows:Scheduler", "false");
        builder.UseSetting("Smtp:Host", "");
        builder.UseSetting("RateLimit:AuthPerMinute", "10000");
        builder.UseSetting("RateLimit:SignupsPerHour", "10000");
        builder.UseSetting("RateLimit:SignupsPerHourTotal", "10000");
    }

    public async Task<HttpClient> LoginAsync(string email = "admin@potel.nl", string password = "welkom123")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var res = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        res.EnsureSuccessStatusCode();
        return client;
    }

    // Een databasecontext binnen de eerste werkruimte, die van admin@potel.nl.
    public IServiceScope DefaultWorkspaceScope()
    {
        var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        db.Tenant.WorkspaceId = db.Workspaces.OrderBy(w => w.Id).First().Id;
        return scope;
    }

    public T WithDb<T>(Func<AppDb, T> action)
    {
        using var scope = DefaultWorkspaceScope();
        return action(scope.ServiceProvider.GetRequiredService<AppDb>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }
}
