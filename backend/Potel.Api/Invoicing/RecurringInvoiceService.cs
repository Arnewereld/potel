using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Notifications;
using Potel.Api.Workflows;

namespace Potel.Api.Invoicing;

public enum RecurringOutcome { Created, AlreadyMade, Finished, Paused, Demo, NotFound }

// Wat één keer factureren opleverde, met de nieuwe factuur en waarom hij een concept bleef als hij verstuurd had moeten worden.
public record RecurringResult(RecurringOutcome Outcome, Invoice? Invoice = null, string? FallbackReason = null);

// Maakt facturen van terugkerende facturen: per periode precies één, ook na een herstart of als twee rondes tegelijk lopen.
// Dat bewaken drie dingen samen: een WriteLock (één schrijver tegelijk), de controle dat de volgende periode nog dezelfde is
// als waarvoor we begonnen, en de unieke index op terugkerende factuur en begin van de periode.
public class RecurringInvoiceService(AppDb db, BusinessClock clock, IOptions<InvoicingLimits> options, IOptions<WorkflowLimits> mailLimits,
    InvoiceMailer mailer, AppUrls urls, IHttpContextAccessor http, Notifier notifier, ILogger<RecurringInvoiceService> logger)
{
    readonly InvoicingLimits limits = options.Value;

    static string CustomerName(Customer? c) => c is null ? "de klant" : string.IsNullOrWhiteSpace(c.Company) ? c.Name : c.Company;

    // Een voorbeeld uit de welkomstwizard of een voorbeeldklant maakt nooit echte facturen.
    public static bool IsDemo(RecurringInvoice t) => t.IsDemo || t.Customer?.IsDemo == true;

    // Alle terugkerende facturen van deze werkruimte die vandaag aan de beurt zijn, samen hooguit max facturen.
    public async Task<int> RunDueAsync(int? max = null)
    {
        var limit = Math.Max(0, max ?? limits.MaxRecurringPerRun);
        var today = clock.Today;
        var ids = await db.RecurringInvoices.AsNoTracking()
            .Where(t => t.Active && !t.IsDemo && !t.Customer!.IsDemo && t.NextRunDate != null && t.NextRunDate <= today)
            .OrderBy(t => t.NextRunDate).ThenBy(t => t.Id).Select(t => t.Id).ToListAsync();
        var made = 0;
        foreach (var id in ids)
        {
            if (made >= limit) break;
            try
            {
                made += await CatchUpAsync(id, today, limit - made);
            }
            catch (Exception ex)
            {
                // Eén kapotte terugkerende factuur houdt de rest niet tegen.
                logger.LogError(ex, "Terugkerende factuur {Id} mislukt", id);
                db.ChangeTracker.Clear();
            }
        }
        return made;
    }

    // Eén terugkerende factuur bijwerken tot vandaag, bijvoorbeeld meteen na opslaan of hervatten.
    public async Task<int> RunOneAsync(int id)
    {
        var t = await db.RecurringInvoices.AsNoTracking().Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == id);
        return t is null || IsDemo(t) ? 0 : await CatchUpAsync(id, clock.Today, Math.Max(1, limits.MaxRecurringPerRun));
    }

    // Maakt een factuur voor elke periode die vandaag aan de beurt is, de oudste eerst.
    async Task<int> CatchUpAsync(int id, DateTime today, int max)
    {
        await SkipBeyondCatchUpAsync(id, today);
        var made = 0;
        // Elke ronde schuift de volgende periode op; meer rondes dan perioden kan dus niet, maar we begrenzen het toch.
        for (var round = 0; made < max && round < max + limits.MaxCatchUpPeriods + 2; round++)
        {
            var t = await db.RecurringInvoices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (t is not { Active: true, NextRunDate: { } run } || run > today) break;
            var result = await CreateAsync(id, t.NextPeriodStart, manual: false);
            if (result.Outcome == RecurringOutcome.Created) made++;
            else if (result.Outcome != RecurringOutcome.AlreadyMade) break;
        }
        return made;
    }

    // Zijn er meer gemiste perioden dan MaxCatchUpPeriods, dan slaan we de oudste over: na een lange storing komen alleen
    // de laatste perioden nog als factuur, en in het logboek staat welke perioden zijn overgeslagen.
    async Task SkipBeyondCatchUpAsync(int id, DateTime today)
    {
        var max = Math.Max(1, limits.MaxCatchUpPeriods);
        var current = await db.RecurringInvoices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (current is null || RecurringPeriods.DueCount(current, today, max) <= max) return;
        db.ChangeTracker.Clear();
        await using var tx = await WriteLock.BeginAsync(db);
        var t = await db.RecurringInvoices.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return;
        var due = RecurringPeriods.Upcoming(t).TakeWhile(p => p.RunDate <= today).Take(10_000).ToList();
        if (due.Count <= max) return;
        var skipped = due[..^max];
        RecurringPeriods.Schedule(t, due[^max].From);
        db.Log("factuur", $"Terugkerende factuur \"{t.Name}\": {skipped.Count} gemiste perioden ({RecurringPeriods.Label(skipped[0].From, skipped[^1].To)}) " +
                          $"zijn overgeslagen. Na een lange onderbreking maken we alleen voor de laatste {max} perioden nog een factuur. Maak de rest zo nodig zelf.");
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    // Maakt de factuur voor de periode die op periodStart begint, als dat nog steeds de volgende periode is.
    // Begint met een lege context, zodat we altijd de stand uit de database zien en niet een oude kopie.
    // Handmatig (Nu aanmaken) mag ook bij een gepauzeerde terugkerende factuur; de planner slaat die over.
    public async Task<RecurringResult> CreateAsync(int id, DateTime periodStart, bool manual = true)
    {
        db.ChangeTracker.Clear();
        Invoice inv;
        RecurringInvoice t;
        await using (var tx = await WriteLock.BeginAsync(db))
        {
            var found = await db.RecurringInvoices.Include(x => x.Lines).Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == id);
            if (found is null) return new(RecurringOutcome.NotFound);
            t = found;
            if (IsDemo(t)) return new(RecurringOutcome.Demo);
            if (!manual && !t.Active) return new(RecurringOutcome.Paused);
            // Een andere ronde was ons voor en heeft deze periode al gemaakt.
            if (t.NextPeriodStart != periodStart.Date) return new(RecurringOutcome.AlreadyMade);
            if (!RecurringPeriods.WithinEnd(t, t.NextPeriodStart)) return new(RecurringOutcome.Finished);

            var period = RecurringPeriods.Period(t, t.NextPeriodStart);
            var next = RecurringPeriods.Start(t.StartDate, t.Interval, period.Index + 1);
            if (await db.Invoices.AnyAsync(i => i.RecurringInvoiceId == t.Id && i.RecurringPeriodStart == period.From))
            {
                RecurringPeriods.Schedule(t, next);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return new(RecurringOutcome.AlreadyMade);
            }

            var settings = await SettingsEndpoints.GetAsync(db);
            inv = Build(t, period, settings, clock.Today);
            db.Invoices.Add(inv);
            RecurringPeriods.Schedule(t, next);
            db.Log("factuur", $"Conceptfactuur voor {CustomerName(t.Customer)} ({RecurringPeriods.Label(period.From, period.To)}) klaargezet door terugkerende factuur \"{t.Name}\"");
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                // De unieke index: deze periode bestaat al. Alles terugdraaien; er is niets meer te doen.
                db.ChangeTracker.Clear();
                return new(RecurringOutcome.AlreadyMade);
            }
            await tx.CommitAsync();
        }

        // Een melding alleen als de planner het deed; bij Nu aanmaken zie je het zelf.
        if (t.Action != RecurringActions.Send)
        {
            if (!manual) await notifier.RecurringInvoiceAsync(t, inv.Id, send: false, fallback: null, except: http.HttpContext?.User.UserId());
            return new(RecurringOutcome.Created, inv);
        }
        var fallback = await SendAsync(inv.Id, t);
        if (!manual) await notifier.RecurringInvoiceAsync(t, inv.Id, send: true, fallback, except: http.HttpContext?.User.UserId());
        return new(RecurringOutcome.Created, await db.Invoices.AsNoTracking().FirstAsync(i => i.Id == inv.Id), fallback);
    }

    // "Direct versturen": de factuur krijgt een nummer en gaat per e-mail naar de klant. Kan dat niet (geen mailserver, geen
    // bevestigd e-mailadres van de beheerder, geen geldig adres bij de klant, ontbrekende factuurgegevens of de daglimiet is
    // op), dan blijft hij een concept en staat in het logboek waarom. Geeft die reden terug, of null als hij verstuurd is.
    async Task<string?> SendAsync(int invoiceId, RecurringInvoice t)
    {
        // Bij "Nu aanmaken" mag het adres in ontwikkelmodus van het verzoek komen; op de achtergrond alleen uit App:BaseUrl.
        var baseUrl = http.HttpContext?.Request is { } request ? urls.Base(request) : urls.Configured;
        var reason = await mailer.BlockerAsync(baseUrl);
        string? number = null;
        if (reason is null)
        {
            db.ChangeTracker.Clear();
            await using var tx = await WriteLock.BeginAsync(db);
            var inv = await InvoiceMailer.WithAll(db).FirstAsync(i => i.Id == invoiceId);
            // Eerst kijken of de mail weg kan; pas dan krijgt de factuur een nummer.
            if (InvoiceMailer.CustomerEmail(inv.Customer) is null) reason = InvoiceMailer.NoEmail;
            else if (await MailQuota.CheckAsync(db, mailLimits.Value) is var quota && quota != QuotaResult.Ok)
                reason = MailQuota.Message(quota, mailLimits.Value.EmailsPerDay(await MailQuota.PlanAsync(db)));
            else
            {
                var (sendProblem, _) = await InvoiceEndpoints.ChangeStatusAsync(db, clock, inv, InvoiceStatus.Sent);
                if (sendProblem is not null) reason = sendProblem.Error;
                else
                {
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                    number = inv.Number;
                }
            }
        }
        if (reason is not null)
        {
            db.ChangeTracker.Clear();
            db.Log("factuur", $"Terugkerende factuur \"{t.Name}\": de factuur staat als concept klaar en is niet verstuurd. {reason}");
            await db.SaveChangesAsync();
            return reason;
        }
        var mail = await mailer.SendAsync(invoiceId, MailSteps.Invoice, automatic: true, baseUrl);
        if (mail.Outcome == SendOutcome.Sent) return null;
        // Het nummer is er al; de factuur ligt vast. Dan moet de mail er alsnog met de hand achteraan.
        db.ChangeTracker.Clear();
        var why = mail.Message ?? "De mail ging niet weg.";
        db.Log("factuur", $"Terugkerende factuur \"{t.Name}\": factuur {number} is verstuurd, maar de e-mail ging niet weg. {why} Mail hem zelf vanuit de factuur.");
        await db.SaveChangesAsync();
        return $"Factuur {number} is verstuurd, maar de e-mail ging niet weg. {why}";
    }

    // De factuur voor één periode: de regels met de periode ingevuld, de leverperiode en de betaaltermijn uit de instellingen.
    // De factuurdatum is vandaag; bij versturen wordt die toch de dag van versturen.
    // In de taal van de klant, ook de maand of periode die Potel invult ({maand} wordt dan October).
    public static Invoice Build(RecurringInvoice t, RecurringPeriod period, Settings settings, DateTime today) => new()
    {
        CustomerId = t.CustomerId, Status = InvoiceStatus.Draft, IssueDate = today, DueDate = today.AddDays(settings.PaymentTermDays),
        VatRegime = t.VatRegime, DeliveryFrom = period.From, DeliveryTo = period.To, Reference = t.Reference,
        Language = t.Customer?.Language ?? Languages.Dutch,
        Notes = string.IsNullOrWhiteSpace(t.Notes) ? null : RecurringPeriods.Fill(t.Notes, period.From, period.To, t.Customer?.Language),
        RecurringInvoiceId = t.Id, RecurringPeriodStart = period.From,
        Lines = t.Lines.OrderBy(l => l.Position).ThenBy(l => l.Id).Select((l, i) => new InvoiceLine
        {
            Description = RecurringPeriods.Fill(l.Description, period.From, period.To, t.Customer?.Language), Quantity = l.Quantity, Unit = l.Unit,
            UnitPrice = l.UnitPrice, VatRate = VatRegimes.ZeroVat(t.VatRegime) ? 0 : l.VatRate, Position = i,
        }).ToList(),
    };
}

// De planner: terugkerende facturen die aan de beurt zijn, vanaf StartHour Nederlandse tijd en niet in een werkruimte die alleen-lezen is.
public class RecurringInvoiceJob(RecurringInvoiceService service, AppDb db, BusinessClock clock, IOptions<InvoicingLimits> options) : IWorkspaceJob
{
    public async Task RunAsync(CancellationToken ct)
    {
        if (clock.Now.Hour < options.Value.StartHour) return;
        if (await db.Workspaces.FindAsync([db.TenantId], ct) is not { } w || w.ReadOnly(DateTime.UtcNow)) return;
        await service.RunDueAsync();
    }
}
