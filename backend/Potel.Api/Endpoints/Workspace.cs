using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public record RegisterRequest(string Company, string Name, string Email, string Password, bool DemoData);
public record DeleteWorkspaceRequest(string Password);

// Begrenst het aanmaken van werkruimtes: per netwerk (IPv4 /24, IPv6 /64) en voor het hele platform samen, per uur.
// Een werkruimte is gratis, dus anders maakt één bron er zoveel als hij wil en vermenigvuldigt hij elke limiet per werkruimte.
public sealed class SignupThrottle(IConfiguration config) : IDisposable
{
    readonly PartitionedRateLimiter<string> perNetwork = PartitionedRateLimiter.Create<string, string>(network =>
        RateLimitPartition.GetFixedWindowLimiter(network, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("RateLimit:SignupsPerHour", 5), Window = TimeSpan.FromHours(1),
        }));

    readonly RateLimiter total = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
    {
        PermitLimit = config.GetValue("RateLimit:SignupsPerHourTotal", 100), Window = TimeSpan.FromHours(1),
    });

    // Het netwerk waar een adres in zit: de eerste drie getallen van een IPv4-adres, de eerste helft van een IPv6-adres.
    public static string Network(IPAddress? ip)
    {
        if (ip is null) return "onbekend";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            ? $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24"
            : $"{Convert.ToHexString(bytes, 0, 8)}/64";
    }

    // Null als het mag, anders de melding.
    public string? TryStart(IPAddress? ip)
    {
        using (var lease = perNetwork.AttemptAcquire(Network(ip)))
            if (!lease.IsAcquired) return "Vanaf jouw netwerk zijn het afgelopen uur al een paar werkruimtes aangemaakt. Probeer het over een uur opnieuw.";
        using (var lease = total.AttemptAcquire())
            if (!lease.IsAcquired) return "Er melden zich nu erg veel mensen tegelijk aan. Probeer het over een uur opnieuw.";
        return null;
    }

    public void Dispose()
    {
        perNetwork.Dispose();
        total.Dispose();
    }
}

public static class WorkspaceEndpoints
{
    public const int TrialDays = 30;

    static object Dto(Workspace w, bool platformAdmin = false) => new
    {
        w.Id, w.Name, w.Plan, w.TrialEndsAt, w.CreatedAt, platformAdmin, maxUsers = Plans.MaxUsers(w.Plan),
        onboarded = w.OnboardedAt != null, demoData = w.DemoDataAt != null,
        trialDaysLeft = w.Plan == Plans.Trial && w.TrialEndsAt is { } end ? Math.Max(0, (int)Math.Ceiling((end - DateTime.UtcNow).TotalDays)) : (int?)null,
    };

    public static void MapWorkspace(this RouteGroupBuilder api)
    {
        // Zelf een account aanmaken: een nieuwe werkruimte met een proefperiode en jij als beheerder.
        api.MapPost("/auth/register", async (AppDb db, HttpContext http, SignupThrottle throttle, AccountMail mail, RegisterRequest req) =>
        {
            var email = (req.Email ?? "").Trim().ToLower();
            if (string.IsNullOrWhiteSpace(req.Company) || string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Vul je naam en bedrijfsnaam in" });
            if (!email.Contains('@') || email.Length < 5) return Results.BadRequest(new { error = "Vul een geldig e-mailadres in" });
            if ((req.Password ?? "").Length < AuthEndpoints.MinPasswordLength)
                return Results.BadRequest(new { error = $"Kies een wachtwoord van minstens {AuthEndpoints.MinPasswordLength} tekens" });
            if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email))
                return Results.Conflict(new { error = AuthEndpoints.EmailUnavailable });
            if (throttle.TryStart(http.Connection.RemoteIpAddress) is { } tooMany)
                return Results.Json(new { error = tooMany }, statusCode: StatusCodes.Status429TooManyRequests);

            var ws = new Workspace { Name = req.Company.Trim(), TrialEndsAt = DateTime.UtcNow.AddDays(TrialDays) };
            db.Workspaces.Add(ws);
            await db.SaveChangesAsync();
            db.Tenant.WorkspaceId = ws.Id;

            db.Settings.Add(new Settings { CompanyName = ws.Name, OwnerName = req.Name.Trim(), Email = email });
            var user = new User { Name = req.Name.Trim(), Email = email, Role = Roles.Admin, LastLoginAt = DateTime.UtcNow };
            user.PasswordHash = AuthEndpoints.Hash(user, req.Password!);
            db.Users.Add(user);
            db.Log("systeem", $"Werkruimte {ws.Name} aangemaakt");
            await db.SaveChangesAsync();
            if (req.DemoData) Seed.Run(db, sampleCompany: false);
            // Werken kan meteen; mail uit werkstromen pas na het bevestigen van het adres.
            await mail.SendVerificationAsync(user, http.Request);

            await AuthEndpoints.SignIn(http, user);
            return Results.Created("/api/workspace", UserDto.From(user));
        }).AllowAnonymous().RequireRateLimiting("auth");

        var g = api.MapGroup("/workspace");

        g.MapGet("/", async (AppDb db, ClaimsPrincipal user) =>
            await db.Workspaces.FindAsync(db.TenantId) is { } w ? Results.Ok(Dto(w, await PlatformEndpoints.IsPlatformAdmin(db, user))) : Results.NotFound());

        g.MapPost("/onboarded", async (AppDb db) =>
        {
            var w = await db.Workspaces.FindAsync(db.TenantId);
            if (w is null) return Results.NotFound();
            w.OnboardedAt ??= DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(Dto(w));
        });

        g.MapPost("/demo-data", async (AppDb db) =>
        {
            if (await db.Customers.AnyAsync() || await db.Projects.AnyAsync())
                return Results.Conflict(new { error = "Er staan al gegevens in je werkruimte; voorbeelddata kan alleen in een lege werkruimte." });
            Seed.Run(db, sampleCompany: false);
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Haalt alle voorbeelddata weg, ook verstuurde voorbeeldfacturen. Wat je zelf hebt toegevoegd blijft staan, ook als het
        // aan een voorbeeldklant of -project hangt; die klant of dat project blijft dan ook.
        g.MapDelete("/demo-data", async (AppDb db) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            // Voorbeelduren die je zelf op een echte factuur zette, blijven staan; die factuur moet blijven kloppen.
            db.TimeEntries.RemoveRange(db.TimeEntries.Where(t => t.IsDemo && (t.InvoiceId == null || db.Invoices.Any(i => i.Id == t.InvoiceId && i.IsDemo))));
            db.Invoices.RemoveRange(db.Invoices.Where(i => i.IsDemo && !i.Credits.Any()));
            db.Leads.RemoveRange(db.Leads.Where(l => l.IsDemo));
            db.Appointments.RemoveRange(db.Appointments.Where(a => a.IsDemo));
            db.CustomRecords.RemoveRange(db.CustomRecords.Where(r => r.IsDemo));
            var workflows = await db.Workflows.Where(w => w.IsDemo).Select(w => w.Id).ToListAsync();
            db.WorkflowRuns.RemoveRange(db.WorkflowRuns.Where(r => workflows.Contains(r.WorkflowId)));
            db.Workflows.RemoveRange(db.Workflows.Where(w => w.IsDemo));
            await db.SaveChangesAsync();
            db.Projects.RemoveRange(db.Projects.Where(p => p.IsDemo && !db.TimeEntries.Any(t => t.ProjectId == p.Id)));
            db.CustomModules.RemoveRange(db.CustomModules.Where(m => m.IsDemo && !db.CustomRecords.Any(r => r.ModuleId == m.Id)));
            await db.SaveChangesAsync();
            var customers = await db.Customers
                .Where(c => c.IsDemo && !db.Invoices.Any(i => i.CustomerId == c.Id) && !db.Projects.Any(p => p.CustomerId == c.Id))
                .Select(c => c.Id).ToListAsync();
            await db.Appointments.Where(a => a.CustomerId != null && customers.Contains(a.CustomerId.Value)).ExecuteUpdateAsync(x => x.SetProperty(a => a.CustomerId, (int?)null));
            await db.Leads.Where(l => l.CustomerId != null && customers.Contains(l.CustomerId.Value)).ExecuteUpdateAsync(x => x.SetProperty(l => l.CustomerId, (int?)null));
            db.Customers.RemoveRange(db.Customers.Where(c => customers.Contains(c.Id)));
            if (await db.Workspaces.FindAsync(db.TenantId) is { } w) w.DemoDataAt = null;
            db.Log("systeem", "Voorbeelddata verwijderd");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Al je gegevens in één JSON-bestand, bijvoorbeeld voor je administratie of om over te stappen.
        g.MapGet("/export", async (AppDb db) =>
        {
            var data = new
            {
                exportedAt = DateTime.UtcNow,
                workspace = await db.Workspaces.FindAsync(db.TenantId),
                settings = await db.Settings.FirstOrDefaultAsync(),
                customers = await db.Customers.ToListAsync(),
                projects = await db.Projects.ToListAsync(),
                timeEntries = await db.TimeEntries.ToListAsync(),
                invoices = await db.Invoices.Include(i => i.Lines).ToListAsync(),
                leads = await db.Leads.ToListAsync(),
                appointments = await db.Appointments.ToListAsync(),
                modules = await db.CustomModules.ToListAsync(),
                records = await db.CustomRecords.ToListAsync(),
                workflows = await db.Workflows.ToListAsync(),
                users = (await db.Users.ToListAsync()).Select(UserDto.From),
            };
            return Results.Json(data, contentType: "application/json", statusCode: 200);
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Verwijdert de hele werkruimte met alle gegevens. Alleen een beheerder, met wachtwoord.
        g.MapDelete("/", async (AppDb db, HttpContext http, ClaimsPrincipal me, [Microsoft.AspNetCore.Mvc.FromBody] DeleteWorkspaceRequest req) =>
        {
            var user = me.UserId() is { } id ? await db.Users.FindAsync(id) : null;
            if (user is null) return Results.Unauthorized();
            if (new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, req.Password ?? "") == PasswordVerificationResult.Failed)
                return Results.BadRequest(new { error = "Je wachtwoord klopt niet" });
            // Een platformbeheerder in deze werkruimte verdwijnt mee. Dat mag alleen een platformbeheerder zelf beslissen,
            // en de laatste platformbeheerder kan zichzelf zo niet weghalen.
            if (await db.Users.AnyAsync(u => u.IsPlatformAdmin && u.Id != user.Id) && !user.IsPlatformAdmin)
                return Results.Json(new { error = "In deze werkruimte zit een account dat ook het platform beheert. Vraag een platformbeheerder eerst die rechten weg te halen." }, statusCode: StatusCodes.Status403Forbidden);
            if (user.IsPlatformAdmin && !await db.Users.IgnoreQueryFilters().AnyAsync(u => u.IsPlatformAdmin && u.WorkspaceId != user.WorkspaceId))
                return Results.Conflict(new { error = "Je bent de laatste platformbeheerder. Wijs eerst op de pagina Platform iemand uit een andere werkruimte aan, anders kan niemand het platform meer beheren." });

            // Volgorde maakt uit vanwege de koppelingen tussen tabellen.
            db.TimeEntries.RemoveRange(db.TimeEntries);
            db.Invoices.RemoveRange(db.Invoices);
            await db.SaveChangesAsync();
            db.Projects.RemoveRange(db.Projects);
            db.Appointments.RemoveRange(db.Appointments);
            db.Leads.RemoveRange(db.Leads);
            await db.SaveChangesAsync();
            db.Customers.RemoveRange(db.Customers);
            db.CustomRecords.RemoveRange(db.CustomRecords);
            db.CustomModules.RemoveRange(db.CustomModules);
            db.WorkflowRuns.RemoveRange(db.WorkflowRuns);
            db.Workflows.RemoveRange(db.Workflows);
            db.Activities.RemoveRange(db.Activities);
            db.Settings.RemoveRange(db.Settings);
            db.AccountTokens.RemoveRange(db.AccountTokens);
            db.Users.RemoveRange(db.Users);
            if (await db.Workspaces.FindAsync(db.TenantId) is { } w) db.Workspaces.Remove(w);
            await db.SaveChangesAsync();
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }
}
