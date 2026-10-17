using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public record LoginRequest(string Email, string Password);
public record PasswordRequest(string Current, string New);
public record UserInput(string Name, string Email, string Role, bool Active, string? Password);
public record UserDto(int Id, int WorkspaceId, string Name, string Email, string Role, bool Active, DateTime CreatedAt, DateTime? LastLoginAt, bool EmailVerified, bool TwoFactor)
{
    // Alleen voor jezelf (zie AuthEndpoints.MeAsync): je moet eerst inloggen in twee stappen instellen voordat je verder kunt.
    public bool TwoFactorSetupRequired { get; init; }

    public static UserDto From(User u) => new(u.Id, u.WorkspaceId, u.Name, u.Email, u.Role, u.Active, u.CreatedAt, u.LastLoginAt, u.EmailVerifiedAt != null, u.TwoFactorEnabled);
}

// Begrenst mislukte inlogpogingen. Streng per e-mailadres en IP-adres samen: wie het wachtwoord raadt, zit na een paar
// fouten een minuut vast, maar de eigenaar op een ander adres kan gewoon inloggen. Daarnaast ruimer per e-mailadres over
// een uur, tegen raden vanaf steeds een ander IP-adres. Die ruime grens houdt alleen adressen tegen waarmee dit account
// nog nooit is ingelogd, zodat niemand een ander buiten kan sluiten door expres fouten te maken.
public sealed class LoginThrottle(IConfiguration config) : IDisposable
{
    public enum Block { None, ThisAddress, ThisAccount }

    readonly PartitionedRateLimiter<string> perAddress = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("RateLimit:AuthPerMinute", 10), Window = TimeSpan.FromMinutes(1),
        }));

    readonly PartitionedRateLimiter<string> perAccount = PartitionedRateLimiter.Create<string, string>(email =>
        RateLimitPartition.GetFixedWindowLimiter(email, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("RateLimit:LoginFailuresPerEmailPerHour", 50), Window = TimeSpan.FromHours(1),
        }));

    // Per e-mailadres de IP-adressen waarmee het laatst is ingelogd (hooguit een paar).
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> known = new();
    const int KnownPerAccount = 5;

    static string Pair(string email, string ip) => $"{email}|{ip}";

    static bool Exhausted(PartitionedRateLimiter<string> limiter, string key)
    {
        using var lease = limiter.AttemptAcquire(key, 0);
        return !lease.IsAcquired;
    }

    public Block Blocked(string email, string ip)
    {
        if (Exhausted(perAddress, Pair(email, ip))) return Block.ThisAddress;
        if (Exhausted(perAccount, email) && !(known.TryGetValue(email, out var ips) && ips.Contains(ip))) return Block.ThisAccount;
        return Block.None;
    }

    public void Failed(string email, string ip)
    {
        perAddress.AttemptAcquire(Pair(email, ip)).Dispose();
        perAccount.AttemptAcquire(email).Dispose();
    }

    public void Succeeded(string email, string ip) =>
        known.AddOrUpdate(email, [ip], (_, ips) => ips.Contains(ip) ? ips : [ip, .. ips.Take(KnownPerAccount - 1)]);

    public void Dispose()
    {
        perAddress.Dispose();
        perAccount.Dispose();
    }
}

public static class AuthEndpoints
{
    public const int MinPasswordLength = 8;
    static readonly PasswordHasher<User> Hasher = new();

    public static string Hash(User u, string password) => Hasher.HashPassword(u, password);

    public static bool PasswordMatches(User u, string? password) =>
        Hasher.VerifyHashedPassword(u, u.PasswordHash, password ?? "") != PasswordVerificationResult.Failed;

    // Jezelf, met of je eerst inloggen in twee stappen moet instellen.
    public static async Task<UserDto> MeAsync(AppDb db, User u) =>
        UserDto.From(u) with { TwoFactorSetupRequired = await Endpoints.TwoFactor.SetupRequiredAsync(db, u) };

    // Te veel mislukte pogingen voor dit e-mailadres, met het wachtwoord of met de code van inloggen in twee stappen.
    public static IResult? Blocked(LoginThrottle throttle, string email, string ip) => throttle.Blocked(email, ip) switch
    {
        LoginThrottle.Block.ThisAddress => Results.Json(new { error = "Te veel mislukte pogingen voor dit e-mailadres. Probeer het over een minuut opnieuw." }, statusCode: 429),
        LoginThrottle.Block.ThisAccount => Results.Json(new { error = "Er zijn te veel mislukte pogingen voor dit e-mailadres. Probeer het over een uur opnieuw, of log in vanaf een adres waar je eerder inlogde." }, statusCode: 429),
        _ => null,
    };

    public const string WorkspaceClaim = "potel:ws";
    public const string StampClaim = "potel:stamp";

    // Eén melding voor elk bezet adres, zodat niemand kan zien of een adres in een andere werkruimte bestaat.
    public const string EmailUnavailable = "Dit e-mailadres is niet beschikbaar. Kies een ander adres.";

    public static int? UserId(this ClaimsPrincipal p) =>
        int.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static int? WorkspaceId(this ClaimsPrincipal p) =>
        int.TryParse(p.FindFirstValue(WorkspaceClaim), out var id) ? id : null;

    public static async Task SignIn(HttpContext http, User u)
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new Claim(WorkspaceClaim, u.WorkspaceId.ToString()),
            new Claim(ClaimTypes.Name, u.Name),
            new Claim(ClaimTypes.Email, u.Email),
            new Claim(ClaimTypes.Role, u.Role),
            new Claim(StampClaim, u.SecurityStamp),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    public static void MapAuth(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth");

        g.MapPost("/login", async (AppDb db, HttpContext http, LoginThrottle throttle, TwoFactor twoFactor, LoginRequest req) =>
        {
            var email = (req.Email ?? "").Trim().ToLower();
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "onbekend";
            if (Blocked(throttle, email, ip) is { } blocked) return blocked;
            // Inloggen gebeurt voordat de werkruimte bekend is; het e-mailadres is uniek over alle werkruimtes.
            var u = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Email == email);
            var ok = u is not null && u.Active && PasswordMatches(u, req.Password);
            if (!ok)
            {
                throttle.Failed(email, ip);
                return Results.Json(new { error = "E-mailadres of wachtwoord klopt niet" }, statusCode: 401);
            }
            db.Tenant.WorkspaceId = u!.WorkspaceId;
            // Met inloggen in twee stappen ben je na het wachtwoord nog niet binnen: eerst de code (zie TwoFactorEndpoints),
            // behalve op een apparaat dat je liet onthouden. Een oude sessie in deze browser stopt.
            if (u.TwoFactorEnabled && !twoFactor.IsRemembered(http, u))
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                twoFactor.StartPendingLogin(http, u);
                return Results.Ok(new { twoFactorRequired = true });
            }
            throttle.Succeeded(email, ip);
            u.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await SignIn(http, u);
            return Results.Ok(await MeAsync(db, u));
        }).AllowAnonymous().RequireRateLimiting("auth");

        g.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).AllowAnonymous();

        g.MapGet("/me", async (AppDb db, ClaimsPrincipal user) =>
            user.UserId() is { } id && await db.Users.FindAsync(id) is { Active: true } u
                ? Results.Ok(await MeAsync(db, u)) : Results.Unauthorized());

        // Een nieuw wachtwoord logt je overal anders uit en maakt open herstellinks ongeldig; deze sessie krijgt een nieuwe cookie en blijft ingelogd.
        g.MapPut("/password", async (AppDb db, HttpContext http, ClaimsPrincipal user, PasswordRequest req) =>
        {
            var u = user.UserId() is { } id ? await db.Users.FindAsync(id) : null;
            if (u is null) return Results.Unauthorized();
            if (!PasswordMatches(u, req.Current))
                return Results.BadRequest(new { error = "Je huidige wachtwoord klopt niet" });
            if ((req.New ?? "").Length < MinPasswordLength)
                return Results.BadRequest(new { error = $"Kies een wachtwoord van minstens {MinPasswordLength} tekens" });
            u.PasswordHash = Hash(u, req.New!);
            u.NewSecurityStamp();
            await AccountMail.RevokeAsync(db, u.Id, TokenPurposes.PasswordReset);
            await db.SaveChangesAsync();
            await SignIn(http, u);
            return Results.NoContent();
        }).RequireRateLimiting("accounts");
    }

    public static void MapUsers(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/users").RequireAuthorization(p => p.RequireRole(Roles.Admin));

        g.MapGet("/", async (AppDb db) => (await db.Users.OrderBy(u => u.Name).ToListAsync()).Select(UserDto.From));

        // Toevoegen en wijzigen is begrensd per werkruimte, zodat niemand zo e-mailadressen kan aftasten.
        g.MapPost("/", async (AppDb db, UserInput input) =>
        {
            var email = (input.Email ?? "").Trim().ToLower();
            if (string.IsNullOrWhiteSpace(input.Name) || !email.Contains('@')) return Results.BadRequest(new { error = "Vul een naam en een geldig e-mailadres in" });
            if (!Roles.All.Contains(input.Role)) return Results.BadRequest(new { error = "Onbekende rol" });
            if ((input.Password ?? "").Length < MinPasswordLength) return Results.BadRequest(new { error = $"Kies een wachtwoord van minstens {MinPasswordLength} tekens" });
            if (await UserLimitReached(db) is { } full) return full;
            if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email)) return Results.Conflict(new { error = EmailUnavailable });
            var u = new User { Name = input.Name.Trim(), Email = email, Role = input.Role, Active = input.Active };
            u.PasswordHash = Hash(u, input.Password!);
            db.Users.Add(u);
            db.Log("gebruiker", $"Gebruiker {u.Name} toegevoegd");
            await db.SaveChangesAsync();
            return Results.Created($"/api/users/{u.Id}", UserDto.From(u));
        }).RequireRateLimiting("accounts");

        g.MapPut("/{id:int}", async (AppDb db, HttpContext http, AccountMail mail, ClaimsPrincipal me, int id, UserInput input) =>
        {
            var u = await db.Users.FindAsync(id);
            if (u is null) return Results.NotFound();
            var email = (input.Email ?? "").Trim().ToLower();
            if (string.IsNullOrWhiteSpace(input.Name) || !email.Contains('@')) return Results.BadRequest(new { error = "Vul een naam en een geldig e-mailadres in" });
            if (!Roles.All.Contains(input.Role)) return Results.BadRequest(new { error = "Onbekende rol" });
            // Eerst de regels binnen de eigen werkruimte, pas daarna of het adres vrij is.
            if (me.UserId() == id && !input.Active) return Results.BadRequest(new { error = "Je kunt jezelf niet uitschakelen" });
            var losesAdmin = u.Role == Roles.Admin && u.Active && (input.Role != Roles.Admin || !input.Active);
            if (losesAdmin && !await db.Users.AnyAsync(x => x.Id != id && x.Role == Roles.Admin && x.Active))
                return Results.BadRequest(new { error = "Er moet minstens één actieve beheerder overblijven" });
            if (!string.IsNullOrEmpty(input.Password) && input.Password.Length < MinPasswordLength)
                return Results.BadRequest(new { error = $"Kies een wachtwoord van minstens {MinPasswordLength} tekens" });
            // Andere rol, ander adres, nieuw wachtwoord of uitgeschakeld: de oude sessies van deze gebruiker stoppen meteen.
            var securityChanged = email != u.Email || input.Role != u.Role || input.Active != u.Active || !string.IsNullOrEmpty(input.Password);
            if (securityChanged && await ProtectedPlatformAdmin(db, me, u) is { } refused) return refused;
            if (email != u.Email && await db.Users.IgnoreQueryFilters().AnyAsync(x => x.Email == email && x.Id != id))
                return Results.Conflict(new { error = EmailUnavailable });

            if (!string.IsNullOrEmpty(input.Password)) u.PasswordHash = Hash(u, input.Password);
            // Uitgeschakeld: ook de agendalink werkt niet meer. Na weer inschakelen maakt hij zelf een nieuwe.
            if (u.Active && !input.Active)
                await db.CalendarFeeds.Where(f => f.UserId == id).ExecuteUpdateAsync(x => x.SetProperty(f => f.TokenHash, (string?)null));
            // Een nieuw adres moet opnieuw bevestigd worden, en links naar het oude adres werken niet meer.
            var emailChanged = email != u.Email;
            if (emailChanged) u.EmailVerifiedAt = null;
            if (emailChanged || !string.IsNullOrEmpty(input.Password)) await AccountMail.RevokeAsync(db, u.Id, emailChanged ? null : TokenPurposes.PasswordReset);
            u.Name = input.Name.Trim(); u.Email = email; u.Role = input.Role; u.Active = input.Active;
            if (securityChanged) u.NewSecurityStamp();
            await db.SaveChangesAsync();
            // Pas je jezelf aan, dan blijf je met een nieuwe cookie ingelogd. Een nieuw eigen adres krijgt meteen een bevestigingsmail.
            if (securityChanged && me.UserId() == id) await SignIn(http, u);
            if (emailChanged && me.UserId() == id) await mail.SendVerificationAsync(u, http.Request);
            return Results.Ok(UserDto.From(u));
        }).RequireRateLimiting("accounts");

        // Telefoon kwijt en geen herstelcodes meer: een beheerder zet inloggen in twee stappen uit voor een medewerker in zijn eigen
        // werkruimte. Die moet daarna opnieuw inloggen (nieuwe SecurityStamp) en kan het opnieuw instellen. Een beheerder zet het
        // alleen voor zichzelf uit, met wachtwoord en code; een platformbeheerder alleen zelf of via een andere platformbeheerder.
        g.MapDelete("/{id:int}/two-factor", async (AppDb db, ClaimsPrincipal me, ILogger<TwoFactor> logger, int id) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            var u = await db.Users.FindAsync(id);
            if (u is null) return Results.NotFound();
            if (me.UserId() == id)
                return Results.BadRequest(new { error = "Zet inloggen in twee stappen voor jezelf uit bij Instellingen, Account, met je wachtwoord en een code." });
            if (u.Role != Roles.Employee)
                return Results.Json(new { error = "Je kunt inloggen in twee stappen alleen opnieuw instellen voor een medewerker. Een beheerder zet het zelf uit." },
                    statusCode: StatusCodes.Status403Forbidden);
            if (await ProtectedPlatformAdmin(db, me, u, PlatformAdminTwoFactorProtected) is { } refused) return refused;
            if (!u.TwoFactorEnabled && u.TwoFactorPendingSecret is null)
                return Results.BadRequest(new { error = $"Inloggen in twee stappen staat voor {u.Name} niet aan." });
            await TwoFactor.ClearAsync(db, u);
            u.NewSecurityStamp();
            var by = me.UserId() is { } myId ? (await db.Users.FindAsync(myId))?.Name : null;
            db.Log("gebruiker", $"Inloggen in twee stappen van {u.Name} uitgezet door {by ?? "een beheerder"}; {u.Name} moet opnieuw inloggen");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            logger.LogInformation("Inloggen in twee stappen van gebruiker {UserId} uitgezet door gebruiker {ById} in werkruimte {WorkspaceId}", u.Id, me.UserId(), u.WorkspaceId);
            return Results.Ok(UserDto.From(u));
        }).RequireRateLimiting("accounts");

        g.MapDelete("/{id:int}", async (AppDb db, ClaimsPrincipal me, int id) =>
        {
            var u = await db.Users.FindAsync(id);
            if (u is null) return Results.NotFound();
            if (me.UserId() == id) return Results.BadRequest(new { error = "Je kunt jezelf niet verwijderen" });
            if (u.Role == Roles.Admin && !await db.Users.AnyAsync(x => x.Id != id && x.Role == Roles.Admin && x.Active))
                return Results.BadRequest(new { error = "Er moet minstens één actieve beheerder overblijven" });
            if (await ProtectedPlatformAdmin(db, me, u) is { } refused) return refused;
            db.AccountTokens.RemoveRange(db.AccountTokens.Where(t => t.UserId == id));
            db.RecoveryCodes.RemoveRange(db.RecoveryCodes.Where(r => r.UserId == id));
            // Zijn agendalink werkt niet meer, en wat voor hem was ingepland, is daarna voor iedereen.
            db.CalendarFeeds.RemoveRange(db.CalendarFeeds.Where(f => f.UserId == id));
            await db.Appointments.Where(a => a.UserId == id).ExecuteUpdateAsync(x => x.SetProperty(a => a.UserId, (int?)null));
            db.Users.Remove(u);
            db.Log("gebruiker", $"Gebruiker {u.Name} verwijderd");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    public const string PlatformAdminTwoFactorProtected =
        "Dit account beheert ook het platform. Daarom kun je inloggen in twee stappen voor dit account niet opnieuw instellen. Vraag het aan een platformbeheerder.";

    public const string PlatformAdminProtected =
        "Dit account beheert ook het platform. Daarom kun je het e-mailadres, het wachtwoord en de rol niet wijzigen, en het account niet uitschakelen of verwijderen. Vraag een platformbeheerder eerst die rechten weg te halen.";

    // Een platformbeheerder kan alle werkruimtes zien en abonnementen omzetten. Een beheerder van zijn werkruimte mag dat account
    // dus niet overnemen (nieuw wachtwoord of e-mailadres, inloggen in twee stappen uitzetten), uitschakelen of verwijderen;
    // alleen hijzelf of een andere platformbeheerder.
    static async Task<IResult?> ProtectedPlatformAdmin(AppDb db, ClaimsPrincipal me, User target, string message = PlatformAdminProtected) =>
        target.IsPlatformAdmin && me.UserId() != target.Id && !await PlatformEndpoints.IsPlatformAdmin(db, me)
            ? Results.Json(new { error = message }, statusCode: StatusCodes.Status403Forbidden)
            : null;

    // Het abonnement bepaalt hoeveel gebruikers er in een werkruimte passen (zie Plans.MaxUsers).
    static async Task<IResult?> UserLimitReached(AppDb db)
    {
        var plan = (await db.Workspaces.FindAsync(db.TenantId))?.Plan ?? Plans.Trial;
        var max = Plans.MaxUsers(plan);
        if (await db.Users.CountAsync() < max) return null;
        var room = max == 1 ? "1 gebruiker" : $"{max} gebruikers";
        return Results.Json(new { error = $"Je abonnement heeft ruimte voor {room}. Verwijder eerst een gebruiker of kies een groter abonnement." },
            statusCode: StatusCodes.Status402PaymentRequired);
    }
}
