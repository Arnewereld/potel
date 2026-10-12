using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Invoicing;

namespace Potel.Api.Endpoints;

public record MailRequest(int Step, string? Subject, string? Body);
public record RemindersOffRequest(bool Off);
public record MailTextInput(string? Subject, string? Body);
public record ReminderSettingsInput(bool Enabled, int FirstDays, int SecondDays, int FinalDays,
    MailTextInput? Invoice, MailTextInput? First, MailTextInput? Second, MailTextInput? Final);
public record PublicInvoiceRequest(string? Token);

// Facturen en herinneringen mailen, de instellingen voor automatische herinneringen, en de openbare pagina achter de link in de mail.
public static class InvoiceMailEndpoints
{
    public const string LinkGone = "Deze link werkt niet (meer). Vraag de afzender van de factuur om een nieuwe link.";

    // De openbare factuurpagina en wat hij ophaalt: niet in zoekmachines, geen adres doorsturen naar andere sites, niet bewaren.
    public static void NoIndex(HttpResponse r)
    {
        r.Headers["X-Robots-Tag"] = "noindex, nofollow";
        r.Headers["Referrer-Policy"] = "no-referrer";
        r.Headers.CacheControl = "no-store";
    }

    static object Text(MailText t) => new { subject = t.Subject, body = t.Body };

    static object SettingsDto(ReminderSettings s, string? problem) => new
    {
        s.Enabled, s.FirstDays, s.SecondDays, s.FinalDays,
        texts = new { invoice = Text(MailTemplates.For(s, MailSteps.Invoice)), first = Text(MailTemplates.For(s, MailSteps.First)),
            second = Text(MailTemplates.For(s, MailSteps.Second)), final = Text(MailTemplates.For(s, MailSteps.Final)) },
        defaults = new { invoice = Text(MailTemplates.Invoice), first = Text(MailTemplates.First), second = Text(MailTemplates.Second), final = Text(MailTemplates.Final) },
        placeholders = MailTemplates.Placeholders,
        canSend = problem is null, problem,
    };

    static string Normalize(string? s) => (s ?? "").Replace("\r\n", "\n").Trim();

    // Een tekst die gelijk is aan de standaardtekst bewaren we als leeg, zodat verbeteringen in de standaardtekst vanzelf meekomen.
    static (string? Subject, string? Body, string? Error) Apply(int step, MailTextInput? input)
    {
        var d = MailTemplates.Default(step);
        var subject = Normalize(input?.Subject ?? d.Subject);
        var body = Normalize(input?.Body ?? d.Body);
        if (MailTemplates.Problem(step, subject, body) is { } error) return (null, null, error);
        return (subject == Normalize(d.Subject) ? null : subject, body == Normalize(d.Body) ? null : body, null);
    }

    public static void MapInvoiceMail(this RouteGroupBuilder api)
    {
        // Kan Potel zelf mailen? Daarmee kiest de frontend tussen versturen per e-mail en een mail in je eigen mailprogramma.
        api.MapGet("/invoices/mail-status", async (AppDb db, InvoiceMailer mailer, AppUrls urls, HttpRequest request) =>
        {
            var problem = await mailer.BlockerAsync(urls.Base(request));
            var reminders = await db.ReminderSettings.AsNoTracking().FirstOrDefaultAsync();
            return Results.Ok(new { canSend = problem is null, problem, remindersEnabled = reminders?.Enabled ?? false });
        });

        // Wat er over deze factuur gemaild is, of er een link openstaat, en wanneer de volgende automatische herinnering komt.
        api.MapGet("/invoices/{id:int}/mails", async (AppDb db, BusinessClock clock, int id) =>
        {
            var inv = await InvoiceMailer.WithAll(db).AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
            if (inv is null) return Results.NotFound();
            var mails = await db.InvoiceMails.AsNoTracking().Where(m => m.InvoiceId == id).ToListAsync();
            var links = await db.InvoiceLinks.AsNoTracking().Where(l => l.InvoiceId == id).ToListAsync();
            var settings = await db.ReminderSettings.AsNoTracking().FirstOrDefaultAsync() ?? new ReminderSettings();
            var today = clock.Today;
            var open = inv.Status is InvoiceStatus.Sent or InvoiceStatus.Overdue && !inv.IsCredit && inv.OpenAmount > 0 && !inv.IsDemo && inv.Customer?.IsDemo != true;
            object? next = null;
            if (open && settings.Enabled && !inv.RemindersOff && inv.Customer?.RemindersOff != true && ReminderService.Next(inv, mails, settings) is { } n)
                next = new { step = n.Step, label = MailSteps.Label(n.Step), date = n.On < today ? today : n.On };
            var lastStep = mails.Where(m => m.Step >= MailSteps.First && m.Status is MailStatus.Sent or MailStatus.Sending).Select(m => m.Step).DefaultIfEmpty(0).Max();
            return Results.Ok(new
            {
                mails = mails.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Select(m => new
                {
                    m.Id, m.Step, label = MailSteps.Label(m.Step), m.To, m.Subject, m.Status, m.Note, m.Automatic, m.SentBy, m.CreatedAt, m.SentAt,
                }),
                activeLinks = links.Count(l => l.RevokedAt == null),
                lastOpenedAt = links.Max(l => l.LastOpenedAt),
                remindersEnabled = settings.Enabled,
                remindersOff = inv.RemindersOff,
                customerRemindersOff = inv.Customer?.RemindersOff ?? false,
                nextReminder = next,
                // Welke herinnering je handmatig als volgende zou sturen.
                nextStep = Math.Min(MailSteps.Final, lastStep + 1),
            });
        });

        // De mail zoals hij weg zou gaan: alles ingevuld behalve {link}, want die maken we pas bij versturen.
        api.MapGet("/invoices/{id:int}/mail-preview", async (AppDb db, InvoiceMailer mailer, AppUrls urls, BusinessClock clock, HttpRequest request, int id, int? step) =>
        {
            var inv = await InvoiceMailer.WithAll(db).AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
            if (inv is null) return Results.NotFound();
            var s = Math.Clamp(step ?? MailSteps.Invoice, MailSteps.Invoice, MailSteps.Final);
            var text = await mailer.ComposeAsync(inv, s, link: null);
            var problem = InvoiceMailer.Problem(inv, s, clock.Today) ?? await mailer.BlockerAsync(urls.Base(request));
            return Results.Ok(new
            {
                step = s, label = MailSteps.Label(s), to = InvoiceMailer.CustomerEmail(inv.Customer) ?? inv.Customer?.Email, text.Subject, text.Body, problem,
                consumer = ReminderRules.IsConsumer(inv, inv.Customer!),
            });
        });

        // Handmatig mailen: de factuur zelf (stap 0) of een herinnering. Altijd naar het adres van de klant, nooit naar een ander adres.
        api.MapPost("/invoices/{id:int}/mail", async (AppDb db, InvoiceMailer mailer, AppUrls urls, HttpContext http, int id, MailRequest req) =>
        {
            if (!await db.Invoices.AnyAsync(i => i.Id == id)) return Results.NotFound();
            var result = await mailer.SendAsync(id, req.Step, automatic: false, urls.Base(http.Request), http.User.FindFirstValue(ClaimTypes.Name),
                req.Subject?.Trim(), req.Body?.Trim());
            return result.Outcome switch
            {
                SendOutcome.Sent => Results.Ok(new { mail = result.Mail }),
                SendOutcome.Failed => Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status502BadGateway),
                _ => Results.BadRequest(new { error = result.Message ?? "Deze mail is net al verstuurd." }),
            };
        });

        // Alle links naar deze factuur dichtzetten. Wie de oude mail opent, ziet dan dat de link niet meer werkt.
        api.MapPost("/invoices/{id:int}/links/revoke", async (AppDb db, int id) =>
        {
            var inv = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
            if (inv is null) return Results.NotFound();
            var now = DateTime.UtcNow;
            var revoked = await db.InvoiceLinks.Where(l => l.InvoiceId == id && l.RevokedAt == null).ExecuteUpdateAsync(x => x.SetProperty(l => l.RevokedAt, now));
            if (revoked > 0)
            {
                db.Log("factuur", $"{(revoked == 1 ? "De link" : $"{revoked} links")} naar {InvoiceEndpoints.Label(inv).ToLowerInvariant()} ingetrokken");
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { revoked });
        });

        // Automatische herinneringen voor deze ene factuur aan of uit. Kan ook na versturen: het staat niet op de factuur.
        api.MapPost("/invoices/{id:int}/reminders", async (AppDb db, int id, RemindersOffRequest req) =>
        {
            var inv = await db.Invoices.FirstOrDefaultAsync(i => i.Id == id);
            if (inv is null) return Results.NotFound();
            if (inv.RemindersOff != req.Off)
            {
                inv.RemindersOff = req.Off;
                db.Log("factuur", $"Automatische herinneringen voor {InvoiceEndpoints.Label(inv).ToLowerInvariant()} {(req.Off ? "uitgezet" : "aangezet")}");
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { remindersOff = inv.RemindersOff });
        });

        // Alleen lezen: zonder opgeslagen instellingen krijg je de standaard (herinneringen uit).
        api.MapGet("/reminders", async (AppDb db, InvoiceMailer mailer, AppUrls urls, HttpRequest request) =>
            Results.Ok(SettingsDto(await db.ReminderSettings.AsNoTracking().FirstOrDefaultAsync() ?? new ReminderSettings(), await mailer.BlockerAsync(urls.Base(request)))));

        // Alleen een beheerder zet herinneringen aan of past de teksten aan: die gaan uit naam van het bedrijf naar klanten.
        api.MapPut("/reminders", async (AppDb db, InvoiceMailer mailer, AppUrls urls, HttpRequest request, ReminderSettingsInput input) =>
        {
            if (input.FirstDays < 1 || input.SecondDays <= input.FirstDays || input.FinalDays <= input.SecondDays || input.FinalDays > 365)
                return Results.BadRequest(new { error = "Kies de dagen op volgorde: eerst de herinnering, dan de tweede herinnering en als laatste de aanmaning, tussen 1 en 365 dagen na de vervaldatum." });
            var texts = new (int Step, MailTextInput? Input)[] { (MailSteps.Invoice, input.Invoice), (MailSteps.First, input.First), (MailSteps.Second, input.Second), (MailSteps.Final, input.Final) }
                .Select(x => (x.Step, Result: Apply(x.Step, x.Input))).ToList();
            if (texts.FirstOrDefault(x => x.Result.Error is not null) is { Result.Error: { } error }) return Results.BadRequest(new { error });
            var s = await ReminderService.SettingsAsync(db);
            var wasOn = s.Enabled;
            s.Enabled = input.Enabled; s.FirstDays = input.FirstDays; s.SecondDays = input.SecondDays; s.FinalDays = input.FinalDays;
            (s.InvoiceSubject, s.InvoiceBody, _) = texts[0].Result;
            (s.FirstSubject, s.FirstBody, _) = texts[1].Result;
            (s.SecondSubject, s.SecondBody, _) = texts[2].Result;
            (s.FinalSubject, s.FinalBody, _) = texts[3].Result;
            db.Log("e-mail", wasOn == s.Enabled ? "Instellingen voor e-mail en herinneringen bijgewerkt"
                : s.Enabled ? "Automatische herinneringen aangezet" : "Automatische herinneringen uitgezet");
            await db.SaveChangesAsync();
            return Results.Ok(SettingsDto(s, await mailer.BlockerAsync(urls.Base(request))));
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // De openbare factuurpagina: zonder inloggen, alleen met het token uit de mail. Laat alleen deze ene factuur zien, met wat
        // er op de factuur zelf staat (plus logo en kleur), en niets anders uit de werkruimte. Een onbekend, ingetrokken of fout
        // token geeft steeds hetzelfde antwoord.
        api.MapPost("/public/invoice", async (AppDb db, HttpResponse response, PublicInvoiceRequest req) =>
        {
            NoIndex(response);
            var token = req.Token?.Trim();
            if (string.IsNullOrEmpty(token) || token.Length > 100) return Results.NotFound(new { error = LinkGone });
            var hash = InvoiceMailer.Hash(token);
            var link = await db.InvoiceLinks.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(l => l.TokenHash == hash && l.RevokedAt == null);
            if (link is null) return Results.NotFound(new { error = LinkGone });
            var inv = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .Include(i => i.Lines).Include(i => i.Credits).ThenInclude(c => c.Lines).AsSplitQuery()
                .FirstOrDefaultAsync(i => i.Id == link.InvoiceId && i.WorkspaceId == link.WorkspaceId);
            if (inv is null || inv.Status == InvoiceStatus.Draft || inv.IsDemo) return Results.NotFound(new { error = LinkGone });
            var brand = await db.Settings.IgnoreQueryFilters().AsNoTracking().Where(s => s.WorkspaceId == link.WorkspaceId).OrderBy(s => s.Id)
                .Select(s => new { s.LogoDataUrl, s.BrandColor }).FirstOrDefaultAsync();
            var creditFor = inv.CreditForInvoiceId is { } originalId
                ? await db.Invoices.IgnoreQueryFilters().AsNoTracking().Where(i => i.Id == originalId && i.WorkspaceId == link.WorkspaceId)
                    .Select(i => new { i.Number, i.IssueDate }).FirstOrDefaultAsync()
                : null;
            await db.InvoiceLinks.IgnoreQueryFilters().Where(l => l.Id == link.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(l => l.LastOpenedAt, DateTime.UtcNow).SetProperty(l => l.Opens, l => l.Opens + 1));
            var b = inv.Buyer;
            return Results.Ok(new
            {
                invoice = new
                {
                    inv.Number, inv.Status, inv.IssueDate, inv.DueDate, inv.DeliveryFrom, inv.DeliveryTo, inv.Reference, inv.VatRegime, inv.Notes, inv.PaidAt,
                    inv.Seller,
                    // Van de klant alleen wat op de factuur staat.
                    buyer = b is null ? null : new { b.Name, b.Contact, b.Address, b.City, b.Country, b.VatNumber },
                    lines = inv.Lines.OrderBy(l => l.Position).ThenBy(l => l.Id).Select(l => new { l.Description, l.Quantity, l.Unit, l.UnitPrice, l.VatRate }),
                    creditForNumber = creditFor?.Number, creditForIssueDate = creditFor?.IssueDate,
                    inv.IsCredit, inv.OpenAmount, inv.FullyCredited, inv.Totals,
                },
                brand = new { logoDataUrl = brand?.LogoDataUrl, brandColor = brand?.BrandColor ?? "#ff6d5a" },
            });
        }).AllowAnonymous().RequireRateLimiting("public");
    }
}
