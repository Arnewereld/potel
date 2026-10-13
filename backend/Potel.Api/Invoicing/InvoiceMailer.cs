using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Payments;
using Potel.Api.Workflows;

namespace Potel.Api.Invoicing;

public enum SendOutcome { Sent, Blocked, Failed, AlreadySent }

public record MailAttempt(SendOutcome Outcome, string? Message = null, InvoiceMail? Mail = null);

// Mailt een verstuurde factuur of een herinnering naar het e-mailadres van de klant, met een openbare link naar de factuur.
// Potel maakt zelf geen PDF (dat kan niet goed zonder extra pakket); de link opent de factuur, met een knop om hem als PDF
// op te slaan of te printen. Elke mail telt mee voor de daglimiet van de werkruimte en gaat alleen weg als de beheerder zijn
// e-mailadres bevestigde. Alles komt in het logboek van de factuur, ook wat niet weg kon.
public class InvoiceMailer(AppDb db, IEmailSender sender, IOptions<WorkflowLimits> limits, BusinessClock clock, InvoiceIdeal ideal, ILogger<InvoiceMailer> logger)
{
    public const string NoServer = "Er is nog geen mailserver ingesteld, dus Potel kan zelf geen e-mail versturen.";
    public const string NoBaseUrl = "Het openbare adres van Potel ontbreekt (App:BaseUrl), dus we kunnen geen link naar de factuur maken.";
    public const string NotVerified = "Het e-mailadres van de beheerder van deze werkruimte is nog niet bevestigd. Klik op de link in de bevestigingsmail; daarna kan Potel e-mail versturen.";
    public const string NoEmail = "Deze klant heeft geen geldig e-mailadres. Vul bij de klant precies één e-mailadres in.";
    public const string LinkPath = "/factuur";

    // 32 willekeurige bytes als base64url. Alleen de SHA-256-hash gaat de database in.
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string token) => AccountMail.Hash(token);
    // Het token staat achter een #, zodat het niet in logboeken van servers of proxy's belandt en niet wordt doorgestuurd.
    public static string Link(string baseUrl, string token) => $"{baseUrl}{LinkPath}#token={token}";

    public static IQueryable<Invoice> WithAll(AppDb db) =>
        db.Invoices.Include(i => i.Lines).Include(i => i.Customer).Include(i => i.Credits).ThenInclude(c => c.Lines).Include(i => i.Payments).AsSplitQuery();

    // Het e-mailadres van de klant als het precies één geldig adres is, anders null.
    public static string? CustomerEmail(Customer? c) => EmailRules.IsSingleAddress(c?.Email?.Trim()) ? c!.Email!.Trim() : null;

    // Wat de werkruimte tegenhoudt om te mailen, of null. Geldt voor alle facturen tegelijk.
    public async Task<string?> BlockerAsync(string? baseUrl)
    {
        if (!sender.Configured) return NoServer;
        if (baseUrl is null) return NoBaseUrl;
        if (!await db.Users.AnyAsync(u => u.Role == Roles.Admin && u.Active && u.EmailVerifiedAt != null)) return NotVerified;
        return null;
    }

    // Wat er mis is om deze stap voor deze factuur te mailen, of null. De factuur moet met klant, regels en creditnota's geladen zijn.
    public static string? Problem(Invoice inv, int step, DateTime today)
    {
        if (inv.IsDemo || inv.Customer?.IsDemo == true) return "Dit is een voorbeeldfactuur. Die mailt Potel niet.";
        if (inv.Status == InvoiceStatus.Draft) return "Verstuur de factuur eerst. Dan krijgt hij een nummer en ligt hij vast.";
        if (step is < MailSteps.Invoice or > MailSteps.Final) return "Kies wat je wilt sturen.";
        if (step > MailSteps.Invoice)
        {
            if (inv.IsCredit) return "Een creditnota hoeft niemand te betalen, dus daar gaat geen herinnering over.";
            if (inv.Status == InvoiceStatus.Paid) return "Deze factuur is al betaald.";
            if (inv.FullyCredited || inv.OpenAmount <= 0) return "Er staat niets meer open op deze factuur.";
            if (inv.DueDate.Date >= today) return "Deze factuur is nog niet te laat. Een herinnering kan vanaf de dag na de vervaldatum.";
        }
        return CustomerEmail(inv.Customer) is null ? NoEmail : null;
    }

    // "Herinnering voor factuur 2026-0012", of bij de factuur zelf "Factuur 2026-0012".
    public static string What(Invoice inv, int step) =>
        step == MailSteps.Invoice ? InvoiceEndpoints.Label(inv) : $"{MailSteps.Label(step)} voor factuur {inv.Number}";

    // De mail zoals hij weg zou gaan. Zonder link blijft {link} staan, zoals in het voorbeeld voordat je verstuurt.
    public async Task<MailText> ComposeAsync(Invoice inv, int step, string? link, string? subject = null, string? body = null)
    {
        var settings = await SettingsEndpoints.GetAsync(db);
        var reminders = await db.ReminderSettings.AsNoTracking().FirstOrDefaultAsync();
        var text = MailTemplates.For(reminders, step, inv.IsCredit);
        if (!string.IsNullOrWhiteSpace(subject)) text = text with { Subject = subject };
        if (!string.IsNullOrWhiteSpace(body)) text = text with { Body = body };
        // Met de eigen Mollie van de werkruimte kan de klant ook met iDEAL betalen; dat staat dan in de betaalgegevens.
        var online = InvoiceIdeal.Payable(inv, inv.Customer?.IsDemo == true) && await ideal.AvailableAsync(db, inv.WorkspaceId);
        return MailTemplates.Fill(text, inv, inv.Customer!, settings, clock.Today, link, online);
    }

    // Mailt één stap. Automatisch gaat elke stap hooguit één keer: de controle en de unieke index op (factuur, stap) zorgen
    // dat twee rondes tegelijk er samen maar één sturen. Handmatig mag je een stap opnieuw sturen.
    public async Task<MailAttempt> SendAsync(int invoiceId, int step, bool automatic, string? baseUrl, string? sentBy = null,
        string? subject = null, string? body = null, CancellationToken ct = default)
    {
        if (await BlockerAsync(baseUrl) is { } blocker) return new(SendOutcome.Blocked, blocker);
        if (subject is { Length: > 200 } || body is { Length: > 5000 }) return new(SendOutcome.Blocked, "Het onderwerp of de tekst is te lang.");
        db.ChangeTracker.Clear();
        InvoiceMail mail;
        InvoiceLink link;
        OutgoingEmail outgoing;
        string what;
        await using (var tx = await WriteLock.BeginAsync(db))
        {
            var inv = await WithAll(db).FirstOrDefaultAsync(i => i.Id == invoiceId);
            if (inv is null) return new(SendOutcome.Blocked, "Deze factuur bestaat niet (meer).");
            if (Problem(inv, step, clock.Today) is { } problem) return new(SendOutcome.Blocked, problem);
            if (automatic && await db.InvoiceMails.AnyAsync(m => m.InvoiceId == invoiceId && m.Step == step
                    && (m.Status == MailStatus.Sent || m.Status == MailStatus.Sending)))
                return new(SendOutcome.AlreadySent);
            // Telt mee voor de daglimiet; gaat er hierna iets mis, dan draait de transactie de telling terug.
            var quota = await MailQuota.ReserveAsync(db, limits.Value);
            if (quota != QuotaResult.Ok) return new(SendOutcome.Blocked, MailQuota.Message(quota, limits.Value.EmailsPerDay(await MailQuota.PlanAsync(db))));

            var token = NewToken();
            var text = await ComposeAsync(inv, step, Link(baseUrl!, token), subject, body);
            var settings = await SettingsEndpoints.GetAsync(db);
            var to = CustomerEmail(inv.Customer)!;
            link = new InvoiceLink { InvoiceId = inv.Id, TokenHash = Hash(token), CreatedAt = clock.UtcNow };
            mail = new InvoiceMail { InvoiceId = inv.Id, Step = step, To = to, Subject = text.Subject, Automatic = automatic, SentBy = sentBy, CreatedAt = clock.UtcNow };
            db.InvoiceLinks.Add(link);
            db.InvoiceMails.Add(mail);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                // De unieke index: een andere ronde stuurt deze stap al.
                db.ChangeTracker.Clear();
                return new(SendOutcome.AlreadySent);
            }
            await tx.CommitAsync();
            what = What(inv, step);
            // Van het adres van het platform, met je bedrijfsnaam als afzender; antwoorden gaan naar je eigen e-mailadres.
            outgoing = new OutgoingEmail(to, text.Subject, text.Body, inv.Seller?.Name ?? settings.CompanyName, settings.Email);
        }

        try
        {
            await sender.SendAsync(outgoing, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Mail over factuur {Invoice} niet verstuurd", invoiceId);
            // Het token heeft de klant nooit gekregen; de link mag dus meteen dicht.
            mail.Status = MailStatus.Failed;
            mail.Note = "De mailserver gaf een fout.";
            link.RevokedAt = clock.UtcNow;
            db.Log("e-mail", $"{what} aan {outgoing.To} kon niet worden verstuurd: de mailserver gaf een fout.");
            await db.SaveChangesAsync();
            return new(SendOutcome.Failed, "De mail kon niet worden verstuurd: de mailserver gaf een fout. Probeer het later opnieuw.", mail);
        }
        mail.Status = MailStatus.Sent;
        mail.SentAt = clock.UtcNow;
        db.Log("e-mail", $"{what} gemaild aan {outgoing.To}{(automatic ? " (automatisch)" : "")}");
        await db.SaveChangesAsync();
        return new(SendOutcome.Sent, null, mail);
    }

    // Een automatische mail kon niet weg om een reden bij deze factuur (zoals een ontbrekend e-mailadres). Dat zetten we
    // één keer per stap en reden in het logboek, niet elke ronde opnieuw.
    public async Task NoteSkippedAsync(Invoice inv, int step, string reason)
    {
        if (await db.InvoiceMails.AnyAsync(m => m.InvoiceId == inv.Id && m.Step == step && m.Status == MailStatus.Skipped && m.Note == reason)) return;
        db.InvoiceMails.Add(new InvoiceMail
        {
            InvoiceId = inv.Id, Step = step, To = inv.Customer?.Email?.Trim() ?? "", Status = MailStatus.Skipped, Note = reason, Automatic = true, CreatedAt = clock.UtcNow,
        });
        db.Log("e-mail", $"{What(inv, step)} niet verstuurd: {reason}");
        await db.SaveChangesAsync();
    }
}
