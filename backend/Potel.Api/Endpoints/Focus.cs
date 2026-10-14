using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

// Focusmodus op de timer: werken in blokken (standaard 25 minuten), met na elk blok een korte pauze en na vier blokken een lange.
// De timer blijft gewoon de timer: bij stoppen boeken we de gewerkte tijd. Een pauze zet de timer stil, dus die boeken we niet.
// Of een blok vol is, rekenen we uit aan de gewerkte tijd; een pauze die je tussendoor zelf neemt, telt dus niet mee in het blok.
public static partial class TimerEndpoints
{
    public const int DefaultFocusMinutes = 25, MinFocusMinutes = 15, MaxFocusMinutes = 90;
    public const int ShortBreakMinutes = 5, LongBreakMinutes = 15, BlocksPerRound = 4;

    // Phase is "focus" (aan het werk), "break" (de pauze loopt) of "ready" (de pauze is om en het volgende blok kan beginnen).
    public record FocusDto(int Minutes, string Phase, int Blocks, int Round, int BlockStart, DateTime? BreakStartedAt, int BreakMinutes, int RemainingSeconds);
    public record FocusRequest(string? Key, int Minutes, int? ProjectId, string? Description, bool? Billable);
    public record FocusDay(string Date, int Blocks);

    public const string FocusLength = "Kies een focusblok van 15 tot 90 minuten.";

    // Werkt de focusstand bij tot nu. Is het blok vol, dan telt het mee en staat de timer stil vanaf het moment dat het vol was:
    // de pauze begint. Keek er niemand, dan gebeurt dit pas later, maar ook dan boeken we alleen het blok en niet de uren erna.
    public static void Settle(RunningTimer t, DateTime now)
    {
        if (t.FocusMinutes is not { } minutes || t.FocusBreakStartedAt is not null) return;
        var over = WorkedSeconds(t, now) - t.FocusBlockStart - minutes * 60;
        if (over < 0) return;
        var full = (t.PausedAt is { } paused && paused < now ? paused : now).AddSeconds(-over);
        t.PausedAt = full;
        t.FocusBreakStartedAt = full;
        t.FocusBlocks++;
        t.FocusRound++;
        if (t.FocusRound >= BlocksPerRound)
        {
            t.FocusRound = 0;
            t.FocusBreakMinutes = LongBreakMinutes;
        }
        else t.FocusBreakMinutes = ShortBreakMinutes;
    }

    static FocusDto? Focus(RunningTimer t, DateTime now)
    {
        if (t.FocusMinutes is not { } minutes) return null;
        if (t.FocusBreakStartedAt is { } breakStart)
        {
            var left = (int)Math.Ceiling(t.FocusBreakMinutes * 60 - (now - breakStart).TotalSeconds);
            return new(minutes, left > 0 ? "break" : "ready", t.FocusBlocks, t.FocusRound, t.FocusBlockStart, Utc(breakStart), t.FocusBreakMinutes, Math.Max(0, left));
        }
        var remaining = (int)Math.Ceiling(minutes * 60 - (WorkedSeconds(t, now) - t.FocusBlockStart));
        return new(minutes, "focus", t.FocusBlocks, t.FocusRound, t.FocusBlockStart, null, 0, Math.Max(0, remaining));
    }

    static void Unpause(RunningTimer t, DateTime now)
    {
        if (t.PausedAt is not { } paused) return;
        t.PausedSeconds += (int)Math.Round(Math.Max(0, (now - paused).TotalSeconds));
        t.PausedAt = null;
    }

    // Verder na een pauze. Na de pauze van de focusmodus begint daarmee het volgende blok.
    static void Resume(RunningTimer t, DateTime now)
    {
        Unpause(t, now);
        if (t.FocusBreakStartedAt is null) return;
        t.FocusBreakStartedAt = null;
        t.FocusBreakMinutes = 0;
        t.FocusBlockStart = (int)WorkedSeconds(t, now);
    }

    // Focusmodus aan, met een blok dat nu begint. Stond de timer stil, dan loopt hij weer: een blok is werken.
    static void StartFocus(RunningTimer t, int minutes, DateTime now)
    {
        if (t.FocusMinutes is null) t.FocusRound = 0;
        t.FocusMinutes = minutes;
        t.FocusBreakStartedAt = null;
        t.FocusBreakMinutes = 0;
        Unpause(t, now);
        t.FocusBlockStart = (int)WorkedSeconds(t, now);
    }

    // Focusmodus uit. De afgemaakte blokken blijven bij de timer en komen op de boeking.
    static void StopFocus(RunningTimer t, DateTime _)
    {
        t.FocusMinutes = null;
        t.FocusBlockStart = 0;
        t.FocusBreakStartedAt = null;
        t.FocusBreakMinutes = 0;
        t.FocusRound = 0;
    }

    // Wisselen van project in focusmodus: het blok of de pauze loopt door op de nieuwe timer, en de lange pauze komt nog steeds na vier blokken.
    static void CarryFocus(RunningTimer old, RunningTimer next, DateTime end, DateTime now)
    {
        if (old.FocusMinutes is not { } minutes) return;
        next.FocusMinutes = minutes;
        next.FocusRound = old.FocusRound;
        if (old.FocusBreakStartedAt is { } breakStart)
        {
            next.PausedAt = now;
            next.FocusBreakStartedAt = breakStart;
            next.FocusBreakMinutes = old.FocusBreakMinutes;
            return;
        }
        var done = Math.Clamp(WorkedSeconds(old, end) - old.FocusBlockStart, 0, minutes * 60 - 1);
        next.FocusBlockStart = -(int)done;
    }

    static void MapFocus(RouteGroupBuilder g)
    {
        // Focusmodus aan: op de timer die loopt, of met een nieuwe timer. De gekozen lengte onthouden we voor de volgende keer, op elk apparaat.
        g.MapPost("/focus", async (AppDb db, ClaimsPrincipal user, BusinessClock clock, FocusRequest req) =>
        {
            if (user.UserId() is not { } me) return Results.Unauthorized();
            if (req.Minutes is < MinFocusMinutes or > MaxFocusMinutes) return Results.BadRequest(new { error = FocusLength });
            await using var tx = await WriteLock.BeginAsync(db);
            var now = Utc(clock.UtcNow);
            var t = await db.RunningTimers.FirstOrDefaultAsync(x => x.UserId == me);
            var created = false;
            if (t is null)
            {
                // Dit apparaat dacht dat er een timer liep, maar die is intussen ergens anders gestopt.
                if (req.Key is not null) return Refuse(await StateAsync(db, me, now), NotRunning);
                var project = req.ProjectId is { } projectId ? await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId) : null;
                if (project is null) return Results.BadRequest(new { error = "Kies een project" });
                t = NewTimer(me, project, req.Description, req.Billable, now);
                db.RunningTimers.Add(t);
                created = true;
            }
            else if (req.Key is null) return Refuse(await StateAsync(db, me, now), AlreadyRunning);
            else if (req.Key != t.Key) return Refuse(await StateAsync(db, me, now), Changed);
            else Settle(t, now);
            StartFocus(t, req.Minutes, now);
            t.UpdatedAt = now;
            (await db.Users.FirstAsync(u => u.Id == me)).FocusMinutes = req.Minutes;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            var state = new TimerState(await DtoAsync(db, t, now), now, null, req.Minutes);
            return created ? Results.Created("/api/timer", state) : Results.Ok(state);
        });

        // Focusmodus uit: de timer loopt als gewone timer verder. Stond hij in een pauze, dan blijft hij stil tot je op Verder drukt.
        g.MapDelete("/focus", (AppDb db, ClaimsPrincipal user, BusinessClock clock, string? key) =>
            ChangeAsync(db, user, clock, key, StopFocus));

        // Afgemaakte focusblokken per dag, alleen die van jezelf. De lopende timer telt mee op de dag waarop hij begon, net als zijn boeking straks.
        g.MapGet("/focus/days", async (AppDb db, ClaimsPrincipal user, BusinessClock clock, DateTime from, DateTime to) =>
        {
            if (user.UserId() is not { } me) return Results.Unauthorized();
            var now = Utc(clock.UtcNow);
            var counts = (await db.TimeEntries.AsNoTracking()
                    .Where(e => e.UserId == me && e.FocusBlocks > 0 && e.Date >= from.Date && e.Date <= to.Date)
                    .Select(e => new { e.Date, e.FocusBlocks }).ToListAsync())
                .GroupBy(e => e.Date.Date).ToDictionary(d => d.Key, d => d.Sum(e => e.FocusBlocks));
            if (await db.RunningTimers.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == me) is { } t)
            {
                Settle(t, now);
                var day = LocalDate(t.StartedAt);
                if (t.FocusBlocks > 0 && day >= from.Date && day <= to.Date) counts[day] = counts.GetValueOrDefault(day) + t.FocusBlocks;
            }
            return Results.Ok(counts.OrderBy(c => c.Key).Select(c => new FocusDay(c.Key.ToString("yyyy-MM-dd"), c.Value)));
        });
    }
}
