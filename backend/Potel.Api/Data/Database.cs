using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Potel.Api.Data;

public static class Database
{
    // Brengt de database op het nieuwste schema. Een database uit de eerste versie (zonder migraties)
    // wordt bewaard als .bak-bestand en opnieuw aangemaakt. Staan er updates klaar, dan eerst een back-up (zie BackupBeforeUpdate).
    // Zet daarna de schrijfmodus op WAL.
    public static void Prepare(AppDb db, ILogger logger, DatabaseBackups backups)
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
        BackupBeforeUpdate(db, logger, backups);
        ReleaseStaleMigrationLock(db, logger);
        db.Database.Migrate();
        // WAL: lezen en schrijven houden elkaar niet op. Nodig nu de werkstroomplanner werkruimtes naast elkaar afwerkt;
        // zonder WAL kan een schrijfactie mislukken met "database is locked" terwijl een ander verzoek leest.
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    public const string BackupFailedMessage =
        "De back-up voor de update is mislukt, dus de database is niet bijgewerkt en Potel start niet. Los het probleem uit het log op " +
        "(bijvoorbeeld vrije schijfruimte of de map bij Backup:Path) en start opnieuw. Wil je echt zonder back-up bijwerken, " +
        "zet dan Backup:AllowMigrationWithoutBackup op true.";

    // Voor een update met nieuwe migraties eerst een back-up "voor-update", zodat je de update kunt terugdraaien. Dit gebeurt
    // voordat er iets aan het schema verandert, ook voordat EF Core zijn eigen tabellen voor het slot aanmaakt. Mislukt de back-up,
    // dan stoppen we: liever geen update dan een update zonder vangnet, tenzij Backup:AllowMigrationWithoutBackup aan staat.
    // Een nieuwe, lege database heeft niets om te bewaren.
    static void BackupBeforeUpdate(AppDb db, ILogger logger, DatabaseBackups backups)
    {
        var applied = db.Database.GetAppliedMigrations().ToList();
        if (applied.Count == 0) return;
        var pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count == 0) return;
        logger.LogInformation("Er staan {Count} databaseupdates klaar ({Migrations}). Eerst een back-up in {Folder}.",
            pending.Count, string.Join(", ", pending), backups.Folder);
        db.Database.CloseConnection();
        var result = backups.Create(beforeUpdate: true);
        if (result.Ok)
        {
            logger.LogInformation("Back-up voor de update gemaakt: {Path}", result.File!.Path);
            return;
        }
        if (backups.Options.AllowMigrationWithoutBackup)
        {
            logger.LogError("De back-up voor de update is mislukt ({Error}). Backup:AllowMigrationWithoutBackup staat aan, dus de database wordt toch bijgewerkt.", result.Error);
            return;
        }
        logger.LogCritical("{Message} Fout: {Error}", BackupFailedMessage, result.Error);
        throw new InvalidOperationException($"{BackupFailedMessage} Fout: {result.Error}");
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
