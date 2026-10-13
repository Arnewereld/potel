using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Potel.Api.Data;

namespace Potel.Api.Tests;

// De migratie voor ritten voegt alleen kolommen en tabellen toe: bestaande klanten, projecten, facturen en uren blijven zoals ze waren.
public class TripMigrationTests : IDisposable
{
    const string Before = "20261013171155_Bankafschriften";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-migratie-ritten-{Guid.NewGuid():N}.db");

    AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path}").Options, new Tenant());

    object? Scalar(string sql)
    {
        using var con = new SqliteConnection($"Data Source={path}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    [Fact]
    public void Existing_customers_projects_and_hours_survive_the_migration()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        using (var con = new SqliteConnection($"Data Source={path}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 'Oud bedrijf', 'team');
                INSERT INTO Customers (Id, WorkspaceId, IsDemo, CreatedAt, Name, Company, RemindersOff) VALUES (1, 1, 0, '2026-01-01 00:00:00', 'Jan', 'Klant B.V.', 0);
                INSERT INTO Projects (Id, WorkspaceId, IsDemo, CustomerId, Name, Billing, Color, CreatedAt, FixedPrice, HourlyRate, Status)
                    VALUES (1, 1, 0, 1, 'Website', 'uur', '#000000', '2026-01-01 00:00:00', 0, 95, 'actief');
                INSERT INTO TimeEntries (Id, WorkspaceId, IsDemo, ProjectId, Date, Minutes, Description, Billable, CreatedAt)
                    VALUES (1, 1, 0, 1, '2026-08-20 00:00:00', 60, 'Bouwen', 1, '2026-08-20 00:00:00');
                """;
            cmd.ExecuteNonQuery();
        }

        using (var db = Db()) db.GetService<IMigrator>().Migrate();

        Assert.Equal("Klant B.V.", Scalar("SELECT Company FROM Customers WHERE Id = 1"));
        Assert.Null(Scalar("SELECT KilometreRate FROM Customers WHERE Id = 1"));
        Assert.Equal(95L, Convert.ToInt64(Scalar("SELECT HourlyRate FROM Projects WHERE Id = 1")));
        Assert.Null(Scalar("SELECT KilometreRate FROM Projects WHERE Id = 1"));
        Assert.Equal(60L, Scalar("SELECT Minutes FROM TimeEntries WHERE Id = 1"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM Trips"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM TripRoutes"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }
}
