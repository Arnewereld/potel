using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Notifications;
using Potel.Api.Workflows;

namespace Potel.Api.Endpoints;

public record NotificationKindSetting(string Id, bool InApp, bool Email);
public record NotificationSettingsInput(List<NotificationKindSetting>? Kinds, bool Digest);

// De meldingen van de ingelogde gebruiker: de bel, de pagina met alle meldingen en wat hij wil krijgen. Altijd alleen je eigen
// meldingen in je eigen werkruimte, en alleen soorten die je met je rol (nog) mag zien: wie geen beheerder meer is, ziet zijn oude
// meldingen over geld niet meer.
public static class NotificationEndpoints
{
    const int PageSize = 30;

    // Je meldingen lezen en je eigen instellingen kun je ook in een werkruimte die alleen-lezen is.
    public static bool IsPersonal(HttpRequest r) =>
        r.Path.StartsWithSegments("/api/notifications", out var rest)
        && ((HttpMethods.IsPost(r.Method) && rest.Value?.EndsWith("/read", StringComparison.OrdinalIgnoreCase) == true)
            || (HttpMethods.IsPut(r.Method) && rest.Equals("/settings", StringComparison.OrdinalIgnoreCase)));

    static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    static object Dto(Notification n) => new { n.Id, n.Kind, n.Title, n.Text, n.Link, createdAt = Utc(n.CreatedAt), read = n.ReadAt != null };

    // De meldingen die deze gebruiker mag zien.
    static async Task<(IQueryable<Notification> Query, User Me)?> MineAsync(AppDb db, ClaimsPrincipal user)
    {
        if (user.UserId() is not { } id || await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id) is not { } me) return null;
        var kinds = NotificationKinds.For(me.Role, me.IsPlatformAdmin).Select(k => k.Id).ToList();
        return (db.Notifications.Where(n => n.UserId == me.Id && n.InApp && kinds.Contains(n.Kind)), me);
    }

    public static void MapNotifications(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/notifications");

        // Nieuwste eerst, per pagina, eventueel alleen ongelezen of één soort. Before is het id van de laatste melding die je al hebt.
        g.MapGet("/", async (AppDb db, ClaimsPrincipal user, string? kind, bool? unread, int? before, int? limit) =>
        {
            if (await MineAsync(db, user) is not { } mine) return Results.Unauthorized();
            var q = mine.Query;
            var total = await q.CountAsync(n => n.ReadAt == null);
            if (!string.IsNullOrEmpty(kind)) q = q.Where(n => n.Kind == kind);
            if (unread == true) q = q.Where(n => n.ReadAt == null);
            if (before is { } b) q = q.Where(n => n.Id < b);
            var take = Math.Clamp(limit ?? PageSize, 1, 100);
            var items = await q.AsNoTracking().OrderByDescending(n => n.Id).Take(take + 1).ToListAsync();
            return Results.Ok(new { items = items.Take(take).Select(Dto), unread = total, more = items.Count > take });
        });

        // Voor de bel: hoeveel er ongelezen zijn, en de meldingen na after (voor een melding in beeld terwijl Potel openstaat).
        g.MapGet("/summary", async (AppDb db, ClaimsPrincipal user, int? after) =>
        {
            if (await MineAsync(db, user) is not { } mine) return Results.Unauthorized();
            var unread = await mine.Query.CountAsync(n => n.ReadAt == null);
            var latest = await mine.Query.MaxAsync(n => (int?)n.Id) ?? 0;
            IEnumerable<object> fresh = after is { } a && a < latest
                ? (await mine.Query.AsNoTracking().Where(n => n.Id > a && n.ReadAt == null).OrderByDescending(n => n.Id).Take(5).ToListAsync()).Select(Dto)
                : [];
            return Results.Ok(new { unread, latest, fresh });
        });

        g.MapPost("/{id:int}/read", async (AppDb db, BusinessClock clock, ClaimsPrincipal user, int id) =>
        {
            if (await MineAsync(db, user) is not { } mine) return Results.Unauthorized();
            var now = clock.UtcNow;
            if (!await mine.Query.AnyAsync(n => n.Id == id)) return Results.NotFound(new { error = "Deze melding bestaat niet (meer)." });
            await mine.Query.Where(n => n.Id == id && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now));
            return Results.Ok(new { unread = await mine.Query.CountAsync(n => n.ReadAt == null) });
        });

        // Alles gelezen, of alles van één soort.
        g.MapPost("/read", async (AppDb db, BusinessClock clock, ClaimsPrincipal user, string? kind) =>
        {
            if (await MineAsync(db, user) is not { } mine) return Results.Unauthorized();
            var now = clock.UtcNow;
            var q = mine.Query.Where(n => n.ReadAt == null);
            if (!string.IsNullOrEmpty(kind)) q = q.Where(n => n.Kind == kind);
            await q.ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now));
            return Results.Ok(new { unread = await mine.Query.CountAsync(n => n.ReadAt == null) });
        });

        g.MapGet("/settings", async (AppDb db, IEmailSender sender, ClaimsPrincipal user) =>
        {
            if (user.UserId() is not { } id || await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id) is not { } me) return Results.Unauthorized();
            var s = await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == me.Id);
            return Results.Ok(SettingsDto(me, s, sender.Configured));
        });

        // Per soort in de app en per e-mail, en of de mails als één overzicht per dag komen. Per e-mail alleen als er een mailserver is;
        // zonder mailserver blijft wat er stond gewoon staan.
        g.MapPut("/settings", async (AppDb db, IEmailSender sender, ClaimsPrincipal user, NotificationSettingsInput input) =>
        {
            if (user.UserId() is not { } id || await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id) is not { } me) return Results.Unauthorized();
            var allowed = NotificationKinds.For(me.Role, me.IsPlatformAdmin).Select(k => k.Id).ToHashSet();
            var kinds = input.Kinds ?? [];
            if (kinds.Any(k => !allowed.Contains(k.Id))) return Results.BadRequest(new { error = "Een van deze soorten meldingen bestaat niet of is niet voor jou." });
            await using var tx = await WriteLock.BeginAsync(db);
            var current = await NotificationMailer.SettingsAsync(db, me.Id);
            var s = await db.NotificationSettings.FirstAsync(x => x.Id == current.Id);
            // Soorten die je niet mag zien (of die je niet meestuurde) houden wat ze hadden.
            var off = NotificationKinds.Split(s.InAppOff).Where(k => !kinds.Any(x => x.Id == k)).Concat(kinds.Where(k => !k.InApp).Select(k => k.Id));
            s.InAppOff = NotificationKinds.Join(off);
            if (sender.Configured)
            {
                var email = NotificationKinds.Split(s.EmailOn).Where(k => !kinds.Any(x => x.Id == k)).Concat(kinds.Where(k => k.Email).Select(k => k.Id));
                s.EmailOn = NotificationKinds.Join(email);
                s.Digest = input.Digest;
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(SettingsDto(me, s, sender.Configured));
        });
    }

    static object SettingsDto(User me, NotificationSettings? s, bool emailAvailable)
    {
        var off = NotificationKinds.Split(s?.InAppOff);
        var email = s is null ? NotificationKinds.EmailByDefault : NotificationKinds.Split(s.EmailOn);
        return new
        {
            kinds = NotificationKinds.For(me.Role, me.IsPlatformAdmin).Select(k => new
            {
                k.Id, k.Label, k.Description, inApp = !off.Contains(k.Id), email = email.Contains(k.Id),
            }),
            digest = s?.Digest ?? false,
            emailAvailable,
            // Mail gaat alleen naar een bevestigd adres.
            emailVerified = me.EmailVerifiedAt is not null,
            email = me.Email,
        };
    }
}
