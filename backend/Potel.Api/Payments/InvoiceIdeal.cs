using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Invoicing;
using Potel.Api.Workflows;

namespace Potel.Api.Payments;

public record IdealStart(string? CheckoutUrl, InvoiceProblem? Problem);

// iDEAL op facturen via de eigen Mollie van de freelancer: de klant betaalt en het geld komt op de rekening van de freelancer.
// Niet te verwarren met MollieBilling, het abonnement op Potel zelf met de sleutel van het platform. Hier gebruiken we altijd
// de sleutel van de werkruimte van de factuur, en het bedrag is altijd wat er volgens de server nog openstaat.
public partial class InvoiceIdeal(MollieClient mollie, IDataProtectionProvider dp, AppUrls urls, BusinessClock clock, ILogger<InvoiceIdeal> logger)
{
    public const string ProtectorPurpose = "Potel.Mollie.InvoiceKey.v1";
    public const string WebhookPath = "/api/public/invoice/mollie-webhook";
    public const string ReturnParameter = "betaling";
    public const string Failed = "Betalen met iDEAL lukt nu even niet. Probeer het later opnieuw of maak het bedrag over.";
    public const string NotOffered = "Online betalen kan bij deze factuur niet. Maak het bedrag over zoals op de factuur staat.";
    public const string JustPaid = "Deze factuur is net met iDEAL betaald. Dank je wel! Ververs de pagina om te zien wat er nog openstaat.";
    public const string Busy = "Er loopt al een betaling met iDEAL voor deze factuur. Wacht even tot die klaar is en ververs dan de pagina.";
    public const string NoBaseUrl = "Het openbare adres van Potel (App:BaseUrl) ontbreekt, dus Mollie kan niet laten weten dat er betaald is. Vraag de beheerder van Potel om het in te stellen.";
    public const string LocalBaseUrl = "Het openbare adres van Potel (App:BaseUrl) wijst naar deze computer. Mollie kan dat niet bereiken, dus online betalen staat uit tot het een adres op internet is.";

    [GeneratedRegex("^tr_[A-Za-z0-9]{1,64}$")]
    public static partial Regex PaymentId();

    // Een API-sleutel van Mollie: live_ of test_ met letters en cijfers. Een organisatietoken (access_) werkt hier niet.
    [GeneratedRegex("^(live|test)_[A-Za-z0-9]{20,100}$")]
    public static partial Regex KeyPattern();

    // Betalingen die nog kunnen lukken.
    static readonly string[] Running = ["open", "pending", "authorized"];

    // Hooguit één betaling tegelijk starten per factuur, zodat twee klikken niet twee betalingen bij Mollie maken.
    static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    IDataProtector Protector => dp.CreateProtector(ProtectorPurpose);

    public string Protect(string key) => Protector.Protect(key);

    public string? Unprotect(string protectedKey)
    {
        try { return Protector.Unprotect(protectedKey); }
        catch (CryptographicException e)
        {
            // De sleutels van KeysPath zijn weg of vervangen: de opgeslagen sleutel is niet meer te lezen.
            logger.LogError(e, "De opgeslagen sleutel van Mollie is niet te ontsleutelen. Stel hem opnieuw in bij Instellingen.");
            return null;
        }
    }

    public static string ModeOf(string key) => key.StartsWith("live_", StringComparison.Ordinal) ? "live" : "test";

    // Waarom online betalen in deze installatie niet kan, of null. Mollie moet ons openbare adres kunnen bereiken.
    public string? BaseUrlProblem()
    {
        if (urls.Configured is not { } configured) return NoBaseUrl;
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri) && !uri.IsLoopback && uri.Host != "localhost" ? null : LocalBaseUrl;
    }

    // Kan deze werkruimte iDEAL aanbieden? Er moet een sleutel zijn en een openbaar adres.
    public async Task<bool> AvailableAsync(AppDb db, int workspaceId) =>
        BaseUrlProblem() is null && await db.MollieAccounts.IgnoreQueryFilters().AnyAsync(a => a.WorkspaceId == workspaceId);

    // Mag deze factuur online betaald worden? Nooit een concept, creditnota, voorbeeld of iets zonder openstaand bedrag.
    // De factuur moet met regels, creditnota's en betalingen geladen zijn.
    public static bool Payable(Invoice inv, bool demoCustomer) =>
        inv.Status is InvoiceStatus.Sent or InvoiceStatus.Overdue && !inv.IsCredit && !inv.IsDemo && !demoCustomer && !inv.FullyCredited && inv.OpenAmount > 0;

    static async Task<bool> DemoCustomerAsync(AppDb db, Invoice inv) =>
        await db.Customers.IgnoreQueryFilters().Where(c => c.Id == inv.CustomerId && c.WorkspaceId == inv.WorkspaceId).Select(c => c.IsDemo).FirstOrDefaultAsync();

    public async Task<bool> CanPayAsync(AppDb db, Invoice inv) =>
        Payable(inv, await DemoCustomerAsync(db, inv)) && await AvailableAsync(db, inv.WorkspaceId);

    static string Description(Invoice inv)
    {
        var seller = inv.Seller?.Name?.Trim();
        var text = $"Factuur {inv.Number}" + (string.IsNullOrEmpty(seller) ? "" : $" van {seller}");
        return text.Length > 250 ? text[..250] : text;
    }

    static IQueryable<Invoice> WithBalance(AppDb db) =>
        db.Invoices.IgnoreQueryFilters().Include(i => i.Lines).Include(i => i.Credits).ThenInclude(c => c.Lines).Include(i => i.Payments).AsSplitQuery();

    // Start een iDEAL-betaling voor wat er nu openstaat, of geeft de betaalpagina van een betaling die nog loopt voor
    // hetzelfde bedrag. Dit verzoek heeft geen ingelogde gebruiker: we werken in de werkruimte van de factuur.
    public async Task<IdealStart> StartAsync(AppDb db, WorkflowEngine engine, int workspaceId, int invoiceId)
    {
        if (BaseUrlProblem() is not null) return new(null, new(NotOffered, Status: StatusCodes.Status409Conflict));
        var account = await db.MollieAccounts.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(a => a.WorkspaceId == workspaceId);
        if (account is null || Unprotect(account.ProtectedKey) is not { } key) return new(null, new(NotOffered, Status: StatusCodes.Status409Conflict));

        var gate = Gates[(int)((uint)invoiceId % Gates.Length)];
        await gate.WaitAsync();
        try
        {
            // Het bedrag komt altijd van hier: wat er nu openstaat, opnieuw opgehaald binnen de vergrendeling.
            var inv = await WithBalance(db).AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId && i.WorkspaceId == workspaceId);
            if (inv is null || !Payable(inv, await DemoCustomerAsync(db, inv))) return new(null, new(NotOffered, Status: StatusCodes.Status409Conflict));
            var amount = Money.Round(inv.OpenAmount);

            // Eerst de betalingen die nog lopen navragen bij Mollie. Is er een net betaald en de webhook nog onderweg, dan zetten
            // we hem meteen op de factuur en starten we geen tweede. Is er een bezig bij de bank, dan wachten we. Staat er een
            // open voor hetzelfde bedrag, dan krijgt de klant dezelfde betaalpagina.
            var running = await db.OnlinePayments.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.WorkspaceId == workspaceId && p.InvoiceId == invoiceId && p.RecordedAt == null && p.Mode == account.Mode && Running.Contains(p.Status))
                .OrderByDescending(p => p.Id).Take(5).ToListAsync();
            foreach (var p in running)
            {
                var info = await mollie.GetInvoicePaymentAsync(key, p.MollieId);
                if (info is null || info.Id != p.MollieId) continue;
                if (info.Status == "paid")
                {
                    await ProcessAsync(db, engine, p.MollieId);
                    return new(null, new(JustPaid, Status: StatusCodes.Status409Conflict));
                }
                if (info.Status is "pending" or "authorized") return new(null, new(Busy, Status: StatusCodes.Status409Conflict));
                if (info.Status == "open" && p.Amount == amount && info.Amount?.Euros == amount && MollieBilling.SafeCheckout(info.Links?.Checkout?.Href))
                    return new(info.Links!.Checkout!.Href, null);
                if (info.Status != p.Status && info.Status is { Length: > 0 and <= 20 })
                    await db.OnlinePayments.IgnoreQueryFilters().Where(x => x.Id == p.Id)
                        .ExecuteUpdateAsync(x => x.SetProperty(o => o.Status, info.Status).SetProperty(o => o.UpdatedAt, clock.UtcNow));
            }

            var baseUrl = urls.Configured!;
            var returnToken = InvoiceMailer.NewToken();
            var created = await mollie.CreateInvoicePaymentAsync(key, MollieAmount.Euro(amount), Description(inv),
                $"{baseUrl}{InvoiceMailer.LinkPath}?{ReturnParameter}={returnToken}", baseUrl + WebhookPath,
                new { workspaceId = workspaceId.ToString(CultureInfo.InvariantCulture), invoiceId = invoiceId.ToString(CultureInfo.InvariantCulture) });
            var href = created.Links?.Checkout?.Href;
            if (!PaymentId().IsMatch(created.Id ?? "") || !MollieBilling.SafeCheckout(href))
            {
                logger.LogError("Mollie gaf een onverwachte betaalpagina terug voor betaling {Id}: {Href}", created.Id, href);
                return new(null, new(Failed, Status: StatusCodes.Status502BadGateway));
            }
            db.Tenant.WorkspaceId = workspaceId;
            db.OnlinePayments.Add(new OnlinePayment
            {
                MollieId = created.Id!, InvoiceId = invoiceId, Amount = amount, Mode = account.Mode, Status = created.Status is { Length: > 0 } s ? s : "open",
                ReturnTokenHash = InvoiceMailer.Hash(returnToken), CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow,
            });
            db.Log("factuur", $"Je klant is een betaling met iDEAL van {InvoicePayments.Euro(amount)} begonnen voor {InvoiceEndpoints.Label(inv).ToLowerInvariant()}"
                              + (account.Mode == "test" ? " (testbetaling)" : ""));
            await db.SaveChangesAsync();
            return new(href, null);
        }
        catch (MollieException e) when (e.Status == HttpStatusCode.Unauthorized)
        {
            logger.LogWarning(e, "Mollie herkent de sleutel van werkruimte {Workspace} niet", workspaceId);
            db.Tenant.WorkspaceId = workspaceId;
            db.ChangeTracker.Clear();
            db.Log("factuur", "Een klant wilde met iDEAL betalen, maar Mollie herkent je sleutel niet. Kijk bij Instellingen, iDEAL.");
            await db.SaveChangesAsync();
            return new(null, new(Failed, Status: StatusCodes.Status502BadGateway));
        }
        catch (Exception e) when (e is MollieException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "iDEAL-betaling starten bij Mollie mislukt voor factuur {Invoice}", invoiceId);
            return new(null, new(Failed, Status: StatusCodes.Status502BadGateway));
        }
        finally { gate.Release(); }
    }

    // Mollie meldt dat er iets met een betaling is gebeurd. Alleen een id dat wij kennen telt; de status vragen we zelf op met
    // de sleutel van die werkruimte. Is hij betaald, dan komt er één betaling op de factuur, ook als Mollie het vaker of
    // tegelijk meldt. Fouten van Mollie gaan door naar de webhook, die dan 503 geeft zodat Mollie het later opnieuw probeert.
    public async Task ProcessAsync(AppDb db, WorkflowEngine engine, string id)
    {
        var known = await db.OnlinePayments.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(p => p.MollieId == id);
        if (known is null) return;
        // Dit verzoek heeft geen ingelogde gebruiker: vanaf hier werken we in de werkruimte van deze betaling.
        db.Tenant.WorkspaceId = known.WorkspaceId;
        var account = await db.MollieAccounts.AsNoTracking().FirstOrDefaultAsync();
        if (account is null || Unprotect(account.ProtectedKey) is not { } key)
        {
            logger.LogWarning("Webhook voor iDEAL-betaling {Id}, maar werkruimte {Workspace} heeft geen bruikbare sleutel van Mollie meer", id, known.WorkspaceId);
            return;
        }
        var info = await mollie.GetInvoicePaymentAsync(key, id);
        if (info is null || info.Id != id)
        {
            logger.LogWarning("Mollie kent iDEAL-betaling {Id} niet met de sleutel van werkruimte {Workspace}", id, known.WorkspaceId);
            return;
        }
        if (info.Meta("invoiceId") != known.InvoiceId.ToString(CultureInfo.InvariantCulture))
        {
            logger.LogWarning("iDEAL-betaling {Id} hoort volgens Mollie niet bij factuur {Invoice}; niets gedaan", id, known.InvoiceId);
            return;
        }

        Invoice? inv = null;
        var becamePaid = false;
        await using (var tx = await WriteLock.BeginAsync(db))
        {
            var row = await db.OnlinePayments.FirstOrDefaultAsync(p => p.Id == known.Id);
            if (row is null) return;
            if (info.Status is { Length: > 0 and <= 20 } status) row.Status = status;
            row.UpdatedAt = clock.UtcNow;
            if (info.Status == "paid")
            {
                row.PaidAt ??= info.PaidAt?.UtcDateTime ?? clock.UtcNow;
                // Eén keer op de factuur, ook als Mollie dezelfde betaling nog eens meldt of tegelijk twee keer.
                if (row.RecordedAt is null)
                {
                    row.RecordedAt = clock.UtcNow;
                    var amount = info.Amount?.Euros;
                    var already = await db.InvoicePayments.AnyAsync(p => p.MolliePaymentId == id);
                    inv = already ? null : await db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == row.InvoiceId);
                    if (inv is not null && amount is > 0)
                    {
                        var paidOn = TimeZoneInfo.ConvertTimeFromUtc(row.PaidAt.Value, BusinessClock.Zone).Date;
                        if (paidOn > clock.Today) paidOn = clock.Today;
                        var note = row.Mode == "test" ? "Testbetaling via Mollie, geen echt geld" : null;
                        // Het geld is echt binnen: ook als er intussen al iets anders betaald is, leggen we het vast (met een melding).
                        var result = await InvoicePayments.AddAsync(db, clock, inv, amount.Value, paidOn, PaymentMethods.Ideal, note, "Mollie",
                            molliePaymentId: id, allowOverpayment: true);
                        if (result.Problem is { } problem)
                        {
                            logger.LogWarning("iDEAL-betaling {Id} kon niet op factuur {Invoice}: {Problem}", id, row.InvoiceId, problem.Error);
                            db.Log("factuur", $"Er kwam {InvoicePayments.Euro(amount.Value)} binnen met iDEAL voor {InvoiceEndpoints.Label(inv).ToLowerInvariant()}, maar dat kon niet op de factuur: {problem.Error}");
                        }
                        becamePaid = result.BecamePaid;
                    }
                }
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        if (becamePaid && inv is not null) await InvoiceEndpoints.TriggerPaid(db, engine, inv);
    }

    // Test de sleutel: werkt hij, en staat iDEAL aan?
    public async Task<(bool Ok, string Message)> TestAsync(string key)
    {
        try
        {
            var methods = await mollie.ListMethodsAsync(key);
            var mode = ModeOf(key);
            return methods.Any(m => m.Id == "ideal")
                ? (true, $"De verbinding werkt ({mode}) en iDEAL staat aan in je Mollie-account.")
                : (false, $"De verbinding werkt ({mode}), maar iDEAL staat nog niet aan in je Mollie-account. Zet iDEAL aan in je Mollie-dashboard bij Betaalmethodes.");
        }
        catch (MollieException e) when (e.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return (false, "Mollie herkent deze sleutel niet. Kopieer hem opnieuw uit je Mollie-dashboard, bij Ontwikkelaars en dan API-sleutels.");
        }
        catch (Exception e) when (e is MollieException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(e, "Verbinding met Mollie testen mislukt");
            return (false, "Mollie is nu niet bereikbaar. Probeer het over een paar minuten opnieuw.");
        }
    }
}
