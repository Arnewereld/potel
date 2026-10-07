using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Potel.Api.Data;

namespace Potel.Api.Tests;

// Een bestaande installatie van voor IsPlatformAdmin: de eigenaar moet de pagina Platform houden, een later aangemelde klant niet.
public class PlatformAdminUpgradeTests
{
    [Fact]
    public void The_first_admin_of_an_existing_install_stays_platform_admin()
    {
        var path = Path.Combine(Path.GetTempPath(), $"potel-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = Open(path))
                db.GetService<IMigrator>().Migrate("20261005223923_Werkruimtes");
            using (var con = new SqliteConnection($"Data Source={path}"))
            {
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = """
                    INSERT OR IGNORE INTO Workspaces (Id, Name, Plan, CreatedAt) VALUES (1, 'Eigenaar', 'team', '2026-01-01 00:00:00');
                    INSERT INTO Workspaces (Id, Name, Plan, CreatedAt) VALUES (2, 'Klant', 'proef', '2026-02-01 00:00:00');
                    INSERT INTO Users (Id, WorkspaceId, Name, Email, PasswordHash, Role, Active, CreatedAt) VALUES
                        (1, 1, 'Eigenaar', 'admin@potel.nl', 'x', 'beheerder', 1, '2026-01-01 00:00:00'),
                        (2, 2, 'Klant', 'klant@example.com', 'x', 'beheerder', 1, '2026-02-01 00:00:00');
                    """;
                cmd.ExecuteNonQuery();
            }
            using (var db = Open(path))
            {
                db.Database.Migrate();
                var admins = db.Users.IgnoreQueryFilters().Where(u => u.IsPlatformAdmin).Select(u => u.Email).ToList();
                Assert.Equal(["admin@potel.nl"], admins);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        }
    }

    static AppDb Open(string path) =>
        new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path}").Options, new Tenant());
}
