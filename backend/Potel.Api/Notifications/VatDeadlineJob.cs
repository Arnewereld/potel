using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Invoicing;

namespace Potel.Api.Notifications;

// De planner: een melding een week voor de deadline van de btw-aangifte over het vorige kwartaal, en op de laatste dag zelf. Niet als
// de aangifte al als gedaan staat, niet met de KOR (dan doe je geen aangifte) en niet in een werkruimte die alleen-lezen is.
// Lag de server op de dag precies een week ervoor stil, dan komt de eerste melding de dag erna.
public class VatDeadlineJob(AppDb db, BusinessClock clock, Notifier notifier, IOptions<InvoicingLimits> options) : IWorkspaceJob
{
    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    public async Task RunAsync(CancellationToken ct)
    {
        if (clock.Now.Hour < options.Value.StartHour) return;
        if (await db.Workspaces.FindAsync([db.TenantId], ct) is not { } w || w.ReadOnly(DateTime.UtcNow)) return;
        await CheckAsync();
    }

    public async Task CheckAsync()
    {
        var today = clock.Today;
        if (VatReturn.PendingOn(today) is not { } pending) return;
        var days = (pending.DueDate.Date - today).Days;
        if (days > 7) return;
        if ((await SettingsEndpoints.GetAsync(db)).VatRegime == VatRegimes.Kor) return;
        if (await db.VatFilings.AnyAsync(f => f.Year == pending.Year && f.Quarter == pending.Quarter)) return;
        var due = pending.DueDate.ToString("dddd d MMMM", Dutch);
        var key = $"btw:{pending.Year}-{pending.Quarter}:{(days == 0 ? "laatste-dag" : "week")}";
        var e = days == 0
            ? new NotificationEvent(NotificationKinds.Vat, key, $"Vandaag is de laatste dag voor je btw-aangifte over {pending.Label}",
                $"Vandaag is de laatste dag om je btw-aangifte over {pending.Label} te doen.", "/btw-aangifte",
                $"Vandaag, {due}, is de laatste dag om je btw-aangifte over {pending.Label} bij de Belastingdienst te doen. Op de pagina Btw-aangifte staan de bedragen per rubriek. Heb je hem al gedaan? Markeer hem daar dan als gedaan.")
            : new NotificationEvent(NotificationKinds.Vat, key, $"Je btw-aangifte over {pending.Label} moet {In(days)} binnen zijn",
                $"Je btw-aangifte over {pending.Label} moet {In(days)} binnen zijn, uiterlijk {due}.", "/btw-aangifte",
                $"Je btw-aangifte over {pending.Label} moet uiterlijk {due} bij de Belastingdienst binnen zijn. Op de pagina Btw-aangifte staan de bedragen per rubriek. Heb je hem al gedaan? Markeer hem daar dan als gedaan, dan krijg je geen melding meer.");
        await notifier.ToWorkspaceAsync(e);
    }

    static string In(int days) => days == 1 ? "morgen" : $"over {days} dagen";
}
