using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// Gedrag dat bij een nieuwe versie van .NET of EF Core ongemerkt kan veranderen.
public class UpgradeTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    [Fact]
    public void Model_has_no_changes_without_a_migration()
    {
        // Sinds EF Core 9 weigert Migrate() bij het opstarten als het model afwijkt van de laatste migratie.
        Assert.False(factory.WithDb(db => db.Database.HasPendingModelChanges()));
    }

    [Fact]
    public async Task Swagger_document_is_served_in_development()
    {
        // De testserver draait in ontwikkelmodus. Swashbuckle moet passen bij de OpenAPI-bibliotheek van ASP.NET Core.
        var res = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("/api/auth/login", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_answers_with_status_codes_instead_of_redirects()
    {
        // Geen verwijzing naar een loginpagina, en bij te weinig rechten de eigen melding.
        var anonymous = await factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).GetAsync("/api/customers");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(anonymous.Headers.Location);

        var admin = await factory.LoginAsync();
        await CreateUserAsync(admin, "upgrade-medewerker@potel.nl");
        var employee = await factory.LoginAsync("upgrade-medewerker@potel.nl", "geheim123");
        var forbidden = await employee.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Null(forbidden.Headers.Location);
        Assert.Equal("Dit mag alleen een beheerder.", await forbidden.ErrorAsync());
    }
}
