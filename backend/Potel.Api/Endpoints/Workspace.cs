using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public record RegisterRequest(string Company, string Name, string Email, string Password, bool DemoData);
public record DeleteWorkspaceRequest(string Password);

public static class WorkspaceEndpoints
{
    public const int TrialDays = 30;

    static object Dto(Workspace w, bool platformAdmin = false) => new
    {
        w.Id, w.Name, w.Plan, w.TrialEndsAt, w.CreatedAt, platformAdmin, maxUsers = Plans.MaxUsers(w.Plan),
        onboarded = w.OnboardedAt != null,
        trialDaysLeft = w.Plan == Plans.Trial && w.TrialEndsAt is { } end ? Math.Max(0, (int)Math.Ceiling((end - DateTime.UtcNow).TotalDays)) : (int?)null,
    };

    public static void MapWorkspace(this RouteGroupBuilder api)
    {
        // Zelf een account aanmaken: een nieuwe werkruimte met een proefperiode en jij als beheerder.
        api.MapPost("/auth/register", async (AppDb db, HttpContext http, RegisterRequest req) =>
        {
            var email = (req.Email ?? "").Trim().ToLower();
            if (string.IsNullOrWhiteSpace(req.Company) || string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Vul je naam en bedrijfsnaam in" });
            if (!email.Contains('@') || email.Length < 5) return Results.BadRequest(new { error = "Vul een geldig e-mailadres in" });
            if ((req.Password ?? "").Length < AuthEndpoints.MinPasswordLength)
                return Results.BadRequest(new { error = $"Kies een wachtwoord van minstens {AuthEndpoints.MinPasswordLength} tekens" });
            if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email))
                return Results.Conflict(new { error = AuthEndpoints.EmailUnavailable });

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
            db.Users.RemoveRange(db.Users);
            if (await db.Workspaces.FindAsync(db.TenantId) is { } w) db.Workspaces.Remove(w);
            await db.SaveChangesAsync();
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }
}
