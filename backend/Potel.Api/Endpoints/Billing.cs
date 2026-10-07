using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Payments;

namespace Potel.Api.Endpoints;

public record CheckoutRequest(string Plan);

// Online betalen van het abonnement via Mollie. Zonder Mollie:ApiKey bestaan deze adressen niet (404) en blijft overstappen
// gaan zoals eerst: de klant mailt en jij zet het abonnement om op de pagina Platform.
public static partial class BillingEndpoints
{
    [GeneratedRegex("^tr_[A-Za-z0-9]{1,64}$")]
    private static partial Regex PaymentId();

    static async Task<object> StatusAsync(AppDb db, Workspace ws)
    {
        var last = await db.MolliePayments.OrderByDescending(p => p.CreatedAt).FirstOrDefaultAsync();
        var now = DateTime.UtcNow;
        return new
        {
            plans = Plans.Paid.Select(MollieBilling.QuoteFor).Select(q => new { id = q.Plan, name = q.Name, net = q.Net, vat = q.Vat, gross = q.Gross, vatRate = q.VatRate }),
            plan = ws.Plan,
            subscriptionActive = ws.MollieSubscriptionId is not null,
            paidUntil = ws.PaidUntil,
            canceledAt = ws.SubscriptionCanceledAt,
            readOnly = ws.ReadOnlyReason(now),
            lastPayment = last is null ? null : new { last.Status, last.Plan, last.SequenceType, last.Amount, last.CreatedAt, last.PaidAt },
        };
    }

    public static void MapBilling(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/billing").AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.RequestServices.GetRequiredService<MollieBilling>().Enabled ? await next(ctx) : Results.NotFound());

        g.MapGet("/", async (AppDb db) =>
            await db.Workspaces.FindAsync(db.TenantId) is { } ws ? Results.Ok(await StatusAsync(db, ws)) : Results.NotFound());

        // Naar de betaalpagina van Mollie voor de eerste maand met iDEAL. Daarna wordt elke maand automatisch afgeschreven.
        g.MapPost("/checkout", async (AppDb db, MollieBilling billing, HttpContext http, ClaimsPrincipal me, CheckoutRequest req) =>
        {
            if (await db.Workspaces.FindAsync(db.TenantId) is not { } ws || me.UserId() is not { } id || await db.Users.FindAsync(id) is not { } user)
                return Results.NotFound();
            var (url, error) = await billing.CheckoutAsync(db, ws, user, req.Plan ?? "", http.Request);
            return error ?? Results.Ok(new { checkoutUrl = url });
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin)).RequireRateLimiting("accounts");

        // Terug van de betaalpagina: meteen de status bij Mollie ophalen, ook als de webhook nog onderweg is.
        g.MapPost("/sync", async (AppDb db, MollieBilling billing, ILogger<MollieBilling> logger) =>
        {
            if (await db.Workspaces.FindAsync(db.TenantId) is not { } ws) return Results.NotFound();
            try { await billing.SyncAsync(db); }
            catch (Exception e) { logger.LogError(e, "Betalingen bijwerken mislukt voor werkruimte {Workspace}", ws.Id); }
            await db.Entry(ws).ReloadAsync();
            return Results.Ok(await StatusAsync(db, ws));
        }).RequireRateLimiting("accounts");

        g.MapPost("/cancel", async (AppDb db, MollieBilling billing, ClaimsPrincipal me) =>
        {
            if (await db.Workspaces.FindAsync(db.TenantId) is not { } ws || me.UserId() is not { } id || await db.Users.FindAsync(id) is not { } user)
                return Results.NotFound();
            if (await billing.CancelAsync(db, ws, user) is { } error) return error;
            return Results.Ok(await StatusAsync(db, ws));
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin)).RequireRateLimiting("accounts");

        // Mollie meldt hier dat er iets met een betaling is gebeurd. Er staat alleen een id in; wat er echt is gebeurd, vragen we
        // zelf op bij Mollie. Een onbekend of verzonnen id doet dus niets. Bij een storing antwoorden we met een fout, zodat
        // Mollie het later opnieuw probeert.
        api.MapPost("/mollie/webhook", async (HttpRequest request, MollieBilling billing, ILogger<MollieBilling> logger) =>
        {
            if (!billing.Enabled) return Results.NotFound();
            if (!request.HasFormContentType || request.ContentLength > 4096) return Results.BadRequest();
            // Mollie stuurt alleen "id=tr_...": een groter verzoek lezen we niet.
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 4096;
            string id;
            try { id = (await request.ReadFormAsync()).TryGetValue("id", out var value) ? value.ToString() : ""; }
            catch (Exception e) when (e is BadHttpRequestException or InvalidDataException) { return Results.BadRequest(); }
            if (!PaymentId().IsMatch(id)) return Results.Ok();
            try { await billing.ProcessAsync(id); }
            catch (Exception e) when (e is MollieException or HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(e, "Webhook voor betaling {Id} niet verwerkt; Mollie probeert het later opnieuw", id);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            return Results.Ok();
        }).AllowAnonymous().RequireRateLimiting("mollie");
    }
}
