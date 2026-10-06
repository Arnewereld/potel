using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Potel.Api.Data;

public static class Database
{
    // Brengt de database op het nieuwste schema. Een database uit de eerste versie (zonder migraties)
    // wordt bewaard als .bak-bestand en opnieuw aangemaakt. Zet daarna de schrijfmodus op WAL.
    public static void Prepare(AppDb db, ILogger logger)
    {
        var legacy = !db.Database.GetAppliedMigrations().Any()
                     && db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = 'Customers'").AsEnumerable().First() > 0;
        if (legacy)
        {
            var path = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
            var backup = $"{path}.{DateTime.Now:yyyyMMddHHmmss}.bak";
            db.Database.CloseConnection();
            SqliteConnection.ClearAllPools();
            File.Move(path, backup);
            logger.LogWarning("Oude database zonder migraties gevonden; bewaard als {Backup} en opnieuw aangemaakt", backup);
        }
        ReleaseStaleMigrationLock(db, logger);
        db.Database.Migrate();
        // WAL: lezen en schrijven houden elkaar niet op. Nodig nu de werkstroomplanner werkruimtes naast elkaar afwerkt;
        // zonder WAL kan een schrijfactie mislukken met "database is locked" terwijl een ander verzoek leest.
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    // Sinds EF Core 9 zet Migrate() bij elke start een slot in __EFMigrationsLock en wacht het eindeloos zolang dat slot er staat.
    // Wordt de app gestopt terwijl hij het slot heeft (crash, kill van de container), dan blijft het staan en start Potel nooit meer op.
    // Een migratie duurt hooguit seconden, dus een slot van meer dan tien minuten oud is achtergebleven en mag weg.
    public static void ReleaseStaleMigrationLock(AppDb db, ILogger logger)
    {
        var hasLock = db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsLock'").AsEnumerable().First() > 0;
        if (!hasLock) return;
        var stamp = db.Database.SqlQueryRaw<string>("SELECT Timestamp AS Value FROM __EFMigrationsLock").AsEnumerable().FirstOrDefault();
        if (stamp is null) return;
        if (DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            && (DateTimeOffset.UtcNow - at).Duration() < TimeSpan.FromMinutes(10)) return;
        db.Database.ExecuteSqlRaw("DELETE FROM __EFMigrationsLock;");
        logger.LogWarning("Achtergebleven migratieslot van {Stamp} opgeruimd", stamp);
    }
}
