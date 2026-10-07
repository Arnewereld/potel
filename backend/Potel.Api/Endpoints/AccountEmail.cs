using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Workflows;

namespace Potel.Api.Endpoints;

public record ForgotPasswordRequest(string Email);
public record ResetPasswordRequest(string Token, string Password);
public record VerifyEmailRequest(string Token);

// Hoeveel accountmails (wachtwoord herstellen, adres bevestigen) één adres per uur krijgt, zodat niemand zo een inbox volspamt.
public sealed class AccountMailThrottle(IConfiguration config) : IDisposable
{
    readonly PartitionedRateLimiter<string> perAddress = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, config.GetValue("RateLimit:AccountMailsPerHour", 3)), Window = TimeSpan.FromHours(1),
        }));

    public bool TryAcquire(string purpose, string email)
    {
        using var lease = perAddress.AttemptAcquire($"{purpose}|{email}");
        return lease.IsAcquired;
    }

    public void Dispose() => perAddress.Dispose();
}

public enum MailOutcome { Sent, NotConfigured, Throttled, Failed }

// Een gevonden link: het token en de gebruiker, met de werkruimte van die gebruiker al gezet.
public record FoundToken(AccountToken Token, User User);

// Mails over je account: een link om je wachtwoord te herstellen of je e-mailadres te bevestigen.
// Ze gaan via de mailserver van het platform maar tellen niet mee voor de daglimieten van werkstromen.
public class AccountMail(AppDb db, IEmailSender sender, SystemMailQueue queue, AppUrls urls, AccountMailThrottle throttle,
    IConfiguration config, IWebHostEnvironment env, ILogger<AccountMail> logger)
{
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan VerifyLifetime = TimeSpan.FromDays(2);
    public const string ResetPath = "/wachtwoord-herstellen";
    public const string VerifyPath = "/email-bevestigen";

    string Product => config["Platform:Name"] is { Length: > 0 } name ? name : "Potel";

    public bool Ready(HttpRequest request) => sender.Configured && urls.Base(request) is not null;

    public string NotConfiguredMessage(string what) =>
        $"Er is nog geen mailserver ingesteld, dus we kunnen geen {what} sturen."
        + (env.IsDevelopment() ? " In ontwikkelmodus staat de link in het serverlog." : "");

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // 32 willekeurige bytes, als base64url in de link. Alleen de hash gaat de database in. Hoort binnen de werkruimte van de gebruiker.
    async Task<string> IssueAsync(User u, string purpose, TimeSpan lifetime)
    {
        var now = DateTime.UtcNow;
        await db.AccountTokens.Where(t => t.UserId == u.Id && (t.UsedAt != null || t.ExpiresAt < now)).ExecuteDeleteAsync();
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        db.AccountTokens.Add(new AccountToken { UserId = u.Id, Purpose = purpose, TokenHash = Hash(token), Email = u.Email, ExpiresAt = now + lifetime });
        await db.SaveChangesAsync();
        return token;
    }

    // De link voor in de mail, of null als er geen mail weg kan. Zonder mailserver staat de link in ontwikkelmodus in het log.
    // Het token staat achter een # zodat het niet in de logboeken van de server of een proxy belandt.
    async Task<string?> LinkAsync(User u, string purpose, TimeSpan lifetime, string path, HttpRequest request)
    {
        var baseUrl = urls.Base(request);
        if (baseUrl is null) return null;
        if (sender.Configured) return $"{baseUrl}{path}#token={await IssueAsync(u, purpose, lifetime)}";
        if (env.IsDevelopment())
            logger.LogWarning("Geen mailserver ingesteld; deze mail aan {Email} is niet verstuurd. De link: {Link}",
                u.Email, $"{baseUrl}{path}#token={await IssueAsync(u, purpose, lifetime)}");
        return null;
    }

    // Wachtwoord vergeten. Of er een account bij het adres hoort of niet: de uitkomst en de duur zijn hetzelfde,
    // want de mail gaat via de wachtrij op de achtergrond. Een uitgeschakeld account krijgt niets.
    public async Task<MailOutcome> RequestResetAsync(string email, HttpRequest request)
    {
        var ready = Ready(request);
        if (!ready && !env.IsDevelopment()) return MailOutcome.NotConfigured;
        var allowed = throttle.TryAcquire(TokenPurposes.PasswordReset, email);
        var u = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Email == email && x.Active);
        if (u is not null && allowed)
        {
            db.Tenant.WorkspaceId = u.WorkspaceId;
            if (await LinkAsync(u, TokenPurposes.PasswordReset, ResetLifetime, ResetPath, request) is { } link
                && !queue.TryEnqueue(new OutgoingEmail(u.Email, $"Kies een nieuw wachtwoord voor {Product}", ResetBody(u, link), Product)))
                logger.LogWarning("Wachtrij voor systeemmails is vol; herstelmail niet verstuurd");
        }
        return ready ? MailOutcome.Sent : MailOutcome.NotConfigured;
    }

    // Een bevestigingsmail, meteen verstuurd. Hoort binnen de werkruimte van de gebruiker.
    public async Task<MailOutcome> SendVerificationAsync(User u, HttpRequest request)
    {
        if (!throttle.TryAcquire(TokenPurposes.VerifyEmail, u.Email)) return MailOutcome.Throttled;
        if (await LinkAsync(u, TokenPurposes.VerifyEmail, VerifyLifetime, VerifyPath, request) is not { } link) return MailOutcome.NotConfigured;
        try
        {
            await sender.SendAsync(new OutgoingEmail(u.Email, $"Bevestig je e-mailadres voor {Product}", VerifyBody(u, link), Product));
            return MailOutcome.Sent;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bevestigingsmail niet verstuurd");
            return MailOutcome.Failed;
        }
    }

    string ResetBody(User u, string link) =>
        $"Hoi {u.Name},\n\nJe hebt gevraagd om een nieuw wachtwoord voor je account bij {Product}. Klik op de link hieronder om een nieuw wachtwoord te kiezen. " +
        $"De link werkt één uur en maar één keer.\n\n{link}\n\nHeb je dit niet zelf gevraagd? Dan kun je deze mail negeren. Je wachtwoord blijft dan hetzelfde.\n\nGroet,\n{Product}";

    string VerifyBody(User u, string link) =>
        $"Hoi {u.Name},\n\nBevestig je e-mailadres voor {Product} met de link hieronder. Daarna kunnen je werkstromen ook e-mail versturen, " +
        $"en weet je zeker dat je een nieuw wachtwoord kunt aanvragen als je het vergeet.\n\n{link}\n\nDe link werkt twee dagen. " +
        $"Heb je geen account bij {Product}? Dan kun je deze mail negeren.\n\nGroet,\n{Product}";

    // Zoekt een link op over alle werkruimtes en zet daarna de werkruimte van de gebruiker. Null als de link niet bestaat
    // of bij een ander adres hoort dan dat de gebruiker nu heeft. Of hij nog geldig is, bepaalt ClaimAsync.
    public async Task<FoundToken?> FindAsync(string? token, string purpose)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return null;
        var hash = Hash(token.Trim());
        var t = await db.AccountTokens.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TokenHash == hash && x.Purpose == purpose);
        if (t is null) return null;
        db.Tenant.WorkspaceId = t.WorkspaceId;
        var u = await db.Users.FirstOrDefaultAsync(x => x.Id == t.UserId);
        return u is null || u.Email != t.Email ? null : new FoundToken(t, u);
    }

    // Gebruikt de link: werkt hooguit één keer, ook als twee verzoeken tegelijk komen, en alleen zolang hij niet verlopen is.
    public async Task<bool> ClaimAsync(AccountToken t)
    {
        var now = DateTime.UtcNow;
        return await db.AccountTokens.Where(x => x.Id == t.Id && x.UsedAt == null && x.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAt, now)) == 1;
    }

    // Maakt alle open links van een gebruiker ongeldig, of alleen die voor één doel.
    public static async Task RevokeAsync(AppDb db, int userId, string? purpose = null)
    {
        var now = DateTime.UtcNow;
        await db.AccountTokens.Where(t => t.UserId == userId && t.UsedAt == null && (purpose == null || t.Purpose == purpose))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now));
    }
}

public static class AccountEmailEndpoints
{
    public const string ResetRequested =
        "Als er een account bij dit e-mailadres hoort, sturen we je binnen een paar minuten een link om een nieuw wachtwoord te kiezen. Kijk ook bij je ongewenste mail.";
    public const string ResetLinkInvalid = "Deze link is verlopen of al gebruikt. Vraag hieronder een nieuwe aan.";
    public const string VerifyLinkInvalid = "Deze bevestigingslink is verlopen of al gebruikt. Log in en vraag een nieuwe aan.";

    public static void MapAccountEmail(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth");

        // Altijd hetzelfde antwoord, zodat niemand zo kan uitzoeken welke adressen een account hebben.
        g.MapPost("/forgot-password", async (AccountMail mail, HttpContext http, ForgotPasswordRequest req) =>
        {
            var email = (req.Email ?? "").Trim().ToLower();
            if (!email.Contains('@') || email.Length > 320) return Results.BadRequest(new { error = "Vul je e-mailadres in" });
            return await mail.RequestResetAsync(email, http.Request) == MailOutcome.NotConfigured
                ? Results.Json(new { error = mail.NotConfiguredMessage("mail") + " Vraag een beheerder van je werkruimte om bij Gebruikers een nieuw wachtwoord voor je in te stellen." },
                    statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(new { message = ResetRequested });
        }).AllowAnonymous().RequireRateLimiting("auth");

        // Een nieuw wachtwoord met de link uit de mail. Alle sessies en andere herstellinks van die gebruiker stoppen; hier log je meteen in.
        g.MapPost("/reset-password", async (AppDb db, AccountMail mail, HttpContext http, ResetPasswordRequest req) =>
        {
            if ((req.Password ?? "").Length < AuthEndpoints.MinPasswordLength)
                return Results.BadRequest(new { error = $"Kies een wachtwoord van minstens {AuthEndpoints.MinPasswordLength} tekens" });
            if (await mail.FindAsync(req.Token, TokenPurposes.PasswordReset) is not { User.Active: true } found || !await mail.ClaimAsync(found.Token))
                return Results.BadRequest(new { error = ResetLinkInvalid });
            var u = found.User;
            u.PasswordHash = AuthEndpoints.Hash(u, req.Password!);
            u.NewSecurityStamp();
            // De link kwam aan in deze inbox, dus het adres klopt.
            u.EmailVerifiedAt ??= DateTime.UtcNow;
            u.LastLoginAt = DateTime.UtcNow;
            await AccountMail.RevokeAsync(db, u.Id, TokenPurposes.PasswordReset);
            db.Log("gebruiker", $"{u.Name} heeft een nieuw wachtwoord gekozen via de link in de mail");
            await db.SaveChangesAsync();
            await AuthEndpoints.SignIn(http, u);
            return Results.Ok(UserDto.From(u));
        }).AllowAnonymous().RequireRateLimiting("auth");

        // De link uit de bevestigingsmail; werkt ook als je in deze browser niet bent ingelogd.
        g.MapPost("/verify-email", async (AppDb db, AccountMail mail, VerifyEmailRequest req) =>
        {
            if (await mail.FindAsync(req.Token, TokenPurposes.VerifyEmail) is not { } found) return Results.BadRequest(new { error = VerifyLinkInvalid });
            var u = found.User;
            if (u.EmailVerifiedAt is not null) return Results.Ok(new { email = u.Email });
            if (!await mail.ClaimAsync(found.Token))
            {
                // Twee keer tegelijk geklikt: het andere verzoek heeft het adres misschien net bevestigd.
                await db.Entry(u).ReloadAsync();
                return u.EmailVerifiedAt is not null ? Results.Ok(new { email = u.Email }) : Results.BadRequest(new { error = VerifyLinkInvalid });
            }
            u.EmailVerifiedAt = DateTime.UtcNow;
            await AccountMail.RevokeAsync(db, u.Id, TokenPurposes.VerifyEmail);
            db.Log("gebruiker", $"{u.Name} heeft het e-mailadres {u.Email} bevestigd");
            await db.SaveChangesAsync();
            return Results.Ok(new { email = u.Email });
        }).AllowAnonymous().RequireRateLimiting("auth");

        g.MapPost("/verify-email/resend", async (AppDb db, AccountMail mail, HttpContext http, ClaimsPrincipal user) =>
        {
            var u = user.UserId() is { } id ? await db.Users.FindAsync(id) : null;
            if (u is null) return Results.Unauthorized();
            if (u.EmailVerifiedAt is not null) return Results.Ok(new { message = "Je e-mailadres is al bevestigd." });
            return await mail.SendVerificationAsync(u, http.Request) switch
            {
                MailOutcome.Sent => Results.Ok(new { message = $"We hebben een nieuwe bevestigingsmail gestuurd naar {u.Email}." }),
                MailOutcome.Throttled => Results.Json(new { error = "Je hebt net al een paar bevestigingsmails gekregen. Kijk ook bij je ongewenste mail, of probeer het over een uur opnieuw." },
                    statusCode: StatusCodes.Status429TooManyRequests),
                MailOutcome.NotConfigured => Results.Json(new { error = mail.NotConfiguredMessage("bevestigingsmail") }, statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Json(new { error = "De bevestigingsmail kon niet worden verstuurd. Probeer het later opnieuw." }, statusCode: StatusCodes.Status503ServiceUnavailable),
            };
        }).RequireRateLimiting("accounts");
    }
}
