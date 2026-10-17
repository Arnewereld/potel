using System.Security.Claims;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public record SwitchWorkspaceRequest(int WorkspaceId);
public record InviteTokenRequest(string Token);
public record AcceptInviteRequest(string Token, string? Password);

// Wat een boekhouder daarnaast mag, als een beheerder het hem bij Gebruikers toestaat. Verder niets.
public static class AccountantPermissions
{
    public const string VatFiling = "btw-aangifte";
    public const string CompleteExpenses = "kosten-aanvullen";

    public static bool Has(User u, string permission) => permission switch
    {
        VatFiling => u.CanFileVat,
        CompleteExpenses => u.CanCompleteExpenses,
        _ => false,
    };
}

// Een markering op een endpoint: dit mag een boekhouder. Zonder markering mag een boekhouder niets (zie Accountants.GateAsync),
// ook niet wat hier later bijkomt. Read geldt alleen voor GET: staat het op een groep, dan blijven de wijzigingen erin dicht.
// Personal is voor je eigen account (wachtwoord, inloggen in twee stappen, je eigen meldingen, wisselen van werkruimte).
// Permission is een wijziging die alleen mag als de beheerder die toestemming gaf.
public sealed class AccountantAccess
{
    public enum Kinds { Read, Personal, Permission }

    public static readonly AccountantAccess Read = new(Kinds.Read, null);
    public static readonly AccountantAccess Personal = new(Kinds.Personal, null);

    public Kinds Kind { get; }
    public string? Permission { get; }

    AccountantAccess(Kinds kind, string? permission)
    {
        Kind = kind;
        Permission = permission;
    }

    public static AccountantAccess For(string permission) => new(Kinds.Permission, permission);

    public bool Allows(HttpRequest r, User u) => Kind switch
    {
        Kinds.Read => HttpMethods.IsGet(r.Method) || HttpMethods.IsHead(r.Method),
        Kinds.Personal => true,
        Kinds.Permission => Permission is not null && AccountantPermissions.Has(u, Permission),
        _ => false,
    };
}

// Een download (een export) die we in het activiteitenlog zetten als een boekhouder hem doet, met wat het was.
public sealed class AccountantExport(Func<HttpContext, AppDb, Task<string>> describe)
{
    public Func<HttpContext, AppDb, Task<string>> Describe { get; } = describe;
}

public static class AccountantEndpointExtensions
{
    // Een boekhouder mag dit bekijken (alleen GET).
    public static TBuilder ForAccountant<TBuilder>(this TBuilder b) where TBuilder : IEndpointConventionBuilder =>
        b.WithMetadata(AccountantAccess.Read);

    // Een boekhouder mag dit ook wijzigen, als de beheerder hem deze toestemming gaf.
    public static TBuilder ForAccountant<TBuilder>(this TBuilder b, string permission) where TBuilder : IEndpointConventionBuilder =>
        b.WithMetadata(AccountantAccess.For(permission));

    // Over je eigen account: dat mag een boekhouder net als iedereen.
    public static TBuilder ForAccountantPersonal<TBuilder>(this TBuilder b) where TBuilder : IEndpointConventionBuilder =>
        b.WithMetadata(AccountantAccess.Personal);

    // Een export die een boekhouder mag downloaden; die komt in het activiteitenlog.
    public static TBuilder AccountantExport<TBuilder>(this TBuilder b, Func<HttpContext, AppDb, Task<string>> describe) where TBuilder : IEndpointConventionBuilder =>
        b.WithMetadata(AccountantAccess.Read, new AccountantExport(describe));

    // Alleen een beheerder, ook in een groep die een boekhouder mag bekijken.
    public static TBuilder AdminOnly<TBuilder>(this TBuilder b) where TBuilder : IEndpointConventionBuilder =>
        b.RequireAuthorization(p => p.RequireRole(Roles.Admin));

    // Een beheerder, of een boekhouder voor wat met ForAccountant gemarkeerd is.
    public static TBuilder AdminOrAccountant<TBuilder>(this TBuilder b) where TBuilder : IEndpointConventionBuilder =>
        b.RequireAuthorization(p => p.RequireRole(Roles.Admin, Roles.Accountant));
}

// Een boekhouder: kijkt mee in de geldzaken van een werkruimte en kan niets wijzigen. Hij wordt per e-mail uitgenodigd en kiest zelf
// zijn wachtwoord; een beheerder kan dat wachtwoord nooit instellen of zien. Is hetzelfde adres boekhouder in meer werkruimtes, dan
// is dat één persoon met één account: in elke werkruimte staat een eigen rij (met eigen rechten, stempel en laatste keer ingelogd),
// maar het wachtwoord, inloggen in twee stappen en de herstelcodes zijn overal gelijk (zie SyncAsync). Na het inloggen wisselt hij
// van werkruimte met een nieuwe sessie voor die werkruimte; gegevens van twee werkruimtes komen nooit in één sessie.
public static class Accountants
{
    // Wat een boekhouder nergens in een antwoord ziet, ook niet als een klant of lead in een factuur of offerte zit:
    // je eigen notities over een klant, en van een lead de waarde, status, bron en notities uit je verkoop.
    static readonly Dictionary<Type, string[]> Hidden = new()
    {
        [typeof(Customer)] = ["notes"],
        [typeof(Lead)] = ["notes", "value", "status", "source"],
    };

    public static void HideFromAccountants(JsonTypeInfo info)
    {
        if (!Hidden.TryGetValue(info.Type, out var names)) return;
        foreach (var p in info.Properties)
            if (names.Contains(p.Name)) p.ShouldSerialize = (_, _) => !ViewerIsAccountant();
    }

    // HttpContextAccessor leest de huidige aanvraag uit een AsyncLocal; een nieuw exemplaar ziet dezelfde.
    static bool ViewerIsAccountant() => new HttpContextAccessor().HttpContext?.User.IsInRole(Roles.Accountant) == true;

    public const string Forbidden = "Als boekhouder kun je dit niet doen. Je kunt de administratie bekijken en exporteren, maar niets wijzigen.";
    public const string InviteInvalid = "Deze uitnodiging is verlopen, al gebruikt of ingetrokken. Vraag je klant om je opnieuw uit te nodigen.";
    public const string NoAccess = "Je hebt geen toegang (meer) tot deze werkruimte.";

    // De andere rijen van dezelfde boekhouder: hetzelfde adres in andere werkruimtes, met een aangenomen uitnodiging.
    public static IQueryable<User> Family(AppDb db, User u) => db.Users.IgnoreQueryFilters()
        .Where(x => x.Email == u.Email && x.Role == Roles.Accountant && !x.InvitePending && x.Id != u.Id);

    // Het account om mee in te loggen uit de rijen met dit adres. Bij een boekhouder in meer werkruimtes: waar hij het laatst was.
    public static User? PickLogin(List<User> candidates) =>
        candidates.FirstOrDefault(u => u.Role != Roles.Accountant)
        ?? candidates.Where(u => u.Role == Roles.Accountant).OrderByDescending(u => u.LastLoginAt ?? DateTime.MinValue).ThenBy(u => u.Id).FirstOrDefault();

    // Kopieert de herstelcodes van de ene rij naar de andere, ook in een andere werkruimte. Bij een boekhouder hangt de hash niet
    // aan de rij maar aan het adres (zie TwoFactor.RecoveryHash), dus dezelfde codes werken overal.
    static Task<int> CopyRecoveryCodesAsync(AppDb db, int fromUserId, int toUserId, int toWorkspaceId) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "RecoveryCodes" ("WorkspaceId", "UserId", "CodeHash", "CreatedAt", "UsedAt")
            SELECT {toWorkspaceId}, {toUserId}, "CodeHash", "CreatedAt", "UsedAt" FROM "RecoveryCodes" WHERE "UserId" = {fromUserId}
            """);

    // Na een nieuw wachtwoord of een andere stand van inloggen in twee stappen bij een boekhouder: dezelfde inloggegevens in elke
    // werkruimte waar hij toegang heeft, met overal een nieuwe stempel, zodat zijn andere sessies stoppen. Roep dit aan nadat de
    // wijzigingen aan u zijn opgeslagen, binnen dezelfde transactie.
    public static async Task SyncAsync(AppDb db, User u)
    {
        if (u.Role != Roles.Accountant || u.InvitePending) return;
        var others = await Family(db, u).Select(x => new { x.Id, x.WorkspaceId }).ToListAsync();
        if (others.Count == 0) return;
        var ids = others.Select(x => x.Id).ToList();
        var stamp = Guid.NewGuid().ToString("N");
        await db.Users.IgnoreQueryFilters().Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.PasswordHash, u.PasswordHash)
            .SetProperty(x => x.TwoFactorSecret, u.TwoFactorSecret)
            .SetProperty(x => x.TwoFactorPendingSecret, (string?)null)
            .SetProperty(x => x.TwoFactorEnabledAt, u.TwoFactorEnabledAt)
            .SetProperty(x => x.TwoFactorLastStep, u.TwoFactorLastStep)
            .SetProperty(x => x.EmailVerifiedAt, u.EmailVerifiedAt)
            .SetProperty(x => x.SecurityStamp, stamp));
        await db.RecoveryCodes.IgnoreQueryFilters().Where(r => ids.Contains(r.UserId)).ExecuteDeleteAsync();
        foreach (var other in others) await CopyRecoveryCodesAsync(db, u.Id, other.Id, other.WorkspaceId);
    }

    // Een open link om het wachtwoord te herstellen werkt na een nieuw wachtwoord nergens meer.
    public static async Task RevokeResetsAsync(AppDb db, User u)
    {
        if (u.Role != Roles.Accountant) return;
        var ids = await Family(db, u).Select(x => x.Id).ToListAsync();
        var now = DateTime.UtcNow;
        await db.AccountTokens.IgnoreQueryFilters().Where(t => ids.Contains(t.UserId) && t.UsedAt == null && t.Purpose == TokenPurposes.PasswordReset)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now));
    }

    // Een code uit de app werkt één keer, ook als je daarna in een andere werkruimte inlogt.
    public static async Task UseStepAsync(AppDb db, User u, long step)
    {
        if (u.Role != Roles.Accountant) return;
        await Family(db, u).Where(x => x.TwoFactorLastStep == null || x.TwoFactorLastStep < step)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TwoFactorLastStep, step));
    }

    // Een gebruikte herstelcode werkt in geen enkele werkruimte meer.
    public static async Task UseRecoveryCodeAsync(AppDb db, User u, string hash, DateTime now)
    {
        if (u.Role != Roles.Accountant) return;
        var ids = await Family(db, u).Select(x => x.Id).ToListAsync();
        await db.RecoveryCodes.IgnoreQueryFilters().Where(r => ids.Contains(r.UserId) && r.CodeHash == hash && r.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.UsedAt, now));
    }

    public record WorkspaceChoice(int WorkspaceId, string Name, bool Current, DateTime? LastLoginAt);

    // De werkruimtes waar je naartoe kunt: voor een boekhouder elke werkruimte waar hij actief toegang heeft, voor anderen alleen de eigen.
    public static async Task<List<WorkspaceChoice>> WorkspacesAsync(AppDb db, User me)
    {
        var rows = new List<User> { me };
        if (me.Role == Roles.Accountant) rows.AddRange(await Family(db, me).Where(x => x.Active).AsNoTracking().ToListAsync());
        var ids = rows.Select(r => r.WorkspaceId).ToList();
        var names = await db.Workspaces.AsNoTracking().Where(w => ids.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name);
        return rows.Where(r => names.ContainsKey(r.WorkspaceId))
            .Select(r => new WorkspaceChoice(r.WorkspaceId, names[r.WorkspaceId], r.Id == me.Id, r.LastLoginAt))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Een boekhouder mag een verzoek alleen doen met een markering die het toestaat. Openbare pagina's en inloggen werken gewoon.
    // Daarna: een export komt in het activiteitenlog van de werkruimte.
    public static async Task GateAsync(HttpContext http, Func<Task> next)
    {
        if (!http.User.IsInRole(Roles.Accountant) || !http.Request.Path.StartsWithSegments("/api"))
        {
            await next();
            return;
        }
        var endpoint = http.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await next();
            return;
        }
        var db = http.RequestServices.GetRequiredService<AppDb>();
        var me = http.User.UserId() is { } id ? await db.Users.FindAsync(id) : null;
        var rules = endpoint?.Metadata.GetOrderedMetadata<AccountantAccess>() ?? [];
        if (me is not { Role: Roles.Accountant } || !rules.Any(r => r.Allows(http.Request, me)))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { error = Forbidden, accountant = true });
            return;
        }
        var export = endpoint!.Metadata.GetMetadata<AccountantExport>();
        await next();
        if (export is null || http.Response.StatusCode != StatusCodes.Status200OK) return;
        try
        {
            using var scope = http.RequestServices.CreateScope();
            var log = scope.ServiceProvider.GetRequiredService<AppDb>();
            log.Tenant.WorkspaceId = db.TenantId;
            log.Log("export", $"{me.Name} (boekhouder) heeft {await export.Describe(http, log)} gedownload");
            await log.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            http.RequestServices.GetRequiredService<ILogger<AccountantExport>>().LogError(ex, "Export van boekhouder {UserId} niet in het activiteitenlog gezet", me.Id);
        }
    }

    public static int RouteId(HttpContext http) => int.TryParse(http.Request.RouteValues["id"]?.ToString(), out var id) ? id : 0;

    // Wat er gedownload werd, zoals het in het activiteitenlog komt.
    public static async Task<string> DescribeHoursExport(HttpContext http, AppDb db)
    {
        var id = RouteId(http);
        var number = await db.Invoices.Where(i => i.Id == id).Select(i => i.Number).FirstOrDefaultAsync();
        return $"de urenspecificatie van factuur {number ?? "(concept)"} als CSV";
    }

    public static async Task<string> DescribeReceiptExport(HttpContext http, AppDb db)
    {
        var id = RouteId(http);
        var e = await db.Expenses.Where(x => x.Id == id).Select(x => new { x.Supplier, x.Date }).FirstOrDefaultAsync();
        return e is null ? "een bonnetje" : $"het bonnetje bij {e.Supplier} van {e.Date:dd-MM-yyyy}";
    }

    public static Task<string> DescribeAnnualExport(HttpContext http, AppDb db)
    {
        var year = int.TryParse(http.Request.Query["year"], out var y) ? y : http.RequestServices.GetRequiredService<BusinessClock>().Today.Year;
        return Task.FromResult($"het jaaroverzicht {year} als CSV");
    }

    public static void MapAccountants(this RouteGroupBuilder api)
    {
        // Waar je naartoe kunt wisselen. Ook voor andere rollen, dan alleen de eigen werkruimte.
        api.MapGet("/auth/workspaces", async (AppDb db, ClaimsPrincipal user) =>
            user.UserId() is { } id && await db.Users.FindAsync(id) is { } me ? Results.Ok(await WorkspacesAsync(db, me)) : Results.Unauthorized())
            .ForAccountant();

        // Wisselen naar een andere werkruimte waar je boekhouder bent: een nieuwe sessie voor die rij, met de rol, stempel en
        // werkruimte van daar. Alleen naar een rij met hetzelfde adres die actief is en waarvan de uitnodiging is aangenomen; is
        // je toegang daar weg of uitgeschakeld, dan kan het niet meer.
        api.MapPost("/auth/switch", async (AppDb db, HttpContext http, ClaimsPrincipal user, SwitchWorkspaceRequest req) =>
        {
            var me = user.UserId() is { } id ? await db.Users.FindAsync(id) : null;
            if (me is null) return Results.Unauthorized();
            if (me.Role != Roles.Accountant) return Results.Json(new { error = "Alleen een boekhouder kan wisselen tussen werkruimtes." }, statusCode: StatusCodes.Status403Forbidden);
            if (req.WorkspaceId == me.WorkspaceId) return Results.Ok(await AuthEndpoints.MeAsync(db, me));
            var target = await Family(db, me).FirstOrDefaultAsync(x => x.WorkspaceId == req.WorkspaceId && x.Active);
            if (target is null || !await db.Workspaces.AnyAsync(w => w.Id == req.WorkspaceId))
                return Results.Json(new { error = NoAccess }, statusCode: StatusCodes.Status404NotFound);
            db.Tenant.WorkspaceId = target.WorkspaceId;
            target.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await AuthEndpoints.SignIn(http, target);
            return Results.Ok(await AuthEndpoints.MeAsync(db, target));
        }).ForAccountantPersonal().RequireRateLimiting("accounts");

        // Wat er in een uitnodiging staat, voor de pagina achter de link: van welke werkruimte, en of je al een account hebt.
        api.MapPost("/auth/invite", async (AppDb db, AccountMail mail, InviteTokenRequest req) =>
        {
            if (await mail.FindAsync(req.Token, TokenPurposes.Invite) is not { Token: var t, User: { Role: Roles.Accountant, InvitePending: true, Active: true } u }
                || t.UsedAt is not null || t.ExpiresAt < DateTime.UtcNow)
                return Results.BadRequest(new { error = InviteInvalid });
            var workspace = (await db.Workspaces.FindAsync(u.WorkspaceId))?.Name ?? "";
            return Results.Ok(new { workspace, name = u.Name, email = u.Email, existingAccount = await Family(db, u).AnyAsync() });
        }).AllowAnonymous().RequireRateLimiting("auth");

        // De uitnodiging aannemen. Met een bestaand account als boekhouder komt deze werkruimte erbij, met hetzelfde wachtwoord en
        // dezelfde inloggen in twee stappen; je logt daarna gewoon in. Anders kies je hier je wachtwoord en ben je meteen binnen.
        // Eén schrijver tegelijk: zo krijgen twee uitnodigingen die tegelijk worden aangenomen nooit twee verschillende wachtwoorden.
        api.MapPost("/auth/invite/accept", async (AppDb db, AccountMail mail, HttpContext http, AcceptInviteRequest req) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            if (await mail.FindAsync(req.Token, TokenPurposes.Invite) is not { User: { Role: Roles.Accountant, InvitePending: true, Active: true } u } found)
                return Results.BadRequest(new { error = InviteInvalid });
            var source = await Family(db, u).OrderByDescending(x => x.LastLoginAt).ThenBy(x => x.Id).FirstOrDefaultAsync();
            if (source is null && (req.Password ?? "").Length < AuthEndpoints.MinPasswordLength)
                return Results.BadRequest(new { error = $"Kies een wachtwoord van minstens {AuthEndpoints.MinPasswordLength} tekens" });
            if (!await mail.ClaimAsync(found.Token)) return Results.BadRequest(new { error = InviteInvalid });
            u.InvitePending = false;
            // De link kwam aan in deze inbox, dus het adres klopt.
            u.EmailVerifiedAt = DateTime.UtcNow;
            if (source is not null)
            {
                u.PasswordHash = source.PasswordHash;
                u.TwoFactorSecret = source.TwoFactorSecret;
                u.TwoFactorEnabledAt = source.TwoFactorEnabledAt;
                u.TwoFactorLastStep = source.TwoFactorLastStep;
            }
            else
            {
                u.PasswordHash = AuthEndpoints.Hash(u, req.Password!);
                u.LastLoginAt = DateTime.UtcNow;
            }
            u.NewSecurityStamp();
            await AccountMail.RevokeAsync(db, u.Id, TokenPurposes.Invite);
            db.Log("gebruiker", $"{u.Name} heeft de uitnodiging als boekhouder aangenomen");
            await db.SaveChangesAsync();
            if (source is not null) await CopyRecoveryCodesAsync(db, source.Id, u.Id, u.WorkspaceId);
            await tx.CommitAsync();
            var workspace = (await db.Workspaces.FindAsync(u.WorkspaceId))?.Name ?? "";
            if (source is not null)
            {
                // Een sessie in deze browser hoort misschien bij iemand anders; inloggen doe je zelf, met je eigen wachtwoord.
                return Results.Ok(new { signedIn = false, workspace, email = u.Email });
            }
            await AuthEndpoints.SignIn(http, u);
            return Results.Ok(new { signedIn = true, workspace, email = u.Email, user = await AuthEndpoints.MeAsync(db, u) });
        }).AllowAnonymous().RequireRateLimiting("auth");

        // De uitnodiging opnieuw sturen, met een nieuwe link. De oude werkt dan niet meer.
        api.MapPost("/users/{id:int}/invite", async (AppDb db, AccountMail mail, HttpContext http, ClaimsPrincipal me, int id) =>
        {
            var u = await db.Users.FindAsync(id);
            if (u is null) return Results.NotFound();
            if (u.Role != Roles.Accountant || !u.InvitePending) return Results.BadRequest(new { error = "Deze gebruiker heeft de uitnodiging al aangenomen." });
            var by = me.UserId() is { } myId ? (await db.Users.FindAsync(myId))?.Name : null;
            return await SendInviteAsync(db, mail, http.Request, u, by) is { } problem ? problem : Results.Ok(UserDto.From(u));
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin)).RequireRateLimiting("accounts");
    }

    // Stuurt de uitnodiging. Null als dat lukte (of in ontwikkelmodus zonder mailserver, met de link in het log), anders de melding.
    public static async Task<IResult?> SendInviteAsync(AppDb db, AccountMail mail, HttpRequest request, User u, string? by)
    {
        var workspace = (await db.Workspaces.FindAsync(u.WorkspaceId))?.Name ?? "een klant";
        return await mail.SendInviteAsync(u, workspace, by, request) switch
        {
            MailOutcome.Sent => null,
            MailOutcome.NotConfigured when mail.DevelopmentLog => null,
            MailOutcome.NotConfigured => Results.Json(new { error = mail.NotConfiguredMessage("uitnodiging") }, statusCode: StatusCodes.Status503ServiceUnavailable),
            MailOutcome.Throttled => Results.Json(new { error = "Er zijn net al een paar uitnodigingen naar dit adres gegaan. Probeer het over een uur opnieuw." },
                statusCode: StatusCodes.Status429TooManyRequests),
            _ => Results.Json(new { error = "De uitnodiging kon niet worden verstuurd. Probeer het later opnieuw." }, statusCode: StatusCodes.Status503ServiceUnavailable),
        };
    }
}
