using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Workflows;

namespace Potel.Api.Notifications;

// Instellingen onder "Notifications" in appsettings.json.
public class NotificationOptions
{
    // Zoveel losse mails met meldingen per gebruiker per dag; de rest komt in een overzicht.
    public int MailsPerUserPerDay { get; set; } = 20;
    // Vanaf hoe laat (Nederlandse tijd) het dagelijkse overzicht weggaat.
    public int DigestHour { get; set; } = 8;
    // Gelezen meldingen blijven zoveel dagen staan.
    public int KeepReadDays { get; set; } = 90;
    // Opent een klant zijn portaal na zoveel dagen weer, dan krijg je een melding.
    public int PortalQuietDays { get; set; } = 7;
}

// Mailt meldingen voor wie dat wil: los, zodra ze er zijn, of als één overzicht per dag. Via de wachtrij voor systeemmails en alleen
// naar actieve gebruikers met een bevestigd adres. Ze tellen mee voor de daglimiet van de werkruimte (en tijdens de proef voor die
// van alle proefwerkruimtes samen), maar mogen daar hooguit de helft van gebruiken: facturen en herinneringen moeten altijd nog
// weg kunnen. Per gebruiker gaan er hooguit MailsPerUserPerDay losse mails per dag weg; wat niet meer past, komt in het overzicht.
// Ruimt daarnaast eens per uur oude gelezen meldingen op.
public sealed class NotificationMailer(IServiceScopeFactory scopes, IEmailSender sender, SystemMailQueue queue, BusinessClock clock, AppUrls urls,
    IOptions<NotificationOptions> options, IOptions<WorkflowLimits> limits, IConfiguration config, ILogger<NotificationMailer> logger) : BackgroundService
{
    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    readonly SemaphoreSlim wake = new(0, 1);
    readonly SemaphoreSlim gate = new(1, 1);
    DateTime cleanedAt = DateTime.MinValue;
    // Ook de wachtende meldingen ophalen als er niets lijkt te wachten: de transactie die ze maakte, was misschien nog bezig.
    volatile bool woken;

    NotificationOptions O => options.Value;
    string Product => config["Platform:Name"] is { Length: > 0 } name ? name : "Potel";

    // Er wacht iets: zo snel mogelijk een ronde.
    public void Wake()
    {
        woken = true;
        try { wake.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await wake.WaitAsync(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Meldingen mailen mislukt");
            }
        }
    }

    // Eén ronde: losse mails, de dagelijkse overzichten en (eens per uur) opruimen. Hooguit één ronde tegelijk.
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var outgoing = new List<OutgoingEmail>();
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                var forced = woken;
                woken = false;
                // Alleen een schrijfslot als er echt iets weg kan: losse mails, of overzichten die vandaag nog niet gingen.
                var direct = forced || await db.Notifications.IgnoreQueryFilters().AnyAsync(n => n.Mail == NotificationMail.Direct, ct);
                var digests = clock.Now.Hour >= O.DigestHour
                    && await DigestWaiting(db).AnyAsync(ct);
                if (direct || digests)
                {
                    await using var tx = await WriteLock.BeginAsync(db);
                    if (!sender.Configured) await SkipAllAsync(db);
                    else
                    {
                        outgoing.AddRange(await DirectAsync(db, ct));
                        outgoing.AddRange(await DigestsAsync(db, ct));
                    }
                    await tx.CommitAsync();
                }
                if (clock.UtcNow - cleanedAt > TimeSpan.FromHours(1))
                {
                    await CleanupAsync(db);
                    cleanedAt = clock.UtcNow;
                }
            }
            foreach (var mail in outgoing)
                if (!queue.TryEnqueue(mail)) logger.LogWarning("Mail met meldingen \"{Subject}\" niet in de wachtrij gezet", mail.Subject);
        }
        finally
        {
            gate.Release();
        }
    }

    // Weg met gelezen meldingen ouder dan KeepReadDays, en met meldingen die alleen per e-mail waren en al weg zijn.
    public async Task<int> CleanupAsync(AppDb db)
    {
        var cutoff = clock.UtcNow.AddDays(-Math.Max(1, O.KeepReadDays));
        return await db.Notifications.IgnoreQueryFilters()
            .Where(n => (n.ReadAt != null && n.ReadAt < cutoff)
                        || (!n.InApp && n.CreatedAt < cutoff && n.Mail != NotificationMail.Direct && n.Mail != NotificationMail.Digest))
            .ExecuteDeleteAsync();
    }

    // Zonder mailserver gaat er niets weg; dan ook later niet ineens alles tegelijk.
    static Task SkipAllAsync(AppDb db) => db.Notifications.IgnoreQueryFilters()
        .Where(n => n.Mail == NotificationMail.Direct || n.Mail == NotificationMail.Digest)
        .ExecuteUpdateAsync(s => s.SetProperty(n => n.Mail, NotificationMail.Skipped));

    static Task SetMailAsync(AppDb db, int id, string mail, DateTime? at = null) => db.Notifications.IgnoreQueryFilters().Where(n => n.Id == id)
        .ExecuteUpdateAsync(s => s.SetProperty(n => n.Mail, mail).SetProperty(n => n.MailedAt, at));

    // Een ontvanger die nog mail mag krijgen: actief, met een bevestigd adres, en de soort mag hij (nog) zien.
    static bool CanReceive(User? u, string kind) =>
        u is { Active: true, EmailVerifiedAt: not null } && EmailRules.IsSingleAddress(u.Email) && NotificationKinds.Allowed(kind, u);

    // Wil hij deze soort (nog) per e-mail? Wie het intussen uitzette, krijgt hem niet meer.
    static bool Wants(NotificationSettings s, string kind) => NotificationKinds.Split(s.EmailOn).Contains(kind);

    async Task<List<OutgoingEmail>> DirectAsync(AppDb db, CancellationToken ct)
    {
        var mails = new List<OutgoingEmail>();
        var pending = await db.Notifications.IgnoreQueryFilters().AsNoTracking().Where(n => n.Mail == NotificationMail.Direct)
            .OrderBy(n => n.Id).Take(200).ToListAsync(ct);
        foreach (var n in pending)
        {
            db.Tenant.WorkspaceId = n.WorkspaceId;
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == n.UserId, ct);
            if (!CanReceive(user, n.Kind) || !Wants(await SettingsAsync(db, n.UserId), n.Kind))
            {
                await SetMailAsync(db, n.Id, NotificationMail.Skipped);
                continue;
            }
            // Over de grens per gebruiker of de helft van de daglimiet: dan in het eerstvolgende overzicht.
            var counted = await ReserveUserAsync(db, user!.Id);
            if (!counted || !await ReserveQuotaAsync(db))
            {
                if (counted) await ReleaseUserAsync(db, user.Id);
                await SetMailAsync(db, n.Id, NotificationMail.Digest);
                continue;
            }
            await SetMailAsync(db, n.Id, NotificationMail.Sent, clock.UtcNow);
            mails.Add(new OutgoingEmail(user.Email, n.Title, Body(user, n), Product));
        }
        return mails;
    }

    // Meldingen voor een overzicht, van gebruikers die er vandaag nog geen kregen.
    IQueryable<Notification> DigestWaiting(AppDb db)
    {
        var today = clock.Today;
        var sentToday = db.NotificationSettings.IgnoreQueryFilters().Where(s => s.DigestSentOn == today).Select(s => s.UserId);
        return db.Notifications.IgnoreQueryFilters().Where(n => n.Mail == NotificationMail.Digest && !sentToday.Contains(n.UserId));
    }

    // Eén overzicht per gebruiker per dag, vanaf DigestHour. Wat je in de app al gelezen hebt, staat er niet meer in.
    async Task<List<OutgoingEmail>> DigestsAsync(AppDb db, CancellationToken ct)
    {
        var mails = new List<OutgoingEmail>();
        if (clock.Now.Hour < O.DigestHour) return mails;
        var today = clock.Today;
        var waiting = await DigestWaiting(db).AsNoTracking().Select(n => new { n.WorkspaceId, n.UserId }).Distinct().ToListAsync(ct);
        foreach (var w in waiting)
        {
            db.Tenant.WorkspaceId = w.WorkspaceId;
            var settings = await SettingsAsync(db, w.UserId);
            if (settings.DigestSentOn == today) continue;
            var rows = await db.Notifications.AsNoTracking().Where(n => n.UserId == w.UserId && n.Mail == NotificationMail.Digest)
                .OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).ToListAsync(ct);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == w.UserId, ct);
            var unread = rows.Where(n => (!n.InApp || n.ReadAt == null) && CanReceive(user, n.Kind) && Wants(settings, n.Kind)).ToList();
            foreach (var n in rows.Except(unread)) await SetMailAsync(db, n.Id, NotificationMail.Skipped);
            if (unread.Count == 0) continue;
            // De daglimiet is op: de volgende ronde opnieuw proberen.
            if (!await ReserveQuotaAsync(db)) continue;
            await db.NotificationSettings.Where(s => s.Id == settings.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.DigestSentOn, today));
            foreach (var n in unread) await SetMailAsync(db, n.Id, NotificationMail.Sent, clock.UtcNow);
            mails.Add(new OutgoingEmail(user!.Email, DigestSubject(unread.Count), DigestBody(user, unread, settings.Digest), Product));
        }
        return mails;
    }

    // De instellingen van een gebruiker, en een rij met de standaarden als die er nog niet is. Altijd binnen een WriteLock.
    public static async Task<NotificationSettings> SettingsAsync(AppDb db, int userId)
    {
        if (await db.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId) is { } s) return s;
        s = new NotificationSettings { UserId = userId, EmailOn = NotificationKinds.Join(NotificationKinds.EmailByDefault) };
        db.NotificationSettings.Add(s);
        await db.SaveChangesAsync();
        db.Entry(s).State = EntityState.Detached;
        return s;
    }

    // Telt een losse mail mee voor deze gebruiker. Onwaar als hij er vandaag al MailsPerUserPerDay kreeg.
    async Task<bool> ReserveUserAsync(AppDb db, int userId)
    {
        var settings = await SettingsAsync(db, userId);
        var limit = Math.Max(0, O.MailsPerUserPerDay);
        var today = clock.Today;
        return await db.NotificationSettings
            .Where(s => s.Id == settings.Id && (s.MailDay != today || s.MailsSent < limit))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.MailsSent, x => x.MailDay == today ? x.MailsSent + 1 : 1)
                .SetProperty(x => x.MailDay, today)) == 1;
    }

    // De losse mail ging toch niet weg: niet meetellen.
    static Task ReleaseUserAsync(AppDb db, int userId) => db.NotificationSettings.Where(s => s.UserId == userId && s.MailsSent > 0)
        .ExecuteUpdateAsync(s => s.SetProperty(x => x.MailsSent, x => x.MailsSent - 1));

    // Telt mee voor de daglimiet van de werkruimte (MailQuota), maar alleen zolang die nog voor minder dan de helft gebruikt is.
    async Task<bool> ReserveQuotaAsync(AppDb db)
    {
        var plan = await MailQuota.PlanAsync(db);
        if (!await MailQuota.ReserveWorkspaceAsync(db, limits.Value.EmailsPerDay(plan) / 2)) return false;
        return plan != Plans.Trial || await MailQuota.ReservePlatformAsync(db, PlatformCounter.TrialEmails, limits.Value.EmailsPerDayAllTrials / 2);
    }

    static string FirstName(User u) => u.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? u.Name;

    string? Url(string? path) => urls.Configured is { } b && path is { Length: > 0 } && path.StartsWith('/') ? b + path : null;

    string Footer(string why)
    {
        var settings = Url("/instellingen?tab=meldingen");
        return $"{why} Dat pas je aan in {Product} onder Instellingen, Meldingen" + (settings is null ? "." : $":\n{settings}");
    }

    string Body(User u, Notification n)
    {
        var b = new StringBuilder($"Hallo {FirstName(u)},\n\n{n.Detail ?? n.Text}\n");
        if (Url(n.Link) is { } link) b.Append($"\nBekijk het in {Product}:\n{link}\n");
        var label = NotificationKinds.Find(n.Kind)?.Label.ToLowerInvariant() ?? "dit onderwerp";
        b.Append($"\n{Footer($"Je krijgt deze mail omdat je meldingen over {label} ook per e-mail wilt.")}\n");
        return b.ToString();
    }

    public static string DigestSubject(int count) => count == 1 ? "Je overzicht van vandaag: 1 melding" : $"Je overzicht van vandaag: {count} meldingen";

    string DigestBody(User u, List<Notification> items, bool digest)
    {
        var b = new StringBuilder($"Hallo {FirstName(u)},\n\n");
        b.Append(digest
            ? "Dit is er gebeurd sinds je vorige overzicht:\n"
            : "Je kreeg op één dag zoveel meldingen per e-mail dat we de rest hier voor je verzamelen:\n");
        foreach (var n in items)
        {
            var at = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(n.CreatedAt, DateTimeKind.Utc), BusinessClock.Zone);
            b.Append($"\n{at.ToString("d MMM HH:mm", Dutch)}  {n.Text}\n");
            if (Url(n.Link) is { } link) b.Append($"{link}\n");
        }
        if (Url("/meldingen") is { } all) b.Append($"\nAl je meldingen:\n{all}\n");
        b.Append($"\n{Footer(digest ? "Je krijgt dit overzicht omdat je je meldingen één keer per dag per e-mail wilt." : "Je krijgt dit overzicht omdat je meldingen per e-mail wilt.")}\n");
        return b.ToString();
    }
}
