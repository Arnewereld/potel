using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

// De timer op de server: per gebruiker hooguit één, op elk apparaat dezelfde. Stoppen maakt een boeking zoals de timer in de
// browser dat altijd deed, met dezelfde afronding en controles. Loopt hij over middernacht of langer dan 12 uur, dan vragen we
// eerst of de eindtijd klopt, in plaats van stilletjes een enorme boeking te maken.
public static class TimerEndpoints
{
    // Langer dan dit aan gewerkte tijd, of over middernacht: eerst de eindtijd bevestigen.
    public const int LongHours = 12;
    // Zoveel mag de klok van een apparaat voorlopen bij een tijd die het meestuurt.
    static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    public record TimerDto(
        string Key, int ProjectId, string ProjectName, string ProjectColor, string? CustomerName, string Description, bool Billable,
        DateTime StartedAt, DateTime? PausedAt, int PausedSeconds, int ElapsedSeconds, bool NeedsConfirmation);

    // Elk antwoord geeft de stand van nu en de tijd van de server, zodat elk apparaat dezelfde verstreken tijd laat zien,
    // ook als de klok van dat apparaat afwijkt. Na stoppen of wisselen staat de nieuwe boeking erbij.
    public record TimerState(TimerDto? Timer, DateTime ServerNow, TimeEntry? Entry = null);

    public record StartRequest(int ProjectId, string? Description, bool? Billable);
    public record KeyRequest(string? Key);
    public record UpdateRequest(string? Key, string? Description, bool? Billable);
    public record StopRequest(string? Key, DateTimeOffset? EndedAt, bool Confirmed);
    public record SwitchRequest(string? Key, int ProjectId, string? Description, bool? Billable, DateTimeOffset? EndedAt, bool Confirmed);
    public record ImportRequest(string? Key, int ProjectId, string? Description, bool? Billable, DateTimeOffset StartedAt);

    public const string NotRunning = "Er loopt geen timer.";
    public const string Changed = "Je timer is intussen op een ander apparaat veranderd. Je ziet nu hoe hij er echt voor staat.";
    public const string AlreadyRunning = "Je timer loopt al, misschien op een ander apparaat. Stop die eerst of wissel van project.";
    public const string ConfirmEnd = "Deze timer loopt over middernacht of langer dan 12 uur. Kies eerst wanneer je gestopt bent.";
    public const string ImportConflict = "Op een ander apparaat liep al een timer. Die loopt door; de timer uit deze browser is niet overgezet.";

    // In een alleen-lezen werkruimte mag je je eigen lopende timer nog weggooien; er wordt dan niets geboekt.
    public static bool IsDiscard(HttpRequest r) =>
        HttpMethods.IsDelete(r.Method) && r.Path.Equals("/api/timer", StringComparison.OrdinalIgnoreCase);

    static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
    static DateTime LocalDate(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(Utc(utc), BusinessClock.Zone).Date;
    static string NewKey() => $"timer-{Guid.NewGuid():N}";
    static bool Matches(RunningTimer t, string? key) => key is null || key == t.Key;

    // Gewerkte seconden tot het eind, zonder de pauzes.
    public static double WorkedSeconds(RunningTimer t, DateTime end)
    {
        if (t.PausedAt is { } p && p < end) end = p;
        return Math.Max(0, (end - t.StartedAt).TotalSeconds - t.PausedSeconds);
    }

    // Afgerond op hele minuten en minstens één, zoals de timer in de browser altijd boekte.
    public static int Minutes(RunningTimer t, DateTime end) =>
        Math.Max(1, (int)Math.Round(WorkedSeconds(t, end) / 60, MidpointRounding.AwayFromZero));

    public static bool IsLong(RunningTimer t, DateTime end) =>
        LocalDate(t.StartedAt) != LocalDate(end) || WorkedSeconds(t, end) > LongHours * 3600;

    static async Task<TimerDto?> DtoAsync(AppDb db, RunningTimer? t, DateTime now)
    {
        if (t is null) return null;
        var p = await db.Projects.Include(x => x.Customer).AsNoTracking().FirstOrDefaultAsync(x => x.Id == t.ProjectId);
        return new TimerDto(t.Key, t.ProjectId, p?.Name ?? "", p?.Color ?? "#ff6d5a", p?.Customer?.Company ?? p?.Customer?.Name,
            t.Description, t.Billable, Utc(t.StartedAt), t.PausedAt is { } paused ? Utc(paused) : null, t.PausedSeconds,
            (int)WorkedSeconds(t, now), IsLong(t, now));
    }

    static async Task<TimerState> StateAsync(AppDb db, int me, DateTime now, TimeEntry? entry = null) =>
        new(await DtoAsync(db, await db.RunningTimers.AsNoTracking().FirstOrDefaultAsync(t => t.UserId == me), now), now, entry);

    // Weigeren, met de echte stand erbij, zodat het apparaat meteen bijgewerkt is.
    static IResult Refuse(TimerState state, string error, bool needsConfirmation = false) =>
        Results.Conflict(new { error, needsConfirmation, timer = state.Timer, serverNow = state.ServerNow });

    // De boeking die deze timer al opleverde: twee keer gestopt, of gestopt in een tabblad met een oudere versie van Potel.
    static Task<TimeEntry?> BookedAsync(AppDb db, string? key) =>
        key is null ? Task.FromResult<TimeEntry?>(null) : db.TimeEntries.AsNoTracking().FirstOrDefaultAsync(e => e.ClientId == key);

    // De eindtijd bij stoppen of wisselen: nu, of wat de gebruiker koos nadat we vroegen of de eindtijd klopt.
    static (DateTime End, string? Error, bool Ask) EndTime(RunningTimer t, DateTimeOffset? endedAt, bool confirmed, DateTime now)
    {
        if (endedAt is { } chosen)
        {
            var end = chosen.UtcDateTime;
            if (end <= t.StartedAt) return (now, "Kies een eindtijd na het begin van de timer.", false);
            if (end > now + Skew) return (now, "Die eindtijd ligt in de toekomst. Kies een tijd van vandaag of eerder.", false);
            return (end > now ? now : end, null, false);
        }
        return (now, null, !confirmed && IsLong(t, now));
    }

    // Stopt de timer en boekt de tijd, met dezelfde controles als uren handmatig boeken. Bestaat de boeking al, dan blijft het daarbij.
    // Hoort binnen een WriteLock; de aanroeper slaat op.
    static async Task<(TimeEntry? Entry, string? Error)> FinishAsync(AppDb db, RunningTimer t, DateTime end, int me)
    {
        if (await BookedAsync(db, t.Key) is { } booked)
        {
            db.RunningTimers.Remove(t);
            return (booked, null);
        }
        var entry = new TimeEntry
        {
            ProjectId = t.ProjectId, Date = LocalDate(t.StartedAt), Minutes = Minutes(t, end),
            Description = t.Description.Trim(), Billable = t.Billable, ClientId = t.Key,
            // Wie boekt, altijd uit de inlog en nooit uit wat de browser meestuurt.
            UserId = me,
        };
        if (TimeEndpoints.Validate(db, entry) is { } error) return (null, error);
        db.TimeEntries.Add(entry);
        db.RunningTimers.Remove(t);
        return (entry, null);
    }

    static RunningTimer NewTimer(int me, Project project, string? description, bool? billable, DateTime startedAt, string? key = null) => new()
    {
        UserId = me, ProjectId = project.Id, Description = description ?? "", Billable = billable ?? project.Billing == Billing.Hourly,
        StartedAt = startedAt, Key = key ?? NewKey(), UpdatedAt = startedAt,
    };

    // Een kleine wijziging aan de eigen timer: pauzeren, verder, omschrijving. Een verouderd scherm (ander kenmerk) wijzigt niets.
    static async Task<IResult> ChangeAsync(AppDb db, ClaimsPrincipal user, BusinessClock clock, string? key, Action<RunningTimer, DateTime> change)
    {
        if (user.UserId() is not { } me) return Results.Unauthorized();
        await using var tx = await WriteLock.BeginAsync(db);
        var now = Utc(clock.UtcNow);
        var t = await db.RunningTimers.FirstOrDefaultAsync(x => x.UserId == me);
        if (t is null || !Matches(t, key)) return Refuse(await StateAsync(db, me, now), t is null ? NotRunning : Changed);
        change(t, now);
        t.UpdatedAt = now;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return Results.Ok(new TimerState(await DtoAsync(db, t, now), now));
    }

    public static void MapTimer(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/timer");

        // Alleen je eigen timer; ook een beheerder ziet die van een ander niet.
        g.MapGet("/", async (AppDb db, ClaimsPrincipal user, BusinessClock clock) =>
            user.UserId() is { } me ? Results.Ok(await StateAsync(db, me, Utc(clock.UtcNow))) : Results.Unauthorized());

        g.MapPost("/", async (AppDb db, ClaimsPrincipal user, BusinessClock clock, StartRequest req) =>
        {
            if (user.UserId() is not { } me) return Results.Unauthorized();
            var now = Utc(clock.UtcNow);
            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == req.ProjectId);
            if (project is null) return Results.BadRequest(new { error = "Kies een project" });
            if (await db.RunningTimers.AnyAsync(t => t.UserId == me)) return Refuse(await StateAsync(db, me, now), AlreadyRunning);
            var timer = NewTimer(me, project, req.Description, req.Billable, now);
            db.RunningTimers.Add(timer);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Tegelijk gestart op twee apparaten: de unieke index hield de tweede tegen.
                db.Entry(timer).State = EntityState.Detached;
                if (!await db.RunningTimers.AnyAsync(t => t.UserId == me)) throw;
                return Refuse(await StateAsync(db, me, now), AlreadyRunning);
            }
            return Results.Created("/api/timer", new TimerState(await DtoAsync(db, timer, now), now));
        });

        g.MapPut("/", (AppDb db, ClaimsPrincipal user, BusinessClock clock, UpdateRequest req) =>
            ChangeAsync(db, user, clock, req.Key, (t, _) =>
            {
                if (req.Description is not null) t.Description = req.Description;
                if (req.Billable is { } billable) t.Billable = billable;
            }));

        g.MapPost("/pause", (AppDb db, ClaimsPrincipal user, BusinessClock clock, KeyRequest req) =>
            ChangeAsync(db, user, clock, req.Key, (t, now) => t.PausedAt ??= now));

        g.MapPost("/resume", (AppDb db, ClaimsPrincipal user, BusinessClock clock, KeyRequest req) =>
            ChangeAsync(db, user, clock, req.Key, (t, now) =>
            {
                if (t.PausedAt is not { } paused) return;
                t.PausedSeconds += (int)Math.Round(Math.Max(0, (now - paused).TotalSeconds));
                t.PausedAt = null;
            }));

        g.MapPost("/stop", async (AppDb db, ClaimsPrincipal user, BusinessClock clock, StopRequest req) =>
        {
            if (user.UserId() is not { } me) return Results.Unauthorized();
            await using var tx = await WriteLock.BeginAsync(db);
            var now = Utc(clock.UtcNow);
            var t = await db.RunningTimers.FirstOrDefaultAsync(x => x.UserId == me);
            if (t is null || !Matches(t, req.Key))
            {
                // Al gestopt, met een dubbelklik of op een ander apparaat: de eerste boeking telt.
                if (await BookedAsync(db, req.Key) is { UserId: var by } booked && by == me) return Results.Ok(await StateAsync(db, me, now, booked));
                return Refuse(await StateAsync(db, me, now), t is null ? NotRunning : Changed);
            }
            var (end, problem, ask) = EndTime(t, req.EndedAt, req.Confirmed, now);
            if (problem is not null) return Results.BadRequest(new { error = problem });
            if (ask) return Refuse(new TimerState(await DtoAsync(db, t, now), now), ConfirmEnd, needsConfirmation: true);
            var (entry, error) = await FinishAsync(db, t, end, me);
            if (error is not null) return Results.BadRequest(new { error });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(new TimerState(null, now, entry));
        });

        // Wisselen van project terwijl de timer loopt: de tijd tot nu gaat als boeking naar het oude project, en de timer loopt verder op het nieuwe.
        g.MapPost("/switch", async (AppDb db, ClaimsPrincipal user, BusinessClock clock, SwitchRequest req) =>
        {
            if (user.UserId() is not { } me) return Results.Unauthorized();
            await using var tx = await WriteLock.BeginAsync(db);
            var now = Utc(clock.UtcNow);
            var t = await db.RunningTimers.FirstOrDefaultAsync(x => x.UserId == me);
            if (t is null || !Matches(t, req.Key)) return Refuse(await StateAsync(db, me, now), t is null ? NotRunning : Changed);
            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == req.ProjectId);
            if (project is null) return Results.BadRequest(new { error = "Kies een project" });
            if (project.Id == t.ProjectId) return Results.BadRequest(new { error = "De timer loopt al op dit project." });
            var (end, problem, ask) = EndTime(t, req.EndedAt, req.Confirmed, now);
            if (problem is not null) return Results.BadRequest(new { error = problem });
            if (ask) return Refuse(new TimerState(await DtoAsync(db, t, now), now), ConfirmEnd, needsConfirmation: true);
            var (entry, error) = await FinishAsync(db, t, end, me);
            if (error is not null) return Results.BadRequest(new { error });
            // Eerst de oude timer weg, dan pas de nieuwe erbij: er mag er maar één tegelijk zijn.
            await db.SaveChangesAsync();
            var next = NewTimer(me, project, req.Description, req.Billable, now);
            db.RunningTimers.Add(next);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(new TimerState(await DtoAsync(db, next, now), now, entry));
        });

        // Weggooien zonder te boeken. Een verouderd scherm gooit nooit een nieuwere timer weg.
        g.MapDelete("/", async (AppDb db, ClaimsPrincipal user, BusinessClock clock, string? key) =>
        {
            if (user.UserId() is not { } me) return Results.Unauthorized();
            await using var tx = await WriteLock.BeginAsync(db);
            var now = Utc(clock.UtcNow);
            var t = await db.RunningTimers.FirstOrDefaultAsync(x => x.UserId == me);
            if (t is null) return Results.Ok(new TimerState(null, now));
            if (!Matches(t, key)) return Refuse(await StateAsync(db, me, now), Changed);
            db.RunningTimers.Remove(t);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(new TimerState(null, now));
        });

        // Eén keer: een timer die nog in de browser stond (van voor de timer op de server) gaat naar de server, met zijn eigen begintijd
        // en kenmerk. Liep er al een, dan blijft die staan. Was hij al geboekt, dan gebeurt er niets.
        g.MapPost("/import", async (AppDb db, ClaimsPrincipal user, BusinessClock clock, ImportRequest req) =>
        {
            if (user.UserId() is not { } me) return Results.Unauthorized();
            await using var tx = await WriteLock.BeginAsync(db);
            var now = Utc(clock.UtcNow);
            var key = string.IsNullOrWhiteSpace(req.Key) || req.Key.Trim().Length > 64 ? null : req.Key.Trim();
            if (await BookedAsync(db, key) is not null) return Results.Ok(await StateAsync(db, me, now));
            if (await db.RunningTimers.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == me) is { } existing)
                return existing.Key == key ? Results.Ok(await StateAsync(db, me, now)) : Refuse(await StateAsync(db, me, now), ImportConflict);
            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == req.ProjectId);
            if (project is null) return Results.BadRequest(new { error = "Het project van de timer in deze browser bestaat niet meer." });
            var started = req.StartedAt.UtcDateTime;
            if (started > now + Skew) return Results.BadRequest(new { error = "De timer in deze browser begint in de toekomst." });
            var timer = NewTimer(me, project, req.Description, req.Billable, started > now ? now : started, key);
            db.RunningTimers.Add(timer);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(new TimerState(await DtoAsync(db, timer, now), now));
        });
    }
}
