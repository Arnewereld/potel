using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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

    // Voor een host die een test zelf maakt in plaats van als fixture: zo start hij net zo, voor het eerste verzoek.
    // Lukt het starten niet, dan ruimt dit de factory meteen op; de fout gaat gewoon door naar de test.
    public static async Task<T> Started<T>(T factory) where T : WebApplicationFactory<Program>
    {
        try
        {
            await OnOwnThread(factory);
        }
        catch
        {
            factory.Dispose();
            throw;
        }
        return factory;
    }
}

// De bronmap van dit testproject, vastgelegd bij het bouwen (zie Potel.Api.Tests.csproj). Omhoog zoeken vanaf de map met de
// testbestanden faalt zodra die ergens anders heen gekopieerd zijn; zo controleren deze tests altijd de echte bronbestanden.
static class SourceTree
{
    public static string TestProject { get; } = typeof(SourceTree).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "TestProjectDir").Value!;

    public static string RepoRoot => Path.GetFullPath(Path.Combine(TestProject, "..", ".."));

    // Een bestand in de repository; bestaat het niet, dan faalt de test in plaats van niets te controleren.
    public static string RepoFile(string name)
    {
        var path = Path.Combine(RepoRoot, name);
        if (!File.Exists(path)) throw new FileNotFoundException($"{name} niet gevonden in {RepoRoot}", path);
        return path;
    }

    // De regels uit de .cs-bestanden van dit testproject, zonder bin en obj en zonder commentaarregels.
    public static List<(string Where, string Text)> TestCode()
    {
        var dir = new DirectoryInfo(TestProject);
        if (!File.Exists(Path.Combine(dir.FullName, "Potel.Api.Tests.csproj"))) throw new DirectoryNotFoundException($"Geen testproject in {dir.FullName}");
        var sep = Path.DirectorySeparatorChar;
        return dir.EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains($"{sep}obj{sep}") && !f.FullName.Contains($"{sep}bin{sep}"))
            .SelectMany(f => File.ReadLines(f.FullName).Select((line, i) => (Where: $"{f.Name}:{i + 1}", Text: line.Trim())))
            .Where(l => !l.Text.StartsWith("//"))
            .ToList();
    }
}

// Start de echte API met een eigen, tijdelijke database per testklasse.
public class PortalFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    readonly string dbPath = Path.Combine(Path.GetTempPath(), $"potel-test-{Guid.NewGuid():N}.db");
    readonly string backupPath = Path.Combine(Path.GetTempPath(), $"potel-test-backups-{Guid.NewGuid():N}");

    public string DatabasePath => dbPath;
    public string BackupPath => backupPath;

    // Als fixture van een testklasse start de host al voor de eerste test, op een eigen thread (zie TestHostStart).
    public Task InitializeAsync() => TestHostStart.OnOwnThread(this);

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("DatabasePath", dbPath);
        // Elke testhost een eigen map voor back-ups, en geen dagelijkse back-ups op de achtergrond.
        builder.UseSetting("Backup:Path", backupPath);
        builder.UseSetting("Backup:Daily", "false");
        builder.UseSetting("Workflows:Scheduler", "false");
        // Een webhook naar een testserver in hetzelfde proces kan op een drukke machine langer dan de gewone 5 seconden duren.
        // Een run heeft daarnaast zijn eigen grens (MaxSecondsPerRun), dus trage webhooks stoppen nog steeds.
        builder.UseSetting("Workflows:WebhookTimeoutSeconds", "30");
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
        if (Directory.Exists(backupPath)) Directory.Delete(backupPath, true);
    }
}

// Bewaakt de regel bij TestDatabase: een nieuwe test die de oude opruimregel overneemt, laat andere tests weer af en toe falen.
public class TestDatabaseTests
{
    [Fact]
    public void No_test_closes_the_connections_of_other_test_classes()
    {
        var code = SourceTree.TestCode();
        Assert.Contains(code, l => l.Where.StartsWith("PortalFactory.cs:"));
        var call = $"{nameof(SqliteConnection.ClearAllPools)}(";
        Assert.Empty(code.Where(l => l.Text.Contains(call)).Select(l => l.Where));
    }

    // Een host die een test zelf maakt, start via TestHostStart.Started. Anders start hij synchroon op een thread van xUnit
    // bij het eerste verzoek, en onder belasting liepen verzoeken dan tegen de time-out van 100 seconden aan.
    [Fact]
    public void Every_host_made_inside_a_test_starts_on_its_own_thread()
    {
        var code = SourceTree.TestCode();
        var made = new Regex(@"\bnew\s+[A-Z]\w*Factory\d*\s*\(|\.WithWebHostBuilder\s*\(");
        Assert.Contains(code, l => made.IsMatch(l.Text));
        Assert.Empty(code.Where(l => made.IsMatch(l.Text) && !l.Text.Contains("TestHostStart.Started(")).Select(l => l.Where));
    }
}
