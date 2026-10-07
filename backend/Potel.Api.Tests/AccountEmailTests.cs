using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Potel.Api.Data;
using Potel.Api.Workflows;
using static Potel.Api.Tests.TestApi;
using static Potel.Api.Tests.WorkflowKit;

namespace Potel.Api.Tests;

// Onthoudt wat er gelogd wordt, om te zien of een link wel of niet in het log belandt.
public sealed class LogCollector : ILoggerProvider
{
    public readonly ConcurrentQueue<(LogLevel Level, string Message)> Entries = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this);
    public void Dispose() { }

    sealed class Logger(LogCollector owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue((logLevel, formatter(state, exception)));
    }
}

// Een mailserver die alles onthoudt, en een vast openbaar adres voor de links in de mail.
public class AccountMailFactory : PortalFactory
{
    public RecordingEmailSender Mail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("App:BaseUrl", "https://potel.example/");
        builder.ConfigureTestServices(s => s.AddSingleton<IEmailSender>(Mail));
    }
}

static class AccountMailKit
{
    static readonly Regex Token = new(@"https://potel\.example(/[a-z-]+)#token=([A-Za-z0-9_-]+)");

    public static IEnumerable<OutgoingEmail> ResetMails(AccountMailFactory f, string to) => f.Mail.Sent.Where(m => m.To == to && m.Subject.Contains("wachtwoord"));
    public static IEnumerable<OutgoingEmail> VerifyMails(AccountMailFactory f, string to) => f.Mail.Sent.Where(m => m.To == to && m.Subject.Contains("Bevestig"));

    public static string TokenFrom(OutgoingEmail mail, string path)
    {
        var m = Token.Match(mail.Body);
        Assert.True(m.Success, mail.Body);
        Assert.Equal(path, m.Groups[1].Value);
        return m.Groups[2].Value;
    }

    // Vraagt een herstellink aan en wacht op de mail; die gaat via de wachtrij op de achtergrond.
    public static async Task<string> RequestResetTokenAsync(AccountMailFactory f, string email, HttpClient? client = null)
    {
        var before = ResetMails(f, email).Count();
        var res = await (client ?? Client(f)).PostAsJsonAsync("/api/auth/forgot-password", new { email });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(await WaitUntil(() => ResetMails(f, email).Count() > before, TimeSpan.FromSeconds(5)), "geen herstelmail ontvangen");
        return TokenFrom(ResetMails(f, email).Last(), "/wachtwoord-herstellen");
    }

    public static string LatestVerifyToken(AccountMailFactory f, string email) => TokenFrom(VerifyMails(f, email).Last(), "/email-bevestigen");

    public static async Task<bool> EmailVerifiedAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("emailVerified").GetBoolean();
}

// Wachtwoord vergeten: een link per mail die één uur en één keer werkt, zonder te verraden welke adressen een account hebben.
public class PasswordResetTests(AccountMailFactory factory) : IClassFixture<AccountMailFactory>
{
    async Task<HttpResponseMessage> ResetAsync(HttpClient c, string token, string password) =>
        await c.PostAsJsonAsync("/api/auth/reset-password", new { token, password });

    [Fact]
    public async Task Forgot_password_answers_the_same_for_unknown_addresses_and_only_mails_real_accounts()
    {
        await RegisterAsync(factory, "vergeten@example.com");
        var known = await Client(factory).PostAsJsonAsync("/api/auth/forgot-password", new { email = " Vergeten@Example.com " });
        var unknown = await Client(factory).PostAsJsonAsync("/api/auth/forgot-password", new { email = "niemand-hier@example.com" });

        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.True(await WaitUntil(() => AccountMailKit.ResetMails(factory, "vergeten@example.com").Any(), TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(factory.Mail.Sent, m => m.To == "niemand-hier@example.com");
    }

    [Fact]
    public async Task Links_use_the_configured_address_and_not_the_host_of_the_request()
    {
        await RegisterAsync(factory, "host@example.com");
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/forgot-password") { Content = JsonContent.Create(new { email = "host@example.com" }) };
        req.Headers.Host = "aanvaller.example";
        req.Headers.Add("X-Forwarded-Host", "aanvaller.example");
        Assert.Equal(HttpStatusCode.OK, (await Client(factory).SendAsync(req)).StatusCode);
        Assert.True(await WaitUntil(() => AccountMailKit.ResetMails(factory, "host@example.com").Any(), TimeSpan.FromSeconds(5)));
        var body = AccountMailKit.ResetMails(factory, "host@example.com").Single().Body;
        Assert.Contains("https://potel.example/wachtwoord-herstellen#token=", body);
        Assert.DoesNotContain("aanvaller", body);
    }

    [Fact]
    public async Task Reset_link_sets_a_new_password_once_and_logs_out_every_other_session()
    {
        var owner = await RegisterAsync(factory, "herstel@example.com");
        var laptop = await LoginAsync(factory, "herstel@example.com");
        var first = await AccountMailKit.RequestResetTokenAsync(factory, "herstel@example.com");
        var second = await AccountMailKit.RequestResetTokenAsync(factory, "herstel@example.com");

        // Dezelfde regels als elders; een te kort wachtwoord verbruikt de link niet.
        var browser = Client(factory);
        var tooShort = await ResetAsync(browser, second, "kort");
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Contains("8 tekens", await tooShort.ErrorAsync());

        Assert.Equal(HttpStatusCode.OK, (await ResetAsync(browser, second, "nieuwgeheim1")).StatusCode);
        // Hier ben je meteen ingelogd; elke andere sessie is uitgelogd.
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/leads")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/leads")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/leads")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLoginAsync(factory, "herstel@example.com")).Status);
        await LoginAsync(factory, "herstel@example.com", "nieuwgeheim1");

        // Dezelfde link werkt geen tweede keer, en de andere open link ook niet meer.
        var again = await ResetAsync(Client(factory), second, "nogeenkeer1");
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("verlopen of al gebruikt", await again.ErrorAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(Client(factory), first, "nogeenkeer1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(Client(factory), "verzonnen-token", "nogeenkeer1")).StatusCode);
        await LoginAsync(factory, "herstel@example.com", "nieuwgeheim1");
    }

    [Fact]
    public async Task Only_a_hash_of_a_long_random_token_is_stored()
    {
        await RegisterAsync(factory, "hash@example.com");
        var token = await AccountMailKit.RequestResetTokenAsync(factory, "hash@example.com");
        Assert.True(Base64Url.DecodeFromChars(token).Length >= 32);

        var stored = factory.WithDb(db => db.AccountTokens.IgnoreQueryFilters()
            .Where(t => t.Email == "hash@example.com" && t.Purpose == TokenPurposes.PasswordReset).ToList());
        var row = Assert.Single(stored);
        Assert.DoesNotContain(token, row.TokenHash);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), row.TokenHash);
        Assert.InRange(row.ExpiresAt - row.CreatedAt, TimeSpan.FromMinutes(59), TimeSpan.FromMinutes(61));
    }

    [Fact]
    public async Task A_reset_link_stops_working_after_an_hour()
    {
        await RegisterAsync(factory, "na-een-uur@example.com");
        var token = await AccountMailKit.RequestResetTokenAsync(factory, "na-een-uur@example.com");
        factory.WithDb(db =>
        {
            foreach (var t in db.AccountTokens.IgnoreQueryFilters().Where(t => t.Email == "na-een-uur@example.com")) t.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            return db.SaveChanges();
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(Client(factory), token, "nieuwgeheim1")).StatusCode);
        await LoginAsync(factory, "na-een-uur@example.com");
    }

    [Fact]
    public async Task A_link_to_an_old_address_stops_working_after_an_email_change()
    {
        var c = await RegisterAsync(factory, "voor-wijziging@example.com");
        var token = await AccountMailKit.RequestResetTokenAsync(factory, "voor-wijziging@example.com");
        (await c.PutAsJsonAsync($"/api/users/{await MyIdAsync(c)}", new { name = "Eigenaar", email = "na-wijziging@example.com", role = "beheerder", active = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(Client(factory), token, "nieuwgeheim1")).StatusCode);
    }

    [Fact]
    public async Task One_address_gets_at_most_three_reset_mails_per_hour_and_the_answer_stays_the_same()
    {
        await RegisterAsync(factory, "veel-mail@example.com");
        var answers = new List<string>();
        for (var i = 0; i < 5; i++)
            answers.Add(await (await Client(factory).PostAsJsonAsync("/api/auth/forgot-password", new { email = "veel-mail@example.com" })).Content.ReadAsStringAsync());
        Assert.Single(answers.Distinct());
        Assert.True(await WaitUntil(() => AccountMailKit.ResetMails(factory, "veel-mail@example.com").Count() >= 3, TimeSpan.FromSeconds(5)));
        await Task.Delay(200);
        Assert.Equal(3, AccountMailKit.ResetMails(factory, "veel-mail@example.com").Count());
    }

    [Fact]
    public async Task A_disabled_account_gets_no_reset_mail()
    {
        var admin = await RegisterAsync(factory, "baas-van-uit@example.com");
        await CreateUserAsync(admin, "uitgeschakeld@example.com", active: false);
        Assert.Equal(HttpStatusCode.OK, (await Client(factory).PostAsJsonAsync("/api/auth/forgot-password", new { email = "uitgeschakeld@example.com" })).StatusCode);
        // De wachtrij werkt op volgorde: is de mail hierna er, dan was er voor het uitgeschakelde account niets.
        await AccountMailKit.RequestResetTokenAsync(factory, "baas-van-uit@example.com");
        Assert.DoesNotContain(factory.Mail.Sent, m => m.To == "uitgeschakeld@example.com");
    }

    [Fact]
    public async Task Changing_the_password_makes_open_reset_links_useless()
    {
        var c = await RegisterAsync(factory, "zelf-gewijzigd@example.com");
        var token = await AccountMailKit.RequestResetTokenAsync(factory, "zelf-gewijzigd@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync("/api/auth/password", new { current = "geheim123", @new = "andersgeheim1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ResetAsync(Client(factory), token, "nieuwgeheim1")).StatusCode);
    }

    [Fact]
    public async Task Expired_trial_can_still_reset_the_password()
    {
        var c = await RegisterAsync(factory, "verlopen-herstel@example.com");
        await ExpireTrialAsync(factory, await WorkspaceIdAsync(c));
        var token = await AccountMailKit.RequestResetTokenAsync(factory, "verlopen-herstel@example.com", c);
        Assert.Equal(HttpStatusCode.OK, (await ResetAsync(c, token, "nieuwgeheim1")).StatusCode);
        await LoginAsync(factory, "verlopen-herstel@example.com", "nieuwgeheim1");
    }

    [Fact]
    public async Task Account_mails_do_not_count_towards_the_workflow_mail_limits()
    {
        int TrialCounter() => factory.WithDb(db => db.PlatformCounters.Where(x => x.Key == PlatformCounter.TrialEmails).Select(x => x.Count).Single());
        var trialBefore = TrialCounter();
        var c = await RegisterAsync(factory, "telt-niet@example.com", verified: false);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/auth/verify-email/resend", null)).StatusCode);
        await AccountMailKit.RequestResetTokenAsync(factory, "telt-niet@example.com");

        var ws = await WorkspaceIdAsync(c);
        Assert.Equal(0, InWorkspace(factory, ws, db => db.Workspaces.Where(w => w.Id == ws).Select(w => w.EmailsSent).Single()));
        Assert.Equal(trialBefore, TrialCounter());
        Assert.Equal(3, factory.Mail.Sent.Count(m => m.To == "telt-niet@example.com"));
    }
}

// Na het aanmelden komt er een bevestigingsmail. Werken kan meteen, mail uit werkstromen pas na bevestigen.
public class EmailVerificationTests(AccountMailFactory factory) : IClassFixture<AccountMailFactory>
{
    static Task<HttpResponseMessage> VerifyAsync(HttpClient c, string token) => c.PostAsJsonAsync("/api/auth/verify-email", new { token });

    static string MailGraph(string to) => GraphJson(
        [Node("s", "trigger.manual"), Node("m", "action.email", new() { ["to"] = to, ["subject"] = "Hoi", ["body"] = "Beste klant," })],
        [Edge("s", "m")]);

    [Fact]
    public async Task Signup_sends_a_verification_link_that_confirms_the_address()
    {
        var c = await RegisterAsync(factory, "bevestig-mij@example.com", verified: false);
        Assert.False(await AccountMailKit.EmailVerifiedAsync(c));
        var token = AccountMailKit.LatestVerifyToken(factory, "bevestig-mij@example.com");

        // De link werkt ook in een browser waar je niet bent ingelogd, en nog een keer klikken is geen probleem.
        var res = await VerifyAsync(Client(factory), token);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("bevestig-mij@example.com", (await res.JsonAsync()).GetProperty("email").GetString());
        Assert.True(await AccountMailKit.EmailVerifiedAsync(c));
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(Client(factory), token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await VerifyAsync(Client(factory), "verzonnen")).StatusCode);
    }

    [Fact]
    public async Task The_configured_admin_and_existing_users_count_as_verified()
    {
        Assert.True(await AccountMailKit.EmailVerifiedAsync(await factory.LoginAsync()));
    }

    [Fact]
    public async Task Workflow_mail_waits_until_an_admin_confirmed_the_address()
    {
        var c = await RegisterAsync(factory, "wacht-op-link@example.com", verified: false);
        var ws = await WorkspaceIdAsync(c);
        var flow = await CreateWorkflowAsync(c, "Mail", MailGraph("klant-van-wacht@example.org"));

        var run = await RunAsync(c, flow);
        Assert.Equal("klaar", run.Status);
        Assert.Equal("let op", run.LastStatus);
        Assert.Contains("nog niet bevestigd", run.LastMessage);
        Assert.DoesNotContain(factory.Mail.Sent, m => m.To == "klant-van-wacht@example.org");
        Assert.Equal(0, InWorkspace(factory, ws, db => db.Workspaces.Where(w => w.Id == ws).Select(w => w.EmailsSent).Single()));

        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(c, AccountMailKit.LatestVerifyToken(factory, "wacht-op-link@example.com"))).StatusCode);
        Assert.Equal("klaar", (await RunAsync(c, flow)).Status);
        Assert.Single(factory.Mail.Sent, m => m.To == "klant-van-wacht@example.org");
    }

    [Fact]
    public async Task Changing_your_email_needs_a_new_confirmation_and_old_links_stop_working()
    {
        var c = await RegisterAsync(factory, "eerste-adres@example.com", verified: false);
        var oldToken = AccountMailKit.LatestVerifyToken(factory, "eerste-adres@example.com");
        var me = await MyIdAsync(c);
        (await c.PutAsJsonAsync($"/api/users/{me}", new { name = "Eigenaar", email = "tweede-adres@example.com", role = "beheerder", active = true })).EnsureSuccessStatusCode();

        Assert.False(await AccountMailKit.EmailVerifiedAsync(c));
        Assert.Equal(HttpStatusCode.BadRequest, (await VerifyAsync(c, oldToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(c, AccountMailKit.LatestVerifyToken(factory, "tweede-adres@example.com"))).StatusCode);
        Assert.True(await AccountMailKit.EmailVerifiedAsync(c));

        // Ook een bevestigd adres moet na een wijziging opnieuw bevestigd worden.
        (await c.PutAsJsonAsync($"/api/users/{me}", new { name = "Eigenaar", email = "derde-adres@example.com", role = "beheerder", active = true })).EnsureSuccessStatusCode();
        Assert.False(await AccountMailKit.EmailVerifiedAsync(c));
        Assert.Single(AccountMailKit.VerifyMails(factory, "derde-adres@example.com"));
    }

    [Fact]
    public async Task An_admin_changing_a_colleagues_email_makes_it_unverified_without_mailing_the_new_address()
    {
        var admin = await RegisterAsync(factory, "baas-wijzigt@example.com");
        var id = await CreateUserAsync(admin, "collega-oud@example.com");
        MarkVerified(factory, "collega-oud@example.com");
        (await admin.PutAsJsonAsync($"/api/users/{id}", new { name = "Collega", email = "collega-nieuw@example.com", role = "medewerker", active = true })).EnsureSuccessStatusCode();
        var colleague = await LoginAsync(factory, "collega-nieuw@example.com");
        Assert.False(await AccountMailKit.EmailVerifiedAsync(colleague));
        Assert.Empty(AccountMailKit.VerifyMails(factory, "collega-nieuw@example.com"));
        Assert.Equal(HttpStatusCode.OK, (await colleague.PostAsync("/api/auth/verify-email/resend", null)).StatusCode);
        Assert.Single(AccountMailKit.VerifyMails(factory, "collega-nieuw@example.com"));
    }

    [Fact]
    public async Task Resending_the_verification_mail_is_limited()
    {
        var c = await RegisterAsync(factory, "nog-een-keer@example.com", verified: false);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++) statuses.Add((await c.PostAsync("/api/auth/verify-email/resend", null)).StatusCode);
        // De mail bij het aanmelden telt mee: drie per uur.
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(3, AccountMailKit.VerifyMails(factory, "nog-een-keer@example.com").Count());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(factory).PostAsync("/api/auth/verify-email/resend", null)).StatusCode);
    }

    [Fact]
    public async Task Expired_trial_can_still_confirm_the_address()
    {
        var c = await RegisterAsync(factory, "verlopen-bevestig@example.com", verified: false);
        await ExpireTrialAsync(factory, await WorkspaceIdAsync(c));
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync("/api/auth/verify-email/resend", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(c, AccountMailKit.LatestVerifyToken(factory, "verlopen-bevestig@example.com"))).StatusCode);
        Assert.True(await AccountMailKit.EmailVerifiedAsync(c));
    }
}

// Zonder mailserver: in ontwikkelmodus staat de link in het log, en de gebruiker hoort dat er geen mail komt.
public class NoMailServerFactory : PortalFactory
{
    public LogCollector Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(l => l.AddProvider(Logs));
    }
}

public class NoMailServerTests(NoMailServerFactory factory) : IClassFixture<NoMailServerFactory>
{
    [Fact]
    public async Task Development_logs_the_link_and_tells_the_user_mail_is_not_set_up()
    {
        var res = await Client(factory).PostAsJsonAsync("/api/auth/forgot-password", new { email = "admin@potel.nl" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var error = await res.ErrorAsync();
        Assert.Contains("geen mailserver", error);
        Assert.Contains(factory.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("/wachtwoord-herstellen#token="));

        // Voor een onbekend adres hetzelfde antwoord.
        var unknown = await Client(factory).PostAsJsonAsync("/api/auth/forgot-password", new { email = "onbekend-adres@example.com" });
        Assert.Equal(res.StatusCode, unknown.StatusCode);
        Assert.Equal(error, await unknown.ErrorAsync());
    }
}

// Productie: links alleen met een ingesteld openbaar adres, nooit uit de Host-kop, en geen links in het log.
public class ProductionMailFactory(bool smtp, string? baseUrl) : PortalFactory
{
    public LogCollector Logs { get; } = new();
    public RecordingEmailSender Mail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
        if (baseUrl is not null) builder.UseSetting("App:BaseUrl", baseUrl);
        builder.ConfigureLogging(l => l.AddProvider(Logs));
        if (smtp) builder.ConfigureTestServices(s => s.AddSingleton<IEmailSender>(Mail));
    }
}

public class ProductionMailTests
{
    static async Task<HttpResponseMessage> ForgotAsync(WebApplicationFactory<Program> f, string email, string? host = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/forgot-password") { Content = JsonContent.Create(new { email }) };
        if (host is not null) req.Headers.Host = host;
        return await f.CreateClient().SendAsync(req);
    }

    [Fact]
    public async Task Without_a_mail_server_no_link_ends_up_in_the_log()
    {
        using var f = new ProductionMailFactory(smtp: false, baseUrl: "https://potel.example");
        Assert.Equal(HttpStatusCode.Created, (await TryRegisterAsync(f, "prod-zonder-mail@example.com")).Status);
        var res = await ForgotAsync(f, "prod-zonder-mail@example.com");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.DoesNotContain("serverlog", await res.ErrorAsync());
        Assert.DoesNotContain(f.Logs.Entries, e => e.Message.Contains("#token="));
    }

    [Fact]
    public async Task Without_a_public_address_nothing_is_mailed_whatever_the_host_header_says()
    {
        using var f = new ProductionMailFactory(smtp: true, baseUrl: null);
        Assert.Equal(HttpStatusCode.Created, (await TryRegisterAsync(f, "prod-zonder-adres@example.com")).Status);
        var res = await ForgotAsync(f, "prod-zonder-adres@example.com", host: "aanvaller.example");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        await Task.Delay(200);
        Assert.Empty(f.Mail.Sent);
        Assert.Contains(f.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("App:BaseUrl"));
    }

    [Fact]
    public async Task With_a_public_address_the_link_uses_it()
    {
        using var f = new ProductionMailFactory(smtp: true, baseUrl: "https://potel.jouwdomein.nl");
        Assert.Equal(HttpStatusCode.Created, (await TryRegisterAsync(f, "prod-met-adres@example.com")).Status);
        var verify = Assert.Single(f.Mail.Sent);
        Assert.Contains("https://potel.jouwdomein.nl/email-bevestigen#token=", verify.Body);
        Assert.Equal(HttpStatusCode.OK, (await ForgotAsync(f, "prod-met-adres@example.com", host: "aanvaller.example")).StatusCode);
        Assert.True(await WaitUntil(() => f.Mail.Sent.Count == 2, TimeSpan.FromSeconds(5)));
        Assert.Contains("https://potel.jouwdomein.nl/wachtwoord-herstellen#token=", f.Mail.Sent.Last().Body);
    }
}

// Inloggen en aanmelden begrensd per IP-adres; wachtwoord vergeten ook.
public class ForgotPasswordRateLimitTests
{
    [Fact]
    public async Task Forgot_password_is_rate_limited_per_address()
    {
        using var factory = new StrictRateLimitFactory();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            statuses.Add((await factory.CreateClient().PostAsJsonAsync("/api/auth/forgot-password", new { email = $"iemand{i}@example.com" })).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}

// Accounts van voor de bevestiging werkten al; die tellen als bevestigd.
public class EmailVerificationMigrationTests : IDisposable
{
    const string Before = "20261006175729_VoorbeelddataGrenzenEnOudeUren";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-migratie-{Guid.NewGuid():N}.db");

    AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path}").Options, new Tenant());

    [Fact]
    public void Existing_users_count_as_verified()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        using (var con = new SqliteConnection($"Data Source={path}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, WebhooksSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 0, 'Oud', 'zzp');
                INSERT INTO Users (Id, WorkspaceId, Name, Email, PasswordHash, Role, Active, CreatedAt, SecurityStamp, IsPlatformAdmin)
                    VALUES (1, 1, 'Oud', 'oud@example.com', 'x', 'beheerder', 1, '2026-01-02 00:00:00', 'stempel', 0);
                """;
            cmd.ExecuteNonQuery();
        }
        using (var db = Db()) db.Database.Migrate();
        using (var db = Db())
            Assert.Equal(new DateTime(2026, 1, 2), db.Users.IgnoreQueryFilters().Single().EmailVerifiedAt);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }
}
