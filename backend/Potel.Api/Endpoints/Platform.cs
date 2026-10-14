using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public record PlanRequest(string Plan, DateTime? TrialEndsAt);
public record PlatformAdminRequest(string Email);

// Voor jou als eigenaar van het platform: alle werkruimtes zien en abonnementen omzetten.
// Wie dit mag staat bij de gebruiker in de database (IsPlatformAdmin), nooit in een e-mailadres uit de cookie.
public static class PlatformEndpoints
{
    // De gebruiker uit de cookie, opgezocht binnen zijn eigen werkruimte.
    public static async Task<bool> IsPlatformAdmin(AppDb db, ClaimsPrincipal user) =>
        user.UserId() is { } id && await db.Users.FindAsync(id) is { Active: true, IsPlatformAdmin: true };

    public static void MapPlatform(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/platform").AddEndpointFilter(async (ctx, next) =>
            await IsPlatformAdmin(ctx.HttpContext.RequestServices.GetRequiredService<AppDb>(), ctx.HttpContext.User)
                ? await next(ctx) : Results.Json(new { error = "Dit is alleen voor de eigenaar van het platform." }, statusCode: 403));

        g.MapGet("/workspaces", async (AppDb db) =>
        {
            var workspaces = await db.Workspaces.OrderByDescending(w => w.CreatedAt).ToListAsync();
            var users = await db.Users.IgnoreQueryFilters().Select(u => new { u.WorkspaceId, u.Email, u.Role, u.LastLoginAt }).ToListAsync();
            var invoices = await db.Invoices.IgnoreQueryFilters().GroupBy(i => i.WorkspaceId).Select(x => new { WorkspaceId = x.Key, Count = x.Count() }).ToListAsync();
            var entries = await db.TimeEntries.IgnoreQueryFilters().GroupBy(t => t.WorkspaceId).Select(x => new { WorkspaceId = x.Key, Count = x.Count() }).ToListAsync();
            return workspaces.Select(w =>
            {
                var mine = users.Where(u => u.WorkspaceId == w.Id).ToList();
                return new
                {
                    w.Id, w.Name, w.Plan, w.TrialEndsAt, w.CreatedAt, onboarded = w.OnboardedAt != null, w.Kvk, w.TermsVersion, w.TermsAcceptedAt,
                    w.PaidUntil, subscriptionActive = w.MollieSubscriptionId != null, w.SubscriptionCanceledAt, readOnly = w.ReadOnly(DateTime.UtcNow),
                    owner = mine.FirstOrDefault(u => u.Role == Roles.Admin)?.Email,
                    users = mine.Count,
                    lastActive = mine.Max(u => u.LastLoginAt),
                    invoices = invoices.FirstOrDefault(i => i.WorkspaceId == w.Id)?.Count ?? 0,
                    timeEntries = entries.FirstOrDefault(i => i.WorkspaceId == w.Id)?.Count ?? 0,
                };
            });
        });

        g.MapPut("/workspaces/{id:int}/plan", async (AppDb db, int id, PlanRequest req) =>
        {
            var w = await db.Workspaces.FindAsync(id);
            if (w is null) return Results.NotFound();
            if (!Plans.All.Contains(req.Plan)) return Results.BadRequest(new { error = "Onbekend abonnement" });
            w.Plan = req.Plan;
            if (req.Plan == Plans.Trial) w.TrialEndsAt = req.TrialEndsAt ?? DateTime.UtcNow.AddDays(WorkspaceEndpoints.TrialDays);
            // Zet je het zelf om, dan loopt het abonnement door tot je het weer omzet, ook als een betaalde periode via Mollie voorbij is.
            // Een lopend abonnement bij Mollie blijft lopen; zeg dat zo nodig op in Mollie.
            w.PaidUntil = null;
            w.SubscriptionCanceledAt = null;
            await db.SaveChangesAsync();
            return Results.Ok(new { w.Id, w.Plan, w.TrialEndsAt, w.PaidUntil, readOnly = w.ReadOnly(DateTime.UtcNow) });
        });

        // Wie het platform nog meer mag beheren. Alleen een platformbeheerder kan dat aanpassen.
        g.MapGet("/admins", async (AppDb db) => await AdminsAsync(db));

        // Back-ups van de hele database: waar ze staan, de laatste en of er iets misging. Downloaden kan bewust niet:
        // de database bevat alle werkruimtes. Terugzetten doe je met de hand op de server (zie de README).
        g.MapGet("/backups", (DatabaseBackups backups) => BackupStatus(backups));

        g.MapPost("/backups", async (DatabaseBackups backups) =>
        {
            var result = await Task.Run(() => backups.Create());
            return result.Outcome switch
            {
                BackupOutcome.Created => Results.Ok(BackupStatus(backups)),
                BackupOutcome.Busy => Results.Conflict(new { error = "Er wordt al een back-up gemaakt. Wacht even en kijk dan opnieuw." }),
                BackupOutcome.Skipped => Results.Json(new { error = $"De back-up is overgeslagen. {result.Error}" }, statusCode: StatusCodes.Status507InsufficientStorage),
                _ => Results.Json(new { error = $"De back-up is mislukt. {result.Error}" }, statusCode: StatusCodes.Status500InternalServerError),
            };
        });

        g.MapPost("/admins", async (AppDb db, PlatformAdminRequest req) =>
        {
            var email = (req.Email ?? "").Trim().ToLower();
            var u = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Email == email);
            if (u is null) return Results.NotFound(new { error = "Er is geen account met dit e-mailadres. Laat die persoon zich eerst aanmelden." });
            u.IsPlatformAdmin = true;
            await db.SaveChangesAsync();
            return Results.Ok(await AdminsAsync(db));
        });

        g.MapDelete("/admins/{id:int}", async (AppDb db, ClaimsPrincipal me, int id) =>
        {
            if (me.UserId() == id) return Results.BadRequest(new { error = "Je kunt jezelf niet weghalen. Vraag dat aan een andere platformbeheerder." });
            var u = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id && x.IsPlatformAdmin);
            if (u is null) return Results.NotFound();
            u.IsPlatformAdmin = false;
            await db.SaveChangesAsync();
            return Results.Ok(await AdminsAsync(db));
        });
    }

    static object BackupStatus(DatabaseBackups backups)
    {
        var files = backups.List();
        var last = files.FirstOrDefault();
        var o = backups.Options;
        return new
        {
            folder = backups.Folder,
            daily = o.Daily,
            keep = o.Keep, keepMonthly = o.KeepMonthly, keepBeforeUpdate = o.KeepBeforeUpdate,
            count = files.Count,
            totalSize = files.Sum(f => f.Size),
            last = last is null ? null : new { name = last.Name, takenAt = DatabaseBackups.TakenUtc(last), size = last.Size, beforeUpdate = last.BeforeUpdate },
            problem = backups.LastProblem,
            running = backups.Running,
        };
    }

    static async Task<List<object>> AdminsAsync(AppDb db)
    {
        var admins = await db.Users.IgnoreQueryFilters().Where(u => u.IsPlatformAdmin).OrderBy(u => u.Email).ToListAsync();
        var ids = admins.Select(u => u.WorkspaceId).ToList();
        var names = await db.Workspaces.Where(w => ids.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name);
        return admins.Select(u => (object)new { u.Id, u.Name, u.Email, workspace = names.GetValueOrDefault(u.WorkspaceId) }).ToList();
    }
}
