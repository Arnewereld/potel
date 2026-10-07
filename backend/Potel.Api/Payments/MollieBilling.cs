using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Workflows;

namespace Potel.Api.Payments;

// Abonnementen betalen via Mollie: de eerste maand met iDEAL, daarna elke maand een automatische incasso (SEPA).
// Wat een betaling heeft gedaan, bepalen we altijd met de status die we zelf bij Mollie opvragen; een webhook zegt alleen
// "kijk eens naar betaling tr_...". Alles wat een abonnement verandert, loopt na elkaar (Gate), en elke betaling wordt maar
// één keer verwerkt, hoe vaak Mollie de webhook ook stuurt.
public sealed class MollieBilling(
    MollieClient mollie, IServiceScopeFactory scopes, AppUrls urls, IOptions<MollieOptions> options, IConfiguration config,
    BusinessClock clock, ILogger<MollieBilling> logger)
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    public const decimal VatRate = 21;
    public const string ReturnPath = "/instellingen?tab=abonnement&betaling=terug";
    public const string WebhookPath = "/api/mollie/webhook";
    public const string Unreachable = "Mollie is even niet bereikbaar. Probeer het over een paar minuten opnieuw.";

    public bool Enabled => mollie.Enabled;
    string Product => config["Platform:Name"] is { Length: > 0 } name ? name : "Potel";

    public record Quote(string Plan, string Name, decimal Net, decimal Vat, decimal Gross, decimal VatRate);

    // De prijs per maand: exclusief btw uit Plans, met 21% btw erbij. Klanten van Potel zijn altijd Nederlandse bedrijven.
    public static Quote QuoteFor(string plan)
    {
        var net = Plans.MonthlyPrice(plan);
        var vat = Money.Round(net * VatRate / 100);
        return new(plan, Plans.Name(plan), net, vat, net + vat, VatRate);
    }

    static string Euro(decimal d) => d.ToString("C", Dutch);
    static string Day(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, BusinessClock.Zone).ToString("d MMMM yyyy", Dutch);
    static DateTime LocalDate(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, BusinessClock.Zone).Date;

    // Alleen echte betaalpagina's van Mollie; zo kan een vreemd antwoord je nooit naar een andere site sturen.
    public static bool SafeCheckout(string? href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("mollie.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".mollie.com", StringComparison.OrdinalIgnoreCase));

    // Waar Mollie wijzigingen meldt. Een adres op je eigen computer kan Mollie niet bereiken; dan halen we de status op als
    // je terugkomt van de betaalpagina (zie SyncAsync).
    string? WebhookUrl(string? baseUrl)
    {
        if (options.Value.WebhookUrl is { Length: > 0 } configured) return configured.Trim();
        if (baseUrl is null) return null;
        var url = baseUrl + WebhookPath;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsLoopback && uri.Host != "localhost" ? url : null;
    }

    static IResult Problem(string error, int status = StatusCodes.Status400BadRequest) => Results.Json(new { error }, statusCode: status);

    // Start een betaling met iDEAL voor de eerste maand. Geeft de betaalpagina van Mollie terug, of een foutmelding.
    public async Task<(string? CheckoutUrl, IResult? Error)> CheckoutAsync(AppDb db, Workspace ws, User user, string plan, HttpRequest request)
    {
        if (!Plans.Paid.Contains(plan)) return (null, Problem("Kies ZZP of Team."));
        var users = await db.Users.CountAsync();
        if (users > Plans.MaxUsers(plan))
            return (null, Problem($"{Plans.Name(plan)} is voor {Plans.MaxUsers(plan)} gebruiker(s), en je werkruimte heeft er {users}. Verwijder eerst gebruikers die je niet meer nodig hebt, of kies Team."));
        var settings = await SettingsEndpoints.GetAsync(db);
        if (string.IsNullOrWhiteSpace(settings.Address) || string.IsNullOrWhiteSpace(settings.City))
            return (null, Problem("Vul eerst je adres en je postcode en plaats in onder Instellingen, Bedrijf. Die komen op de factuur voor je abonnement."));
        if (urls.Base(request) is not { } baseUrl)
            return (null, Problem("Online betalen is nog niet goed ingesteld op deze server: het openbare adres ontbreekt. Neem contact op met de beheerder van het platform.", StatusCodes.Status503ServiceUnavailable));

        await Gate.WaitAsync();
        try
        {
            await db.Entry(ws).ReloadAsync();
            if (ws.Plan == plan && ws.MollieSubscriptionId is not null && !ws.ReadOnly(DateTime.UtcNow))
                return (null, Problem($"Je hebt {Plans.Name(plan)} al, en het abonnement loopt. Je hoeft niets te doen."));
            if (ws.MollieCustomerId is null)
            {
                ws.MollieCustomerId = (await mollie.CreateCustomerAsync(settings.CompanyName, user.Email, ws.Id)).Id;
                await db.SaveChangesAsync();
            }
            var q = QuoteFor(plan);
            var name = ws.Name.Length > 80 ? ws.Name[..80] : ws.Name;
            var payment = await mollie.CreateFirstPaymentAsync(ws.MollieCustomerId, MollieAmount.Euro(q.Gross), $"{Product} {q.Name} voor {name}",
                baseUrl + ReturnPath, WebhookUrl(baseUrl), new { workspaceId = ws.Id.ToString(CultureInfo.InvariantCulture), plan, vatRate = q.VatRate.ToString(CultureInfo.InvariantCulture) });
            var href = payment.Links?.Checkout?.Href;
            if (!SafeCheckout(href) || !payment.Id.StartsWith("tr_"))
            {
                logger.LogError("Mollie gaf een onverwachte betaalpagina terug voor betaling {Id}: {Href}", payment.Id, href);
                return (null, Problem(Unreachable, StatusCodes.Status502BadGateway));
            }
            db.MolliePayments.Add(new MolliePayment { Id = payment.Id, Plan = plan, SequenceType = "first", Status = payment.Status, Amount = q.Gross, VatRate = q.VatRate });
            db.Log("abonnement", $"Betaling met iDEAL voor {q.Name} gestart door {user.Email}");
            await db.SaveChangesAsync();
            return (href, null);
        }
        catch (Exception e) when (e is MollieException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Betaling starten bij Mollie mislukt voor werkruimte {Workspace}", ws.Id);
            return (null, Problem(Unreachable, StatusCodes.Status502BadGateway));
        }
        finally { Gate.Release(); }
    }

    // Zegt het abonnement op bij Mollie. Er wordt niets meer afgeschreven; werken kan tot het einde van de betaalde periode.
    public async Task<IResult?> CancelAsync(AppDb db, Workspace ws, User user)
    {
        await Gate.WaitAsync();
        try
        {
            await db.Entry(ws).ReloadAsync();
            if (ws.MollieSubscriptionId is not { } sub || ws.MollieCustomerId is not { } customer)
                return Problem("Je hebt geen lopend abonnement om op te zeggen.");
            await mollie.CancelSubscriptionAsync(customer, sub);
            ws.MollieSubscriptionId = null;
            ws.SubscriptionCanceledAt = DateTime.UtcNow;
            db.Log("abonnement", ws.PaidUntil is { } until
                ? $"Abonnement opgezegd door {user.Email}. Je kunt nog werken tot {Day(until)}; daarna wordt de werkruimte alleen-lezen."
                : $"Abonnement opgezegd door {user.Email}.");
            await db.SaveChangesAsync();
            return null;
        }
        catch (Exception e) when (e is MollieException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Opzeggen bij Mollie mislukt voor werkruimte {Workspace}", ws.Id);
            return Problem(Unreachable, StatusCodes.Status502BadGateway);
        }
        finally { Gate.Release(); }
    }

    // Voor het verwijderen van een werkruimte: eerst het abonnement stoppen, zodat er niets meer wordt afgeschreven.
    public async Task<bool> StopBeforeDeleteAsync(Workspace ws)
    {
        if (ws.MollieSubscriptionId is not { } sub || ws.MollieCustomerId is not { } customer) return true;
        if (!Enabled) { logger.LogWarning("Werkruimte {Workspace} heeft abonnement {Sub} bij Mollie, maar Mollie:ApiKey ontbreekt. Zeg het op in Mollie.", ws.Id, sub); return true; }
        await Gate.WaitAsync();
        try { await mollie.CancelSubscriptionAsync(customer, sub); return true; }
        catch (Exception e) when (e is MollieException or HttpRequestException or TaskCanceledException)
        {
            logger.LogError(e, "Opzeggen bij Mollie mislukt voor werkruimte {Workspace}", ws.Id);
            return false;
        }
        finally { Gate.Release(); }
    }

    // Haalt de status op van betalingen die nog lopen, bijvoorbeeld als je net terugkomt van de betaalpagina.
    public async Task SyncAsync(AppDb db)
    {
        var since = DateTime.UtcNow.AddDays(-3);
        var open = await db.MolliePayments.Where(p => (p.Status == "open" || p.Status == "pending" || p.Status == "authorized") && p.CreatedAt > since)
            .OrderByDescending(p => p.CreatedAt).Select(p => p.Id).Take(3).ToListAsync();
        foreach (var id in open) await ProcessAsync(id);
    }

    // Verwerkt de actuele status van een betaling. Geeft de status bij Mollie terug, of null als we de betaling niet kennen.
    // Gooit bij een storing van Mollie een fout, zodat de webhook het later opnieuw probeert.
    public async Task<string?> ProcessAsync(string paymentId)
    {
        await Gate.WaitAsync();
        try
        {
            var p = await mollie.GetPaymentAsync(paymentId);
            if (p is null || p.Id != paymentId)
            {
                logger.LogInformation("Betaling {Id} is niet bekend bij Mollie; genegeerd", paymentId);
                return null;
            }
            if (await ApplyAsync(p) is not { } row) return null;
            if (row.AppliedAt is not null && row.InvoiceId is null) await OwnerInvoiceAsync(row.Id);
            return p.Status;
        }
        finally { Gate.Release(); }
    }

    // Wat we over een betaling weten vóórdat we iets opslaan. Alle vragen aan Mollie gebeuren hier, buiten de schrijfvergrendeling.
    record Prepared(int WorkspaceId, string Plan, decimal VatRate, string? NewSubscriptionId, bool SubscriptionStopped);

    async Task<MolliePayment?> ApplyAsync(MolliePaymentInfo p)
    {
        Prepared? prep;
        using (var scope = scopes.CreateScope())
            prep = await PrepareAsync(scope.ServiceProvider.GetRequiredService<AppDb>(), p);
        if (prep is null) return null;

        using var write = scopes.CreateScope();
        var db = write.ServiceProvider.GetRequiredService<AppDb>();
        db.Tenant.WorkspaceId = prep.WorkspaceId;
        await using var tx = await WriteLock.BeginAsync(db);
        var ws = await db.Workspaces.FindAsync(prep.WorkspaceId);
        if (ws is null) return null;
        var row = await db.MolliePayments.FirstOrDefaultAsync(x => x.Id == p.Id);
        if (row is null)
        {
            row = new MolliePayment
            {
                Id = p.Id, Plan = prep.Plan, SequenceType = "recurring", SubscriptionId = p.SubscriptionId, Amount = p.Amount?.Euros ?? 0,
                VatRate = prep.VatRate, Status = "open", CreatedAt = p.CreatedAt?.UtcDateTime ?? DateTime.UtcNow,
            };
            db.MolliePayments.Add(row);
        }
        var previous = row.Status;
        row.Status = p.Status;
        var q = Euro(row.Amount);

        if (p.Status == "paid" && row.AppliedAt is null)
        {
            var paidAt = p.PaidAt?.UtcDateTime ?? DateTime.UtcNow;
            row.PaidAt = paidAt;
            if (row.SequenceType == "first")
            {
                row.PeriodStart = paidAt;
                row.PeriodEnd = paidAt.AddMonths(1);
                ws.Plan = row.Plan;
                ws.TrialEndsAt = null;
                ws.PaidUntil = row.PeriodEnd;
                ws.SubscriptionCanceledAt = null;
                ws.MollieSubscriptionId = prep.NewSubscriptionId;
                row.SubscriptionId = prep.NewSubscriptionId;
                db.Log("abonnement", prep.NewSubscriptionId is not null
                    ? $"Abonnement {Plans.Name(row.Plan)} betaald met iDEAL ({q}). Daarna schrijven we elke maand {q} af via automatische incasso."
                    : $"Abonnement {Plans.Name(row.Plan)} betaald met iDEAL ({q}), tot {Day(row.PeriodEnd.Value)}. De maandelijkse incasso kon niet worden ingesteld; betaal daarna opnieuw onder Instellingen, Abonnement.");
                if (prep.NewSubscriptionId is null)
                    logger.LogWarning("Werkruimte {Workspace} betaalde {Payment}, maar er is geen abonnement bij Mollie aangemaakt", ws.Id, p.Id);
            }
            else
            {
                // Een incasso sluit aan op de vorige periode, ook als hij een paar dagen later binnenkomt.
                var start = ws.PaidUntil is { } until && until >= paidAt.AddDays(-2 * Plans.PaymentGraceDays) ? until : paidAt;
                row.PeriodStart = start;
                row.PeriodEnd = start.AddMonths(1);
                if (ws.PaidUntil is null || ws.PaidUntil < row.PeriodEnd) ws.PaidUntil = row.PeriodEnd;
                if (ws.MollieSubscriptionId == row.SubscriptionId) ws.Plan = row.Plan;
                db.Log("abonnement", $"Incasso van {q} voor {Plans.Name(row.Plan)} ontvangen. Je abonnement is betaald tot {Day(ws.PaidUntil!.Value)}.");
            }
            row.AppliedAt = DateTime.UtcNow;
        }
        else if (previous != p.Status && p.Status is "failed" or "canceled" or "expired")
        {
            if (row.SequenceType == "first")
                db.Log("abonnement", $"De betaling met iDEAL voor {Plans.Name(row.Plan)} is niet gelukt. Er is niets afgeschreven en niets veranderd; je kunt het opnieuw proberen onder Instellingen, Abonnement.");
            else
            {
                db.Log("abonnement", ws.PaidUntil is { } until
                    ? $"De incasso van {q} voor je abonnement is niet gelukt. Betaal opnieuw met iDEAL onder Instellingen, Abonnement, anders wordt de werkruimte na {Day(until.AddDays(Plans.PaymentGraceDays))} alleen-lezen."
                    : $"De incasso van {q} voor je abonnement is niet gelukt.");
                logger.LogWarning("Incasso {Payment} van werkruimte {Workspace} is {Status}", p.Id, ws.Id, p.Status);
            }
            if (prep.SubscriptionStopped && ws.MollieSubscriptionId == row.SubscriptionId)
            {
                ws.MollieSubscriptionId = null;
                ws.SubscriptionCanceledAt = DateTime.UtcNow;
                db.Log("abonnement", "Mollie heeft het abonnement stopgezet. Kies opnieuw een abonnement om door te gaan.");
            }
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return row;
    }

    async Task<Prepared?> PrepareAsync(AppDb db, MolliePaymentInfo p)
    {
        var row = await db.MolliePayments.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.Id);
        Workspace? ws;
        string plan;
        decimal rate;
        if (row is null)
        {
            // Een incasso kennen we nog niet: die maakt Mollie zelf aan voor een abonnement van een van onze klanten.
            if (p.SequenceType != "recurring" || p.CustomerId is null || p.SubscriptionId is null) return Ignore(p, "onbekende betaling");
            ws = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.MollieCustomerId == p.CustomerId);
            if (ws is null) return Ignore(p, "geen werkruimte bij deze klant");
            var sub = await mollie.GetSubscriptionAsync(p.CustomerId, p.SubscriptionId);
            if (sub is null || sub.Meta("workspaceId") != ws.Id.ToString(CultureInfo.InvariantCulture)) return Ignore(p, "abonnement hoort niet bij deze werkruimte");
            plan = sub.Meta("plan") is { } metaPlan && Plans.Paid.Contains(metaPlan) ? metaPlan : ws.Plan;
            if (!Plans.Paid.Contains(plan)) return Ignore(p, "onbekend abonnement");
            rate = decimal.TryParse(sub.Meta("vatRate"), NumberStyles.Number, CultureInfo.InvariantCulture, out var r) ? r : VatRate;
        }
        else
        {
            ws = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == row.WorkspaceId);
            if (ws is null) return Ignore(p, "werkruimte bestaat niet meer");
            if (row.SequenceType == "first" && p.Meta("workspaceId") != ws.Id.ToString(CultureInfo.InvariantCulture)) return Ignore(p, "andere werkruimte in metadata");
            if (row.SequenceType == "first" && p.Amount?.Euros != row.Amount) return Ignore(p, "ander bedrag dan gevraagd");
            plan = row.Plan;
            rate = row.VatRate;
        }
        if (p.CustomerId is null || p.CustomerId != ws.MollieCustomerId) return Ignore(p, "andere klant bij Mollie");

        string? newSub = null;
        var stopped = false;
        var unapplied = row?.AppliedAt is null;
        if (p.Status == "paid" && unapplied && (row?.SequenceType ?? "recurring") == "first")
        {
            // Een ouder abonnement (bijvoorbeeld bij overstappen van ZZP naar Team) stopt, zodat er nooit twee lopen.
            if (ws.MollieSubscriptionId is { } old) await mollie.CancelSubscriptionAsync(p.CustomerId, old);
            var q = QuoteFor(plan);
            var paidAt = p.PaidAt?.UtcDateTime ?? DateTime.UtcNow;
            try
            {
                var sub = await mollie.CreateSubscriptionAsync(p.CustomerId, MollieAmount.Euro(Money.Round(row!.Amount)), DateOnly.FromDateTime(LocalDate(paidAt.AddMonths(1))),
                    $"{Product} {q.Name}, werkruimte {ws.Id}, sinds {p.Id}", WebhookUrl(urls.Configured),
                    new { workspaceId = ws.Id.ToString(CultureInfo.InvariantCulture), plan, vatRate = rate.ToString(CultureInfo.InvariantCulture) },
                    idempotencyKey: $"potel-{p.Id}");
                newSub = sub.Id;
            }
            catch (MollieException e) when ((int)e.Status is >= 400 and < 500)
            {
                // Bijvoorbeeld geen geldig machtiging: de betaalde maand telt wel, maar daarna moet de klant opnieuw betalen.
                logger.LogError(e, "Abonnement aanmaken bij Mollie mislukt voor werkruimte {Workspace}", ws.Id);
            }
        }
        else if (p.Status is "failed" or "canceled" or "expired" && row?.SequenceType != "first" && p.SubscriptionId is { } sid && row?.Status != p.Status)
        {
            // Na een mislukte incasso kan Mollie het abonnement stoppen. Dan moet de klant opnieuw beginnen.
            var sub = await mollie.GetSubscriptionAsync(p.CustomerId, sid);
            stopped = sub is null || sub.Status != "active";
        }
        return new(ws.Id, plan, rate, newSub, stopped);
    }

    Prepared? Ignore(MolliePaymentInfo p, string why)
    {
        logger.LogWarning("Betaling {Id} van Mollie genegeerd: {Reason}", p.Id, why);
        return null;
    }

    // Maakt de factuur voor een betaalde maand in de werkruimte van de eigenaar van het platform (de werkruimte van de eerste
    // platformbeheerder), bij een klant met de gegevens van de betalende werkruimte. De factuur wordt verstuurd en meteen betaald.
    // Mist er iets om te mogen versturen, dan blijft hij als concept staan met een melding in het logboek van de eigenaar.
    async Task OwnerInvoiceAsync(string paymentId)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var owner = await db.Users.IgnoreQueryFilters().Where(u => u.IsPlatformAdmin && u.Active).OrderBy(u => u.Id).Select(u => u.WorkspaceId).FirstOrDefaultAsync();
        if (owner == 0)
        {
            logger.LogWarning("Er is geen platformbeheerder, dus geen factuur voor betaling {Payment}. Maak jezelf platformbeheerder en maak de factuur zelf.", paymentId);
            return;
        }
        var row = await db.MolliePayments.IgnoreQueryFilters().FirstAsync(p => p.Id == paymentId);
        if (row.WorkspaceId == owner) return; // je eigen werkruimte factureer je niet aan jezelf
        var payer = await db.Workspaces.FirstAsync(w => w.Id == row.WorkspaceId);
        var s = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.WorkspaceId == payer.Id) ?? new Settings { CompanyName = payer.Name };
        var adminEmail = await db.Users.IgnoreQueryFilters().Where(u => u.WorkspaceId == payer.Id && u.Role == Roles.Admin && u.Active)
            .OrderBy(u => u.Id).Select(u => u.Email).FirstOrDefaultAsync();

        db.Tenant.WorkspaceId = owner;
        await using var tx = await WriteLock.BeginAsync(db);
        var invoice = await db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Reference == row.Id);
        var becamePaid = false;
        if (invoice is null)
        {
            var customer = payer.BillingCustomerId is { } cid ? await db.Customers.FirstOrDefaultAsync(c => c.Id == cid) : null;
            string? Pick(string? current, string? value) => string.IsNullOrWhiteSpace(current) ? (string.IsNullOrWhiteSpace(value) ? current : value.Trim()) : current;
            if (customer is null)
            {
                customer = new Customer { Name = (s.OwnerName ?? s.CompanyName).Trim(), Country = Countries.Netherlands };
                db.Customers.Add(customer);
            }
            customer.Company = Pick(customer.Company, s.CompanyName);
            customer.Email = Pick(customer.Email, s.Email ?? adminEmail);
            customer.Address = Pick(customer.Address, s.Address);
            customer.City = Pick(customer.City, s.City);
            customer.VatNumber = Pick(customer.VatNumber, InvoiceParty.NormalizeVatId(s.Btw));
            customer.Notes = Pick(customer.Notes, $"Klant van {Product}: werkruimte {payer.Id}, KvK {payer.Kvk ?? s.Kvk ?? "onbekend"}.");
            await db.SaveChangesAsync();
            payer.BillingCustomerId = customer.Id;

            var from = LocalDate(row.PeriodStart ?? row.PaidAt ?? DateTime.UtcNow);
            var to = LocalDate(row.PeriodEnd ?? (row.PaidAt ?? DateTime.UtcNow).AddMonths(1)).AddDays(-1);
            if (to < from) to = from;
            var net = Money.Round(row.Amount * 100 / (100 + row.VatRate));
            invoice = new Invoice
            {
                CustomerId = customer.Id, IssueDate = clock.Today, DueDate = clock.Today, Reference = row.Id,
                VatRegime = row.VatRate > 0 ? VatRegimes.Normal : VatRegimes.DefaultFor(await SettingsEndpoints.GetAsync(db), customer),
                DeliveryFrom = from, DeliveryTo = to,
                Notes = $"Al betaald via Mollie op {Day(row.PaidAt ?? DateTime.UtcNow)} (betaling {row.Id}).",
                Lines = [new InvoiceLine { Description = $"{Product} {Plans.Name(row.Plan)}, {from.ToString("d MMMM yyyy", Dutch)} t/m {to.ToString("d MMMM yyyy", Dutch)}", Quantity = 1, Unit = "maand", UnitPrice = net, VatRate = row.VatRate }],
            };
            db.Invoices.Add(invoice);
            db.Log("factuur", $"Conceptfactuur voor het abonnement van {customer.Company ?? customer.Name} aangemaakt (betaling {row.Id})");
            await db.SaveChangesAsync();
            var paidOn = row.PaidAt is { } paid ? LocalDate(paid) : clock.Today;
            var (problem, paidNow) = await InvoiceEndpoints.ChangeStatusAsync(db, clock, invoice, InvoiceStatus.Paid, paidOn);
            if (problem is not null)
            {
                db.Log("factuur", $"De factuur voor het abonnement van {customer.Company ?? customer.Name} staat als concept klaar, want versturen lukte niet: {problem.Error}");
                logger.LogWarning("Factuur voor betaling {Payment} blijft concept: {Problem}", row.Id, problem.Error);
            }
            becamePaid = paidNow;
            await db.SaveChangesAsync();
        }
        row.InvoiceId = invoice.Id;
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        if (becamePaid) await InvoiceEndpoints.TriggerPaid(db, scope.ServiceProvider.GetRequiredService<WorkflowEngine>(), invoice);
    }
}
