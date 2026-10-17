using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Notifications;

public record NotificationKind(string Id, string Label, string Description, string For);

// De soorten meldingen, zoals de gebruiker ze in zijn instellingen ziet, en voor wie ze zijn: geld alleen voor beheerders,
// het platform alleen voor platformbeheerders. Werkstromen zijn voor de beheerders en voor wie een run zelf startte.
public static class NotificationKinds
{
    public const string Quote = "offerte";
    public const string Payment = "betaling";
    public const string Overdue = "te-laat";
    public const string Recurring = "terugkerend";
    public const string FixedCost = "vaste-kosten";
    public const string Portal = "portaal";
    public const string Vat = "btw";
    public const string Workflow = "werkstroom";
    public const string Backup = "back-up";

    public const string Admins = "beheerders";
    public const string Everyone = "iedereen";
    public const string Platform = "platform";

    public static readonly NotificationKind[] All =
    [
        new(Quote, "Offertes", "Een klant accepteert of wijst een offerte af.", Admins),
        new(Payment, "Betalingen", "Er is betaald op een factuur: met iDEAL, via de bank of met de hand.", Admins),
        new(Overdue, "Te late facturen", "Een factuur is over de vervaldatum, of er ging een automatische herinnering uit.", Admins),
        new(Recurring, "Terugkerende facturen", "Een terugkerende factuur is klaargezet of verstuurd.", Admins),
        new(FixedCost, "Vaste kosten", "Een vaste kost staat als concept klaar of is aan de beurt.", Admins),
        new(Portal, "Klantportaal", "Een klant opent zijn portaal voor het eerst, of na een stille periode weer.", Admins),
        new(Vat, "Btw-aangifte", "Een week voor de deadline van je btw-aangifte, en op de laatste dag.", Admins),
        new(Workflow, "Werkstromen", "Een werkstroom loopt vast.", Everyone),
        new(Backup, "Back-ups", "De dagelijkse back-up van de database is mislukt of overgeslagen.", Platform),
    ];

    // Wat al per e-mail kwam voordat er meldingen waren: een mail als je klant over een offerte besluit.
    public static readonly string[] EmailByDefault = [Quote];

    public static NotificationKind? Find(string? id) => All.FirstOrDefault(k => k.Id == id);

    public static bool Allowed(NotificationKind kind, string role, bool platformAdmin) => kind.For switch
    {
        Admins => role == Roles.Admin,
        Platform => platformAdmin,
        _ => true,
    };

    public static bool Allowed(string kind, User u) => Find(kind) is { } k && Allowed(k, u.Role, u.IsPlatformAdmin);

    // De soorten die deze gebruiker mag krijgen en zien.
    public static List<NotificationKind> For(string role, bool platformAdmin) => All.Where(k => Allowed(k, role, platformAdmin)).ToList();

    public static string[] Split(string? list) => (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string Join(IEnumerable<string> kinds) => string.Join(",", kinds.Where(k => Find(k) is not null).Distinct().Order(StringComparer.Ordinal));
}

// Eén gebeurtenis: de soort, een sleutel die hem uniek maakt (zoals betaling:12), de onderwerpregel voor een mail, de zin in de bel,
// een pad in de app en eventueel meer uitleg voor in de mail.
public record NotificationEvent(string Kind, string Key, string Title, string Text, string? Link = null, string? Detail = null);

// Maakt meldingen. Per ontvanger hooguit één per sleutel (INSERT OR IGNORE op de unieke index), dus een gebeurtenis die twee keer
// langskomt, bijvoorbeeld een webhook die Mollie herhaalt, geeft geen tweede melding. Rechtstreeks in SQL, zodat het niets doet met
// wat de aanroeper nog aan wijzigingen open heeft staan, en binnen diens transactie valt als die er is. Een fout hier houdt de
// gebeurtenis zelf nooit tegen: een betaling telt ook als de melding niet lukt.
public partial class Notifier(AppDb db, BusinessClock clock, Workflows.IEmailSender sender, NotificationMailer mailer, ILogger<Notifier> logger)
{
    AppDb Db => db;
    BusinessClock Clock => clock;

    // Voor de beheerders van de werkruimte waarin db nu werkt, plus eventueel gebruikers uit die werkruimte voor wie het over hun eigen
    // werk gaat (zoals wie een werkstroom startte). Except is wie het zelf deed: die ziet het al.
    public async Task<int> ToWorkspaceAsync(NotificationEvent e, int? except = null, params int?[] alsoUsers)
    {
        try
        {
            if (db.TenantId == 0) throw new InvalidOperationException("Geen werkruimte bekend voor deze melding");
            var extra = alsoUsers.OfType<int>().ToList();
            var users = await db.Users.AsNoTracking().Where(u => u.Active && (u.Role == Roles.Admin || extra.Contains(u.Id))).ToListAsync();
            return await CreateAsync(e, users.Where(u => u.Id != except).ToList());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Melding {Key} niet gemaakt", e.Key);
            return 0;
        }
    }

    // Voor alle platformbeheerders, in welke werkruimte ze ook zitten. Elke melding hoort bij de werkruimte van de ontvanger.
    public async Task<int> ToPlatformAdminsAsync(NotificationEvent e)
    {
        try
        {
            var users = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Active && u.IsPlatformAdmin).ToListAsync();
            return await CreateAsync(e, users);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Melding {Key} niet gemaakt", e.Key);
            return 0;
        }
    }

    async Task<int> CreateAsync(NotificationEvent e, List<User> recipients)
    {
        var kind = NotificationKinds.Find(e.Kind) ?? throw new ArgumentException($"Onbekende soort melding {e.Kind}", nameof(e));
        recipients = recipients.Where(u => NotificationKinds.Allowed(kind, u.Role, u.IsPlatformAdmin)).DistinctBy(u => u.Id).ToList();
        if (recipients.Count == 0) return 0;
        var ids = recipients.Select(u => u.Id).ToList();
        var settings = await db.NotificationSettings.IgnoreQueryFilters().AsNoTracking().Where(s => ids.Contains(s.UserId)).ToDictionaryAsync(s => s.UserId);
        var title = Workflows.EmailRules.OneLine(e.Title, 200);
        var text = Workflows.EmailRules.OneLine(e.Text, 500);
        var detail = e.Detail is { Length: > 4000 } d ? d[..4000] : e.Detail;
        var key = e.Key.Length > 200 ? e.Key[..200] : e.Key;
        var now = clock.UtcNow;
        var created = 0;
        var wake = false;
        foreach (var u in recipients)
        {
            var s = settings.GetValueOrDefault(u.Id);
            var inApp = !NotificationKinds.Split(s?.InAppOff).Contains(kind.Id);
            var email = sender.Configured && (s is null ? NotificationKinds.EmailByDefault : NotificationKinds.Split(s.EmailOn)).Contains(kind.Id);
            if (!inApp && !email) continue;
            string? mail = email ? s?.Digest == true ? NotificationMail.Digest : NotificationMail.Direct : null;
            var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT OR IGNORE INTO "Notifications" ("WorkspaceId", "UserId", "Kind", "Key", "Title", "Text", "Detail", "Link", "CreatedAt", "InApp", "Mail")
                VALUES ({u.WorkspaceId}, {u.Id}, {kind.Id}, {key}, {title}, {text}, {detail}, {e.Link}, {now}, {inApp}, {mail})
                """);
            created += rows;
            if (rows == 1 && mail is not null) wake = true;
        }
        // De mailer wacht tot een lopende transactie klaar is (WriteLock) en ziet de nieuwe meldingen dan meteen.
        if (wake) mailer.Wake();
        return created;
    }
}
