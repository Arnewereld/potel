using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Invoicing;
using Potel.Api.Payments;
using Potel.Api.Workflows;

namespace Potel.Api.Endpoints;

public record MollieKeyRequest(string? Key);
public record PaymentStatusRequest(string? Ref);

// iDEAL op facturen via de eigen Mollie van de werkruimte: de sleutel instellen bij Instellingen, de knop op de openbare
// factuurpagina, de webhook van Mollie en de status als de klant terugkomt van de betaalpagina.
public static class InvoiceIdealEndpoints
{
    public const string UnknownPayment = "We kunnen deze betaling niet vinden. Open de factuur opnieuw via de link in de mail.";

    // De sleutel weghalen mag ook in een werkruimte die alleen-lezen is: het is een geheim dat je kwijt wilt kunnen.
    public static bool IsRemoveKey(HttpRequest r) =>
        HttpMethods.IsDelete(r.Method) && r.Path.Equals("/api/settings/mollie", StringComparison.OrdinalIgnoreCase);

    // Of er een sleutel is (test of live en de laatste 4 tekens), en of online betalen in deze installatie kan.
    static async Task<object> DtoAsync(AppDb db, InvoiceIdeal ideal)
    {
        var a = await db.MollieAccounts.AsNoTracking().FirstOrDefaultAsync();
        var problem = ideal.BaseUrlProblem();
        return new
        {
            key = a is null ? null : new { a.Mode, last4 = a.KeyLast4, a.UpdatedAt, a.UpdatedBy },
            available = a is not null && problem is null,
            problem,
        };
    }

    public static void MapInvoiceIdeal(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/settings/mollie");

        g.MapGet("/", async (AppDb db, InvoiceIdeal ideal) => Results.Ok(await DtoAsync(db, ideal)));

        g.MapPut("/", async (AppDb db, InvoiceIdeal ideal, BusinessClock clock, ClaimsPrincipal user, MollieKeyRequest req) =>
        {
            var key = req.Key?.Trim() ?? "";
            if (key.StartsWith("access_", StringComparison.Ordinal))
                return Results.BadRequest(new { error = "Dit is een organisatietoken. Gebruik een API-sleutel die begint met live_ of test_. Die vind je in je Mollie-dashboard bij Ontwikkelaars en dan API-sleutels." });
            if (!InvoiceIdeal.KeyPattern().IsMatch(key))
                return Results.BadRequest(new { error = "Dit lijkt geen API-sleutel van Mollie. Een sleutel begint met live_ of test_ en bestaat verder uit letters en cijfers." });
            await using var tx = await WriteLock.BeginAsync(db);
            var a = await db.MollieAccounts.FirstOrDefaultAsync();
            if (a is null) db.MollieAccounts.Add(a = new MollieAccount());
            a.ProtectedKey = ideal.Protect(key);
            a.KeyLast4 = key[^4..];
            a.Mode = InvoiceIdeal.ModeOf(key);
            a.UpdatedAt = clock.UtcNow;
            a.UpdatedBy = user.FindFirstValue(ClaimTypes.Name);
            db.Log("instellingen", $"Sleutel van Mollie voor iDEAL op facturen opgeslagen ({a.Mode})");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(await DtoAsync(db, ideal));
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        g.MapDelete("/", async (AppDb db, InvoiceIdeal ideal) =>
        {
            if (await db.MollieAccounts.ExecuteDeleteAsync() > 0)
            {
                db.Log("instellingen", "Sleutel van Mollie verwijderd; klanten kunnen facturen niet meer met iDEAL betalen");
                await db.SaveChangesAsync();
            }
            return Results.Ok(await DtoAsync(db, ideal));
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Vraagt Mollie welke betaalmethodes aan staan. Zo zie je of de sleutel werkt en of iDEAL aan staat.
        g.MapPost("/test", async (AppDb db, InvoiceIdeal ideal) =>
        {
            var a = await db.MollieAccounts.AsNoTracking().FirstOrDefaultAsync();
            if (a is null) return Results.BadRequest(new { error = "Er is nog geen sleutel van Mollie ingesteld." });
            if (ideal.Unprotect(a.ProtectedKey) is not { } key)
                return Results.Ok(new { ok = false, message = "De opgeslagen sleutel is niet meer te lezen. Stel hem opnieuw in." });
            var (ok, message) = await ideal.TestAsync(key);
            return Results.Ok(new { ok, message });
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin)).RequireRateLimiting("accounts");

        // Betaal nu met iDEAL, vanaf de openbare factuurpagina of het klantportaal. Alleen het token van de factuurlink gaat mee:
        // welk bedrag, voor welke factuur en met welke sleutel bepaalt de server.
        api.MapPost("/public/invoice/pay", async (AppDb db, InvoiceIdeal ideal, WorkflowEngine engine, BusinessClock clock, HttpResponse response, PublicInvoiceRequest req) =>
        {
            InvoiceMailEndpoints.NoIndex(response);
            if (await InvoiceMailEndpoints.OpenLinkAsync(db, clock, req.Token) is not { } found)
                return Results.NotFound(new { error = InvoiceMailEndpoints.LinkGone });
            var start = await ideal.StartAsync(db, engine, found.Link.WorkspaceId, found.Invoice.Id);
            return start.Problem?.ToResult() ?? Results.Ok(new { checkoutUrl = start.CheckoutUrl });
        }).AllowAnonymous().RequireRateLimiting("pay");

        // Mollie meldt hier iDEAL-betalingen van facturen. Er staat alleen een id in; een onbekend id doet niets. Wat er
        // gebeurd is, vragen we op met de sleutel van de werkruimte van die betaling. Bij een storing antwoorden we met een
        // fout, zodat Mollie het later opnieuw probeert. Los van /api/mollie/webhook (het abonnement op Potel).
        api.MapPost("/public/invoice/mollie-webhook", async (HttpRequest request, AppDb db, InvoiceIdeal ideal, WorkflowEngine engine, ILogger<InvoiceIdeal> logger) =>
        {
            if (!request.HasFormContentType || request.ContentLength > 4096) return Results.BadRequest();
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 4096;
            string id;
            try { id = (await request.ReadFormAsync()).TryGetValue("id", out var value) ? value.ToString() : ""; }
            catch (Exception e) when (e is BadHttpRequestException or InvalidDataException) { return Results.BadRequest(); }
            if (!InvoiceIdeal.PaymentId().IsMatch(id)) return Results.Ok();
            try { await ideal.ProcessAsync(db, engine, id); }
            catch (Exception e) when (e is MollieException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(e, "Webhook voor iDEAL-betaling {Id} niet verwerkt; Mollie probeert het later opnieuw", id);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            return Results.Ok();
        }).AllowAnonymous().RequireRateLimiting("mollie");

        // Terug van de betaalpagina: wat wij over de betaling weten (de webhook houdt het bij). De code uit het terugkeeradres
        // laat alleen deze ene betaling zien, niet de factuur zelf.
        api.MapPost("/public/invoice/payment-status", async (AppDb db, HttpResponse response, PaymentStatusRequest req) =>
        {
            InvoiceMailEndpoints.NoIndex(response);
            var code = req.Ref?.Trim();
            if (string.IsNullOrEmpty(code) || code.Length > 100) return Results.NotFound(new { error = UnknownPayment });
            var hash = InvoiceMailer.Hash(code);
            var p = await db.OnlinePayments.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.ReturnTokenHash == hash);
            if (p is null) return Results.NotFound(new { error = UnknownPayment });
            var inv = await db.Invoices.IgnoreQueryFilters().AsNoTracking().Where(i => i.Id == p.InvoiceId && i.WorkspaceId == p.WorkspaceId)
                .Select(i => new { i.Number, i.Status, Seller = i.Seller == null ? null : i.Seller.Name }).FirstOrDefaultAsync();
            if (inv is null) return Results.NotFound(new { error = UnknownPayment });
            var brand = await db.Settings.IgnoreQueryFilters().AsNoTracking().Where(s => s.WorkspaceId == p.WorkspaceId).OrderBy(s => s.Id)
                .Select(s => new { s.LogoDataUrl, s.BrandColor }).FirstOrDefaultAsync();
            return Results.Ok(new
            {
                status = p.Status,
                paid = p.Status == "paid",
                amount = p.Amount,
                test = p.Mode == "test",
                number = inv.Number,
                seller = inv.Seller,
                invoicePaid = inv.Status == InvoiceStatus.Paid,
                brand = new { logoDataUrl = brand?.LogoDataUrl, brandColor = brand?.BrandColor ?? "#ff6d5a" },
            });
        }).AllowAnonymous().RequireRateLimiting("public");
    }
}
