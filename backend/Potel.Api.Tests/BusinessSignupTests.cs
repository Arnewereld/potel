using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Potel.Api.Data;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// Alleen zakelijke klanten: KvK-nummer, zakelijk gebruik en akkoord op de voorwaarden, afgedwongen op de server.
public class BusinessSignupTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    static object Signup(string email, string? kvk = "12345678", bool acceptTerms = true, bool businessUse = true) =>
        new { company = "Bedrijf", name = "Eigenaar", email, password = "geheim123", demoData = false, kvk, acceptTerms, businessUse };

    async Task<HttpResponseMessage> PostAsync(object body) => await Client(factory).PostAsJsonAsync("/api/auth/register", body);

    bool Exists(string email) => factory.WithDb(db => db.Users.IgnoreQueryFilters().Any(u => u.Email == email));

    [Fact]
    public async Task Signup_without_accepting_the_terms_is_refused()
    {
        var res = await PostAsync(Signup("geen-akkoord@example.com", acceptTerms: false));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("voorwaarden", await res.ErrorAsync());
        Assert.False(Exists("geen-akkoord@example.com"));
    }

    [Fact]
    public async Task Signup_without_confirming_business_use_is_refused()
    {
        var res = await PostAsync(Signup("prive@example.com", businessUse: false));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("zakelijk gebruik", await res.ErrorAsync());
        Assert.False(Exists("prive@example.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234567")]
    [InlineData("123456789")]
    [InlineData("1234567a")]
    [InlineData("١٢٣٤٥٦٧٨")]
    public async Task Signup_needs_a_kvk_number_of_eight_digits(string? kvk)
    {
        var email = $"kvk-{Guid.NewGuid():N}@example.com";
        var res = await PostAsync(Signup(email, kvk: kvk));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("KvK-nummer", await res.ErrorAsync());
        Assert.False(Exists(email));
    }

    [Fact]
    public async Task Accepted_terms_version_time_and_user_are_stored_on_the_workspace()
    {
        var client = Client(factory);
        var res = await client.PostAsJsonAsync("/api/auth/register", Signup("akkoord@example.com", kvk: "87.65 43 21"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var userId = (await res.JsonAsync()).GetProperty("id").GetInt32();
        var wsId = await WorkspaceIdAsync(client);

        var ws = factory.WithDb(db => db.Workspaces.Single(w => w.Id == wsId));
        Assert.Equal(Terms.Version, ws.TermsVersion);
        Assert.Equal(userId, ws.TermsAcceptedByUserId);
        Assert.Equal("akkoord@example.com", ws.TermsAcceptedByEmail);
        Assert.InRange(ws.TermsAcceptedAt!.Value, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("87654321", ws.Kvk);
        // Het KvK-nummer staat ook meteen bij de bedrijfsgegevens voor op de factuur.
        Assert.Equal("87654321", (await client.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("kvk").GetString());
    }

    [Fact]
    public async Task The_platform_owner_sees_kvk_and_accepted_terms_per_workspace()
    {
        var c = await RegisterAsync(factory, "zichtbaar-kvk@example.com");
        var id = await WorkspaceIdAsync(c);
        var owner = await factory.LoginAsync();
        var row = (await owner.GetFromJsonAsync<List<JsonElement>>("/api/platform/workspaces"))!.Single(r => r.GetProperty("id").GetInt32() == id);
        Assert.Equal("12345678", row.GetProperty("kvk").GetString());
        Assert.Equal(Terms.Version, row.GetProperty("termsVersion").GetString());
    }

    [Fact]
    public async Task Platform_details_are_public_and_show_placeholders_until_filled_in()
    {
        var info = await Client(factory).GetFromJsonAsync<JsonElement>("/api/public/platform");
        Assert.Equal(Terms.Version, info.GetProperty("termsVersion").GetString());
        Assert.StartsWith("[Vul in", info.GetProperty("company").GetString());
        Assert.StartsWith("[Vul in", info.GetProperty("kvk").GetString());
        Assert.False(info.GetProperty("complete").GetBoolean());
        Assert.NotEmpty(info.GetProperty("subProcessors").EnumerateArray());
    }

    [Fact]
    public void Frontend_has_the_legal_pages_marked_as_draft()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "frontend", "src", "App.tsx"))) dir = dir.Parent;
        if (dir is null) return; // alleen de backend uitgecheckt
        var src = Path.Combine(dir.FullName, "frontend", "src");
        var app = File.ReadAllText(Path.Combine(src, "App.tsx"));
        foreach (var path in new[] { "/voorwaarden", "/privacy", "/verwerkersovereenkomst" }) Assert.Contains($"'{path}'", app);
        var legal = File.ReadAllText(Path.Combine(src, "pages", "public", "Legal.tsx"));
        Assert.Contains("Concept: laat deze tekst nakijken door een jurist voordat je verkoopt", legal);
        // Het contactadres komt uit de instellingen van de server, niet uit een vast adres in plans.ts.
        Assert.DoesNotContain("salesEmail", File.ReadAllText(Path.Combine(src, "lib", "plans.ts")));
    }
}

// Met ingevulde gegevens van de eigenaar.
public class FilledPlatformFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Platform:Company", "Devwerk B.V.");
        builder.UseSetting("Platform:Address", "Kanaalweg 1");
        builder.UseSetting("Platform:City", "3526 KL Utrecht");
        builder.UseSetting("Platform:Email", "verkoop@devwerk.nl");
        builder.UseSetting("Platform:Kvk", "11223344");
        builder.UseSetting("Platform:VatId", "NL001122334B01");
        builder.UseSetting("Platform:SubProcessors:0:Name", "Hetzner Online GmbH");
        builder.UseSetting("Platform:SubProcessors:0:Location", "Duitsland");
        builder.UseSetting("Platform:SubProcessors:1:Name", "Scaleway SAS");
        builder.UseSetting("Platform:SubProcessors:1:Location", "Frankrijk");
    }
}

public class FilledPlatformTests(FilledPlatformFactory factory) : IClassFixture<FilledPlatformFactory>
{
    [Fact]
    public async Task Filled_in_details_come_from_the_configuration()
    {
        var info = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/public/platform");
        Assert.Equal("Devwerk B.V.", info.GetProperty("company").GetString());
        Assert.Equal("verkoop@devwerk.nl", info.GetProperty("email").GetString());
        Assert.Equal("11223344", info.GetProperty("kvk").GetString());
        Assert.Equal("Hetzner Online GmbH", info.GetProperty("subProcessors")[0].GetProperty("name").GetString());
        Assert.True(info.GetProperty("complete").GetBoolean());
    }
}

// Bestaande werkruimtes houden hun gegevens en krijgen het KvK-nummer uit hun bedrijfsgegevens.
public class BusinessSignupMigrationTests : IDisposable
{
    const string Before = "20261007110652_WachtwoordHerstellenEnEmailBevestigen";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-migratie-{Guid.NewGuid():N}.db");

    AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path}").Options, new Tenant());

    [Fact]
    public void Existing_workspaces_get_their_kvk_and_no_accepted_terms()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        using (var con = new SqliteConnection($"Data Source={path}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, WebhooksSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 0, 'Met KvK', 'zzp');
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, WebhooksSent, Name, Plan) VALUES (2, '2026-01-01 00:00:00', 0, 0, 'Zonder KvK', 'zzp');
                INSERT INTO Settings (Id, WorkspaceId, CompanyName, BrandColor, Kvk, DefaultHourlyRate, PaymentTermDays, WeeklyHoursTarget, YearlyHoursTarget)
                    VALUES (1, 1, 'Met KvK', '#ff6d5a', '11223344', 90, 14, 24, 1225);
                INSERT INTO Settings (Id, WorkspaceId, CompanyName, BrandColor, Kvk, DefaultHourlyRate, PaymentTermDays, WeeklyHoursTarget, YearlyHoursTarget)
                    VALUES (2, 2, 'Zonder KvK', '#ff6d5a', '', 90, 14, 24, 1225);
                """;
            cmd.ExecuteNonQuery();
        }
        using (var db = Db()) db.Database.Migrate();
        using (var db = Db())
        {
            var ws = db.Workspaces.OrderBy(w => w.Id).ToList();
            Assert.Equal(["Met KvK", "Zonder KvK"], ws.Select(w => w.Name));
            Assert.Equal("11223344", ws[0].Kvk);
            Assert.Null(ws[1].Kvk);
            Assert.All(ws, w => Assert.Null(w.TermsVersion));
            Assert.Equal("11223344", db.Settings.IgnoreQueryFilters().Single(s => s.WorkspaceId == 1).Kvk);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }
}
