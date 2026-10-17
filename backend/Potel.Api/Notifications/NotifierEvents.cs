using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Invoicing;

namespace Potel.Api.Notifications;

// De meldingen per gebeurtenis: wat erin staat en voor wie. De sleutel maakt elke gebeurtenis één keer per gebruiker.
public partial class Notifier
{
    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    static string Euro(decimal d) => d.ToString("C", Dutch);
    static string Day(DateTime d) => d.ToString("d MMMM yyyy", Dutch);
    static string Name(Customer? c) => c is null ? "je klant" : string.IsNullOrWhiteSpace(c.Company) ? c.Name : c.Company;
    static string Buyer(Invoice i) => i.Buyer?.Name is { Length: > 0 } name ? name : Name(i.Customer);
    static string Local(DateTime utc)
    {
        var at = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), BusinessClock.Zone);
        return $"op {at.ToString("d MMMM yyyy", Dutch)} om {at:HH:mm}";
    }

    // Je klant accepteerde of wees een offerte af via de link. Dit was al een mail aan de beheerders; die staat nu in Detail.
    public Task<int> QuoteDecidedAsync(Quote q)
    {
        var accepted = q.Status == QuoteStatus.Accepted;
        var who = q.Buyer?.Name ?? "Je klant";
        var subject = string.IsNullOrWhiteSpace(q.Subject) ? "" : $" ({q.Subject.Trim()})";
        var when = Local(q.DecidedAt ?? Clock.UtcNow);
        var detail = accepted
            ? $"Goed nieuws: {who} heeft offerte {q.Number}{subject} geaccepteerd {when}. Akkoord gegeven door: {q.DecidedName}.\n\nIn Potel maak je er een project of factuur van."
            : $"{who} heeft offerte {q.Number}{subject} afgewezen {when}."
              + (q.RejectReason is { } reason ? $"\n\nDe toelichting:\n{reason}" : " Er is geen toelichting gegeven.");
        return ToWorkspaceAsync(new NotificationEvent(NotificationKinds.Quote, $"offerte:{q.Id}",
            accepted ? $"Offerte {q.Number} is geaccepteerd" : $"Offerte {q.Number} is afgewezen",
            accepted ? $"{who} heeft offerte {q.Number} geaccepteerd." : $"{who} heeft offerte {q.Number} afgewezen.",
            $"/offertes/{q.Id}", detail));
    }

    // Er is betaald op een factuur. Except is wie de betaling zelf vastlegde of koppelde.
    public async Task<int> PaymentAsync(int invoiceId, InvoicePayment? payment, int? except = null)
    {
        if (payment is null) return 0;
        var inv = await Db.Invoices.AsNoTracking().Include(i => i.Lines).Include(i => i.Customer).Include(i => i.Payments)
            .Include(i => i.Credits).ThenInclude(c => c.Lines).AsSplitQuery().FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (inv is null || inv.IsDemo || inv.Customer?.IsDemo == true) return 0;
        var how = payment.Method switch { PaymentMethods.Ideal => "met iDEAL", PaymentMethods.Bank => "via de bank", _ => "met de hand vastgelegd" };
        var label = InvoiceEndpoints.Label(inv);
        var paid = inv.Status == InvoiceStatus.Paid;
        var text = paid
            ? $"{label} van {Buyer(inv)} is betaald: {Euro(payment.Amount)} {how}."
            : $"{Buyer(inv)} betaalde {Euro(payment.Amount)} op {label.ToLowerInvariant()} {how}. Er staat nog {Euro(inv.OpenAmount)} open.";
        return await ToWorkspaceAsync(new NotificationEvent(NotificationKinds.Payment, $"betaling:{payment.Id}",
            paid ? $"{label} is betaald" : $"Deelbetaling op {label.ToLowerInvariant()}", text, $"/facturen/{inv.Id}"), except);
    }

    // Een factuur ging net op verlopen (zie InvoiceEndpoints.MarkOverdue).
    public Task<int> OverdueAsync(Invoice i)
    {
        if (i.IsDemo || i.Customer?.IsDemo == true) return Task.FromResult(0);
        var label = InvoiceEndpoints.Label(i);
        return ToWorkspaceAsync(new NotificationEvent(NotificationKinds.Overdue, $"te-laat:{i.Id}", $"{label} is te laat",
            $"{label} aan {Buyer(i)} is over de vervaldatum van {Day(i.DueDate)}. Er staat nog {Euro(i.OpenAmount)} open.", $"/facturen/{i.Id}"));
    }

    // De planner stuurde een automatische herinnering of aanmaning.
    public Task<int> ReminderSentAsync(Invoice i, int step)
    {
        var label = InvoiceEndpoints.Label(i);
        var what = step switch { MailSteps.First => "Een herinnering", MailSteps.Second => "Een tweede herinnering", _ => "Een aanmaning" };
        return ToWorkspaceAsync(new NotificationEvent(NotificationKinds.Overdue, $"herinnering:{i.Id}:{step}",
            $"{MailSteps.Label(step)} verstuurd voor {label.ToLowerInvariant()}",
            $"{what} voor {label.ToLowerInvariant()} is naar {Buyer(i)} gestuurd. Er staat nog {Euro(i.OpenAmount)} open.", $"/facturen/{i.Id}"));
    }

    // Een terugkerende factuur zette een factuur klaar, of verstuurde hem. Except is wie dat met een eigen verzoek in gang zette,
    // zoals bij het aanmaken van een terugkerende factuur die al aan de beurt is: die ziet het al.
    public async Task<int> RecurringInvoiceAsync(RecurringInvoice t, int invoiceId, bool send, string? fallback, int? except = null)
    {
        var inv = await Db.Invoices.AsNoTracking().Include(i => i.Customer).FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (inv is null) return 0;
        var who = Name(inv.Customer);
        NotificationEvent e;
        if (send && fallback is null)
            e = new(NotificationKinds.Recurring, $"terugkerend:{inv.Id}", $"Factuur {inv.Number} is verstuurd",
                $"Factuur {inv.Number} voor {who} is verstuurd door je terugkerende factuur \"{t.Name}\".", $"/facturen/{inv.Id}");
        else if (send && inv.Status == InvoiceStatus.Draft)
            e = new(NotificationKinds.Recurring, $"terugkerend:{inv.Id}", $"Terugkerende factuur \"{t.Name}\" niet verstuurd",
                $"De factuur voor {who} van je terugkerende factuur \"{t.Name}\" is niet verstuurd. Kijk hem na en verstuur hem zelf.",
                $"/facturen/{inv.Id}", $"De factuur voor {who} van je terugkerende factuur \"{t.Name}\" is niet automatisch verstuurd. {fallback}");
        else if (send)
            // Het nummer is er al, maar de mail naar de klant ging niet weg.
            e = new(NotificationKinds.Recurring, $"terugkerend:{inv.Id}", $"Factuur {inv.Number} is niet gemaild",
                $"Factuur {inv.Number} voor {who} staat op verstuurd, maar de e-mail ging niet weg. Mail hem zelf vanuit de factuur.",
                $"/facturen/{inv.Id}", $"Je terugkerende factuur \"{t.Name}\" heeft factuur {inv.Number} voor {who} verstuurd, maar de e-mail ging niet weg. {fallback}");
        else
            e = new(NotificationKinds.Recurring, $"terugkerend:{inv.Id}", $"Conceptfactuur voor {who} staat klaar",
                $"Je terugkerende factuur \"{t.Name}\" heeft een conceptfactuur voor {who} klaargezet. Kijk hem na en verstuur hem.", $"/facturen/{inv.Id}");
        return await ToWorkspaceAsync(e, except);
    }

    // Een vaste kost zette een uitgave als concept klaar, of is aan de beurt om zelf te boeken. Except zoals hierboven.
    public Task<int> FixedCostAsync(RecurringExpense t, RecurringPeriod p, bool drafted, int? except = null)
    {
        var period = RecurringExpenseService.Label(p);
        return ToWorkspaceAsync(drafted
            ? new NotificationEvent(NotificationKinds.FixedCost, $"vaste-kosten:{t.Id}:{p.From:yyyy-MM-dd}", $"Uitgave bij {t.Supplier} staat klaar",
                $"Je vaste kost bij {t.Supplier} ({period}) staat als concept bij je kosten. Kijk hem na.", "/kosten?tab=vast")
            : new NotificationEvent(NotificationKinds.FixedCost, $"vaste-kosten:{t.Id}:{p.From:yyyy-MM-dd}", $"Vaste kost bij {t.Supplier} is aan de beurt",
                $"Je vaste kost bij {t.Supplier} ({period}) is aan de beurt. Boek de uitgave of sla deze periode over.", "/kosten?tab=vast"), except);
    }

    // Een klant opende zijn portaal voor het eerst, of na een tijd weer. Per klant hooguit één melding per dag.
    public Task<int> PortalOpenedAsync(Customer c, bool first, DateTime today) =>
        ToWorkspaceAsync(new NotificationEvent(NotificationKinds.Portal, $"portaal:{c.Id}:{today:yyyy-MM-dd}",
            first ? $"{Name(c)} opende het klantportaal" : $"{Name(c)} keek weer in het klantportaal",
            first ? $"{Name(c)} heeft het klantportaal voor het eerst geopend." : $"{Name(c)} heeft het klantportaal weer geopend, voor het eerst sinds een tijdje.",
            $"/klanten/{c.Id}"));

    // De dagelijkse back-up mislukte of is overgeslagen. Voor de platformbeheerders, hooguit één keer per dag.
    public Task<int> BackupFailedAsync(BackupResult result)
    {
        var skipped = result.Outcome == BackupOutcome.Skipped;
        return ToPlatformAdminsAsync(new NotificationEvent(NotificationKinds.Backup, $"back-up:{Clock.Today:yyyy-MM-dd}",
            skipped ? "De back-up van de database is overgeslagen" : "De back-up van de database is mislukt",
            skipped ? "De dagelijkse back-up van de database is overgeslagen. Kijk op de pagina Platform waarom." : "De dagelijkse back-up van de database is mislukt. Kijk op de pagina Platform wat er misging.",
            "/platform", $"{(skipped ? "De dagelijkse back-up is overgeslagen" : "De dagelijkse back-up is mislukt")}: {result.Error ?? "onbekende fout"} Potel probeert het over een uur opnieuw."));
    }

    // Een werkstroom liep vast. Voor de beheerders en voor wie de run met de hand startte, behalve except: die zag het al gebeuren.
    public Task<int> WorkflowFailedAsync(Workflow wf, WorkflowRun run, string? reason, int? except = null) =>
        ToWorkspaceAsync(new NotificationEvent(NotificationKinds.Workflow, $"werkstroom:{run.Id}", $"Werkstroom \"{wf.Name}\" is vastgelopen",
            $"Je werkstroom \"{wf.Name}\" is vastgelopen. Bekijk de laatste run.", $"/werkstromen/{wf.Id}",
            $"Je werkstroom \"{wf.Name}\" is vastgelopen ({run.Trigger}).{(reason is { Length: > 0 } ? $" {reason}" : "")}"), except, run.StartedByUserId);
}
