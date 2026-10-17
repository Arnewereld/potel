using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Potel.Api.Data;

namespace Potel.Api.Tests;

// De migratie voor de taal per klant voegt alleen kolommen toe: alles wat er al was, blijft Nederlands en verder ongewijzigd.
public class LanguageMigrationTests : IDisposable
{
    const string Before = "20261013231254_Belastingpot";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-migratie-taal-{Guid.NewGuid():N}.db");

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
    public void Existing_customers_leads_invoices_and_quotes_stay_dutch()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        using (var con = new SqliteConnection($"Data Source={path}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 'Oud bedrijf', 'team');
                INSERT INTO Customers (Id, WorkspaceId, IsDemo, CreatedAt, Name, Company, Country, RemindersOff) VALUES (1, 1, 0, '2026-01-01 00:00:00', 'Jan', 'Klant B.V.', 'Ierland', 0);
                INSERT INTO Leads (Id, WorkspaceId, Name, Value, Status, CreatedAt, IsDemo) VALUES (1, 1, 'Piet', 1000, 'offerte', '2026-01-01 00:00:00', 0);
                INSERT INTO Invoices (Id, WorkspaceId, CustomerId, Number, Status, IssueDate, DueDate, VatRegime, IsDemo, RemindersOff, HourSpecification, HourAmounts, Notes) VALUES
                    (1, 1, 1, '2026-0001', 'verzonden', '2026-09-01 00:00:00', '2026-09-15 00:00:00', 'verlegd', 0, 0, 0, 0, 'Bedankt!');
                INSERT INTO Quotes (Id, WorkspaceId, IsDemo, Version, IssueDate, ValidUntil, Status, VatRegime, CreatedAt, CustomerId, Number) VALUES
                    (1, 1, 0, 1, '2026-09-01 00:00:00', '2026-10-01 00:00:00', 'verzonden', 'verlegd', '2026-09-01 00:00:00', 1, 'OFF-2026-001');
                """;
            cmd.ExecuteNonQuery();
        }

        using (var db = Db()) db.GetService<IMigrator>().Migrate();

        foreach (var table in new[] { "Customers", "Leads", "Invoices", "Quotes" })
            Assert.Equal("nl", Scalar($"SELECT Language FROM {table} WHERE Id = 1"));
        Assert.Equal("Klant B.V.", Scalar("SELECT Company FROM Customers WHERE Id = 1"));
        Assert.Equal("2026-0001", Scalar("SELECT Number FROM Invoices WHERE Id = 1"));
        Assert.Equal("Bedankt!", Scalar("SELECT Notes FROM Invoices WHERE Id = 1"));
        Assert.Equal("OFF-2026-001", Scalar("SELECT Number FROM Quotes WHERE Id = 1"));
        Assert.Equal("Piet", Scalar("SELECT Name FROM Leads WHERE Id = 1"));
    }

    public void Dispose()
    {
        TestDatabase.Delete(path);
    }
}
