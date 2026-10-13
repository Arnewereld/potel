using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Potel.Api.Data;

namespace Potel.Api.Tests;

// De migratie voor investeringen voegt alleen kolommen toe: bestaande kosten blijven gewone kosten met hetzelfde bedrag en dezelfde btw.
public class InvestmentMigrationTests : IDisposable
{
    const string Before = "20261013180706_Ritten";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-migratie-investeringen-{Guid.NewGuid():N}.db");

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
    public void Existing_expenses_stay_plain_costs_after_the_migration()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        using (var con = new SqliteConnection($"Data Source={path}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 'Oud bedrijf', 'team');
                INSERT INTO Expenses (Id, WorkspaceId, IsDemo, Date, Supplier, Category, Amount, VatRate, VatAmount, ReverseCharge, CreatedAt)
                    VALUES (1, 1, 0, '2026-02-12 00:00:00', 'Coolblue', 'hardware', '2899.0', '21.0', '503.13', 0, '2026-02-12 00:00:00');
                """;
            cmd.ExecuteNonQuery();
        }

        using (var db = Db()) db.GetService<IMigrator>().Migrate();

        Assert.Equal("Coolblue", Scalar("SELECT Supplier FROM Expenses WHERE Id = 1"));
        Assert.Equal("2899.0", Scalar("SELECT Amount FROM Expenses WHERE Id = 1"));
        Assert.Equal("503.13", Scalar("SELECT VatAmount FROM Expenses WHERE Id = 1"));
        Assert.Equal(0L, Scalar("SELECT Investment FROM Expenses WHERE Id = 1"));
        Assert.Equal(0L, Scalar("SELECT PassengerCar FROM Expenses WHERE Id = 1"));
        Assert.Null(Scalar("SELECT UsefulLifeYears FROM Expenses WHERE Id = 1"));
        Assert.Null(Scalar("SELECT ResidualValue FROM Expenses WHERE Id = 1"));
        Assert.Null(Scalar("SELECT DisposedOn FROM Expenses WHERE Id = 1"));
        Assert.Null(Scalar("SELECT DisposalPrice FROM Expenses WHERE Id = 1"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }
}
