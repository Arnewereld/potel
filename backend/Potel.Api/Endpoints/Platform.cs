using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public record PlanRequest(string Plan, DateTime? TrialEndsAt);

// Voor jou als eigenaar van het platform: alle werkruimtes zien en abonnementen omzetten.
// Wie dit mag staat in de instelling PlatformAdmins (e-mailadressen).
public static class PlatformEndpoints
{
    public static bool IsPlatformAdmin(IConfiguration config, ClaimsPrincipal user)
    {
        var email = user.FindFirstValue(ClaimTypes.Email)?.ToLower();
        return email is not null && (config.GetSection("PlatformAdmins").Get<string[]>() ?? []).Any(a => a.Trim().ToLower() == email);
    }

    public static void MapPlatform(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/platform").AddEndpointFilter(async (ctx, next) =>
            IsPlatformAdmin(ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>(), ctx.HttpContext.User)
                ? await next(ctx) : Results.Forbid());

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
                    w.Id, w.Name, w.Plan, w.TrialEndsAt, w.CreatedAt, onboarded = w.OnboardedAt != null,
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
            await db.SaveChangesAsync();
            return Results.Ok(new { w.Id, w.Plan, w.TrialEndsAt });
        });
    }
}
