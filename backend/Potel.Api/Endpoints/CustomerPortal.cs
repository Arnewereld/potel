using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Invoicing;
using Potel.Api.Payments;

namespace Potel.Api.Endpoints;

public record PortalSettingsInput(bool ShowHours, bool ShowBudget, bool ShowInvoices, bool ShowAmounts, string? Note);
public record PortalLinkRequest(int? ExpiresInDays);
public record PublicPortalRequest(string? Token);

// Het klantportaal: per klant een geheime link waarmee die klant zonder inloggen zijn eigen lopende projecten, uren en
// facturen ziet. Net als bij de factuurlink staat alleen de SHA-256-hash van het token in de database, is de link in te
// trekken en geeft een onbekende, ingetrokken of verlopen link steeds hetzelfde antwoord. Het portaal toont nooit andere
// klanten, interne notities, niet-factureerbare uren, kosten of iets anders uit de werkruimte dan bedrijfsnaam, logo en kleur.
public static partial class CustomerPortalEndpoints
{
    public const string LinkPath = "/portaal";
    public const string LinkGone = "Deze link werkt niet (meer). Vraag je contactpersoon om een nieuwe link.";
    public const int MaxNoteLength = 1000;
    public const int MaxExpiryDays = 3650;
    // Zoveel weken laten we zien hoeveel uur er per week op een project ging.
    public const int BurnWeeks = 8;
    // Recente uren: hooguit zoveel dagen terug en zoveel regels.
    public const int RecentDays = 90;
    public const int MaxEntries = 50;
    public const int MaxInvoices = 50;

    [GeneratedRegex(@"^/\d+/portal/revoke/?$")]
    private static partial Regex RevokePath();

    // Een portaallink intrekken mag ook in een werkruimte die alleen-lezen is: het is een veiligheidsmaatregel.
    public static bool IsRevoke(HttpRequest r) =>
        HttpMethods.IsPost(r.Method) && r.Path.StartsWithSegments("/api/customers", out var rest) && RevokePath().IsMatch(rest.Value ?? "");

    // Het token van een factuurlink in het portaal, afgeleid van het portaaltoken. Zo is de link bij elk bezoek hetzelfde
    // zonder dat we een token bewaren, en geeft een doorgestuurde factuurlink geen toegang tot de rest van het portaal.
    public static string InvoiceToken(string portalToken, int invoiceId) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(portalToken), Encoding.UTF8.GetBytes($"potel-portaal-factuur:{invoiceId}")));

    // Of een factuurlink uit het portaal nog werkt: de portaallink werkt, hoort bij de klant van de factuur en facturen staan aan.
    public static async Task<bool> InvoiceLinkWorksAsync(AppDb db, int workspaceId, int portalLinkId, int customerId, DateTime utcNow)
    {
        var link = await db.PortalLinks.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == portalLinkId && l.WorkspaceId == workspaceId && l.CustomerId == customerId);
        if (link is null || !link.Works(utcNow)) return false;
        var settings = await db.CustomerPortals.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.CustomerId == customerId);
        return settings?.ShowInvoices ?? true;
    }

    static object? LinkDto(PortalLink? l, DateTime utcNow) => l is null ? null : new
    {
        l.CreatedAt, l.CreatedBy, l.ExpiresAt, l.LastOpenedAt, l.Opens, expired = !l.Works(utcNow),
    };

    // Instellingen en de stand van de link, voor de klantpagina. Het token zelf staat nergens.
    static async Task<object> DtoAsync(AppDb db, int customerId, DateTime utcNow)
    {
        var s = await db.CustomerPortals.AsNoTracking().FirstOrDefaultAsync(p => p.CustomerId == customerId) ?? new CustomerPortal();
        var links = await db.PortalLinks.AsNoTracking().Where(l => l.CustomerId == customerId).ToListAsync();
        var current = links.Where(l => l.RevokedAt == null).OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id).FirstOrDefault();
        return new
        {
            s.ShowHours, s.ShowBudget, s.ShowInvoices, s.ShowAmounts, s.Note,
            link = LinkDto(current, utcNow),
            // Ook van een ingetrokken link: wanneer de klant zijn portaal voor het laatst opende.
            lastOpenedAt = links.Max(l => l.LastOpenedAt),
        };
    }

    static string CustomerName(Customer c) => string.IsNullOrWhiteSpace(c.Company) ? c.Name : c.Company.Trim();

    public static void MapCustomerPortal(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/customers/{id:int}/portal");

        // Iedereen in de werkruimte ziet of er een link is en wanneer hij geopend is; alleen een beheerder wijzigt iets.
        g.MapGet("/", async (AppDb db, BusinessClock clock, int id) =>
            await db.Customers.AnyAsync(c => c.Id == id) ? Results.Ok(await DtoAsync(db, id, clock.UtcNow)) : Results.NotFound());

        g.MapPut("/", async (AppDb db, BusinessClock clock, int id, PortalSettingsInput input) =>
        {
            var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Replace("\r\n", "\n").Trim();
            if (note is { Length: > MaxNoteLength }) return Results.BadRequest(new { error = $"Het bericht voor je klant mag hooguit {MaxNoteLength} tekens zijn." });
            await using var tx = await WriteLock.BeginAsync(db);
            var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id);
            if (customer is null) return Results.NotFound();
            var s = await db.CustomerPortals.FirstOrDefaultAsync(p => p.CustomerId == id);
            if (s is null) db.CustomerPortals.Add(s = new CustomerPortal { CustomerId = id });
            s.ShowHours = input.ShowHours; s.ShowBudget = input.ShowBudget; s.ShowInvoices = input.ShowInvoices; s.ShowAmounts = input.ShowAmounts;
            s.Note = note; s.UpdatedAt = clock.UtcNow;
            db.Log("klant", $"Klantportaal van {customer.Name} bijgewerkt");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(await DtoAsync(db, id, clock.UtcNow));
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Een nieuwe link. Een oude link van deze klant werkt daarna niet meer. Het token zie je alleen nu.
        g.MapPost("/link", async (AppDb db, BusinessClock clock, ClaimsPrincipal user, int id, PortalLinkRequest? req) =>
        {
            if (req?.ExpiresInDays is { } days && (days < 1 || days > MaxExpiryDays))
                return Results.BadRequest(new { error = "Kies hoe lang de link werkt: tussen 1 dag en 10 jaar, of zonder einddatum." });
            await using var tx = await WriteLock.BeginAsync(db);
            var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id);
            if (customer is null) return Results.NotFound();
            var now = clock.UtcNow;
            await db.PortalLinks.Where(l => l.CustomerId == id && l.RevokedAt == null).ExecuteUpdateAsync(x => x.SetProperty(l => l.RevokedAt, now));
            var token = InvoiceMailer.NewToken();
            db.PortalLinks.Add(new PortalLink
            {
                CustomerId = id, TokenHash = InvoiceMailer.Hash(token), CreatedAt = now, CreatedBy = user.FindFirstValue(ClaimTypes.Name),
                ExpiresAt = req?.ExpiresInDays is { } d ? now.AddDays(d) : null,
            });
            db.Log("klant", $"Nieuwe link naar het klantportaal van {customer.Name} gemaakt");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            // Het token staat achter een #, zodat het niet in logboeken van servers of proxy's belandt.
            return Results.Ok(new { path = $"{LinkPath}#token={token}", portal = await DtoAsync(db, id, now) });
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        g.MapPost("/revoke", async (AppDb db, BusinessClock clock, int id) =>
        {
            var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (customer is null) return Results.NotFound();
            var now = clock.UtcNow;
            var revoked = await db.PortalLinks.Where(l => l.CustomerId == id && l.RevokedAt == null).ExecuteUpdateAsync(x => x.SetProperty(l => l.RevokedAt, now));
            if (revoked > 0)
            {
                db.Log("klant", $"Link naar het klantportaal van {customer.Name} ingetrokken");
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { revoked, portal = await DtoAsync(db, id, now) });
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Het portaal zelf: zonder inloggen, alleen met het token uit de link. Alles wordt expliciet gefilterd op de werkruimte
        // en de klant van de link; de rest van de werkruimte komt er nooit in.
        api.MapPost("/public/portal", async (AppDb db, BusinessClock clock, InvoiceIdeal ideal, HttpResponse response, PublicPortalRequest req) =>
        {
            InvoiceMailEndpoints.NoIndex(response);
            var gone = Results.NotFound(new { error = LinkGone });
            var token = req.Token?.Trim();
            if (string.IsNullOrEmpty(token) || token.Length > 100) return gone;
            var hash = InvoiceMailer.Hash(token);
            var now = clock.UtcNow;
            var link = await db.PortalLinks.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(l => l.TokenHash == hash);
            if (link is null || !link.Works(now)) return gone;
            var ws = link.WorkspaceId;
            var customer = await db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.Id == link.CustomerId && c.WorkspaceId == ws);
            if (customer is null) return gone;
            var s = await db.CustomerPortals.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(p => p.WorkspaceId == ws && p.CustomerId == customer.Id)
                    ?? new CustomerPortal();
            var brand = await db.Settings.IgnoreQueryFilters().AsNoTracking().Where(x => x.WorkspaceId == ws).OrderBy(x => x.Id)
                .Select(x => new { x.CompanyName, x.LogoDataUrl, x.BrandColor }).FirstOrDefaultAsync();
            var today = clock.Today;

            // Alleen lopende projecten van deze klant, en alleen factureerbare uren: niet-factureerbare uren zie je klant nooit,
            // ook niet in een totaal. Ook de recente uren komen alleen van lopende projecten.
            var projects = await db.Projects.IgnoreQueryFilters().AsNoTracking().Where(p => p.WorkspaceId == ws && p.CustomerId == customer.Id).ToListAsync();
            var active = projects.Where(p => p.Status == "actief").OrderBy(p => p.Name).ToList();
            var activeIds = active.Select(p => p.Id).ToList();
            var billable = db.TimeEntries.IgnoreQueryFilters().AsNoTracking().Where(t => t.WorkspaceId == ws && t.Billable);
            var totals = await billable.Where(t => activeIds.Contains(t.ProjectId))
                .GroupBy(t => t.ProjectId).Select(x => new { ProjectId = x.Key, Minutes = x.Sum(t => t.Minutes), Last = x.Max(t => t.Date) })
                .ToDictionaryAsync(x => x.ProjectId);
            var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            var firstWeek = monday.AddDays(-7 * (BurnWeeks - 1));
            var weekly = s.ShowHours
                ? await billable.Where(t => activeIds.Contains(t.ProjectId) && t.Date >= firstWeek).Select(t => new { t.ProjectId, t.Date, t.Minutes }).ToListAsync()
                : [];
            var hourly = projects.Where(p => p.Billing == Billing.Hourly).ToDictionary(p => p.Id, p => p.HourlyRate);
            var amounts = s.ShowAmounts;

            var projectList = active.Select(p =>
            {
                var used = totals.GetValueOrDefault(p.Id);
                var isHourly = p.Billing == Billing.Hourly;
                var minutes = used?.Minutes ?? 0;
                return new
                {
                    p.Name, p.Billing, p.Deadline,
                    budgetHours = s.ShowBudget ? p.BudgetHours : null,
                    minutes = s.ShowHours || s.ShowBudget ? minutes : (int?)null,
                    lastEntry = s.ShowHours ? used?.Last : null,
                    weeks = s.ShowHours
                        ? Enumerable.Range(0, BurnWeeks).Select(i => firstWeek.AddDays(7 * i)).Select(w => new
                        {
                            start = w, minutes = weekly.Where(e => e.ProjectId == p.Id && e.Date >= w && e.Date < w.AddDays(7)).Sum(e => e.Minutes),
                        }).ToList()
                        : null,
                    hourlyRate = amounts && isHourly ? p.HourlyRate : (decimal?)null,
                    value = amounts && isHourly ? Money.Round(ProjectEndpoints.Hours(minutes) * p.HourlyRate) : (decimal?)null,
                    fixedPrice = amounts && !isHourly ? p.FixedPrice : (decimal?)null,
                };
            }).ToList();

            var names = active.ToDictionary(p => p.Id, p => p.Name);
            var entries = s.ShowHours
                ? (await billable.Where(t => activeIds.Contains(t.ProjectId) && t.Date >= today.AddDays(-RecentDays))
                    .OrderByDescending(t => t.Date).ThenByDescending(t => t.Id).Take(MaxEntries)
                    .Select(t => new { t.ProjectId, t.Date, t.Minutes, t.Description }).ToListAsync())
                    .Select(t => new
                    {
                        t.Date, project = names[t.ProjectId], t.Minutes, t.Description,
                        amount = amounts && hourly.TryGetValue(t.ProjectId, out var rate) ? Money.Round(ProjectEndpoints.Hours(t.Minutes) * rate) : (decimal?)null,
                    }).ToList()
                : null;

            object? invoices = null;
            if (s.ShowInvoices)
            {
                var list = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                    .Include(i => i.Lines).Include(i => i.Credits).ThenInclude(c => c.Lines).Include(i => i.Payments).AsSplitQuery()
                    .Where(i => i.WorkspaceId == ws && i.CustomerId == customer.Id && i.Status != InvoiceStatus.Draft)
                    .OrderByDescending(i => i.IssueDate).ThenByDescending(i => i.Id).Take(MaxInvoices).ToListAsync();
                var links = await InvoiceLinksAsync(db, link, token, list.Where(i => !i.IsDemo).Select(i => i.Id).ToList(), now);
                // Met iDEAL betalen kan via de link naar de factuur, als de freelancer zijn eigen Mollie heeft ingesteld.
                var online = await ideal.AvailableAsync(db, ws);
                invoices = list.Select(i => new
                {
                    i.Number, i.IssueDate, i.DueDate,
                    status = i.Status == InvoiceStatus.Sent && !i.IsCredit && i.DueDate.Date < today ? InvoiceStatus.Overdue : i.Status,
                    i.IsCredit, total = i.Totals.Total, i.OpenAmount, i.FullyCredited, i.PaidAt,
                    link = links.TryGetValue(i.Id, out var t) ? $"{InvoiceMailer.LinkPath}#token={t}" : null,
                    canPay = online && links.ContainsKey(i.Id) && InvoiceIdeal.Payable(i, customer.IsDemo),
                }).ToList();
            }

            await db.PortalLinks.IgnoreQueryFilters().Where(l => l.Id == link.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(l => l.LastOpenedAt, now).SetProperty(l => l.Opens, l => l.Opens + 1));

            return Results.Ok(new
            {
                company = new { name = brand?.CompanyName ?? "", logoDataUrl = brand?.LogoDataUrl, brandColor = brand?.BrandColor ?? "#ff6d5a" },
                customer = new { name = CustomerName(customer) },
                note = s.Note,
                visible = new { hours = s.ShowHours, budget = s.ShowBudget, invoices = s.ShowInvoices, amounts = s.ShowAmounts },
                projects = projectList,
                entries,
                invoices,
                expiresAt = link.ExpiresAt,
            });
        }).AllowAnonymous().RequireRateLimiting("portal");
    }

    // Zorgt dat er voor elke factuur een factuurlink bij deze portaallink bestaat en geeft de werkende tokens terug.
    // Heeft de freelancer de links van een factuur ingetrokken, dan krijgt die factuur hier geen link.
    static async Task<Dictionary<int, string>> InvoiceLinksAsync(AppDb db, PortalLink portal, string portalToken, List<int> invoiceIds, DateTime now)
    {
        if (invoiceIds.Count == 0) return [];
        var tokens = invoiceIds.ToDictionary(id => id, id => InvoiceToken(portalToken, id));
        var hashes = tokens.ToDictionary(x => InvoiceMailer.Hash(x.Value), x => x.Key);
        var keys = hashes.Keys.ToList();
        var existing = await db.InvoiceLinks.IgnoreQueryFilters().AsNoTracking().Where(l => keys.Contains(l.TokenHash)).ToListAsync();
        var missing = hashes.Where(h => existing.All(l => l.TokenHash != h.Key)).ToList();
        if (missing.Count > 0)
        {
            // Nieuwe records krijgen de werkruimte van de link; dit verzoek heeft zelf geen ingelogde gebruiker.
            db.Tenant.WorkspaceId = portal.WorkspaceId;
            foreach (var (hash, invoiceId) in missing)
                db.InvoiceLinks.Add(new InvoiceLink { InvoiceId = invoiceId, TokenHash = hash, PortalLinkId = portal.Id, CreatedAt = now });
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
            {
                // Tegelijk geopend in een tweede tabblad: die maakte dezelfde links net. Lees ze opnieuw.
                db.ChangeTracker.Clear();
            }
            existing = await db.InvoiceLinks.IgnoreQueryFilters().AsNoTracking().Where(l => keys.Contains(l.TokenHash)).ToListAsync();
        }
        return existing.Where(l => l.RevokedAt == null && l.WorkspaceId == portal.WorkspaceId && l.PortalLinkId == portal.Id && hashes.GetValueOrDefault(l.TokenHash) == l.InvoiceId)
            .ToDictionary(l => l.InvoiceId, l => tokens[l.InvoiceId]);
    }
}
