using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Endpoints;

namespace Potel.Api.Invoicing;

public record PaymentResult(InvoiceProblem? Problem, InvoicePayment? Payment = null, bool BecamePaid = false);

// Deelbetalingen op facturen. Elke betaling haalt iets van het openstaande bedrag af; is dat 0, dan gaat de factuur op betaald
// met als betaaldatum de datum van de laatste betaling. Haal je een betaling weg en staat er weer iets open, dan gaat hij terug
// naar verzonden of verlopen. Meer betalen dan er openstaat kan niet. Alles hier hoort binnen een WriteLock.
public static class InvoicePayments
{
    public const string OverpaidPrefix = "Dat is meer dan er openstaat.";
    public const string AlreadyPaidError = "Deze factuur is al helemaal betaald. Er staat niets meer open.";
    public const string MaxNote = "Een notitie bij een betaling mag hooguit 500 tekens zijn.";

    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    public static string Euro(decimal d) => d.ToString("C", Dutch);

    // Het totaal na verstuurde creditnota's en wat er al betaald is, zoals het nu in de database staat. De regels van de factuur
    // zelf komen uit het geladen object (bij versturen zijn die nog niet opgeslagen).
    public static async Task<(decimal Net, decimal Paid, DateTime? LastDate)> BalanceAsync(AppDb db, Invoice inv)
    {
        var credited = (await db.Invoices.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.CreditForInvoiceId == inv.Id && i.Status != InvoiceStatus.Draft).ToListAsync()).Sum(InvoiceEndpoints.Total);
        var payments = await db.InvoicePayments.AsNoTracking().Where(p => p.InvoiceId == inv.Id).Select(p => new { p.Amount, p.Date }).ToListAsync();
        var last = payments.Count == 0 ? (DateTime?)null : payments.Max(p => p.Date);
        return (InvoiceEndpoints.Total(inv) + credited, payments.Sum(p => p.Amount), last);
    }

    static PaymentResult Fail(string error, int status = StatusCodes.Status400BadRequest) => new(new InvoiceProblem(error, Status: status));

    // Een betaling mag niet ver in de toekomst liggen (een dag speling voor tijdzones) en niet van voor 2000.
    public static string? DateProblem(DateTime date, BusinessClock clock) =>
        date.Year < 2000 ? "Vul de datum van de betaling in."
        : date.Date > clock.Today.AddDays(1) ? "Een betaling kan niet in de toekomst liggen. Vul de dag in waarop het geld binnenkwam."
        : null;

    // Legt een betaling vast. Met allowOverpayment (alleen voor geld dat echt al binnen is, zoals een betaalde iDEAL-betaling)
    // mag het ook meer zijn dan er openstaat; dan komt er een melding in het logboek.
    public static async Task<PaymentResult> AddAsync(AppDb db, BusinessClock clock, Invoice inv, decimal amount, DateTime date, string method,
        string? note = null, string? createdBy = null, int? bankTransactionId = null, string? molliePaymentId = null, bool allowOverpayment = false)
    {
        if (inv.Status == InvoiceStatus.Draft) return Fail("Verstuur de factuur eerst. Een concept kan nog niet betaald worden.");
        if (inv.IsCredit) return Fail("Een creditnota hoeft niemand te betalen. Markeer hem als verrekend.");
        if (!PaymentMethods.All.Contains(method)) return Fail("Kies hoe er betaald is: handmatig, met iDEAL of via de bank.");
        amount = Money.Round(amount);
        if (amount <= 0) return Fail("Vul een bedrag groter dan nul in.");
        if (amount > Money.MaxAmount) return Fail("Dat bedrag is te groot.");
        if (DateProblem(date, clock) is { } dateProblem) return Fail(dateProblem);
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > 500 }) return Fail(MaxNote);

        var (net, paid, last) = await BalanceAsync(db, inv);
        var open = Math.Max(0, net - paid);
        if (!allowOverpayment)
        {
            if (net <= 0) return Fail(InvoiceEndpoints.FullyCreditedError, StatusCodes.Status409Conflict);
            if (inv.Status == InvoiceStatus.Paid || open <= 0) return Fail(AlreadyPaidError, StatusCodes.Status409Conflict);
            if (amount > open)
                return Fail($"{OverpaidPrefix} Op {InvoiceEndpoints.Label(inv).ToLowerInvariant()} staat nog {Euro(open)} open. Een hoger bedrag kun je niet als betaling vastleggen; heeft je klant te veel betaald, dan betaal je het verschil terug.");
        }

        var payment = new InvoicePayment
        {
            InvoiceId = inv.Id, Amount = amount, Date = date.Date, Method = method, Note = note, BankTransactionId = bankTransactionId,
            MolliePaymentId = molliePaymentId, CreatedBy = createdBy, CreatedAt = clock.UtcNow,
        };
        db.InvoicePayments.Add(payment);
        var rest = Math.Max(0, open - amount);
        db.Log("factuur", $"Betaling van {Euro(amount)} ({PaymentMethods.Label(method)}) vastgelegd voor {InvoiceEndpoints.Label(inv).ToLowerInvariant()}"
                          + (rest > 0 ? $"; er staat nog {Euro(rest)} open" : ""));
        if (amount > open)
            db.Log("factuur", $"Op {InvoiceEndpoints.Label(inv).ToLowerInvariant()} is {Euro(amount - open)} te veel betaald. Kijk of je dat aan je klant moet terugbetalen.");

        var becamePaid = false;
        var lastDate = last is { } l && l > date.Date ? l : date.Date;
        if (inv.Status == InvoiceStatus.Paid)
            inv.PaidAt = lastDate;
        else if (net > 0 && paid + amount >= net)
        {
            db.Log("factuur", $"{InvoiceEndpoints.Label(inv)}: {inv.Status} → {InvoiceStatus.Paid}");
            inv.Status = InvoiceStatus.Paid;
            inv.PaidAt = lastDate;
            becamePaid = true;
        }
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // De unieke index: deze Mollie-betaling of banktransactie staat er net al (een tweede verzoek was ons voor).
            db.ChangeTracker.Clear();
            return Fail("Deze betaling is al vastgelegd.", StatusCodes.Status409Conflict);
        }
        return new(null, payment, becamePaid);
    }

    // Haalt een betaling weg. Staat er daarna weer iets open, dan is de factuur niet meer betaald: hij gaat terug naar
    // verzonden, of naar verlopen als de vervaldatum voorbij is. Een gekoppelde banktransactie staat daarna weer open.
    public static async Task RemoveAsync(AppDb db, BusinessClock clock, Invoice inv, InvoicePayment payment, string? by = null)
    {
        db.InvoicePayments.Remove(payment);
        await db.SaveChangesAsync();
        var (net, paid, last) = await BalanceAsync(db, inv);
        db.Log("factuur", $"Betaling van {Euro(payment.Amount)} van {payment.Date:dd-MM-yyyy} weggehaald bij {InvoiceEndpoints.Label(inv).ToLowerInvariant()}{(by is null ? "" : $" door {by}")}");
        if (inv.Status == InvoiceStatus.Paid && !inv.IsCredit)
        {
            if (paid < net)
            {
                var back = inv.DueDate.Date < clock.Today ? InvoiceStatus.Overdue : InvoiceStatus.Sent;
                db.Log("factuur", $"{InvoiceEndpoints.Label(inv)}: {InvoiceStatus.Paid} → {back}; er staat weer {Euro(net - paid)} open");
                inv.Status = back;
                inv.PaidAt = null;
            }
            else if (last is { } l) inv.PaidAt = l;
        }
        await db.SaveChangesAsync();
    }

    // Na een verstuurde creditnota kan het zijn dat de betalingen het nieuwe totaal al dekken: dan is de factuur betaald.
    public static async Task SettleAfterCreditAsync(AppDb db, int invoiceId)
    {
        var inv = await db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (inv is null || inv.Status is not (InvoiceStatus.Sent or InvoiceStatus.Overdue)) return;
        var (net, paid, last) = await BalanceAsync(db, inv);
        if (net <= 0 || paid <= 0 || paid < net) return;
        db.Log("factuur", $"{InvoiceEndpoints.Label(inv)}: {inv.Status} → {InvoiceStatus.Paid}; na de creditnota is alles betaald");
        inv.Status = InvoiceStatus.Paid;
        inv.PaidAt = last;
    }
}
