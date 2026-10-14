using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// De tijdberekening zelf: RFC 6238 met de testvectoren uit de bijlage, en Base32 uit RFC 4648.
public class TotpTests
{
    static readonly byte[] Seed = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Codes_match_the_rfc_6238_test_vectors(long unixTime, string expected)
    {
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixTime));
        Assert.Equal(expected, Totp.Code(Seed, step, digits: 8));
        // Met 6 cijfers zijn het de laatste 6 van dezelfde waarde.
        Assert.Equal(expected[^6..], Totp.Code(Seed, step));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    [InlineData("12345678901234567890", "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ")]
    public void Base32_follows_rfc_4648(string input, string expected) => Assert.Equal(expected, Base32.Encode(Encoding.ASCII.GetBytes(input)));

    [Fact]
    public void One_step_of_clock_skew_either_way_is_accepted_but_not_more()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        var step = Totp.StepAt(now);
        Assert.Equal(step, Totp.Match(Seed, Totp.Code(Seed, step), now));
        Assert.Equal(step - 1, Totp.Match(Seed, Totp.Code(Seed, step - 1), now));
        Assert.Equal(step + 1, Totp.Match(Seed, Totp.Code(Seed, step + 1), now));
        Assert.Null(Totp.Match(Seed, Totp.Code(Seed, step - 2), now));
        Assert.Null(Totp.Match(Seed, Totp.Code(Seed, step + 2), now));
        Assert.Null(Totp.Match(Seed, "12345", now));
        Assert.Null(Totp.Match(Seed, "12345a", now));
        Assert.Null(Totp.Match(Seed, "", now));
    }
}

public class TwoFactorFactory : AccountMailFactory
{
    // Een klok die de test zelf verzet, zodat we niet op de volgende 30 seconden hoeven te wachten.
    public MovableClock Clock { get; } = new(DateTime.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<BusinessClock>(Clock));
    }
}

static class TwoFactorKit
{
    public static byte[] Base32Decode(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = text.Replace(" ", "").ToUpperInvariant();
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. bytes];
    }

    // De code die nu (plus of min een aantal stappen van 30 seconden) in de app staat.
    public static string Code(TwoFactorFactory f, byte[] secret, int steps = 0) =>
        Totp.Code(secret, Totp.StepAt(new DateTimeOffset(DateTime.SpecifyKind(f.Clock.UtcNow, DateTimeKind.Utc))) + steps);

    public record Enabled(byte[] Secret, List<string> RecoveryCodes);

    // Zet inloggen in twee stappen aan zoals de app dat doet: wachtwoord, sleutel overnemen, eerste code.
    public static async Task<Enabled> EnableAsync(TwoFactorFactory f, HttpClient c, string password = "geheim123")
    {
        var setup = await (await c.PostAsJsonAsync("/api/auth/two-factor/setup", new { password })).JsonAsync();
        var secret = Base32Decode(setup.GetProperty("key").GetString()!);
        var res = await c.PostAsJsonAsync("/api/auth/two-factor/enable", new { code = Code(f, secret) });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.JsonAsync();
        // De eerste code is nu gebruikt; de volgende login moet een nieuwere tijdstap hebben.
        f.Clock.Value += TimeSpan.FromSeconds(30);
        return new Enabled(secret, body.GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToList());
    }

    public static async Task<HttpResponseMessage> PasswordStepAsync(HttpClient c, string email, string password = "geheim123") =>
        await c.PostAsJsonAsync("/api/auth/login", new { email, password });

    public static async Task<HttpResponseMessage> CodeStepAsync(HttpClient c, string code, bool remember = false) =>
        await c.PostAsJsonAsync("/api/auth/login/two-factor", new { code, rememberDevice = remember });

    public static async Task<bool> NeedsCodeAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.JsonAsync()).TryGetProperty("twoFactorRequired", out var v) && v.GetBoolean();
    }

    // Volledig inloggen met wachtwoord en code.
    public static async Task<HttpClient> LoginWithCodeAsync(TwoFactorFactory f, string email, byte[] secret, string password = "geheim123")
    {
        var c = Client(f);
        Assert.True(await NeedsCodeAsync(await PasswordStepAsync(c, email, password)));
        f.Clock.Value += TimeSpan.FromSeconds(30);
        Assert.Equal(HttpStatusCode.OK, (await CodeStepAsync(c, Code(f, secret))).StatusCode);
        return c;
    }

    public static string? SetCookie(HttpResponseMessage res, string name) =>
        res.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal) && v.Length > name.Length + 1)?[(name.Length + 1)..]
            : null;
}

public class TwoFactorTests(TwoFactorFactory factory) : IClassFixture<TwoFactorFactory>
{
    TwoFactorFactory F => factory;

    [Fact]
    public async Task Setup_needs_the_password_and_a_valid_code_before_it_is_switched_on()
    {
        var c = await RegisterAsync(F, "tf-setup@example.com");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/two-factor/setup", new { password = "fout" })).StatusCode);

        var setup = await (await c.PostAsJsonAsync("/api/auth/two-factor/setup", new { password = "geheim123" })).JsonAsync();
        var key = setup.GetProperty("key").GetString()!;
        Assert.Matches(@"^([A-Z2-7]{4} ){7}[A-Z2-7]{4}$", key);
        var url = setup.GetProperty("otpauthUrl").GetString()!;
        Assert.StartsWith("otpauth://totp/Potel:tf-setup%40example.com?", url);
        Assert.Contains($"secret={key.Replace(" ", "")}", url);
        Assert.Contains("issuer=Potel", url);
        Assert.Contains("algorithm=SHA1&digits=6&period=30", url);

        // Een verkeerde code zet niets aan.
        var secret = TwoFactorKit.Base32Decode(key);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/two-factor/enable", new { code = TwoFactorKit.Code(F, secret, steps: 5) })).StatusCode);
        Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/auth/two-factor")).GetProperty("enabled").GetBoolean());

        var res = await c.PostAsJsonAsync("/api/auth/two-factor/enable", new { code = TwoFactorKit.Code(F, secret) });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var codes = (await res.JsonAsync()).GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches("^[a-z2-9]{4}-[a-z2-9]{4}-[a-z2-9]{4}$", code));

        var status = await c.GetFromJsonAsync<JsonElement>("/api/auth/two-factor");
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(10, status.GetProperty("recoveryCodesLeft").GetInt32());
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("twoFactor").GetBoolean());
        // Aan is aan: opnieuw instellen kan pas na uitzetten.
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/auth/two-factor/setup", new { password = "geheim123" })).StatusCode);
    }

    [Fact]
    public async Task The_secret_is_stored_encrypted_and_recovery_codes_only_as_hashes()
    {
        var c = await RegisterAsync(F, "tf-opslag@example.com");
        var enabled = await TwoFactorKit.EnableAsync(F, c);
        var key = Base32.Encode(enabled.Secret);
        var (stored, hashes) = F.WithDb(db =>
        {
            var u = db.Users.IgnoreQueryFilters().Single(x => x.Email == "tf-opslag@example.com");
            return (u.TwoFactorSecret!, db.RecoveryCodes.IgnoreQueryFilters().Where(r => r.UserId == u.Id).Select(r => r.CodeHash).ToList());
        });
        Assert.DoesNotContain(key, stored);
        Assert.DoesNotContain(Convert.ToBase64String(enabled.Secret), stored);
        Assert.Equal(enabled.Secret, F.Services.GetRequiredService<TwoFactor>().Unprotect(stored));
        Assert.Equal(10, hashes.Count);
        Assert.All(enabled.RecoveryCodes, code => Assert.DoesNotContain(hashes, h => h.Contains(code.Replace("-", ""), StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Login_needs_a_second_step_and_the_password_alone_is_no_session()
    {
        var c = await RegisterAsync(F, "tf-login@example.com");
        var enabled = await TwoFactorKit.EnableAsync(F, c);

        var browser = Client(F);
        var first = await TwoFactorKit.PasswordStepAsync(browser, "tf-login@example.com");
        Assert.True(await TwoFactorKit.NeedsCodeAsync(first));
        Assert.Null(TwoFactorKit.SetCookie(first, "potel.auth"));
        Assert.NotNull(TwoFactorKit.SetCookie(first, TwoFactor.PendingCookie));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/customers")).StatusCode);

        // Een verkeerde code: nog steeds niet binnen.
        Assert.Equal(HttpStatusCode.Unauthorized, (await TwoFactorKit.CodeStepAsync(browser, TwoFactorKit.Code(F, enabled.Secret, steps: 4))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/customers")).StatusCode);

        var ok = await TwoFactorKit.CodeStepAsync(browser, TwoFactorKit.Code(F, enabled.Secret));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("tf-login@example.com", (await ok.JsonAsync()).GetProperty("user").GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/customers")).StatusCode);

        // Het koekje voor de tweede stap is daarna weg; nog een keer de tweede stap kan niet.
        Assert.Equal(HttpStatusCode.Unauthorized, (await TwoFactorKit.CodeStepAsync(browser, TwoFactorKit.Code(F, enabled.Secret, 1))).StatusCode);
    }

    [Fact]
    public async Task A_code_works_once_and_older_codes_are_refused()
    {
        var c = await RegisterAsync(F, "tf-replay@example.com");
        var enabled = await TwoFactorKit.EnableAsync(F, c);
        var code = TwoFactorKit.Code(F, enabled.Secret);

        var a = Client(F);
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await TwoFactorKit.PasswordStepAsync(a, "tf-replay@example.com")));
        Assert.Equal(HttpStatusCode.OK, (await TwoFactorKit.CodeStepAsync(a, code)).StatusCode);

        // Dezelfde code nog een keer, ook in een andere browser: geweigerd. Een code van een eerdere stap ook.
        var b = Client(F);
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await TwoFactorKit.PasswordStepAsync(b, "tf-replay@example.com")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await TwoFactorKit.CodeStepAsync(b, code)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TwoFactorKit.CodeStepAsync(b, TwoFactorKit.Code(F, enabled.Secret, -1))).StatusCode);
        // De volgende code wel.
        Assert.Equal(HttpStatusCode.OK, (await TwoFactorKit.CodeStepAsync(b, TwoFactorKit.Code(F, enabled.Secret, 1))).StatusCode);
    }

    [Fact]
    public async Task A_phone_clock_one_step_off_is_fine_but_two_steps_is_not()
    {
        var c = await RegisterAsync(F, "tf-klok@example.com");
        var enabled = await TwoFactorKit.EnableAsync(F, c);
        F.Clock.Value += TimeSpan.FromMinutes(10);

        var browser = Client(F);
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await TwoFactorKit.PasswordStepAsync(browser, "tf-klok@example.com")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await TwoFactorKit.CodeStepAsync(browser, TwoFactorKit.Code(F, enabled.Secret, -2))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TwoFactorKit.CodeStepAsync(browser, TwoFactorKit.Code(F, enabled.Secret, 2))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TwoFactorKit.CodeStepAsync(browser, TwoFactorKit.Code(F, enabled.Secret, -1))).StatusCode);

        var other = Client(F);
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await TwoFactorKit.PasswordStepAsync(other, "tf-klok@example.com")));
        Assert.Equal(HttpStatusCode.OK, (await TwoFactorKit.CodeStepAsync(other, TwoFactorKit.Code(F, enabled.Secret, 1))).StatusCode);
    }

    [Fact]
    public async Task Recovery_codes_work_once_and_new_ones_replace_the_old()
    {
        var c = await RegisterAsync(F, "tf-herstel@example.com");
        var enabled = await TwoFactorKit.EnableAsync(F, c);
        var first = enabled.RecoveryCodes[0];

        var browser = Client(F);
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await TwoFactorKit.PasswordStepAsync(browser, "tf-herstel@example.com")));
        // Hoofdletters en spaties maken niet uit.
        var ok = await TwoFactorKit.CodeStepAsync(browser, " " + first.ToUpperInvariant().Replace("-", " ") + " ");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(9, (await ok.JsonAsync()).GetProperty("recoveryCodesLeft").GetInt32());

        var again = Client(F);
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await TwoFactorKit.PasswordStepAsync(again, "tf-herstel@example.com")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await TwoFactorKit.CodeStepAsync(again, first)).StatusCode);

        // Nieuwe herstelcodes vragen een code uit de app, geen herstelcode.
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsJsonAsync("/api/auth/two-factor/recovery-codes", new { code = enabled.RecoveryCodes[1] })).StatusCode);
        var fresh = await browser.PostAsJsonAsync("/api/auth/two-factor/recovery-codes", new { code = TwoFactorKit.Code(F, enabled.Secret) });
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        var newCodes = (await fresh.JsonAsync()).GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Equal(10, (await browser.GetFromJsonAsync<JsonElement>("/api/auth/two-factor")).GetProperty("recoveryCodesLeft").GetInt32());

        // Nieuwe codes veranderen de beveiligingsstempel: een half afgemaakte inlog moet opnieuw beginnen.
        Assert.True((await (await TwoFactorKit.CodeStepAsync(again, newCodes[0])).JsonAsync()).GetProperty("expired").GetBoolean());
        var third = Client(F);
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await TwoFactorKit.PasswordStepAsync(third, "tf-herstel@example.com")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await TwoFactorKit.CodeStepAsync(third, enabled.RecoveryCodes[2])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TwoFactorKit.CodeStepAsync(third, newCodes[0])).StatusCode);
    }

    [Fact]
    public async Task Turning_it_on_or_making_new_codes_signs_out_other_sessions_but_not_this_one()
    {
        var c = await RegisterAsync(F, "tf-sessies@example.com");
        var other = await LoginAsync(F, "tf-sessies@example.com");
        var enabled = await TwoFactorKit.EnableAsync(F, c);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/customers")).StatusCode);

        var second = await TwoFactorKit.LoginWithCodeAsync(F, "tf-sessies@example.com", enabled.Secret);
        F.Clock.Value += TimeSpan.FromSeconds(30);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/auth/two-factor/recovery-codes", new { code = TwoFactorKit.Code(F, enabled.Secret) })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/customers")).StatusCode);
    }

    [Fact]
    public async Task Turning_it_off_needs_the_password_and_a_code()
    {
        var c = await RegisterAsync(F, "tf-uit@example.com");
        var enabled = await TwoFactorKit.EnableAsync(F, c);
        var other = await TwoFactorKit.LoginWithCodeAsync(F, "tf-uit@example.com", enabled.Secret);
        F.Clock.Value += TimeSpan.FromSeconds(30);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/two-factor/disable", new { password = "fout", code = TwoFactorKit.Code(F, enabled.Secret) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/two-factor/disable", new { password = "geheim123", code = "000000" })).StatusCode);
        Assert.True((await c.GetFromJsonAsync<JsonElement>("/api/auth/two-factor")).GetProperty("enabled").GetBoolean());

        var off = await c.PostAsJsonAsync("/api/auth/two-factor/disable", new { password = "geheim123", code = TwoFactorKit.Code(F, enabled.Secret) });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.False((await off.JsonAsync()).GetProperty("twoFactor").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/customers")).StatusCode);
        Assert.Equal(0, F.WithDb(db => db.RecoveryCodes.IgnoreQueryFilters().Count(r => db.Users.IgnoreQueryFilters().Any(u => u.Id == r.UserId && u.Email == "tf-uit@example.com"))));
        // Daarna weer gewoon met alleen het wachtwoord.
        await LoginAsync(F, "tf-uit@example.com");
    }

    [Fact]
    public async Task Remembered_device_skips_the_code_until_the_security_stamp_changes()
    {
        var c = await RegisterAsync(F, "tf-onthoud@example.com");
        var enabled = await TwoFactorKit.EnableAsync(F, c);

        var laptop = Client(F);
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await TwoFactorKit.PasswordStepAsync(laptop, "tf-onthoud@example.com")));
        var res = await TwoFactorKit.CodeStepAsync(laptop, TwoFactorKit.Code(F, enabled.Secret), remember: true);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var device = TwoFactorKit.SetCookie(res, TwoFactor.RememberCookie);
        Assert.NotNull(device);

        await laptop.PostAsync("/api/auth/logout", null);
        Assert.False(await TwoFactorKit.NeedsCodeAsync(await TwoFactorKit.PasswordStepAsync(laptop, "tf-onthoud@example.com")));
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/customers")).StatusCode);
        // Een andere browser moet wel de code geven.
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await TwoFactorKit.PasswordStepAsync(Client(F), "tf-onthoud@example.com")));

        // Het koekje hoort bij deze gebruiker: bij een ander account helpt het niet, ook niet met het wachtwoord van dat account.
        var otherUser = await RegisterAsync(F, "tf-onthoud-ander@example.com");
        await TwoFactorKit.EnableAsync(F, otherUser);
        var thief = F.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { email = "tf-onthoud-ander@example.com", password = "geheim123" }) };
        req.Headers.Add("Cookie", $"{TwoFactor.RememberCookie}={device}");
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await thief.SendAsync(req)));

        // Een nieuw wachtwoord verandert de SecurityStamp: het onthouden apparaat moet weer een code geven.
        Assert.Equal(HttpStatusCode.NoContent, (await laptop.PutAsJsonAsync("/api/auth/password", new { current = "geheim123", @new = "nieuwgeheim1" })).StatusCode);
        await laptop.PostAsync("/api/auth/logout", null);
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await TwoFactorKit.PasswordStepAsync(laptop, "tf-onthoud@example.com", "nieuwgeheim1")));
    }

    [Fact]
    public async Task Password_reset_never_skips_the_second_step()
    {
        var c = await RegisterAsync(F, "tf-reset@example.com");
        var enabled = await TwoFactorKit.EnableAsync(F, c);
        // Zelfs in een browser die dit apparaat liet onthouden.
        var browser = Client(F);
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await TwoFactorKit.PasswordStepAsync(browser, "tf-reset@example.com")));
        Assert.Equal(HttpStatusCode.OK, (await TwoFactorKit.CodeStepAsync(browser, TwoFactorKit.Code(F, enabled.Secret), remember: true)).StatusCode);
        await browser.PostAsync("/api/auth/logout", null);

        var token = await AccountMailKit.RequestResetTokenAsync(F, "tf-reset@example.com", browser);
        var reset = await browser.PostAsJsonAsync("/api/auth/reset-password", new { token, password = "helemaalnieuw1" });
        Assert.True(await TwoFactorKit.NeedsCodeAsync(reset));
        Assert.Null(TwoFactorKit.SetCookie(reset, "potel.auth"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/auth/me")).StatusCode);
        // De oude sessie stopt ook.
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/customers")).StatusCode);

        F.Clock.Value += TimeSpan.FromSeconds(30);
        Assert.Equal(HttpStatusCode.OK, (await TwoFactorKit.CodeStepAsync(browser, TwoFactorKit.Code(F, enabled.Secret))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/customers")).StatusCode);
    }

    [Fact]
    public async Task An_expired_or_forged_second_step_is_refused()
    {
        var c = await RegisterAsync(F, "tf-verlopen@example.com");
        var enabled = await TwoFactorKit.EnableAsync(F, c);
        var userId = await MyIdAsync(c);
        var workspaceId = await WorkspaceIdAsync(c);
        var stamp = F.WithDb(db => db.Users.IgnoreQueryFilters().Single(u => u.Id == userId).SecurityStamp);
        var protector = F.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(TwoFactor.PendingPurpose).ToTimeLimitedDataProtector();

        async Task<HttpStatusCode> WithPending(string value)
        {
            var client = F.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login/two-factor") { Content = JsonContent.Create(new { code = TwoFactorKit.Code(F, enabled.Secret), rememberDevice = false }) };
            req.Headers.Add("Cookie", $"{TwoFactor.PendingCookie}={value}");
            return (await client.SendAsync(req)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await WithPending(protector.Protect($"{userId}|{workspaceId}|{stamp}", DateTimeOffset.UtcNow.AddSeconds(-1))));
        Assert.Equal(HttpStatusCode.Unauthorized, await WithPending("verzonnen"));
        // Met een andere doelstelling ondertekend (zoals het koekje van een onthouden apparaat) werkt het ook niet.
        var remember = F.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(TwoFactor.RememberPurpose).ToTimeLimitedDataProtector();
        Assert.Equal(HttpStatusCode.Unauthorized, await WithPending(remember.Protect($"{userId}|{workspaceId}|{stamp}", TimeSpan.FromMinutes(5))));
        // Een geldig koekje met een oude SecurityStamp ook niet.
        Assert.Equal(HttpStatusCode.Unauthorized, await WithPending(protector.Protect($"{userId}|{workspaceId}|ouder", TimeSpan.FromMinutes(5))));
        Assert.Equal(HttpStatusCode.OK, await WithPending(protector.Protect($"{userId}|{workspaceId}|{stamp}", TimeSpan.FromMinutes(5))));
    }

    // Elk eindpunt onder /api dat een ingelogde gebruiker vraagt, weigert het koekje van de tweede stap, waar je het ook meestuurt.
    [Fact]
    public async Task The_pending_login_state_opens_no_api_endpoint()
    {
        var c = await RegisterAsync(F, "tf-alles@example.com");
        await TwoFactorKit.EnableAsync(F, c);
        var pending = TwoFactorKit.SetCookie(await TwoFactorKit.PasswordStepAsync(Client(F), "tf-alles@example.com"), TwoFactor.PendingCookie);
        Assert.NotNull(pending);

        var raw = F.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
        var endpoints = F.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.TrimStart('/').StartsWith("api/", StringComparison.Ordinal) == true)
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .ToList();
        Assert.True(endpoints.Count > 100);
        var checkedRoutes = 0;
        foreach (var e in endpoints)
        {
            var path = "/" + string.Join('/', e.RoutePattern.PathSegments.Select(s => string.Concat(s.Parts.Select(p => p switch
            {
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternLiteralPart l => l.Content,
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternParameterPart { IsCatchAll: true } => "iets",
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternParameterPart q => q.ParameterPolicies.Any(x => x.Content == "int") ? "1" : "iets",
                _ => "",
            }))));
            foreach (var method in e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                foreach (var cookie in new[] { $"{TwoFactor.PendingCookie}={pending}", $"potel.auth={pending}" })
                {
                    var req = new HttpRequestMessage(new HttpMethod(method), path);
                    if (method is "POST" or "PUT" or "PATCH" or "DELETE") req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    req.Headers.Add("Cookie", cookie);
                    var res = await raw.SendAsync(req);
                    Assert.True(res.StatusCode == HttpStatusCode.Unauthorized, $"{method} {path} met {cookie.Split('=')[0]} gaf {(int)res.StatusCode}");
                }
                checkedRoutes++;
            }
        }
        Assert.True(checkedRoutes > 100);
    }

    [Fact]
    public async Task A_workspace_admin_resets_two_step_login_only_for_employees_of_their_own_workspace()
    {
        var boss = await RegisterAsync(F, "tf-baas@example.com");
        var employeeId = await CreateUserAsync(boss, "tf-medewerker@example.com");
        var adminId = await CreateUserAsync(boss, "tf-collega@example.com", role: "beheerder");
        var employee = await LoginAsync(F, "tf-medewerker@example.com");
        var employeeTf = await TwoFactorKit.EnableAsync(F, employee);
        var colleague = await LoginAsync(F, "tf-collega@example.com");
        await TwoFactorKit.EnableAsync(F, colleague);
        var employeeSession = await TwoFactorKit.LoginWithCodeAsync(F, "tf-medewerker@example.com", employeeTf.Secret);

        // Een andere werkruimte ziet de medewerker niet eens, en een medewerker mag het niet.
        var stranger = await RegisterAsync(F, "tf-vreemde@example.com");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/users/{employeeId}/two-factor")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employeeSession.DeleteAsync($"/api/users/{employeeId}/two-factor")).StatusCode);
        // Een andere beheerder of jezelf: niet via deze weg.
        Assert.Equal(HttpStatusCode.Forbidden, (await boss.DeleteAsync($"/api/users/{adminId}/two-factor")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await boss.DeleteAsync($"/api/users/{await MyIdAsync(boss)}/two-factor")).StatusCode);

        var users = await boss.GetFromJsonAsync<List<JsonElement>>("/api/users");
        Assert.True(users!.Single(u => u.GetProperty("id").GetInt32() == employeeId).GetProperty("twoFactor").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await boss.DeleteAsync($"/api/users/{employeeId}/two-factor")).StatusCode);
        // De medewerker is overal uitgelogd en logt daarna in met alleen het wachtwoord.
        Assert.Equal(HttpStatusCode.Unauthorized, (await employeeSession.GetAsync("/api/customers")).StatusCode);
        Assert.False(await TwoFactorKit.NeedsCodeAsync(await TwoFactorKit.PasswordStepAsync(Client(F), "tf-medewerker@example.com")));
        // Het staat in het logboek van de werkruimte.
        var wsId = await WorkspaceIdAsync(boss);
        Assert.True(F.WithDb(db => db.Activities.IgnoreQueryFilters().Any(a => a.WorkspaceId == wsId && a.Text.Contains("Inloggen in twee stappen van tf-medewerker uitgezet"))));
        Assert.Equal(HttpStatusCode.BadRequest, (await boss.DeleteAsync($"/api/users/{employeeId}/two-factor")).StatusCode);
    }

    [Fact]
    public async Task A_workspace_admin_cannot_reset_a_platform_admin_unless_they_are_one()
    {
        var boss = await RegisterAsync(F, "tf-platformbaas@example.com");
        var helperId = await CreateUserAsync(boss, "tf-platformhulp@example.com");
        var helper = await LoginAsync(F, "tf-platformhulp@example.com");
        await TwoFactorKit.EnableAsync(F, helper);
        var owner = await F.LoginAsync();
        (await owner.PostAsJsonAsync("/api/platform/admins", new { email = "tf-platformhulp@example.com" })).EnsureSuccessStatusCode();

        var refused = await boss.DeleteAsync($"/api/users/{helperId}/two-factor");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("platform", await refused.ErrorAsync());

        (await owner.PostAsJsonAsync("/api/platform/admins", new { email = "tf-platformbaas@example.com" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await boss.DeleteAsync($"/api/users/{helperId}/two-factor")).StatusCode);

        // De pagina Platform laat zien welke platformbeheerders het aan hebben.
        var admins = await owner.GetFromJsonAsync<List<JsonElement>>("/api/platform/admins");
        Assert.False(admins!.Single(a => a.GetProperty("email").GetString() == "tf-platformhulp@example.com").GetProperty("twoFactor").GetBoolean());
        await TwoFactorKit.EnableAsync(F, boss);
        admins = await owner.GetFromJsonAsync<List<JsonElement>>("/api/platform/admins");
        Assert.True(admins!.Single(a => a.GetProperty("email").GetString() == "tf-platformbaas@example.com").GetProperty("twoFactor").GetBoolean());
    }

    [Fact]
    public async Task Mandatory_for_admins_makes_admins_set_it_up_before_they_can_continue()
    {
        var boss = await RegisterAsync(F, "tf-verplicht@example.com");
        await CreateUserAsync(boss, "tf-verplicht-beheerder@example.com", role: "beheerder");
        await CreateUserAsync(boss, "tf-verplicht-medewerker@example.com");
        var otherAdmin = await LoginAsync(F, "tf-verplicht-beheerder@example.com");
        var employee = await LoginAsync(F, "tf-verplicht-medewerker@example.com");

        // Eerst zelf aanzetten, dan pas verplicht maken.
        Assert.Equal(HttpStatusCode.BadRequest, (await boss.PutAsJsonAsync("/api/workspace/two-factor", new { requireForAdmins = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PutAsJsonAsync("/api/workspace/two-factor", new { requireForAdmins = true })).StatusCode);
        await TwoFactorKit.EnableAsync(F, boss);
        var set = await boss.PutAsJsonAsync("/api/workspace/two-factor", new { requireForAdmins = true });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.True((await set.JsonAsync()).GetProperty("requireTwoFactorForAdmins").GetBoolean());

        // De andere beheerder kan nu alleen nog inloggen in twee stappen instellen, ook in zijn lopende sessie.
        var blocked = await otherAdmin.GetAsync("/api/customers");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.True((await blocked.JsonAsync()).GetProperty("twoFactorSetupRequired").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await otherAdmin.GetAsync("/api/workspace")).StatusCode);
        Assert.True((await otherAdmin.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("twoFactorSetupRequired").GetBoolean());
        var relogin = await LoginAsync(F, "tf-verplicht-beheerder@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await relogin.GetAsync("/api/customers")).StatusCode);
        var login = await TwoFactorKit.PasswordStepAsync(Client(F), "tf-verplicht-beheerder@example.com");
        Assert.True((await login.JsonAsync()).GetProperty("twoFactorSetupRequired").GetBoolean());

        // Medewerkers merken er niets van, en de beheerder die het al had ook niet.
        Assert.Equal(HttpStatusCode.OK, (await employee.GetAsync("/api/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await boss.GetAsync("/api/customers")).StatusCode);

        await TwoFactorKit.EnableAsync(F, otherAdmin);
        Assert.Equal(HttpStatusCode.OK, (await otherAdmin.GetAsync("/api/customers")).StatusCode);
        Assert.False((await otherAdmin.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("twoFactorSetupRequired").GetBoolean());
    }

    [Fact]
    public void The_server_command_turns_it_off_for_a_locked_out_account()
    {
        var c = RegisterAsync(F, "tf-server@example.com").GetAwaiter().GetResult();
        TwoFactorKit.EnableAsync(F, c).GetAwaiter().GetResult();
        Assert.False(F.WithDb(db => Seed.ResetTwoFactor(db, "bestaat-niet@example.com", NullLogger.Instance)));
        Assert.True(F.WithDb(db => Seed.ResetTwoFactor(db, " TF-Server@example.com ", NullLogger.Instance)));
        Assert.Equal(HttpStatusCode.Unauthorized, c.GetAsync("/api/customers").GetAwaiter().GetResult().StatusCode);
        Assert.False(TwoFactorKit.NeedsCodeAsync(TwoFactorKit.PasswordStepAsync(Client(F), "tf-server@example.com").GetAwaiter().GetResult()).GetAwaiter().GetResult());
    }
}

// Mislukte codes tellen mee voor dezelfde blokkade als foute wachtwoorden; de eigenaar op een bekend adres komt er nog in.
public class TwoFactorProxyFactory : BehindProxyFactory
{
    // Een klok die de test zelf verzet, zodat we niet op de volgende 30 seconden hoeven te wachten.
    public MovableClock Clock { get; } = new(DateTime.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Inloggen met een code kost twee verzoeken; iets ruimer dan 3, zodat de eigenaar twee keer kan inloggen.
        builder.UseSetting("RateLimit:AuthPerMinute", "6");
        builder.ConfigureTestServices(s => s.AddSingleton<BusinessClock>(Clock));
    }
}

public class TwoFactorLockoutTests(TwoFactorProxyFactory factory) : IClassFixture<TwoFactorProxyFactory>
{
    static async Task<HttpResponseMessage> Send(HttpClient c, string peer, string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-Test-Peer", peer);
        return await c.SendAsync(req);
    }

    string Code(byte[] secret, int steps = 0) =>
        Totp.Code(secret, Totp.StepAt(new DateTimeOffset(DateTime.SpecifyKind(factory.Clock.UtcNow, DateTimeKind.Utc))) + steps);

    [Fact]
    public async Task Wrong_codes_count_toward_the_login_lockout_and_the_owner_on_a_known_address_still_gets_in()
    {
        const string email = "tf-blokkade@example.com";
        var owner = Client(factory);
        Assert.Equal(HttpStatusCode.Created, (await Send(owner, "203.0.113.50", "/api/auth/register",
            new { company = "Blokkade", name = "Eigenaar", email, password = "geheim123", demoData = false, kvk = "12345678", acceptTerms = true, businessUse = true })).StatusCode);
        var setup = await (await Send(owner, "203.0.113.50", "/api/auth/two-factor/setup", new { password = "geheim123" })).JsonAsync();
        var secret = TwoFactorKit.Base32Decode(setup.GetProperty("key").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await Send(owner, "203.0.113.50", "/api/auth/two-factor/enable", new { code = Code(secret) })).StatusCode);
        factory.Clock.Value += TimeSpan.FromSeconds(30);
        // De eigenaar logt in vanaf zijn vaste adres: dat adres is daarna bekend.
        var home = Client(factory);
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await Send(home, "203.0.113.51", "/api/auth/login", new { email, password = "geheim123" })));
        Assert.Equal(HttpStatusCode.OK, (await Send(home, "203.0.113.51", "/api/auth/login/two-factor", new { code = Code(secret), rememberDevice = false })).StatusCode);

        // Iemand met het wachtwoord probeert codes vanaf steeds een ander adres (per adres onder de grens per minuut).
        HttpClient attacker = null!;
        for (var i = 1; i <= 3; i++)
        {
            attacker = Client(factory);
            var peer = $"198.51.100.{i}";
            Assert.True(await TwoFactorKit.NeedsCodeAsync(await Send(attacker, peer, "/api/auth/login", new { email, password = "geheim123" })));
            for (var j = 0; j < (i < 3 ? 2 : 1); j++)
                Assert.Equal(HttpStatusCode.Unauthorized, (await Send(attacker, peer, "/api/auth/login/two-factor", new { code = "000000", rememberDevice = false })).StatusCode);
        }
        // De zesde poging aan de tweede stap wordt niet meer bekeken, ook niet met de goede code.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Send(attacker, "198.51.100.3", "/api/auth/login/two-factor", new { code = Code(secret, 1), rememberDevice = false })).StatusCode);
        // Na vijf fouten voor dit account (de grens per uur): vanaf een nieuw adres komt zelfs het goede wachtwoord er niet meer door.
        var late = await Send(Client(factory), "198.51.100.99", "/api/auth/login", new { email, password = "geheim123" });
        Assert.Equal(HttpStatusCode.TooManyRequests, late.StatusCode);
        Assert.Contains("uur", await late.ErrorAsync());

        // De eigenaar vanaf zijn bekende adres wel.
        factory.Clock.Value += TimeSpan.FromSeconds(30);
        var again = Client(factory);
        Assert.True(await TwoFactorKit.NeedsCodeAsync(await Send(again, "203.0.113.51", "/api/auth/login", new { email, password = "geheim123" })));
        Assert.Equal(HttpStatusCode.OK, (await Send(again, "203.0.113.51", "/api/auth/login/two-factor", new { code = Code(secret), rememberDevice = false })).StatusCode);
    }

    [Fact]
    public async Task The_second_step_is_rate_limited_per_address()
    {
        var c = Client(factory);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
            codes.Add((await Send(c, "198.51.100.200", "/api/auth/login/two-factor", new { code = "123456", rememberDevice = false })).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }
}

// De migratie voor inloggen in twee stappen voegt alleen kolommen en een tabel toe: bestaande gebruikers en werkruimtes blijven
// zoals ze waren, met inloggen in twee stappen uit en niet verplicht.
public class TwoFactorMigrationTests : IDisposable
{
    const string Before = "20261014085821_Focusmodus";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-migratie-2fa-{Guid.NewGuid():N}.db");

    AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path}").Options, new Tenant());

    object? Scalar(string sql)
    {
        using var con = new SqliteConnection($"Data Source={path}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    [Fact]
    public void Existing_users_and_workspaces_survive_with_two_step_login_off()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        using (var con = new SqliteConnection($"Data Source={path}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 'Oud bedrijf', 'team');
                INSERT INTO Users (Id, WorkspaceId, Active, CreatedAt, Email, IsPlatformAdmin, Name, PasswordHash, Role, SecurityStamp, FocusMinutes)
                    VALUES (1, 1, 1, '2026-01-01 00:00:00', 'jan@example.com', 1, 'Jan', 'hash', 'beheerder', 'stempel', 50);
                """;
            cmd.ExecuteNonQuery();
        }

        using (var db = Db()) db.GetService<IMigrator>().Migrate();

        Assert.Equal("jan@example.com", Scalar("SELECT Email FROM Users WHERE Id = 1"));
        Assert.Equal("hash", Scalar("SELECT PasswordHash FROM Users WHERE Id = 1"));
        Assert.Equal("stempel", Scalar("SELECT SecurityStamp FROM Users WHERE Id = 1"));
        Assert.Equal(50L, Scalar("SELECT FocusMinutes FROM Users WHERE Id = 1"));
        Assert.Null(Scalar("SELECT TwoFactorSecret FROM Users WHERE Id = 1"));
        Assert.Null(Scalar("SELECT TwoFactorEnabledAt FROM Users WHERE Id = 1"));
        Assert.Equal(0L, Scalar("SELECT RequireTwoFactorForAdmins FROM Workspaces WHERE Id = 1"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM RecoveryCodes"));
        using var check = Db();
        var jan = check.Users.IgnoreQueryFilters().Single(u => u.Id == 1);
        Assert.False(jan.TwoFactorEnabled);
    }

    public void Dispose()
    {
        TestDatabase.Delete(path);
    }
}
