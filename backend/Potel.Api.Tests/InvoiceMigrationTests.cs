using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Potel.Api.Data;

namespace Potel.Api.Tests;

// De migratie naar facturen volgens de regels houdt bestaande gegevens geldig: verstuurde facturen houden hun nummer
// en krijgen de gegevens van beide partijen, concepten verliezen hun voorlopige nummer, creditnota's raken gekoppeld.
public class InvoiceMigrationTests : IDisposable
{
    const string Before = "20261006090358_WerkstroomGrenzen";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-migratie-{Guid.NewGuid():N}.db");

    AppDb Db(int workspaceId = 0) =>
        new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path}").Options, new Tenant { WorkspaceId = workspaceId });

    void Exec(string sql)
    {
        using var con = new SqliteConnection($"Data Source={path}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

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
    public void Existing_invoices_survive_the_migration()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);

        // Een werkruimte zoals hij er vóór deze migratie uitzag.
        Exec("""
            INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 'Oud bedrijf', 'team');
            INSERT INTO Settings (Id, WorkspaceId, BrandColor, CompanyName, OwnerName, Address, City, Kvk, Btw, Iban, DefaultHourlyRate, PaymentTermDays, WeeklyHoursTarget, YearlyHoursTarget)
                VALUES (1, 1, '#ff6d5a', ' Oud Bedrijf B.V. ', 'Eigenaar', 'Straat 1', '1234 AB Utrecht', '12345678', 'NL001234567B01', '', 95, 14, 32, 1225);
            INSERT INTO Customers (Id, WorkspaceId, CreatedAt, Name, Company, Address, City, VatNumber) VALUES (1, 1, '2026-01-01 00:00:00', 'Jan', 'Klant GmbH', 'Weg 2', '10115 Berlin', 'DE123456789');
            INSERT INTO Projects (Id, WorkspaceId, CustomerId, Name, Billing, Color, CreatedAt, FixedPrice, HourlyRate, Status)
                VALUES (1, 1, 1, 'Website', 'uur', '#000000', '2026-01-01 00:00:00', 0, 95, 'actief');
            INSERT INTO Invoices (Id, WorkspaceId, CustomerId, Number, Status, IssueDate, DueDate, ReverseCharge, Notes) VALUES
                (1, 1, 1, '2026-0001', 'verzonden', '2026-09-01 00:00:00', '2026-09-15 00:00:00', 1, NULL),
                (2, 1, 1, '2026-0002', 'concept',   '2026-09-05 00:00:00', '2026-09-19 00:00:00', 0, NULL),
                (3, 1, 1, '2026-0003', 'verzonden', '2026-09-10 00:00:00', '2026-09-10 00:00:00', 1, 'Creditnota voor factuur 2026-0001 van 01-09-2026.'),
                (4, 1, 1, '2026-0004', 'betaald',   '2026-09-12 00:00:00', '2026-09-26 00:00:00', 0, 'Dank je wel');
            INSERT INTO InvoiceLines (Id, InvoiceId, Description, Quantity, Unit, UnitPrice, VatRate) VALUES
                (1, 1, 'Website, augustus', 3, 'uur', 95, 0),
                (2, 2, 'Concept', 1, 'stuk', 100, 21),
                (3, 3, 'Website, augustus', -3, 'uur', 95, 0),
                (4, 4, 'Hosting', 1, 'stuk', 50, 21);
            INSERT INTO TimeEntries (Id, WorkspaceId, ProjectId, Date, Minutes, Description, Billable, CreatedAt, InvoiceId) VALUES
                (1, 1, 1, '2026-08-20 00:00:00', 60, 'Bouwen', 1, '2026-08-20 00:00:00', 1),
                (2, 1, 1, '2026-08-10 00:00:00', 120, 'Ontwerp', 1, '2026-08-10 00:00:00', 1),
                (3, 1, 1, '2026-09-02 00:00:00', 30, 'Open', 1, '2026-09-02 00:00:00', NULL);
            """);

        using (var db = Db()) db.GetService<IMigrator>().Migrate();

        // Verstuurd: nummer blijft, btw verlegd, momentopname van beide partijen, leverperiode uit de uren.
        Assert.Equal("2026-0001", Scalar("SELECT Number FROM Invoices WHERE Id = 1"));
        Assert.Equal("verlegd", Scalar("SELECT VatRegime FROM Invoices WHERE Id = 1"));
        Assert.Equal("Oud Bedrijf B.V.", Scalar("SELECT Seller_Name FROM Invoices WHERE Id = 1"));
        Assert.Equal("NL001234567B01", Scalar("SELECT Seller_VatNumber FROM Invoices WHERE Id = 1"));
        Assert.Null(Scalar("SELECT Seller_Iban FROM Invoices WHERE Id = 1"));
        Assert.Equal("Klant GmbH", Scalar("SELECT Buyer_Name FROM Invoices WHERE Id = 1"));
        Assert.Equal("Jan", Scalar("SELECT Buyer_Contact FROM Invoices WHERE Id = 1"));
        Assert.Equal("DE123456789", Scalar("SELECT Buyer_VatNumber FROM Invoices WHERE Id = 1"));
        Assert.Equal(1L, Scalar("SELECT SentAt = IssueDate FROM Invoices WHERE Id = 1"));
        Assert.StartsWith("2026-08-10", (string?)Scalar("SELECT DeliveryFrom FROM Invoices WHERE Id = 1"));
        Assert.StartsWith("2026-08-20", (string?)Scalar("SELECT DeliveryTo FROM Invoices WHERE Id = 1"));

        // Concept: geen nummer en geen momentopname; die komen pas bij versturen.
        Assert.Null(Scalar("SELECT Number FROM Invoices WHERE Id = 2"));
        Assert.Null(Scalar("SELECT Seller_Name FROM Invoices WHERE Id = 2"));
        Assert.StartsWith("2026-09-05", (string?)Scalar("SELECT DeliveryFrom FROM Invoices WHERE Id = 2"));

        // Creditnota: gekoppeld aan de factuur uit zijn opmerking, met diens leverperiode.
        Assert.Equal(1L, Scalar("SELECT CreditForInvoiceId FROM Invoices WHERE Id = 3"));
        Assert.StartsWith("2026-08-10", (string?)Scalar("SELECT DeliveryFrom FROM Invoices WHERE Id = 3"));
        Assert.Null(Scalar("SELECT CreditForInvoiceId FROM Invoices WHERE Id = 4"));
        Assert.Equal("normaal", Scalar("SELECT VatRegime FROM Invoices WHERE Id = 4"));
        Assert.StartsWith("2026-09-12", (string?)Scalar("SELECT DeliveryFrom FROM Invoices WHERE Id = 4"));

        // Uren hangen aan hun regel, niets is verdwenen, klanten wonen in Nederland tenzij anders ingevuld.
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM TimeEntries WHERE InvoiceLineId = 1"));
        Assert.Null(Scalar("SELECT InvoiceLineId FROM TimeEntries WHERE Id = 3"));
        Assert.Equal(4L, Scalar("SELECT COUNT(*) FROM Invoices"));
        Assert.Equal(4L, Scalar("SELECT COUNT(*) FROM InvoiceLines"));
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM TimeEntries"));
        Assert.Equal("Nederland", Scalar("SELECT Country FROM Customers WHERE Id = 1"));
        Assert.Equal("normaal", Scalar("SELECT VatRegime FROM Settings WHERE Id = 1"));

        // En het model leest alles gewoon in.
        using (var db = Db(1))
        {
            var invoices = db.Invoices.Include(i => i.Lines).Include(i => i.CreditFor).OrderBy(i => i.Id).ToList();
            Assert.Equal("Oud Bedrijf B.V.", invoices[0].Seller?.Name);
            Assert.Equal("Klant GmbH", invoices[0].Buyer?.Name);
            Assert.Null(invoices[1].Seller);
            Assert.Equal("2026-0001", invoices[2].CreditForNumber);
            Assert.True(invoices[2].IsCredit);
            Assert.Equal(-285m, invoices[2].Totals.Total);
        }

        // Terug naar de vorige versie kan ook; het concept krijgt dan een plaatsvervangend nummer.
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        Assert.Equal("2026-0001", Scalar("SELECT Number FROM Invoices WHERE Id = 1"));
        Assert.Equal(1L, Scalar("SELECT ReverseCharge FROM Invoices WHERE Id = 3"));
        Assert.Equal("concept-2", Scalar("SELECT Number FROM Invoices WHERE Id = 2"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }
}
