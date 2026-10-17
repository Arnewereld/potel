using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Notifications;
using Potel.Api.Workflows;

namespace Potel.Api.Invoicing;

// Automatische herinneringen bij te late facturen, in drie stappen: een vriendelijke herinnering, een tweede herinnering en
// een aanmaning, elk een ingesteld aantal dagen na de vervaldatum. Stappen gaan altijd op volgorde en met tussenruimte, ook als
// je herinneringen pas aanzet bij een factuur die al lang open staat. Een betaalde, helemaal gecrediteerde of voorbeeldfactuur
// krijgt er nooit een, en ook niet als je ze voor de factuur of de klant hebt uitgezet.
public class ReminderService(AppDb db, BusinessClock clock, InvoiceMailer mailer, AppUrls urls, Notifier notifier, IOptions<InvoicingLimits> options)
{
    // Na een fout van de mailserver proberen we dezelfde stap pas na een paar uur opnieuw.
    static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

    public static async Task<ReminderSettings> SettingsAsync(AppDb db)
    {
        var s = await db.ReminderSettings.FirstOrDefaultAsync();
        if (s is not null) return s;
        s = new ReminderSettings();
        db.ReminderSettings.Add(s);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Een ander verzoek maakte hem net tegelijk aan (unieke index op de werkruimte).
            db.ChangeTracker.Clear();
            s = await db.ReminderSettings.FirstAsync();
        }
        return s;
    }

    static DateTime LocalDay(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), BusinessClock.Zone).Date;

    // De volgende stap voor deze factuur en vanaf welke dag hij mag, of null als alle stappen geweest zijn.
    // Een stap telt als gedaan als hij verstuurd is, handmatig of automatisch (of net onderweg is).
    public static (int Step, DateTime On)? Next(Invoice inv, IEnumerable<InvoiceMail> mails, ReminderSettings s)
    {
        int[] days = [0, s.FirstDays, s.SecondDays, s.FinalDays];
        var done = mails.Where(m => m.InvoiceId == inv.Id && m.Step >= MailSteps.First && m.Status is MailStatus.Sent or MailStatus.Sending).ToList();
        var last = done.Count == 0 ? 0 : done.Max(m => m.Step);
        if (last >= MailSteps.Final) return null;
        var step = last + 1;
        var on = inv.DueDate.Date.AddDays(days[step]);
        if (last > 0)
        {
            // Minstens zoveel dagen na de vorige stap als ertussen hoort te zitten.
            var previous = LocalDay(done.Where(m => m.Step == last).Max(m => m.SentAt ?? m.CreatedAt));
            var gap = previous.AddDays(Math.Max(1, days[step] - days[last]));
            if (gap > on) on = gap;
        }
        return (step, on);
    }

    // Facturen die een herinnering kunnen krijgen: verstuurd, voorbij de vervaldatum, nog iets open, en niet uitgezet.
    async Task<List<Invoice>> CandidatesAsync(DateTime today) =>
        (await InvoiceMailer.WithAll(db).AsNoTracking()
            .Where(i => (i.Status == InvoiceStatus.Sent || i.Status == InvoiceStatus.Overdue) && i.DueDate < today && i.CreditForInvoiceId == null
                        && !i.IsDemo && !i.RemindersOff && !i.Customer!.IsDemo && !i.Customer.RemindersOff)
            .OrderBy(i => i.DueDate).ThenBy(i => i.Id).ToListAsync())
        .Where(i => !i.IsCredit && !i.FullyCredited && i.OpenAmount > 0).ToList();

    // Eén ronde voor deze werkruimte. Geeft terug hoeveel herinneringen er weg gingen.
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        // Zonder instellingen staan herinneringen uit; die rij maken we pas als een beheerder ze opslaat.
        var settings = await db.ReminderSettings.FirstOrDefaultAsync(ct);
        if (settings is not { Enabled: true }) return 0;
        var today = clock.Today;
        var candidates = await CandidatesAsync(today);
        if (candidates.Count == 0) return 0;
        var ids = candidates.Select(i => i.Id).ToList();
        var mails = await db.InvoiceMails.AsNoTracking().Where(m => ids.Contains(m.InvoiceId)).ToListAsync();
        var due = candidates.Select(i => (Invoice: i, Next: Next(i, mails, settings)))
            .Where(x => x.Next is { } n && n.On <= today)
            // Na een fout van de mailserver even wachten met dezelfde stap.
            .Where(x => !mails.Any(m => m.InvoiceId == x.Invoice.Id && m.Step == x.Next!.Value.Step && m.Status == MailStatus.Failed
                                        && m.CreatedAt > clock.UtcNow - RetryAfter))
            .ToList();
        if (due.Count == 0) return 0;

        // Kan er in deze werkruimte helemaal geen mail weg, dan één melding per dag in plaats van één per factuur per ronde.
        var baseUrl = urls.Configured;
        if (await mailer.BlockerAsync(baseUrl) is { } blocker)
        {
            await NoticeAsync(settings, today, blocker, due.Count);
            return 0;
        }

        var sent = 0;
        foreach (var (inv, next) in due.Take(Math.Max(1, options.Value.MaxRemindersPerRun)))
        {
            if (ct.IsCancellationRequested) break;
            var step = next!.Value.Step;
            if (InvoiceMailer.Problem(inv, step, today) is { } problem)
            {
                await mailer.NoteSkippedAsync(inv, step, problem);
                continue;
            }
            var result = await mailer.SendAsync(inv.Id, step, automatic: true, baseUrl, ct: ct);
            if (result.Outcome == SendOutcome.Sent)
            {
                sent++;
                await notifier.ReminderSentAsync(inv, step);
            }
            else if (result.Outcome == SendOutcome.Blocked)
            {
                // De daglimiet is bereikt (of iets anders voor de hele werkruimte): de rest wacht tot een volgende ronde.
                db.ChangeTracker.Clear();
                await NoticeAsync(await db.ReminderSettings.FirstAsync(ct), today, result.Message!, due.Count - sent);
                break;
            }
        }
        return sent;
    }

    async Task NoticeAsync(ReminderSettings settings, DateTime today, string reason, int waiting)
    {
        var key = $"{today:yyyy-MM-dd} {reason}";
        if (settings.LastNotice == key) return;
        settings.LastNotice = key;
        db.Log("e-mail", $"Automatische herinneringen niet verstuurd: {reason} {(waiting == 1 ? "Er wacht 1 factuur" : $"Er wachten {waiting} facturen")} op een herinnering.");
        await db.SaveChangesAsync();
    }
}

// De planner: eerst facturen die verlopen zijn op "verlopen" zetten (met de trigger "Factuur te laat"), dan de herinneringen.
// Vanaf StartHour Nederlandse tijd, zodat er 's nachts geen mail naar klanten gaat, en niet in een werkruimte die alleen-lezen is.
public class ReminderJob(AppDb db, BusinessClock clock, WorkflowEngine engine, ReminderService reminders, Notifier notifier, IOptions<InvoicingLimits> options) : IWorkspaceJob
{
    public async Task RunAsync(CancellationToken ct)
    {
        if (clock.Now.Hour < options.Value.StartHour) return;
        if (await db.Workspaces.FindAsync([db.TenantId], ct) is not { } w || w.ReadOnly(DateTime.UtcNow)) return;
        await InvoiceEndpoints.MarkOverdue(db, clock, engine, notifier);
        await reminders.RunAsync(ct);
    }
}
