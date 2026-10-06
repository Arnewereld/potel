using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Potel.Api.Data;

namespace Potel.Api.Tests;

// Sinds EF Core 9 neemt Migrate() een slot in de database. Een app die stopt terwijl hij dat slot heeft,
// mag de volgende start niet voor altijd laten wachten.
public class MigrationLockTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-slot-{Guid.NewGuid():N}.db");

    AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path}").Options, new Tenant());

    // Neemt het slot zoals Migrate() dat doet en laat het staan, alsof de app op dat moment werd gestopt.
    void LeaveLockBehind(TimeSpan? age = null)
    {
        using var db = Db();
        db.Database.Migrate();
        db.GetService<IHistoryRepository>().AcquireDatabaseLock();
        // Microsoft.Data.Sqlite schrijft een DateTimeOffset in dezelfde vorm als EF Core in het slot.
        if (age is { } a) db.Database.ExecuteSql($"UPDATE __EFMigrationsLock SET Timestamp = {DateTimeOffset.UtcNow - a}");
    }

    int Locks()
    {
        using var db = Db();
        return db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM __EFMigrationsLock").AsEnumerable().First();
    }

    [Fact]
    public async Task Startup_clears_a_lock_left_behind_by_a_crash()
    {
        LeaveLockBehind(TimeSpan.FromHours(1));
        Assert.Equal(1, Locks());

        // Zonder opruimen blijft Migrate() hier eindeloos wachten.
        await Task.Run(() => { using var db = Db(); Database.Prepare(db, NullLogger.Instance); }).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, Locks());
    }

    [Fact]
    public void A_lock_that_was_just_taken_is_left_alone()
    {
        // Een andere start die nu migreert, houdt zijn slot (met het tijdstip zoals EF Core het zelf schrijft).
        LeaveLockBehind();
        using (var db = Db()) Database.ReleaseStaleMigrationLock(db, NullLogger.Instance);
        Assert.Equal(1, Locks());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }
}
