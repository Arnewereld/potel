using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Potel.Api.Data;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Een database van voor de koppeling tussen uren en factuurregels, met concepten waar al uren op staan.
public class LegacyInvoiceDatabaseFactory : WebApplicationFactory<Program>
{
    readonly string dbPath = Path.Combine(Path.GetTempPath(), $"potel-legacy-{Guid.NewGuid():N}.db");

    public LegacyInvoiceDatabaseFactory()
    {
        using (var db = new AppDb(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={dbPath}").Options, new Tenant()))
            db.GetService<IMigrator>().Migrate("20261006090358_WerkstroomGrenzen");
        using var con = new SqliteConnection($"Data Source={dbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 'Oud bedrijf', 'team');
            INSERT INTO Users (Id, WorkspaceId, Name, Email, PasswordHash, Role, Active, CreatedAt, IsPlatformAdmin, SecurityStamp)
                VALUES (1, 1, 'Eigenaar', 'oud@example.com', $hash, 'beheerder', 1, '2026-01-01 00:00:00', 0, '');
            INSERT INTO Settings (Id, WorkspaceId, BrandColor, CompanyName, Address, City, Kvk, Btw, DefaultHourlyRate, PaymentTermDays, WeeklyHoursTarget, YearlyHoursTarget)
                VALUES (1, 1, '#ff6d5a', 'Oud Bedrijf', 'Straat 1', '1234 AB Utrecht', '12345678', 'NL001234567B01', 95, 14, 32, 1225);
            INSERT INTO Customers (Id, WorkspaceId, CreatedAt, Name, Company, Address, City, VatNumber) VALUES
                (1, 1, '2026-01-01 00:00:00', 'Jan', 'Klant B.V.', 'Weg 2', '1000 AA Amsterdam', 'NL812345678B01');
            INSERT INTO Projects (Id, WorkspaceId, CustomerId, Name, Billing, Color, CreatedAt, FixedPrice, HourlyRate, Status) VALUES
                (1, 1, 1, 'Website', 'uur', '#000000', '2026-01-01 00:00:00', 0, 95, 'actief'),
                (2, 1, 1, 'App', 'uur', '#000000', '2026-01-01 00:00:00', 0, 80, 'actief');
            INSERT INTO Invoices (Id, WorkspaceId, CustomerId, Number, Status, IssueDate, DueDate, ReverseCharge) VALUES
                (1, 1, 1, '2026-0001', 'concept', '2026-10-01 00:00:00', '2026-10-15 00:00:00', 0),
                (2, 1, 1, '2026-0002', 'verzonden', '2026-09-01 00:00:00', '2026-09-15 00:00:00', 1),
                (3, 1, 1, '2026-0003', 'concept', '2026-10-05 00:00:00', '2026-10-19 00:00:00', 0);
            INSERT INTO InvoiceLines (Id, InvoiceId, Description, Quantity, Unit, UnitPrice, VatRate) VALUES
                (1, 1, '01-10-2026 Bouwen', 1, 'uur', 95, 21),
                (2, 1, '02-10-2026 Testen', 2, 'uur', 95, 21),
                (3, 2, 'Advies', 1, 'stuk', 500, 0),
                (4, 3, 'Website: werkzaamheden 03-10 t/m 04-10-2026', 1.5, 'uur', 95, 21),
                (5, 3, 'App: werkzaamheden 03-10 t/m 03-10-2026', 1, 'uur', 80, 21);
            INSERT INTO TimeEntries (Id, WorkspaceId, ProjectId, Date, Minutes, Description, Billable, CreatedAt, InvoiceId) VALUES
                (1, 1, 1, '2026-10-01 00:00:00', 60, 'Bouwen', 1, '2026-10-01 00:00:00', 1),
                (2, 1, 1, '2026-10-02 00:00:00', 120, 'Testen', 1, '2026-10-02 00:00:00', 1),
                (3, 1, 1, '2026-10-03 00:00:00', 60, 'Ontwerp', 1, '2026-10-03 00:00:00', 3),
                (4, 1, 1, '2026-10-04 00:00:00', 30, 'Teksten', 1, '2026-10-04 00:00:00', 3),
                (5, 1, 2, '2026-10-03 00:00:00', 60, 'Schermen', 1, '2026-10-03 00:00:00', 3);
            """;
        cmd.Parameters.AddWithValue("$hash", Potel.Api.Endpoints.AuthEndpoints.Hash(new User(), "geheim123"));
        cmd.ExecuteNonQuery();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("DatabasePath", dbPath);
        builder.UseSetting("Workflows:Scheduler", "false");
        builder.UseSetting("Smtp:Host", "");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }
}

// Na de update horen de oude uren bij hun regel: een regel weghalen geeft alleen die uren vrij.
public class UpgradedInvoiceTests(LegacyInvoiceDatabaseFactory factory) : IClassFixture<LegacyInvoiceDatabaseFactory>
{
    static object Keep(JsonElement line) => new
    {
        id = line.GetProperty("id").GetInt32(), description = line.GetProperty("description").GetString(), quantity = line.GetProperty("quantity").GetDecimal(),
        unit = line.GetProperty("unit").GetString(), unitPrice = line.GetProperty("unitPrice").GetDecimal(), vatRate = line.GetProperty("vatRate").GetDecimal(),
    };

    static async Task<Dictionary<int, JsonElement>> EntriesAsync(HttpClient c, int projectId) =>
        (await c.GetFromJsonAsync<List<JsonElement>>($"/api/time?projectId={projectId}"))!.ToDictionary(e => e.GetProperty("id").GetInt32());

    static bool Billed(JsonElement entry) => entry.GetProperty("invoiceId").ValueKind != JsonValueKind.Null;

    [Fact]
    public async Task Removing_a_line_from_an_upgraded_detailed_concept_releases_only_its_hours()
    {
        var c = await TestApi.LoginAsync(factory, "oud@example.com");
        var inv = await GetInvoiceAsync(c, 1);
        var put = await c.PutAsJsonAsync("/api/invoices/1", WithLines(inv, new[] { Keep(inv.GetProperty("lines")[0]) }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var entries = await EntriesAsync(c, 1);
        Assert.True(Billed(entries[1]));
        Assert.False(Billed(entries[2]));
    }

    [Fact]
    public async Task Removing_one_project_from_an_upgraded_summary_concept_releases_only_that_project()
    {
        var c = await TestApi.LoginAsync(factory, "oud@example.com");
        var inv = await GetInvoiceAsync(c, 3);
        var website = inv.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("description").GetString()!.StartsWith("Website"));
        var put = await c.PutAsJsonAsync("/api/invoices/3", WithLines(inv, new[] { Keep(website) }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var site = await EntriesAsync(c, 1);
        Assert.True(Billed(site[3]) && Billed(site[4]));
        Assert.False(Billed((await EntriesAsync(c, 2))[5]));
    }

    [Fact]
    public async Task An_upgraded_reverse_charge_invoice_can_be_credited()
    {
        var c = await TestApi.LoginAsync(factory, "oud@example.com");
        Assert.Equal("verlegd", (await GetInvoiceAsync(c, 2)).GetProperty("vatRegime").GetString());
        var credit = await c.PostAsync("/api/invoices/2/credit", null);
        Assert.Equal(HttpStatusCode.Created, credit.StatusCode);
        var creditId = (await Json(credit)).GetProperty("id").GetInt32();
        var sent = await SetStatusAsync(c, creditId, "verzonden");
        Assert.True(sent.StatusCode == HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
    }
}
