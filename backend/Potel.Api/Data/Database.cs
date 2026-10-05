using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Potel.Api.Data;

public static class Database
{
    // Brengt de database op het nieuwste schema. Een database uit de eerste versie (zonder migraties)
    // wordt bewaard als .bak-bestand en opnieuw aangemaakt.
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
        db.Database.Migrate();
    }
}
