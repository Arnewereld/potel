using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Potel.Api.Data;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// Back-ups maken en opruimen, los van de app: een kopie van één moment, ook terwijl er geschreven wordt.
public class BackupTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), $"potel-backup-{Guid.NewGuid():N}");
    string DbPath => Path.Combine(dir, "potel.db");
    string Folder => Path.Combine(dir, "backups");

    public BackupTests() => Directory.CreateDirectory(dir);

    DatabaseBackups Backups(BackupOptions? options = null, BusinessClock? clock = null) =>
        new(DbPath, options ?? new BackupOptions(), NullLogger.Instance, clock);

    static SqliteConnection Open(string path, bool readOnly = false)
    {
        var con = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        con.Open();
        return con;
    }

    static object? Scalar(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    // Een database in WAL-modus, zoals die van de app, met wat gewicht zodat een kopie even duurt.
    void CreateDatabase()
    {
        using var con = Open(DbPath);
        Scalar(con, "PRAGMA journal_mode=WAL;");
        Scalar(con, "CREATE TABLE A (Id INTEGER PRIMARY KEY, V TEXT); CREATE TABLE B (Id INTEGER PRIMARY KEY, V TEXT); CREATE TABLE Ballast (Id INTEGER PRIMARY KEY, Data BLOB);");
        using var tx = con.BeginTransaction();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO Ballast (Data) VALUES (randomblob(4096))";
        for (var i = 0; i < 2000; i++) cmd.ExecuteNonQuery();
        tx.Commit();
    }

    [Fact]
    public async Task Backups_are_consistent_and_openable_while_writes_happen()
    {
        CreateDatabase();
        var backups = Backups();
        using var stop = new CancellationTokenSource();
        var written = 0;
        // Elke schrijfactie zet in één transactie een rij in A en een rij in B. In een kopie van één moment zijn ze dus altijd even lang.
        var writer = Task.Run(() =>
        {
            using var con = Open(DbPath);
            while (!stop.IsCancellationRequested)
            {
                using var tx = con.BeginTransaction();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "INSERT INTO A (V) VALUES ('a'); INSERT INTO B (V) VALUES ('b');";
                cmd.ExecuteNonQuery();
                tx.Commit();
                Interlocked.Increment(ref written);
            }
        });

        var results = new List<BackupResult>();
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(50);
            var before = Volatile.Read(ref written);
            results.Add(backups.Create());
            Assert.True(Volatile.Read(ref written) >= before);
        }
        await stop.CancelAsync();
        await writer;

        Assert.All(results, r => Assert.Equal(BackupOutcome.Created, r.Outcome));
        // Vijf keer in korte tijd: elke back-up krijgt toch een eigen bestand.
        Assert.Equal(5, results.Select(r => r.File!.Path).Distinct().Count());
        Assert.Empty(Directory.GetFiles(Folder, "*.part*"));

        var counts = new List<long>();
        foreach (var r in results)
        {
            // Los te openen, zonder -wal ernaast, heel, en van één moment. Alleen leesbaar voor de gebruiker van Potel.
            Assert.False(File.Exists(r.File!.Path + "-wal"));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(r.File.Path));
            using var con = Open(r.File.Path, readOnly: true);
            Assert.Equal("ok", Scalar(con, "PRAGMA integrity_check;"));
            Assert.Equal("delete", Scalar(con, "PRAGMA journal_mode;"));
            var a = (long)Scalar(con, "SELECT COUNT(*) FROM A")!;
            Assert.Equal(a, (long)Scalar(con, "SELECT COUNT(*) FROM B")!);
            Assert.Equal(2000L, (long)Scalar(con, "SELECT COUNT(*) FROM Ballast")!);
            counts.Add(a);
        }
        // Er werd tussendoor echt geschreven.
        Assert.True(counts.Last() > counts.First());
        Assert.True(written > counts.Last());
    }

    [Fact]
    public void Low_disk_space_skips_the_backup_and_writes_nothing()
    {
        CreateDatabase();
        var backups = Backups(new BackupOptions { MinFreeMegabytes = int.MaxValue });
        var result = backups.Create();
        Assert.Equal(BackupOutcome.Skipped, result.Outcome);
        Assert.Contains("schijfruimte", result.Error);
        Assert.Empty(Directory.GetFiles(Folder));
        Assert.Contains("schijfruimte", backups.LastProblem!.Message);

        // Is er weer ruimte, dan lukt het en is de melding weg.
        var ok = Backups(new BackupOptions { MinFreeMegabytes = 0 });
        Assert.True(ok.Create().Ok);
        Assert.Null(ok.LastProblem);
    }

    [Fact]
    public void A_failed_backup_leaves_no_partial_file()
    {
        File.WriteAllText(DbPath, new string('x', 8192));
        var backups = Backups();
        var result = backups.Create();
        Assert.Equal(BackupOutcome.Failed, result.Outcome);
        Assert.NotNull(backups.LastProblem);
        Assert.Empty(Directory.GetFiles(Folder));
    }

    [Fact]
    public void A_missing_database_is_reported_instead_of_creating_an_empty_one()
    {
        var result = Backups().Create();
        Assert.Equal(BackupOutcome.Failed, result.Outcome);
        Assert.False(File.Exists(DbPath));
    }

    static BackupFile Fake(DateTime taken, bool beforeUpdate = false) =>
        new($"/b/{(beforeUpdate ? "potel-voor-update-" : "potel-")}{taken:yyyy-MM-dd-HHmmss}.db", "", taken, beforeUpdate, 1);

    [Fact]
    public void Retention_keeps_the_last_day_fourteen_days_twelve_months_and_ten_update_backups()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0);
        var files = new List<BackupFile>();
        // Elke nacht om drie uur, ruim een jaar lang, en vandaag nog twee keer op de knop gedrukt.
        for (var day = new DateTime(2025, 9, 10); day <= now.Date; day = day.AddDays(1)) files.Add(Fake(day.AddHours(3)));
        files.Add(Fake(now.Date.AddHours(9)));
        files.Add(Fake(now.Date.AddHours(11)));
        for (var i = 0; i < 15; i++) files.Add(Fake(now.AddDays(-i * 3).AddHours(-1), beforeUpdate: true));

        var deleted = DatabaseBackups.ToDelete(files, now, new BackupOptions()).Select(f => f.Path).ToHashSet();
        var kept = files.Where(f => !deleted.Contains(f.Path)).ToList();
        var regular = kept.Where(f => !f.BeforeUpdate).Select(f => f.Taken).ToList();

        // Alles van vandaag (binnen 24 uur): 03:00, 09:00 en 11:00.
        Assert.Contains(now.Date.AddHours(3), regular);
        Assert.Contains(now.Date.AddHours(9), regular);
        // De nieuwste van de 14 laatste dagen: vandaag en 1 tot en met 13 oktober.
        for (var d = 1; d <= 13; d++) Assert.Contains(new DateTime(2026, 10, d, 3, 0, 0), regular);
        Assert.DoesNotContain(new DateTime(2026, 9, 29, 3, 0, 0), regular);
        // De nieuwste van elke maand, twaalf maanden terug: 30 september, 31 augustus, ... tot en met 30 november 2025.
        Assert.Contains(new DateTime(2026, 9, 30, 3, 0, 0), regular);
        Assert.Contains(new DateTime(2026, 2, 28, 3, 0, 0), regular);
        Assert.Contains(new DateTime(2025, 11, 30, 3, 0, 0), regular);
        Assert.DoesNotContain(new DateTime(2025, 10, 31, 3, 0, 0), regular);
        Assert.DoesNotContain(new DateTime(2025, 9, 30, 3, 0, 0), regular);
        Assert.Equal(3 + 13 + 11, regular.Count);
        // Van voor een update de tien nieuwste.
        var updates = kept.Where(f => f.BeforeUpdate).ToList();
        Assert.Equal(10, updates.Count);
        Assert.Equal(files.Where(f => f.BeforeUpdate).OrderByDescending(f => f.Taken).Take(10).Select(f => f.Path), updates.OrderByDescending(f => f.Taken).Select(f => f.Path));
    }

    [Fact]
    public void Retention_never_removes_the_newest_backup()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0);
        var only = Fake(new DateTime(2020, 1, 1, 3, 0, 0));
        Assert.Empty(DatabaseBackups.ToDelete([only], now, new BackupOptions { Keep = 1, KeepMonthly = 0 }));
        var update = Fake(new DateTime(2020, 1, 1, 3, 0, 0), beforeUpdate: true);
        Assert.Empty(DatabaseBackups.ToDelete([update], now, new BackupOptions { KeepBeforeUpdate = 0 }));
    }

    [Fact]
    public void Cleaning_up_removes_old_backups_but_never_other_files()
    {
        CreateDatabase();
        Directory.CreateDirectory(Folder);
        var clock = new FixedClock(new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc));
        foreach (var name in new[] { "potel-2026-01-05-030000.db", "potel-2026-01-20-030000.db", "potel-2025-01-20-030000.db", "notities.txt", "potel.db", "potel-kopie.db" })
            File.WriteAllText(Path.Combine(Folder, name), "x");
        // Een half bestand van een poging die lang geleden halverwege stopte.
        var stale = Path.Combine(Folder, "potel-2026-10-13-030000.db.part");
        File.WriteAllText(stale, "x");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        var backups = Backups(new BackupOptions { Keep = 2, KeepMonthly = 0 }, clock);
        Assert.True(backups.Create().Ok);

        var names = Directory.GetFiles(Folder).Select(Path.GetFileName).ToHashSet();
        Assert.Contains("potel-2026-10-14-120000.db", names);
        Assert.Contains("potel-2026-01-20-030000.db", names);
        Assert.DoesNotContain("potel-2026-01-05-030000.db", names);
        Assert.DoesNotContain("potel-2025-01-20-030000.db", names);
        Assert.DoesNotContain("potel-2026-10-13-030000.db.part", names);
        Assert.Contains("notities.txt", names);
        Assert.Contains("potel.db", names);
        Assert.Contains("potel-kopie.db", names);
    }

    [Fact]
    public void A_new_backup_is_due_only_when_the_newest_is_a_day_old()
    {
        CreateDatabase();
        var clock = new FixedClock(new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc));
        var backups = Backups(clock: clock);
        Assert.True(backups.NextDue() <= clock.Now);
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, $"potel-voor-update-{clock.Now.AddHours(-1):yyyy-MM-dd-HHmmss}.db"), "x");
        Assert.Equal(clock.Now.AddHours(23), backups.NextDue());
        // Een bestand met een naam ver in de toekomst houdt de dagelijkse back-ups niet tegen.
        File.WriteAllText(Path.Combine(Folder, "potel-2099-01-01-030000.db"), "x");
        Assert.Equal(clock.Now.AddHours(23), backups.NextDue());
    }

    public void Dispose()
    {
        TestDatabase.Delete(DbPath);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

// Een update: de app start op een database met een ouder schema.
public class BackupBeforeUpdateTests : IDisposable
{
    const string Before = "20261014081958_TimerOpDeServer";
    readonly string dir = Path.Combine(Path.GetTempPath(), $"potel-update-{Guid.NewGuid():N}");
    string DbPath => Path.Combine(dir, "potel.db");
    string Folder => Path.Combine(dir, "backups");

    public BackupBeforeUpdateTests()
    {
        Directory.CreateDirectory(dir);
        using (var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={DbPath}").Options, new Tenant()))
            db.GetService<IMigrator>().Migrate(Before);
        using var con = new SqliteConnection($"Data Source={DbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 'Oud bedrijf', 'team');
            """;
        cmd.ExecuteNonQuery();
    }

    sealed class UpdateFactory(string dbPath, params (string Key, string Value)[] settings) : PortalFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("DatabasePath", dbPath);
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            TestDatabase.ClearPool(dbPath);
        }
    }

    static object? Scalar(string path, string sql)
    {
        using var con = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    static bool HasFocusColumn(string path) => (long)Scalar(path, "SELECT COUNT(*) FROM pragma_table_info('Users') WHERE name = 'FocusMinutes'")! == 1;
    static string LastMigration(string path) => (string)Scalar(path, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1")!;

    [Fact]
    public async Task Startup_makes_a_backup_with_the_old_schema_before_migrating()
    {
        using (var f = new UpdateFactory(DbPath, ("Backup:Path", Folder)))
        {
            await TestHostStart.OnOwnThread(f);
            var file = Assert.Single(Directory.GetFiles(Folder, "potel-voor-update-*.db"));
            Assert.Matches(@"potel-voor-update-\d{4}-\d{2}-\d{2}-\d{6}\.db$", file);

            // De back-up heeft het oude schema en de oude gegevens; de database zelf is bijgewerkt.
            Assert.Equal(Before, LastMigration(file));
            Assert.False(HasFocusColumn(file));
            Assert.Equal("Oud bedrijf", Scalar(file, "SELECT Name FROM Workspaces WHERE Id = 1"));
            Assert.Equal("ok", Scalar(file, "PRAGMA integrity_check;"));
            Assert.False(f.WithDb(db => db.Database.GetPendingMigrations().Any()));
        }
        Assert.True(HasFocusColumn(DbPath));

        // Een volgende start zonder nieuwe migraties maakt geen nieuwe back-up voor een update.
        using (var again = new UpdateFactory(DbPath, ("Backup:Path", Folder)))
            await TestHostStart.OnOwnThread(again);
        Assert.Single(Directory.GetFiles(Folder, "potel-voor-update-*.db"));
    }

    [Fact]
    public async Task A_failed_backup_before_an_update_stops_the_migration()
    {
        // Een bestand waar de map voor back-ups had moeten komen: de back-up kan niet.
        File.WriteAllText(Folder, "geen map");
        using (var f = new UpdateFactory(DbPath, ("Backup:Path", Folder)))
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => TestHostStart.OnOwnThread(f));
            Assert.Contains("Backup:AllowMigrationWithoutBackup", ex.ToString());
        }
        Assert.Equal(Before, LastMigration(DbPath));
        Assert.False(HasFocusColumn(DbPath));
    }

    [Fact]
    public async Task The_override_migrates_without_a_backup()
    {
        File.WriteAllText(Folder, "geen map");
        using (var f = new UpdateFactory(DbPath, ("Backup:Path", Folder), ("Backup:AllowMigrationWithoutBackup", "true")))
        {
            await TestHostStart.OnOwnThread(f);
            Assert.False(f.WithDb(db => db.Database.GetPendingMigrations().Any()));
        }
        Assert.True(HasFocusColumn(DbPath));
    }

    [Fact]
    public async Task The_daily_backup_runs_at_startup_when_the_newest_is_older_than_a_day()
    {
        // Eerst bijwerken (met de back-up voor de update), en die back-up oud maken.
        using (var f = new UpdateFactory(DbPath, ("Backup:Path", Folder)))
            await TestHostStart.OnOwnThread(f);
        var update = Assert.Single(Directory.GetFiles(Folder));
        File.Move(update, Path.Combine(Folder, "potel-voor-update-2020-01-01-030000.db"));

        using var daily = new UpdateFactory(DbPath, ("Backup:Path", Folder), ("Backup:Daily", "true"), ("Backup:StartupDelaySeconds", "0"));
        await TestHostStart.OnOwnThread(daily);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Directory.GetFiles(Folder, "potel-20*.db").Length == 0 && DateTime.UtcNow < deadline) await Task.Delay(100);
        var made = Assert.Single(Directory.GetFiles(Folder, "potel-20*.db"));
        Assert.Equal("ok", Scalar(made, "PRAGMA integrity_check;"));
        Assert.True(File.Exists(Path.Combine(Folder, "potel-voor-update-2020-01-01-030000.db")));
    }

    public void Dispose()
    {
        TestDatabase.Delete(DbPath);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

// Back-ups op de pagina Platform: alleen voor platformbeheerders, en geen download van de hele database.
public class PlatformBackupTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task Only_platform_admins_see_and_make_backups()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/platform/backups")).StatusCode);
        var stranger = await RegisterAsync(factory, "backup-vreemde@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync("/api/platform/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.PostAsync("/api/platform/backups", null)).StatusCode);
        Assert.False(Directory.Exists(factory.BackupPath) && Directory.GetFiles(factory.BackupPath).Length > 0);

        var owner = await factory.LoginAsync();
        var status = await owner.GetFromJsonAsync<JsonElement>("/api/platform/backups");
        Assert.Equal(factory.BackupPath, status.GetProperty("folder").GetString());
        Assert.Equal(0, status.GetProperty("count").GetInt32());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("last").ValueKind);

        var made = await owner.PostAsync("/api/platform/backups", null);
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var after = await made.JsonAsync();
        Assert.Equal(1, after.GetProperty("count").GetInt32());
        var name = after.GetProperty("last").GetProperty("name").GetString()!;
        Assert.StartsWith("potel-", name);
        Assert.True(after.GetProperty("last").GetProperty("size").GetInt64() > 0);
        Assert.True(File.Exists(Path.Combine(factory.BackupPath, name)));
        Assert.Equal(JsonValueKind.Null, after.GetProperty("problem").ValueKind);

        // De database zelf is nergens te downloaden, ook niet voor de platformbeheerder.
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/platform/backups/{name}")).StatusCode);
    }
}
