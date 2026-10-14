using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
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

// Ruimt een tijdelijke testdatabase op. Sluit alleen de verbindingen met die ene database, nooit met SqliteConnection.ClearAllPools():
// dat gooit ook de verbindingen weg van testklassen die op dat moment draaien. Microsoft.Data.Sqlite 10.0.12 ziet een verbinding die
// net geopend wordt dan soms voor een vergeten verbinding aan. Die wordt gesloten terwijl een verzoek hem gebruikt (ObjectDisposedException),
// of twee verzoeken krijgen dezelfde verbinding. Zo faalde af en toe een willekeurige test in een heel andere klasse.
static class TestDatabase
{
    public static void ClearPool(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        SqliteConnection.ClearPool(connection);
    }

    public static void Delete(string path)
    {
        ClearPool(path);
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }
}

// Start een testhost op een eigen thread. WebApplicationFactory start een host synchroon: wie hem als eerste gebruikt, wacht tot de migraties
// en de voorbeelddata klaar zijn, en dat duurt onder belasting lang. xUnit draait testcode op een paar eigen threads, één per processorkern,
// en de testclient van ASP.NET Core maakt elk verzoek op diezelfde threads af. Waren ze allemaal bezet met het starten van hosts, dan bleef
// een verzoek dat de server allang had beantwoord liggen tot de HttpClient na 100 seconden opgaf. Op een eigen thread blokkeert het starten
// geen thread van xUnit en ook geen thread van de threadpool.
static class TestHostStart
{
    public static Task OnOwnThread(WebApplicationFactory<Program> factory)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try
            {
                _ = factory.Services;
                started.SetResult();
            }
            catch (Exception e)
            {
                started.SetException(e);
            }
        }) { IsBackground = true, Name = "Testhost starten" }.Start();
        return started.Task;
    }
}

// Start de echte API met een eigen, tijdelijke database per testklasse.
public class PortalFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    readonly string dbPath = Path.Combine(Path.GetTempPath(), $"potel-test-{Guid.NewGuid():N}.db");

    public string DatabasePath => dbPath;

    // Als fixture van een testklasse start de host al voor de eerste test, op een eigen thread (zie TestHostStart).
    public Task InitializeAsync() => TestHostStart.OnOwnThread(this);

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

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
        TestDatabase.Delete(dbPath);
    }
}

// Bewaakt de regel bij TestDatabase: een nieuwe test die de oude opruimregel overneemt, laat andere tests weer af en toe falen.
public class TestDatabaseTests
{
    [Fact]
    public void No_test_closes_the_connections_of_other_test_classes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Potel.Api.Tests.csproj"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var sep = Path.DirectorySeparatorChar;
        var call = $"{nameof(SqliteConnection.ClearAllPools)}(";
        var found = dir.EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains($"{sep}obj{sep}") && !f.FullName.Contains($"{sep}bin{sep}"))
            .SelectMany(f => File.ReadLines(f.FullName).Select((line, i) => (Where: $"{f.Name}:{i + 1}", Text: line.Trim())))
            .Where(l => !l.Text.StartsWith("//") && l.Text.Contains(call))
            .Select(l => l.Where)
            .ToList();
        Assert.Empty(found);
    }
}
