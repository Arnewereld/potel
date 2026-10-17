using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Potel.Api.Data;

// Instellingen voor de back-ups, onder "Backup" in appsettings.json.
public class BackupOptions
{
    // Leeg: een map "backups" naast de database.
    public string? Path { get; set; }
    // Elke dag een back-up op de achtergrond, en bij het starten als de nieuwste ouder is dan 24 uur.
    public bool Daily { get; set; } = true;
    // Van zoveel dagen blijft de nieuwste back-up staan, en daarnaast de nieuwste van elke maand, zoveel maanden terug.
    public int Keep { get; set; } = 14;
    public int KeepMonthly { get; set; } = 12;
    // Zoveel back-ups van voor een update blijven staan.
    public int KeepBeforeUpdate { get; set; } = 10;
    // Zoveel MB moet er na een back-up nog vrij zijn op de schijf; anders slaan we hem over.
    public int MinFreeMegabytes { get; set; } = 500;
    // Hoe lang de achtergrondtaak na het starten wacht, zodat het opstarten niet op een back-up wacht.
    public int StartupDelaySeconds { get; set; } = 30;
    // Alleen als je weet wat je doet: de database ook bijwerken als de back-up voor de update mislukt.
    public bool AllowMigrationWithoutBackup { get; set; }
}

// Een back-up in de map: wanneer hij gemaakt is (Nederlandse tijd, uit de bestandsnaam) en of het er een van voor een update is.
public record BackupFile(string Path, string Name, DateTime Taken, bool BeforeUpdate, long Size);

public enum BackupOutcome { Created, Skipped, Failed, Busy }

public record BackupResult(BackupOutcome Outcome, BackupFile? File = null, string? Error = null)
{
    public bool Ok => Outcome == BackupOutcome.Created;
}

public record BackupProblem(DateTime At, string Message);

// Kopieën van de database met de backup-API van SQLite. Die maakt een kopie van één moment, ook terwijl de app schrijft (WAL),
// zonder de app stil te zetten. Een kopie wordt eerst als .part geschreven, gecontroleerd en pas dan hernoemd; mislukt er iets,
// dan ruimen we het halve bestand op. Is de schijf bijna vol, dan slaan we de back-up over in plaats van de schijf vol te schrijven.
// Back-ups downloaden kan niet via de website: de database bevat alle werkruimtes. Terugzetten doe je met de hand (zie de README).
public partial class DatabaseBackups
{
    public const string Prefix = "potel-";
    public const string BeforeUpdateMarker = "voor-update-";
    const string StampFormat = "yyyy-MM-dd-HHmmss";
    const string PartExtension = ".part";

    // potel-2026-10-14-031500.db of potel-voor-update-2026-10-14-031500.db, met eventueel -2 erachter als de naam al bestond.
    [GeneratedRegex(@"^potel-(voor-update-)?(\d{4}-\d{2}-\d{2}-\d{6})(-\d{1,3})?\.db$")]
    private static partial Regex NamePattern();

    readonly ILogger logger;
    readonly BusinessClock clock;
    readonly SemaphoreSlim gate = new(1, 1);

    public DatabaseBackups(string databasePath, BackupOptions options, ILogger logger, BusinessClock? clock = null)
    {
        DatabasePath = System.IO.Path.GetFullPath(databasePath);
        Options = options;
        Folder = System.IO.Path.GetFullPath(string.IsNullOrWhiteSpace(options.Path)
            ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(DatabasePath) ?? ".", "backups")
            : options.Path);
        this.logger = logger;
        this.clock = clock ?? new BusinessClock();
    }

    public string DatabasePath { get; }
    public string Folder { get; }
    public BackupOptions Options { get; }
    public bool Running => gate.CurrentCount == 0;
    // De laatste fout of overgeslagen back-up sinds het starten; weg na een gelukte back-up.
    public BackupProblem? LastProblem { get; private set; }

    // Alle back-ups in de map, nieuwste eerst. Andere bestanden in de map laten we met rust.
    public List<BackupFile> List()
    {
        if (!Directory.Exists(Folder)) return [];
        var files = new List<BackupFile>();
        foreach (var info in new DirectoryInfo(Folder).EnumerateFiles("potel-*.db"))
        {
            var m = NamePattern().Match(info.Name);
            if (!m.Success || !DateTime.TryParseExact(m.Groups[2].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var taken)) continue;
            files.Add(new BackupFile(info.FullName, info.Name, taken, m.Groups[1].Success, info.Length));
        }
        return files.OrderByDescending(f => f.Taken).ThenByDescending(f => f.Name, StringComparer.Ordinal).ToList();
    }

    // Moet er een nieuwe dagelijkse back-up komen? Ja als er geen is of de nieuwste (van welke soort ook) 24 uur of ouder is.
    // Een naam met een tijd ver in de toekomst (een verzet klokje, een hernoemd bestand) houdt de dagelijkse back-ups niet tegen.
    public DateTime NextDue()
    {
        var now = clock.Now;
        return List().FirstOrDefault(f => f.Taken <= now.AddHours(2)) is { } newest ? newest.Taken.AddHours(24) : now;
    }

    // Maakt een back-up en ruimt daarna oude op. Hooguit één tegelijk; een tweede krijgt Busy.
    public BackupResult Create(bool beforeUpdate = false)
    {
        if (!gate.Wait(0)) return new BackupResult(BackupOutcome.Busy, Error: "Er loopt al een back-up.");
        try
        {
            var result = CreateLocked(beforeUpdate);
            LastProblem = result.Ok ? null : new BackupProblem(DateTime.UtcNow, result.Error ?? "Onbekende fout");
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    BackupResult CreateLocked(bool beforeUpdate)
    {
        string? part = null;
        try
        {
            if (!File.Exists(DatabasePath)) return Fail($"De database {DatabasePath} bestaat niet.", null);
            CreateFolder();
            RemoveStaleParts();

            // Nooit de schijf vol schrijven: na de back-up moet er nog MinFreeMegabytes vrij zijn.
            var expected = SizeOf(DatabasePath) + SizeOf(DatabasePath + "-wal");
            var free = FreeBytes(Folder);
            var reserve = Math.Max(0L, Options.MinFreeMegabytes) * 1024 * 1024;
            if (free - expected < reserve)
            {
                var message = $"Te weinig vrije schijfruimte voor een back-up: {Megabytes(free)} vrij, nodig is {Megabytes(expected)} plus {Options.MinFreeMegabytes} MB reserve (Backup:MinFreeMegabytes).";
                logger.LogWarning("Back-up overgeslagen. {Message} Map: {Folder}", message, Folder);
                return new BackupResult(BackupOutcome.Skipped, Error: message);
            }

            var final = FreeName(clock.Now, beforeUpdate);
            part = final + PartExtension;
            Copy(part);
            // Een back-up bevat alle werkruimtes: alleen leesbaar voor de gebruiker waaronder Potel draait.
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(part, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(part, final);
            part = null;

            var file = List().First(f => f.Path == final);
            logger.LogInformation("Back-up gemaakt: {Path} ({Size})", final, Megabytes(file.Size));
            Prune();
            return new BackupResult(BackupOutcome.Created, file);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, ex, part);
        }
    }

    BackupResult Fail(string message, Exception? ex, string? part = null)
    {
        if (part is not null) RemovePart(part);
        logger.LogError(ex, "Back-up van de database mislukt: {Message} Map: {Folder}", message, Folder);
        return new BackupResult(BackupOutcome.Failed, Error: message);
    }

    // De kopie zelf: één stap met de backup-API (een leesslot op één moment), daarna zonder WAL-bestand ernaast en gecontroleerd.
    // Zonder pool, zodat er geen verbinding met het halve bestand blijft hangen en de verbindingen van de app ongemoeid blijven.
    void Copy(string target)
    {
        var source = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        var destination = new SqliteConnectionStringBuilder { DataSource = target, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var from = new SqliteConnection(source);
        using var to = new SqliteConnection(destination);
        from.Open();
        // Wacht even als er net een checkpoint loopt, in plaats van meteen op te geven.
        Execute(from, "PRAGMA busy_timeout = 30000;");
        to.Open();
        from.BackupDatabase(to);
        Execute(to, "PRAGMA journal_mode = DELETE;");
        var check = Execute(to, "PRAGMA quick_check;");
        if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"De kopie is niet in orde: {check}");
    }

    static string? Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }

    // Een nieuwe map is alleen voor de gebruiker waaronder Potel draait. Een bestaande map laten we zoals hij is.
    void CreateFolder()
    {
        if (Directory.Exists(Folder)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Folder);
        else Directory.CreateDirectory(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    string FreeName(DateTime now, bool beforeUpdate)
    {
        var stem = Prefix + (beforeUpdate ? BeforeUpdateMarker : "") + now.ToString(StampFormat, CultureInfo.InvariantCulture);
        var path = System.IO.Path.Combine(Folder, stem + ".db");
        for (var i = 2; File.Exists(path) || File.Exists(path + PartExtension); i++)
            path = System.IO.Path.Combine(Folder, $"{stem}-{i}.db");
        return path;
    }

    // Halve bestanden van een eerdere poging die halverwege stopte (stroom eraf, app gestopt). Alleen oude, voor het geval
    // er op hetzelfde moment een ander proces (zoals de opdracht platform-admin) met dezelfde map bezig is.
    void RemoveStaleParts()
    {
        foreach (var info in new DirectoryInfo(Folder).EnumerateFiles(Prefix + "*" + PartExtension + "*"))
            if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromHours(1)) RemovePart(info.FullName);
    }

    void RemovePart(string part)
    {
        foreach (var f in new[] { part, part + "-journal", part + "-wal", part + "-shm" })
        {
            try { if (File.Exists(f)) File.Delete(f); }
            catch (Exception ex) { logger.LogWarning(ex, "Half back-upbestand {Path} kon niet worden opgeruimd", f); }
        }
    }

    // Ruimt back-ups op die volgens de bewaarregels weg mogen.
    public void Prune()
    {
        foreach (var f in ToDelete(List(), clock.Now, Options))
        {
            try
            {
                File.Delete(f.Path);
                logger.LogInformation("Oude back-up opgeruimd: {Name}", f.Name);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Oude back-up {Name} kon niet worden opgeruimd", f.Name);
            }
        }
    }

    // De bewaarregels. Van de gewone back-ups blijven staan: alles van het laatste etmaal (ook een paar keer op de knop gedrukt),
    // de nieuwste van elk van de laatste Keep dagen met een back-up, en de nieuwste van elke maand, KeepMonthly maanden terug.
    // Van de back-ups van voor een update blijven de nieuwste KeepBeforeUpdate staan. De allernieuwste gaat nooit weg.
    public static List<BackupFile> ToDelete(IEnumerable<BackupFile> files, DateTime now, BackupOptions options)
    {
        var all = files.OrderByDescending(f => f.Taken).ThenByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        var regular = all.Where(f => !f.BeforeUpdate).ToList();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (all.FirstOrDefault() is { } newest) keep.Add(newest.Path);
        foreach (var f in regular.Where(f => now - f.Taken < TimeSpan.FromHours(24))) keep.Add(f.Path);
        foreach (var day in regular.GroupBy(f => f.Taken.Date).Take(Math.Max(1, options.Keep))) keep.Add(day.First().Path);
        var firstMonth = new DateTime(now.Year, now.Month, 1).AddMonths(-(Math.Max(0, options.KeepMonthly) - 1));
        if (options.KeepMonthly > 0)
            foreach (var month in regular.Where(f => f.Taken >= firstMonth).GroupBy(f => (f.Taken.Year, f.Taken.Month))) keep.Add(month.First().Path);
        foreach (var f in all.Where(f => f.BeforeUpdate).Take(Math.Max(1, options.KeepBeforeUpdate))) keep.Add(f.Path);
        return all.Where(f => !keep.Contains(f.Path)).ToList();
    }

    // De tijd uit de bestandsnaam (Nederlandse tijd) als UTC, voor de pagina Platform.
    public static DateTime TakenUtc(BackupFile f)
    {
        try { return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(f.Taken, DateTimeKind.Unspecified), BusinessClock.Zone); }
        catch (ArgumentException) { return File.GetLastWriteTimeUtc(f.Path); }
    }

    static long SizeOf(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    // Op Linux en macOS werkt elk pad; Windows wil de schijf zelf.
    static long FreeBytes(string folder) =>
        new DriveInfo(OperatingSystem.IsWindows() ? System.IO.Path.GetPathRoot(folder)! : folder).AvailableFreeSpace;

    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    public static string Megabytes(long bytes) => string.Format(Dutch, "{0:0.0} MB", bytes / 1024d / 1024d);
}

// Maakt elke dag een back-up, en meteen na het starten als de nieuwste ouder is dan 24 uur. Kijkt elk uur opnieuw,
// zodat het ook klopt na een nacht waarin je laptop sliep. Na een fout of een volle schijf probeert hij het een uur later opnieuw.
public class BackupScheduler(DatabaseBackups backups, BusinessClock clock, IServiceScopeFactory scopes, ILogger<BackupScheduler> logger) : BackgroundService
{
    static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);
    static readonly TimeSpan MinWait = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, backups.Options.StartupDelaySeconds)), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = MaxWait;
                try
                {
                    var due = backups.NextDue();
                    if (due <= clock.Now)
                    {
                        var result = await Task.Run(() => backups.Create(), stoppingToken);
                        if (result.Ok) wait = TimeSpan.FromHours(24);
                        else if (result.Outcome is BackupOutcome.Failed or BackupOutcome.Skipped) await NotifyAsync(result);
                    }
                    else wait = due - clock.Now;
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Dagelijkse back-up mislukt");
                }
                await Task.Delay(wait < MinWait ? MinWait : wait > MaxWait ? MaxWait : wait, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // Een melding voor de platformbeheerders; de sleutel per dag houdt het bij één melding, ook als het elk uur opnieuw misgaat.
    async Task NotifyAsync(BackupResult result)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Notifications.Notifier>().BackupFailedAsync(result);
    }
}
