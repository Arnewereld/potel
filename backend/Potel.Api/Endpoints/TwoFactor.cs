using System.Buffers.Binary;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public record TwoFactorPasswordRequest(string Password);
public record TwoFactorCodeRequest(string Code);
public record TwoFactorDisableRequest(string Password, string Code);
public record TwoFactorLoginRequest(string Code, bool RememberDevice);
public record TwoFactorPolicyRequest(bool RequireForAdmins);

// Tijdgebonden codes volgens RFC 6238 (TOTP): HMAC-SHA1, 6 cijfers, elke 30 seconden een nieuwe. Zo werken Google Authenticator,
// Microsoft Authenticator, 1Password, Bitwarden en de meeste andere apps.
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int SecretBytes = 20;

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step, int digits = Digits)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0F;
        var binary = (hash[offset] & 0x7F) << 24 | hash[offset + 1] << 16 | hash[offset + 2] << 8 | hash[offset + 3];
        var modulo = 1;
        for (var i = 0; i < digits; i++) modulo *= 10;
        return (binary % modulo).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    // De tijdstap waar de code bij hoort: de huidige, de vorige of de volgende, voor een telefoon waarvan de klok iets voor- of
    // achterloopt. Alle drie worden altijd vergeleken, in vaste tijd. Null als de code niet klopt.
    public static long? Match(byte[] secret, string code, DateTimeOffset now)
    {
        if (code.Length != Digits || !code.All(char.IsAsciiDigit)) return null;
        var given = Encoding.ASCII.GetBytes(code);
        var current = StepAt(now);
        long? found = null;
        for (var step = current - 1; step <= current + 1; step++)
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), given) && found is null) found = step;
        return found;
    }
}

// Base32 volgens RFC 4648, zonder opvulling: zo staat het geheim in de instelsleutel en in de otpauth-link.
public static class Base32
{
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
}

public enum CodeKind { None, App, Recovery }

public record PendingLogin(int UserId, int WorkspaceId, string Stamp);

// Inloggen in twee stappen. Het geheim staat versleuteld in de database (Data Protection, net als het GitHub-token).
// Na een goed wachtwoord krijgt de browser alleen een kort geldig, ondertekend koekje voor de tweede stap (PendingCookie): dat is
// geen sessie, de inlogcookie komt pas na een goede code. "Dit apparaat onthouden" is een apart ondertekend koekje, gebonden
// aan de gebruiker en zijn SecurityStamp: een nieuw wachtwoord of een andere stand van inloggen in twee stappen maakt het ongeldig.
public class TwoFactor(IDataProtectionProvider dp, IConfiguration config, IWebHostEnvironment env, BusinessClock clock)
{
    public const string SecretPurpose = "Potel.TwoFactor.Secret.v1";
    public const string PendingPurpose = "Potel.TwoFactor.PendingLogin.v1";
    public const string RememberPurpose = "Potel.TwoFactor.RememberDevice.v1";
    public const string PendingCookie = "potel.2fa";
    public const string RememberCookie = "potel.2fa.device";
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RememberLifetime = TimeSpan.FromDays(30);
    public const int RecoveryCodeCount = 10;
    public const int RecoveryCodeLength = 12;
    // Zonder 0, o, 1, i en l, zodat je een code niet verkeerd overneemt van papier.
    const string RecoveryAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";
    // Op het verzoek gezet als deze beheerder eerst inloggen in twee stappen moet instellen (zie Program.cs).
    public const string SetupRequiredItem = "potel:2fa-setup";
    public const string SetupRequiredMessage = "In deze werkruimte is inloggen in twee stappen verplicht voor beheerders. Stel het eerst in, dan kun je verder.";
    public const string WrongCode = "Deze code klopt niet. Vul de code in die nu in je app staat, of een herstelcode.";
    public const string LoginExpired = "Je inlogpoging is verlopen. Log opnieuw in met je e-mailadres en wachtwoord.";

    IDataProtector Secrets => dp.CreateProtector(SecretPurpose);
    ITimeLimitedDataProtector Pending => dp.CreateProtector(PendingPurpose).ToTimeLimitedDataProtector();
    ITimeLimitedDataProtector Remember => dp.CreateProtector(RememberPurpose).ToTimeLimitedDataProtector();

    public string Issuer => config["Platform:Name"] is { Length: > 0 } name ? name : "Potel";

    // Nu, voor de tijdstap van de codes.
    public DateTimeOffset Now => new(DateTime.SpecifyKind(clock.UtcNow, DateTimeKind.Utc));

    public string Protect(byte[] secret) => Convert.ToBase64String(Secrets.Protect(secret));

    // Null als er niets is of het niet meer te ontsleutelen is (de sleutels uit KeysPath zijn weg); dan werken alleen herstelcodes nog.
    public byte[]? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try { return Secrets.Unprotect(Convert.FromBase64String(stored)); }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    // Wat de gebruiker in zijn app zet: de sleutel in groepjes van vier, en een otpauth-link om op een telefoon op te tikken.
    public object SetupInfo(User u, byte[] secret)
    {
        var key = Base32.Encode(secret);
        var issuer = Uri.EscapeDataString(Issuer);
        return new
        {
            key = string.Join(' ', key.Chunk(4).Select(c => new string(c))),
            otpauthUrl = $"otpauth://totp/{issuer}:{Uri.EscapeDataString(u.Email)}?secret={key}&issuer={issuer}&algorithm=SHA1&digits={Totp.Digits}&period={Totp.StepSeconds}",
            issuer = Issuer,
            account = u.Email,
        };
    }

    CookieOptions Cookie(TimeSpan? lifetime) => new()
    {
        HttpOnly = true, Secure = !env.IsDevelopment(), SameSite = SameSiteMode.Strict, Path = "/api/auth", MaxAge = lifetime, IsEssential = true,
    };

    public void StartPendingLogin(HttpContext http, User u) =>
        http.Response.Cookies.Append(PendingCookie, Pending.Protect($"{u.Id}|{u.WorkspaceId}|{u.SecurityStamp}", PendingLifetime), Cookie(PendingLifetime));

    public PendingLogin? ReadPendingLogin(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(PendingCookie, out var value) || string.IsNullOrEmpty(value) || value.Length > 2000) return null;
        try
        {
            var parts = Pending.Unprotect(value).Split('|');
            return parts.Length == 3 && int.TryParse(parts[0], out var id) && int.TryParse(parts[1], out var ws) ? new PendingLogin(id, ws, parts[2]) : null;
        }
        catch (CryptographicException) { return null; }
    }

    public void EndPendingLogin(HttpContext http) => http.Response.Cookies.Delete(PendingCookie, Cookie(null));

    public void RememberDevice(HttpContext http, User u) =>
        http.Response.Cookies.Append(RememberCookie, Remember.Protect($"{u.Id}|{u.SecurityStamp}", RememberLifetime), Cookie(RememberLifetime));

    public void ForgetDevice(HttpContext http) => http.Response.Cookies.Delete(RememberCookie, Cookie(null));

    // Mag deze browser de tweede stap overslaan? Alleen met een geldig koekje voor precies deze gebruiker en zijn huidige SecurityStamp.
    public bool IsRemembered(HttpContext http, User u)
    {
        if (!http.Request.Cookies.TryGetValue(RememberCookie, out var value) || string.IsNullOrEmpty(value) || value.Length > 2000) return false;
        if (string.IsNullOrEmpty(u.SecurityStamp)) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Remember.Unprotect(value)), Encoding.UTF8.GetBytes($"{u.Id}|{u.SecurityStamp}"));
        }
        catch (CryptographicException) { return false; }
    }

    public static string Normalize(string? input) =>
        input is null || input.Length > 64 ? "" : new string(input.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToLowerInvariant).ToArray());

    static string RecoveryHash(int userId, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{userId}:{code}")));

    // Een code uit de app (huidige, vorige of volgende tijdstap) of, als dat hier mag, een herstelcode. Een code uit de app werkt één keer:
    // de tijdstap moet nieuwer zijn dan de laatst gebruikte, en dat leggen we in één update vast, ook als twee verzoeken tegelijk komen.
    // Een herstelcode werkt ook één keer. Hoort binnen de werkruimte van de gebruiker.
    public async Task<CodeKind> VerifyAsync(AppDb db, User u, string? input, bool allowRecovery)
    {
        var code = Normalize(input);
        if (code.Length == Totp.Digits && code.All(char.IsAsciiDigit))
        {
            if (Unprotect(u.TwoFactorSecret) is not { } secret || Totp.Match(secret, code, Now) is not { } step) return CodeKind.None;
            var claimed = await db.Users.Where(x => x.Id == u.Id && x.TwoFactorEnabledAt != null && (x.TwoFactorLastStep == null || x.TwoFactorLastStep < step))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.TwoFactorLastStep, step));
            return claimed == 1 ? CodeKind.App : CodeKind.None;
        }
        if (allowRecovery && code.Length == RecoveryCodeLength)
        {
            var hash = RecoveryHash(u.Id, code);
            var now = DateTime.UtcNow;
            var used = await db.RecoveryCodes.Where(r => r.UserId == u.Id && r.CodeHash == hash && r.UsedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.UsedAt, now));
            return used == 1 ? CodeKind.Recovery : CodeKind.None;
        }
        return CodeKind.None;
    }

    // Tien nieuwe herstelcodes; de oude werken dan niet meer. Alleen de hashes gaan de database in: de codes zie je één keer.
    // Sla daarna de wijzigingen op.
    public static async Task<List<string>> NewRecoveryCodesAsync(AppDb db, User u)
    {
        await db.RecoveryCodes.Where(r => r.UserId == u.Id).ExecuteDeleteAsync();
        var codes = new HashSet<string>();
        while (codes.Count < RecoveryCodeCount) codes.Add(RandomNumberGenerator.GetString(RecoveryAlphabet, RecoveryCodeLength));
        db.RecoveryCodes.AddRange(codes.Select(c => new RecoveryCode { UserId = u.Id, CodeHash = RecoveryHash(u.Id, c) }));
        return codes.Select(c => $"{c[..4]}-{c[4..8]}-{c[8..]}").ToList();
    }

    public static Task<int> RecoveryCodesLeftAsync(AppDb db, int userId) => db.RecoveryCodes.CountAsync(r => r.UserId == userId && r.UsedAt == null);

    // Zet inloggen in twee stappen helemaal uit, met de herstelcodes. Sla daarna de wijzigingen op.
    public static async Task ClearAsync(AppDb db, User u)
    {
        u.TwoFactorSecret = null;
        u.TwoFactorPendingSecret = null;
        u.TwoFactorEnabledAt = null;
        u.TwoFactorLastStep = null;
        await db.RecoveryCodes.Where(r => r.UserId == u.Id).ExecuteDeleteAsync();
    }

    // Moet deze gebruiker eerst inloggen in twee stappen instellen? Alleen een beheerder, als zijn werkruimte het verplicht.
    public static async Task<bool> SetupRequiredAsync(AppDb db, User u) =>
        u.Role == Roles.Admin && !u.TwoFactorEnabled
        && await db.Workspaces.Where(w => w.Id == u.WorkspaceId).Select(w => w.RequireTwoFactorForAdmins).FirstOrDefaultAsync();

    // Wat mag zolang een beheerder het nog moet instellen: inloggen, uitloggen, instellen (alles onder /api/auth) en openbare pagina's.
    public static bool AllowedBeforeSetup(HttpRequest r) => r.Path.StartsWithSegments("/api/auth") || r.Path.StartsWithSegments("/api/public");
}

public static class TwoFactorEndpoints
{
    static async Task<User?> CurrentAsync(AppDb db, ClaimsPrincipal user) => user.UserId() is { } id ? await db.Users.FindAsync(id) : null;

    static object Status(User u, int recoveryCodesLeft, bool setupRequired, bool requiredForAdmins) => new
    {
        enabled = u.TwoFactorEnabled, enabledAt = u.TwoFactorEnabledAt, recoveryCodesLeft, setupRequired, requiredForAdmins,
    };

    public static void MapTwoFactor(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth/two-factor");

        g.MapGet("/", async (AppDb db, ClaimsPrincipal user) =>
        {
            if (await CurrentAsync(db, user) is not { } u) return Results.Unauthorized();
            var required = await db.Workspaces.Where(w => w.Id == u.WorkspaceId).Select(w => w.RequireTwoFactorForAdmins).FirstOrDefaultAsync();
            return Results.Ok(Status(u, await TwoFactor.RecoveryCodesLeftAsync(db, u.Id), await TwoFactor.SetupRequiredAsync(db, u), required));
        });

        // Stap 1 van instellen: met je wachtwoord, zodat iemand met alleen een overgenomen sessie het niet op zijn eigen telefoon zet.
        g.MapPost("/setup", async (AppDb db, TwoFactor tf, ClaimsPrincipal user, TwoFactorPasswordRequest req) =>
        {
            if (await CurrentAsync(db, user) is not { } u) return Results.Unauthorized();
            if (u.TwoFactorEnabled) return Results.Conflict(new { error = "Inloggen in twee stappen staat al aan." });
            if (!AuthEndpoints.PasswordMatches(u, req.Password)) return Results.BadRequest(new { error = "Je wachtwoord klopt niet" });
            var secret = RandomNumberGenerator.GetBytes(Totp.SecretBytes);
            u.TwoFactorPendingSecret = tf.Protect(secret);
            await db.SaveChangesAsync();
            return Results.Ok(tf.SetupInfo(u, secret));
        }).RequireRateLimiting("accounts");

        // Stap 2: pas aan als de eerste code uit de app klopt. Je krijgt de herstelcodes één keer te zien; andere sessies stoppen.
        g.MapPost("/enable", async (AppDb db, TwoFactor tf, HttpContext http, ClaimsPrincipal user, TwoFactorCodeRequest req) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            if (await CurrentAsync(db, user) is not { } u) return Results.Unauthorized();
            if (u.TwoFactorEnabled) return Results.Conflict(new { error = "Inloggen in twee stappen staat al aan." });
            if (tf.Unprotect(u.TwoFactorPendingSecret) is not { } secret)
                return Results.BadRequest(new { error = "Begin opnieuw met instellen: er is nog geen sleutel voor je app." });
            if (Totp.Match(secret, TwoFactor.Normalize(req.Code), tf.Now) is not { } step)
                return Results.BadRequest(new { error = "Deze code klopt niet. Kijk of je de sleutel goed hebt overgenomen en vul de code in die nu in je app staat." });
            u.TwoFactorSecret = u.TwoFactorPendingSecret;
            u.TwoFactorPendingSecret = null;
            u.TwoFactorEnabledAt = DateTime.UtcNow;
            u.TwoFactorLastStep = step;
            var codes = await TwoFactor.NewRecoveryCodesAsync(db, u);
            u.NewSecurityStamp();
            db.Log("gebruiker", $"{u.Name} heeft inloggen in twee stappen aangezet");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            await AuthEndpoints.SignIn(http, u);
            return Results.Ok(new { recoveryCodes = codes, user = await AuthEndpoints.MeAsync(db, u) });
        }).RequireRateLimiting("accounts");

        // Uitzetten vraagt je wachtwoord en een code uit de app, of een herstelcode als je telefoon kwijt is.
        g.MapPost("/disable", async (AppDb db, TwoFactor tf, HttpContext http, ClaimsPrincipal user, TwoFactorDisableRequest req) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            if (await CurrentAsync(db, user) is not { } u) return Results.Unauthorized();
            if (!u.TwoFactorEnabled) return Results.BadRequest(new { error = "Inloggen in twee stappen staat al uit." });
            if (!AuthEndpoints.PasswordMatches(u, req.Password)) return Results.BadRequest(new { error = "Je wachtwoord klopt niet" });
            if (await tf.VerifyAsync(db, u, req.Code, allowRecovery: true) == CodeKind.None) return Results.BadRequest(new { error = TwoFactor.WrongCode });
            await TwoFactor.ClearAsync(db, u);
            u.NewSecurityStamp();
            db.Log("gebruiker", $"{u.Name} heeft inloggen in twee stappen uitgezet");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            await AuthEndpoints.SignIn(http, u);
            tf.ForgetDevice(http);
            return Results.Ok(await AuthEndpoints.MeAsync(db, u));
        }).RequireRateLimiting("accounts");

        // Nieuwe herstelcodes, met een code uit de app. De oude werken daarna niet meer.
        g.MapPost("/recovery-codes", async (AppDb db, TwoFactor tf, HttpContext http, ClaimsPrincipal user, TwoFactorCodeRequest req) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            if (await CurrentAsync(db, user) is not { } u) return Results.Unauthorized();
            if (!u.TwoFactorEnabled) return Results.BadRequest(new { error = "Zet eerst inloggen in twee stappen aan." });
            if (await tf.VerifyAsync(db, u, req.Code, allowRecovery: false) != CodeKind.App)
                return Results.BadRequest(new { error = "Deze code klopt niet. Vul de code in die nu in je app staat; een herstelcode werkt hier niet." });
            var codes = await TwoFactor.NewRecoveryCodesAsync(db, u);
            u.NewSecurityStamp();
            db.Log("gebruiker", $"{u.Name} heeft nieuwe herstelcodes gemaakt voor inloggen in twee stappen");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            await AuthEndpoints.SignIn(http, u);
            return Results.Ok(new { recoveryCodes = codes, user = await AuthEndpoints.MeAsync(db, u) });
        }).RequireRateLimiting("accounts");

        // De tweede stap van inloggen. Werkt alleen met het korte koekje van een goed wachtwoord (zie /api/auth/login), telt mislukte
        // pogingen mee voor de blokkade van inloggen, en geeft pas na een goede code de echte inlogcookie.
        api.MapPost("/auth/login/two-factor", async (AppDb db, HttpContext http, LoginThrottle throttle, TwoFactor tf, ILogger<TwoFactor> logger, TwoFactorLoginRequest req) =>
        {
            if (tf.ReadPendingLogin(http) is not { } pending) return Expired(http, tf);
            db.Tenant.WorkspaceId = pending.WorkspaceId;
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == pending.UserId);
            if (u is not { Active: true } || !u.TwoFactorEnabled || u.SecurityStamp != pending.Stamp) return Expired(http, tf);
            var ip = http.Connection.RemoteIpAddress?.ToString() ?? "onbekend";
            if (AuthEndpoints.Blocked(throttle, u.Email, ip) is { } blocked) return blocked;
            var kind = await tf.VerifyAsync(db, u, req.Code, allowRecovery: true);
            if (kind == CodeKind.None)
            {
                throttle.Failed(u.Email, ip);
                return Results.Json(new { error = TwoFactor.WrongCode }, statusCode: StatusCodes.Status401Unauthorized);
            }
            throttle.Succeeded(u.Email, ip);
            tf.EndPendingLogin(http);
            u.LastLoginAt = DateTime.UtcNow;
            int? left = null;
            if (kind == CodeKind.Recovery)
            {
                left = await TwoFactor.RecoveryCodesLeftAsync(db, u.Id);
                db.Log("gebruiker", $"{u.Name} heeft ingelogd met een herstelcode (nog {left} over)");
                logger.LogInformation("Gebruiker {UserId} heeft ingelogd met een herstelcode", u.Id);
            }
            await db.SaveChangesAsync();
            await AuthEndpoints.SignIn(http, u);
            if (req.RememberDevice) tf.RememberDevice(http, u);
            return Results.Ok(new { user = await AuthEndpoints.MeAsync(db, u), recoveryCodesLeft = left });
        }).AllowAnonymous().RequireRateLimiting("auth");
    }

    static IResult Expired(HttpContext http, TwoFactor tf)
    {
        tf.EndPendingLogin(http);
        return Results.Json(new { error = TwoFactor.LoginExpired, expired = true }, statusCode: StatusCodes.Status401Unauthorized);
    }
}
