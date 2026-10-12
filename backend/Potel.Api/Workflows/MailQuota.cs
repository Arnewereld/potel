using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Workflows;

public enum QuotaResult { Ok, WorkspaceFull, TrialsFull }

// De daglimiet voor e-mail uit een werkruimte. Werkstromen, facturen en herinneringen tellen samen mee, en tijdens de proef
// ook voor de limiet van alle proefwerkruimtes samen. Elke telling gaat in één update, zodat twee verzoeken tegelijk
// samen niet over de limiet heen kunnen.
public static class MailQuota
{
    public static async Task<string> PlanAsync(AppDb db) =>
        await db.Workspaces.AsNoTracking().Where(w => w.Id == db.TenantId).Select(w => w.Plan).FirstOrDefaultAsync() ?? Plans.Trial;

    // Telt één e-mail mee. Bij WorkspaceFull of TrialsFull mag hij niet weg.
    public static async Task<QuotaResult> ReserveAsync(AppDb db, WorkflowLimits limits)
    {
        var plan = await PlanAsync(db);
        if (!await ReserveWorkspaceAsync(db, limits.EmailsPerDay(plan))) return QuotaResult.WorkspaceFull;
        if (plan == Plans.Trial && !await ReservePlatformAsync(db, PlatformCounter.TrialEmails, limits.EmailsPerDayAllTrials)) return QuotaResult.TrialsFull;
        return QuotaResult.Ok;
    }

    // Is er vandaag nog ruimte, zonder iets mee te tellen? Om vooraf te kijken, bijvoorbeeld voordat een factuur een nummer krijgt.
    public static async Task<QuotaResult> CheckAsync(AppDb db, WorkflowLimits limits)
    {
        var plan = await PlanAsync(db);
        var today = DateTime.UtcNow.Date;
        var limit = limits.EmailsPerDay(plan);
        var w = await db.Workspaces.AsNoTracking().Where(x => x.Id == db.TenantId).Select(x => new { x.EmailDay, x.EmailsSent }).FirstOrDefaultAsync();
        if (limit <= 0 || (w is not null && w.EmailDay == today && w.EmailsSent >= limit)) return QuotaResult.WorkspaceFull;
        if (plan != Plans.Trial) return QuotaResult.Ok;
        var c = await db.PlatformCounters.AsNoTracking().FirstOrDefaultAsync(x => x.Key == PlatformCounter.TrialEmails);
        return limits.EmailsPerDayAllTrials <= 0 || (c is not null && c.Day == today && c.Count >= limits.EmailsPerDayAllTrials)
            ? QuotaResult.TrialsFull : QuotaResult.Ok;
    }

    public static string Message(QuotaResult result, int limit) => result == QuotaResult.TrialsFull
        ? "Tijdens de proefperiode kan er vandaag geen e-mail meer uit. Morgen kan het weer, of kies een abonnement."
        : $"Je werkruimte heeft vandaag de daglimiet van {limit} e-mails bereikt. Morgen kan het weer.";

    // Telt een e-mail mee voor de daglimiet van deze werkruimte. Onwaar als de limiet voor vandaag al bereikt is.
    public static async Task<bool> ReserveWorkspaceAsync(AppDb db, int limit)
    {
        if (limit <= 0) return false;
        var today = DateTime.UtcNow.Date;
        var id = db.TenantId;
        return await db.Workspaces
            .Where(w => w.Id == id && (w.EmailDay != today || w.EmailsSent < limit))
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.EmailsSent, w => w.EmailDay == today ? w.EmailsSent + 1 : 1)
                .SetProperty(w => w.EmailDay, today)) == 1;
    }

    // Telt mee voor een daglimiet van het hele platform, zoals alle e-mail uit proefwerkruimtes samen.
    public static async Task<bool> ReservePlatformAsync(AppDb db, string key, int limit)
    {
        if (limit <= 0) return false;
        var today = DateTime.UtcNow.Date;
        return await db.PlatformCounters
            .Where(c => c.Key == key && (c.Day != today || c.Count < limit))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Count, c => c.Day == today ? c.Count + 1 : 1)
                .SetProperty(c => c.Day, today)) == 1;
    }
}
