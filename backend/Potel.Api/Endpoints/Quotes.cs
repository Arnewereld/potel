using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Potel.Api.Data;
using Potel.Api.Invoicing;
using Potel.Api.Workflows;

namespace Potel.Api.Endpoints;

public record QuoteProjectRequest(string? Billing, string? Name);
public record QuoteInvoiceRequest(bool Deposit);
public record QuoteMailRequest(string? Subject, string? Body);
public record PublicQuoteRequest(string? Token);
public record PublicQuoteAccept(string? Token, string? Name, bool Agree);
public record PublicQuoteReject(string? Token, string? Reason);

// Offertes: dezelfde regels, eenheden, btw-regelingen en A4-opmaak als facturen. Een concept pas je aan; bij versturen krijgt
// de offerte een nummer (OFF-2026-001) en ligt hij vast. Je klant bekijkt hem via een geheime link (net als de factuurlink:
// alleen de hash van het token staat in de database) en accepteert of wijst hem daar af, één keer, vóór het einde van de
// geldigheid en niet als je hem hebt ingetrokken. Project of factuur maken doe je daarna zelf; Potel doet dat nooit vanzelf.
public static partial class QuoteEndpoints
{
    public const string LinkPath = "/offerte";
    public const string LinkGone = "Deze link werkt niet (meer). Vraag de afzender van de offerte om een nieuwe link.";
    public const string FrozenError = "Deze offerte is al verstuurd en ligt vast. Wil je iets veranderen? Maak dan een nieuwe versie.";
    public const string NumberPrefix = "OFF-";
    public const string DemoNumberPrefix = "VOORBEELD-OFF-";
    public const int MaxTextLength = 5000;
    public const int MaxReasonLength = 1000;
    // Zo lang na de geldigheidsdatum werkt een link nog, om terug te kijken wat er is afgesproken.
    public const int LinkDaysAfterValidity = 365;

    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    [GeneratedRegex(@"^/\d+/(withdraw|links/revoke)/?$")]
    private static partial Regex SafetyPath();

    // Een offerte intrekken of zijn links dichtzetten mag ook in een werkruimte die alleen-lezen is: dat is een veiligheidsmaatregel.
    public static bool IsSafetyAction(HttpRequest r) =>
        HttpMethods.IsPost(r.Method) && r.Path.StartsWithSegments("/api/quotes", out var rest) && SafetyPath().IsMatch(rest.Value ?? "");

    public static string Label(Quote q) => q.Number is null ? "Conceptofferte" : $"Offerte {q.Number}";

    static string Date(DateTime d) => d.ToString("d MMMM yyyy", Dutch);
    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Replace("\r\n", "\n").Trim();
    static string Join(List<string> items) => items.Count == 1 ? items[0] : $"{string.Join(", ", items[..^1])} en {items[^1]}";

    static string Name(Customer c) => string.IsNullOrWhiteSpace(c.Company) ? c.Name : c.Company.Trim();
    static string Name(Lead l) => string.IsNullOrWhiteSpace(l.Company) ? l.Name : l.Company.Trim();
    static string Recipient(Quote q) => q.Buyer?.Name ?? (q.Customer is { } c ? Name(c) : q.Lead is { } l ? Name(l) : "onbekend");

    // Een lead op de offerte: naam, bedrijf en contactgegevens. Een adres heeft een lead nog niet; dat komt als hij klant wordt.
    public static InvoiceParty BuyerOf(Lead l) => new()
    {
        Name = string.IsNullOrWhiteSpace(l.Company) ? l.Name.Trim() : l.Company.Trim(),
        Contact = string.IsNullOrWhiteSpace(l.Company) ? null : l.Name.Trim(),
        Email = string.IsNullOrWhiteSpace(l.Email) ? null : l.Email.Trim(),
        Phone = string.IsNullOrWhiteSpace(l.Phone) ? null : l.Phone.Trim(),
    };

    // Het volgende nummer in de reeks van het jaar, per werkruimte en zonder gaten: OFF-2026-001, OFF-2026-002, ...
    // Voorbeeldoffertes hebben een eigen reeks. Alleen binnen een WriteLock gebruiken om een nummer te geven.
    public static async Task<string> NextNumber(AppDb db, int year)
    {
        var prefix = $"{NumberPrefix}{year}-";
        var numbers = await db.Quotes.Where(q => !q.IsDemo && q.Number != null && q.Number.StartsWith(prefix)).Select(q => q.Number!).ToListAsync();
        var max = numbers.Select(n => int.TryParse(n[prefix.Length..], out var x) ? x : 0).DefaultIfEmpty(0).Max();
        return $"{prefix}{max + 1:000}";
    }

    static IQueryable<Quote> Full(AppDb db) =>
        db.Quotes.Include(q => q.Lines.OrderBy(l => l.Position).ThenBy(l => l.Id)).Include(q => q.Customer).Include(q => q.Lead).AsSplitQuery();

    // De offerte met wat de frontend erbij nodig heeft: de status van vandaag, de vorige versie en of er een nieuwere is.
    static async Task<Quote> WithExtrasAsync(AppDb db, Quote q, DateTime today)
    {
        q.State = QuoteStatus.Of(q, today);
        if (q.PreviousVersionId is { } previous) q.PreviousNumber = await db.Quotes.Where(x => x.Id == previous).Select(x => x.Number).FirstOrDefaultAsync();
        q.NewerVersionId = await db.Quotes.Where(x => x.PreviousVersionId == q.Id).OrderByDescending(x => x.Id).Select(x => (int?)x.Id).FirstOrDefaultAsync();
        return q;
    }

    static async Task<IResult> OkAsync(AppDb db, BusinessClock clock, int id, bool created = false)
    {
        var q = await WithExtrasAsync(db, await Full(db).AsNoTracking().FirstAsync(x => x.Id == id), clock.Today);
        return created ? Results.Created($"/api/quotes/{id}", q) : Results.Ok(q);
    }

    // Controles bij opslaan van een concept.
    static async Task<string?> ValidateAsync(AppDb db, Quote input)
    {
        if ((input.CustomerId is null) == (input.LeadId is null)) return "Kies een klant of een lead.";
        Customer? customer = null;
        if (input.CustomerId is { } customerId && (customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId)) is null) return "Kies een klant.";
        if (input.LeadId is { } leadId && !await db.Leads.AnyAsync(l => l.Id == leadId)) return "Kies een lead.";
        if (input.Lines.Count == 0) return "Voeg minstens één regel toe";
        if (input.Lines.Any(l => string.IsNullOrWhiteSpace(l.Description))) return "Elke regel heeft een omschrijving nodig";
        if (input.Lines.Any(l => Money.TooLarge(l.AsInvoiceLine()))) return Money.TooLargeError;
        if (input.ValidUntil.Date < input.IssueDate.Date) return "De geldigheid eindigt voor de offertedatum.";
        if (!VatRegimes.All.Contains(input.VatRegime)) return "Kies een btw-regeling";
        if (input.DepositPercent is { } deposit && (deposit <= 0 || deposit > 100)) return "Een aanbetaling is een percentage van meer dan 0 tot en met 100.";
        if (input.Subject?.Trim().Length > 200) return "Het onderwerp mag hooguit 200 tekens zijn.";
        if (input.Intro?.Length > MaxTextLength || input.Terms?.Length > MaxTextLength) return $"De inleiding en de voorwaarden mogen elk hooguit {MaxTextLength} tekens zijn.";
        if (!Languages.IsKnown(input.Language)) return Languages.Error;
        if (customer is not null) return InvoiceEndpoints.RegimeError(input.VatRegime, customer);
        return input.VatRegime is VatRegimes.ReverseCharge or VatRegimes.OutsideEu
            ? "Van een lead weten we het land en het btw-nummer nog niet. Kies normaal, KOR of vrijgesteld, of maak eerst een klant van de lead."
            : null;
    }

    // Regels overnemen, in dezelfde volgorde. Zonder btw-regeling met btw staan alle regels op 0%.
    static List<QuoteLine> CopyLines(IEnumerable<QuoteLine> lines, string regime) =>
        lines.Select((l, i) => new QuoteLine
        {
            Description = l.Description.Trim(), Quantity = l.Quantity, Unit = string.IsNullOrWhiteSpace(l.Unit) ? "stuk" : l.Unit.Trim(),
            UnitPrice = l.UnitPrice, VatRate = VatRegimes.ZeroVat(regime) ? 0 : l.VatRate, Position = i,
        }).ToList();

    static void CopyFields(Quote from, Quote to)
    {
        to.CustomerId = from.CustomerId; to.LeadId = from.CustomerId is null ? from.LeadId : null;
        to.IssueDate = from.IssueDate.Date; to.ValidUntil = from.ValidUntil.Date; to.VatRegime = from.VatRegime;
        to.Subject = Clean(from.Subject); to.Intro = Clean(from.Intro); to.Terms = Clean(from.Terms);
        to.DepositPercent = from.DepositPercent is { } d ? Math.Round(d, 2, MidpointRounding.AwayFromZero) : null;
    }

    // De taal van een nieuwe offerte: die van de klant of de lead.
    static async Task<string> LanguageForAsync(AppDb db, int? customerId, int? leadId) =>
        customerId is { } c ? await db.Customers.Where(x => x.Id == c).Select(x => x.Language).FirstOrDefaultAsync() ?? Languages.Dutch
        : leadId is { } l ? await db.Leads.Where(x => x.Id == l).Select(x => x.Language).FirstOrDefaultAsync() ?? Languages.Dutch
        : Languages.Dutch;

    // Wat er op een offerte moet staan voordat hij weg kan. Ook op een offerte hoort je KvK-nummer (Handelsregisterwet).
    // De frontend toont dezelfde lijst (missingForQuote in frontend/src/lib/quote.ts).
    public static List<string> Missing(Quote q, Settings s, InvoiceParty buyer)
    {
        var missing = new List<string>();
        void Need(string? value, string label) { if (string.IsNullOrWhiteSpace(value)) missing.Add(label); }
        Need(s.CompanyName, "je bedrijfsnaam");
        Need(s.Address, "je adres");
        Need(s.City, "je postcode en plaats");
        Need(s.Kvk, "je KvK-nummer");
        Need(buyer.Name, "de naam van de klant");
        if (q.Lines.Count == 0) missing.Add("minstens één regel");
        return missing;
    }

    // Versturen: alles moet kloppen, dan krijgt de offerte het volgende nummer en leggen we jouw gegevens en die van je klant
    // of lead vast. De offertedatum wordt vandaag en de geldigheid schuift mee, zodat je klant evenveel dagen heeft.
    // Een nieuwe versie trekt de vorige in, zodat je klant niet per ongeluk de oude accepteert. Alleen binnen een WriteLock.
    static async Task<string?> SendAsync(AppDb db, BusinessClock clock, Quote q)
    {
        if (q.IsDemo) return "Dit is een voorbeeldofferte. Maak er een kopie van als je hem echt wilt versturen.";
        var settings = await SettingsEndpoints.GetAsync(db);
        var customer = q.CustomerId is { } customerId ? await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId) : null;
        var lead = q.LeadId is { } leadId ? await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId) : null;
        if (customer is null && lead is null) return "Kies eerst een klant of een lead.";
        var buyer = customer is not null ? InvoiceParty.Buyer(customer) : BuyerOf(lead!);
        var missing = Missing(q, settings, buyer);
        if (missing.Count > 0) return $"Vul eerst {Join(missing)} in. Daarna kun je de offerte versturen.";
        if (customer is not null && InvoiceEndpoints.RegimeError(q.VatRegime, customer) is { } regimeError) return regimeError;
        if (customer is null && q.VatRegime is VatRegimes.ReverseCharge or VatRegimes.OutsideEu)
            return "Van een lead weten we het land en het btw-nummer nog niet. Kies normaal, KOR of vrijgesteld, of maak eerst een klant van de lead.";
        if (q.Totals.Total < 0) return "Het totaal van een offerte kan niet negatief zijn.";

        var today = clock.Today;
        var term = q.ValidUntil.Date - q.IssueDate.Date;
        q.IssueDate = today;
        q.ValidUntil = today + (term < TimeSpan.Zero ? TimeSpan.Zero : term);
        q.Number = await NextNumber(db, today.Year);
        q.Seller = InvoiceParty.Seller(settings);
        q.Buyer = buyer;
        q.SentAt = clock.UtcNow;
        q.Status = QuoteStatus.Sent;
        if (lead is not null && lead.Status is "nieuw" or "contact")
        {
            db.Log("lead", $"Lead {lead.Name}: {lead.Status} → offerte");
            lead.Status = "offerte";
        }
        if (q.PreviousVersionId is { } previous && await db.Quotes.FirstOrDefaultAsync(x => x.Id == previous && x.Status == QuoteStatus.Sent) is { } old)
        {
            old.Status = QuoteStatus.Withdrawn;
            old.WithdrawnAt = clock.UtcNow;
            db.Log("offerte", $"Offerte {old.Number} ingetrokken: vervangen door versie {q.Version} ({q.Number})");
        }
        db.Log("offerte", $"Offerte {q.Number} verstuurd aan {buyer.Name}");
        return null;
    }

    // Waarom de klant (nu) niet kan accepteren of afwijzen, of null.
    // In de taal van de offerte, want je klant leest het.
    static string? DecisionProblem(Quote q, DateTime today)
    {
        var lang = q.Language;
        var seller = q.Seller?.Name ?? ClientTexts.T(lang, "quote.sender");
        string On(string key, DateTime? at) => at is { } a ? ClientTexts.T(lang, key + "On", ("date", ClientTexts.Date(lang, Local(a)))) : ClientTexts.T(lang, key);
        return q.Status switch
        {
            QuoteStatus.Accepted => On("quote.accepted", q.DecidedAt),
            QuoteStatus.Rejected => On("quote.rejected", q.DecidedAt),
            QuoteStatus.Withdrawn => ClientTexts.T(lang, "quote.withdrawn", ("seller", seller)),
            QuoteStatus.Sent when today > q.ValidUntil.Date => ClientTexts.T(lang, "quote.expired", ("date", ClientTexts.Date(lang, q.ValidUntil)), ("seller", seller)),
            QuoteStatus.Sent => null,
            _ => ClientTexts.T(lang, "quote.linkGone"),
        };
    }

    static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), BusinessClock.Zone);

    static DateTime LinkExpiry(Quote q) => DateTime.SpecifyKind(q.ValidUntil.Date.AddDays(LinkDaysAfterValidity + 1), DateTimeKind.Utc);

    // Het e-mailadres van de klant of de lead als het precies één geldig adres is.
    static string? RecipientEmail(Quote q)
    {
        var email = q.CustomerId is not null ? q.Customer?.Email : q.Lead?.Email;
        return EmailRules.IsSingleAddress(email?.Trim()) ? email!.Trim() : null;
    }

    static string? MailProblem(Quote q, DateTime today)
    {
        if (q.IsDemo) return "Dit is een voorbeeldofferte. Die mailt Potel niet.";
        if (q.Status == QuoteStatus.Draft) return "Verstuur de offerte eerst. Dan krijgt hij een nummer en ligt hij vast.";
        if (q.Status is QuoteStatus.Accepted or QuoteStatus.Rejected) return "Je klant heeft al over deze offerte besloten.";
        if (q.Status == QuoteStatus.Withdrawn) return "Deze offerte is ingetrokken.";
        if (today > q.ValidUntil.Date) return "Deze offerte is verlopen. Maak een nieuwe versie met een nieuwe geldigheid.";
        return RecipientEmail(q) is null
            ? (q.CustomerId is not null ? InvoiceMailer.NoEmail : "Deze lead heeft geen geldig e-mailadres. Vul bij de lead precies één e-mailadres in.")
            : null;
    }

    // De standaardmail bij een offerte, in de taal van de offerte. {link} wordt bij versturen de persoonlijke link.
    public static MailText DefaultMail(Quote q, Settings s)
    {
        var lang = q.Language;
        var seller = q.Seller?.Name ?? s.CompanyName;
        var first = (q.CustomerId is not null ? q.Customer?.Name : q.Lead?.Name)?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var total = ClientTexts.Euro(lang, q.Totals.Total);
        return new MailText(
            ClientTexts.T(lang, "quote.mail.subject", ("number", q.Number), ("seller", seller)),
            ClientTexts.T(lang, "quote.mail.body",
                ("greeting", first is null ? ClientTexts.T(lang, "quote.mail.hello") : ClientTexts.T(lang, "quote.mail.hi", ("name", first))),
                ("number", q.Number),
                ("about", string.IsNullOrWhiteSpace(q.Subject) ? "" : ClientTexts.T(lang, "quote.mail.about", ("subject", q.Subject.Trim()))),
                ("amount", q.VatRegime == VatRegimes.Normal ? ClientTexts.T(lang, "quote.mail.inclVat", ("amount", total)) : total),
                ("validUntil", ClientTexts.Date(lang, q.ValidUntil)),
                ("owner", string.IsNullOrWhiteSpace(s.OwnerName) ? seller : s.OwnerName.Trim()),
                ("seller", seller)));
    }

    public static void MapQuotes(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/quotes");

        // Concepten bovenaan, daarna de verstuurde offertes van nieuw naar oud.
        g.MapGet("/", async (AppDb db, BusinessClock clock) =>
        {
            var today = clock.Today;
            var list = await Full(db).AsNoTracking().ToListAsync();
            var numbers = list.Where(q => q.Number != null).ToDictionary(q => q.Id, q => q.Number);
            var newer = list.Where(q => q.PreviousVersionId != null).GroupBy(q => q.PreviousVersionId!.Value).ToDictionary(x => x.Key, x => x.Max(q => q.Id));
            foreach (var q in list)
            {
                q.State = QuoteStatus.Of(q, today);
                q.PreviousNumber = q.PreviousVersionId is { } p ? numbers.GetValueOrDefault(p) : null;
                q.NewerVersionId = newer.TryGetValue(q.Id, out var n) ? n : null;
            }
            return list.OrderBy(q => q.Status != QuoteStatus.Draft).ThenBy(q => q.IsDemo).ThenByDescending(q => q.SentAt ?? q.CreatedAt).ThenByDescending(q => q.Id);
        });

        g.MapGet("/next-number", async (AppDb db, BusinessClock clock) => new { number = await NextNumber(db, clock.Today.Year) });

        g.MapGet("/{id:int}", async (AppDb db, BusinessClock clock, int id) =>
            await db.Quotes.AnyAsync(q => q.Id == id) ? await OkAsync(db, clock, id) : Results.NotFound());

        g.MapPost("/", async (AppDb db, BusinessClock clock, Quote input) =>
        {
            if (await ValidateAsync(db, input) is { } error) return Results.BadRequest(new { error });
            var q = new Quote { Lines = CopyLines(input.Lines, input.VatRegime) };
            CopyFields(input, q);
            // Zonder meegestuurde taal die van de klant of lead.
            q.Language = input.LanguageGiven ? Languages.Of(input.Language) : await LanguageForAsync(db, q.CustomerId, q.LeadId);
            db.Quotes.Add(q);
            var who = q.CustomerId is { } c ? Name(await db.Customers.FirstAsync(x => x.Id == c)) : Name(await db.Leads.FirstAsync(x => x.Id == q.LeadId));
            db.Log("offerte", $"Conceptofferte voor {who} aangemaakt");
            await db.SaveChangesAsync();
            return await OkAsync(db, clock, q.Id, created: true);
        });

        // Alleen een concept kan nog veranderen; de regels worden in één keer vervangen.
        g.MapPut("/{id:int}", async (AppDb db, BusinessClock clock, int id, Quote input) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            var q = await db.Quotes.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
            if (q is null) return Results.NotFound();
            if (q.Status != QuoteStatus.Draft) return Results.Conflict(new { error = FrozenError });
            if (await ValidateAsync(db, input) is { } error) return Results.BadRequest(new { error });
            CopyFields(input, q);
            if (input.LanguageGiven) q.Language = Languages.Of(input.Language);
            db.QuoteLines.RemoveRange(q.Lines);
            q.Lines = CopyLines(input.Lines, input.VatRegime);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return await OkAsync(db, clock, id);
        });

        // Versturen legt de offerte vast. Alleen een beheerder: een offerte is een aanbod namens het bedrijf.
        g.MapPost("/{id:int}/send", async (AppDb db, BusinessClock clock, int id) =>
        {
            await using (var tx = await WriteLock.BeginAsync(db))
            {
                var q = await db.Quotes.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
                if (q is null) return Results.NotFound();
                if (q.Status != QuoteStatus.Draft) return Results.Conflict(new { error = "Deze offerte is al verstuurd." });
                if (await SendAsync(db, clock, q) is { } error) return Results.BadRequest(new { error });
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            return await OkAsync(db, clock, id);
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Intrekken: de klant kan daarna niet meer accepteren. De link blijft werken, zodat hij ziet dat de offerte is ingetrokken.
        g.MapPost("/{id:int}/withdraw", async (AppDb db, BusinessClock clock, int id) =>
        {
            await using (var tx = await WriteLock.BeginAsync(db))
            {
                var q = await db.Quotes.FirstOrDefaultAsync(x => x.Id == id);
                if (q is null) return Results.NotFound();
                if (q.Status == QuoteStatus.Draft) return Results.BadRequest(new { error = "Een concept is nog niet verstuurd. Dat kun je gewoon verwijderen." });
                if (q.Status is QuoteStatus.Accepted or QuoteStatus.Rejected) return Results.Conflict(new { error = "Je klant heeft al over deze offerte besloten. Intrekken kan niet meer." });
                if (q.Status == QuoteStatus.Sent)
                {
                    q.Status = QuoteStatus.Withdrawn;
                    q.WithdrawnAt = clock.UtcNow;
                    db.Log("offerte", $"Offerte {q.Number} ingetrokken");
                    await db.SaveChangesAsync();
                }
                await tx.CommitAsync();
            }
            return await OkAsync(db, clock, id);
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Een kopie als concept. Van een verstuurde offerte wordt dat de volgende versie, die bij versturen de vorige vervangt.
        g.MapPost("/{id:int}/duplicate", async (AppDb db, BusinessClock clock, int id) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            var source = await Full(db).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (source is null) return Results.NotFound();
            var version = source.Status != QuoteStatus.Draft && !source.IsDemo;
            if (version && await db.Quotes.Where(x => x.PreviousVersionId == id).Select(x => new { x.Id, x.Version }).FirstOrDefaultAsync() is { } newer)
                return Results.Conflict(new { error = $"Van deze offerte bestaat al versie {newer.Version}. Maak een nieuwe versie vanuit die offerte.", quoteId = newer.Id });
            var settings = await SettingsEndpoints.GetAsync(db);
            var today = clock.Today;
            var q = new Quote
            {
                IssueDate = today, ValidUntil = today.AddDays(settings.QuoteValidDays), Version = version ? source.Version + 1 : 1,
                PreviousVersionId = version ? source.Id : null, Lines = CopyLines(source.Lines, source.VatRegime),
            };
            CopyFields(source, q);
            q.IssueDate = today;
            q.ValidUntil = today.AddDays(settings.QuoteValidDays);
            // Een nieuw concept, ook een nieuwe versie, krijgt de taal die de klant of lead nu heeft.
            q.Language = await LanguageForAsync(db, q.CustomerId, q.LeadId);
            db.Quotes.Add(q);
            db.Log("offerte", version ? $"Versie {q.Version} van offerte {source.Number} gemaakt" : $"Conceptofferte gemaakt als kopie van {Label(source).ToLowerInvariant()}");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return await OkAsync(db, clock, q.Id, created: true);
        });

        // Alleen een concept of een voorbeeldofferte mag weg. Een verstuurde offerte bewaar je, net als een factuur.
        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var q = await db.Quotes.FirstOrDefaultAsync(x => x.Id == id);
            if (q is null) return Results.NotFound();
            if (q.Status != QuoteStatus.Draft && !q.IsDemo)
                return Results.Conflict(new { error = "Een verstuurde offerte kun je niet verwijderen. Trek hem in als hij niet meer geldt." });
            db.Quotes.Remove(q);
            db.Log("offerte", q.IsDemo ? $"Voorbeeldofferte {q.Number} verwijderd" : "Conceptofferte verwijderd");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Of er een link openstaat, wanneer hij geopend is en of de offerte gemaild is. Het token zelf staat nergens.
        g.MapGet("/{id:int}/links", async (AppDb db, BusinessClock clock, int id) =>
        {
            var q = await db.Quotes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (q is null) return Results.NotFound();
            var links = await db.QuoteLinks.AsNoTracking().Where(l => l.QuoteId == id).ToListAsync();
            var now = clock.UtcNow;
            return Results.Ok(new
            {
                active = links.Count(l => l.Works(now)), lastOpenedAt = links.Max(l => l.LastOpenedAt), opens = links.Sum(l => l.Opens),
                q.MailedAt, q.MailedTo,
            });
        });

        // Een nieuwe geheime link, om zelf te delen. Het token zie je alleen nu.
        g.MapPost("/{id:int}/link", async (AppDb db, BusinessClock clock, ClaimsPrincipal user, int id) =>
        {
            var q = await db.Quotes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (q is null) return Results.NotFound();
            if (q.Status == QuoteStatus.Draft) return Results.BadRequest(new { error = "Verstuur de offerte eerst. Dan krijgt hij een nummer en ligt hij vast." });
            if (q.IsDemo) return Results.BadRequest(new { error = "Dit is een voorbeeldofferte. Daar maakt Potel geen link voor." });
            var token = InvoiceMailer.NewToken();
            db.QuoteLinks.Add(new QuoteLink
            {
                QuoteId = id, TokenHash = InvoiceMailer.Hash(token), CreatedAt = clock.UtcNow, CreatedBy = user.FindFirstValue(ClaimTypes.Name), ExpiresAt = LinkExpiry(q),
            });
            db.Log("offerte", $"Link naar offerte {q.Number} gemaakt");
            await db.SaveChangesAsync();
            // Het token staat achter een #, zodat het niet in logboeken van servers of proxy's belandt.
            return Results.Ok(new { path = $"{LinkPath}#token={token}" });
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Links dichtzetten mag iedereen, net als bij facturen: wie ziet dat een link op de verkeerde plek is beland, moet direct kunnen ingrijpen.
        g.MapPost("/{id:int}/links/revoke", async (AppDb db, BusinessClock clock, int id) =>
        {
            var q = await db.Quotes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (q is null) return Results.NotFound();
            var now = clock.UtcNow;
            var revoked = await db.QuoteLinks.Where(l => l.QuoteId == id && l.RevokedAt == null).ExecuteUpdateAsync(x => x.SetProperty(l => l.RevokedAt, now));
            if (revoked > 0)
            {
                db.Log("offerte", $"{(revoked == 1 ? "De link" : $"{revoked} links")} naar {Label(q).ToLowerInvariant()} ingetrokken");
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { revoked });
        });

        // De mail zoals hij weg zou gaan, met {link} nog als woord, en wat er eventueel nog in de weg zit.
        g.MapGet("/{id:int}/mail-preview", async (AppDb db, InvoiceMailer mailer, AppUrls urls, BusinessClock clock, HttpRequest request, int id) =>
        {
            var q = await Full(db).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (q is null) return Results.NotFound();
            var text = DefaultMail(q, await SettingsEndpoints.GetAsync(db));
            var problem = MailProblem(q, clock.Today) ?? await mailer.BlockerAsync(urls.Base(request));
            return Results.Ok(new { to = RecipientEmail(q), text.Subject, text.Body, problem });
        });

        // Mailen naar de klant of lead, met een persoonlijke link. Telt mee voor de daglimiet, net als facturen.
        g.MapPost("/{id:int}/mail", async (AppDb db, InvoiceMailer mailer, IEmailSender sender, IOptions<WorkflowLimits> limits, AppUrls urls,
            BusinessClock clock, HttpContext http, ILogger<Quote> logger, int id, QuoteMailRequest req) =>
        {
            var baseUrl = urls.Base(http.Request);
            if (await mailer.BlockerAsync(baseUrl) is { } blocker) return Results.BadRequest(new { error = blocker });
            var subject = EmailRules.OneLine(req.Subject);
            var body = (req.Body ?? "").Replace("\r\n", "\n").Trim();
            if (subject == "" || body == "") return Results.BadRequest(new { error = "Vul een onderwerp en een tekst in." });
            if (req.Body!.Length > 5000) return Results.BadRequest(new { error = "De tekst is te lang." });
            if (!body.Contains("{link}")) return Results.BadRequest(new { error = "Zet {link} in de tekst. Daar komt de persoonlijke link naar de offerte." });

            QuoteLink link;
            OutgoingEmail outgoing;
            string number;
            await using (var tx = await WriteLock.BeginAsync(db))
            {
                var q = await Full(db).FirstOrDefaultAsync(x => x.Id == id);
                if (q is null) return Results.NotFound();
                if (MailProblem(q, clock.Today) is { } problem) return Results.BadRequest(new { error = problem });
                var quota = await MailQuota.ReserveAsync(db, limits.Value);
                if (quota != QuotaResult.Ok) return Results.BadRequest(new { error = MailQuota.Message(quota, limits.Value.EmailsPerDay(await MailQuota.PlanAsync(db))) });
                var token = InvoiceMailer.NewToken();
                link = new QuoteLink
                {
                    QuoteId = id, TokenHash = InvoiceMailer.Hash(token), CreatedAt = clock.UtcNow, CreatedBy = http.User.FindFirstValue(ClaimTypes.Name), ExpiresAt = LinkExpiry(q),
                };
                db.QuoteLinks.Add(link);
                var to = RecipientEmail(q)!;
                q.MailedAt = clock.UtcNow;
                q.MailedTo = to;
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                number = q.Number!;
                var settings = await SettingsEndpoints.GetAsync(db);
                outgoing = new OutgoingEmail(to, subject, body.Replace("{link}", $"{baseUrl}{LinkPath}#token={token}"), q.Seller?.Name ?? settings.CompanyName, settings.Email);
            }

            try
            {
                await sender.SendAsync(outgoing);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Mail over offerte {Quote} niet verstuurd", id);
                // Het token heeft de klant nooit gekregen; de link mag dus meteen dicht.
                link.RevokedAt = clock.UtcNow;
                db.Log("e-mail", $"Offerte {number} aan {outgoing.To} kon niet worden verstuurd: de mailserver gaf een fout.");
                await db.SaveChangesAsync();
                return Results.Json(new { error = "De mail kon niet worden verstuurd: de mailserver gaf een fout. Probeer het later opnieuw." }, statusCode: StatusCodes.Status502BadGateway);
            }
            db.Log("e-mail", $"Offerte {number} gemaild aan {outgoing.To}");
            await db.SaveChangesAsync();
            return Results.Ok(new { mailedTo = outgoing.To });
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Na akkoord: een project voor de klant, per uur of met een vaste prijs, met als budget het totaal zonder btw.
        g.MapPost("/{id:int}/project", async (AppDb db, BusinessClock clock, int id, QuoteProjectRequest req) =>
        {
            var billing = string.IsNullOrWhiteSpace(req.Billing) ? Billing.Fixed : req.Billing.Trim();
            if (!Billing.All.Contains(billing)) return Results.BadRequest(new { error = "Kies per uur of een vaste prijs." });
            await using var tx = await WriteLock.BeginAsync(db);
            var q = await Full(db).FirstOrDefaultAsync(x => x.Id == id);
            if (q is null) return Results.NotFound();
            if (q.Status != QuoteStatus.Accepted) return Results.BadRequest(new { error = "Een project maak je als je klant de offerte heeft geaccepteerd." });
            if (q.CustomerId is not { } customerId) return Results.BadRequest(new { error = "Deze offerte hoort niet bij een klant. Maak eerst een klant van de lead." });
            if (q.ProjectId is { } existing && await db.Projects.AnyAsync(p => p.Id == existing))
                return Results.Conflict(new { error = "Van deze offerte is al een project gemaakt.", projectId = existing });
            var settings = await SettingsEndpoints.GetAsync(db);
            var subtotal = q.Totals.Subtotal;
            var hourLines = q.Lines.Where(l => l.Unit == "uur").ToList();
            var rate = hourLines.FirstOrDefault(l => l.UnitPrice > 0)?.UnitPrice ?? settings.DefaultHourlyRate;
            var name = Clean(req.Name) ?? Clean(q.Subject) ?? $"Project uit offerte {q.Number}";
            var project = new Project
            {
                Name = name.Length > 200 ? name[..200] : name, CustomerId = customerId, Billing = billing, HourlyRate = rate,
                FixedPrice = billing == Billing.Fixed ? subtotal : 0,
                // Per uur: zoveel uur past in het bedrag van de offerte. Vaste prijs: de uren die in de offerte stonden.
                BudgetHours = billing == Billing.Hourly
                    ? (rate > 0 ? Math.Round(subtotal / rate, 1, MidpointRounding.AwayFromZero) : null)
                    : (hourLines.Sum(l => l.Quantity) is > 0 and var hours ? hours : null),
                Description = $"Gemaakt uit offerte {q.Number}{(q.DecidedAt is { } at ? $", geaccepteerd op {Date(Local(at))}" : "")}.",
            };
            db.Projects.Add(project);
            await db.SaveChangesAsync();
            q.ProjectId = project.Id;
            db.Log("project", $"Project {project.Name} gemaakt uit offerte {q.Number}");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Created($"/api/projects/{project.Id}", new { project.Id, project.Name });
        });

        // Na akkoord: een conceptfactuur met de regels van de offerte, of alleen de aanbetaling. Komt de hele factuur na een
        // aanbetaling, dan gaat die er met een min-regel weer af, zodat de klant niets dubbel betaalt.
        g.MapPost("/{id:int}/invoice", async (AppDb db, BusinessClock clock, int id, QuoteInvoiceRequest req) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            var q = await Full(db).FirstOrDefaultAsync(x => x.Id == id);
            if (q is null) return Results.NotFound();
            if (q.Status != QuoteStatus.Accepted) return Results.BadRequest(new { error = "Een factuur maak je als je klant de offerte heeft geaccepteerd." });
            if (q.CustomerId is not { } customerId || q.Customer is null) return Results.BadRequest(new { error = "Deze offerte hoort niet bij een klant. Maak eerst een klant van de lead." });
            if (InvoiceEndpoints.RegimeError(q.VatRegime, q.Customer) is { } regimeError)
                return Results.BadRequest(new { error = $"De btw-regeling van de offerte past niet bij de klant. {regimeError}" });
            var existing = req.Deposit ? q.DepositInvoiceId : q.InvoiceId;
            if (existing is { } invoiceId && await db.Invoices.AnyAsync(i => i.Id == invoiceId))
                return Results.Conflict(new { error = req.Deposit ? "Voor de aanbetaling is al een factuur gemaakt." : "Van deze offerte is al een factuur gemaakt.", invoiceId });

            // De factuur krijgt de taal van de klant, en de regels die Potel zelf schrijft dus ook.
            var lang = q.Customer.Language;
            List<InvoiceLine> lines;
            if (req.Deposit)
            {
                if (q.DepositPercent is not { } percent) return Results.BadRequest(new { error = "Op deze offerte staat geen aanbetaling." });
                var groups = q.Totals.VatGroups.Where(x => x.Base != 0).ToList();
                lines = groups.Select(x => new InvoiceLine
                {
                    Description = ClientTexts.T(lang, "line.deposit", ("percent", ClientTexts.Number(lang, percent, "0.##")), ("number", q.Number))
                                  + (groups.Count > 1 ? ClientTexts.T(lang, "line.depositVat", ("rate", ClientTexts.Number(lang, x.Rate, "0.##"))) : ""),
                    Quantity = 1, Unit = "stuk", UnitPrice = Money.Round(x.Base * percent / 100), VatRate = x.Rate,
                }).ToList();
                if (lines.Count == 0) return Results.BadRequest(new { error = "Het totaal van de offerte is nul, dus er is niets aan te betalen." });
            }
            else
            {
                lines = q.Lines.OrderBy(l => l.Position).ThenBy(l => l.Id).Select(l => l.AsInvoiceLine()).ToList();
                if (q.DepositInvoiceId is { } depositId && await db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == depositId) is { } deposit)
                    lines.AddRange(deposit.Lines.OrderBy(l => l.Position).Select(l => new InvoiceLine
                    {
                        Description = deposit.Number is null ? ClientTexts.T(lang, "line.depositMinusDraft") : ClientTexts.T(lang, "line.depositMinus", ("number", deposit.Number)),
                        Quantity = -l.Quantity, Unit = l.Unit, UnitPrice = l.UnitPrice, VatRate = l.VatRate,
                    }));
            }
            var settings = await SettingsEndpoints.GetAsync(db);
            var today = clock.Today;
            var invoice = new Invoice
            {
                CustomerId = customerId, IssueDate = today, DueDate = today.AddDays(settings.PaymentTermDays), VatRegime = q.VatRegime, DeliveryFrom = today,
                Language = lang, Notes = $"{ClientTexts.T(lang, "note.quote", ("number", q.Number))} {SettingsEndpoints.Note(lang)}",
                Lines = lines.Select((l, i) => { l.Position = i; if (VatRegimes.ZeroVat(q.VatRegime)) l.VatRate = 0; return l; }).ToList(),
            };
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
            if (req.Deposit) q.DepositInvoiceId = invoice.Id; else q.InvoiceId = invoice.Id;
            db.Log("factuur", $"Conceptfactuur{(req.Deposit ? " voor de aanbetaling" : "")} gemaakt uit offerte {q.Number}");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Created($"/api/invoices/{invoice.Id}", new { invoice.Id });
        });

        MapPublic(api);
    }

    // De link en de offerte achter een token, of null als het token niet (meer) werkt. Een onbekend, ingetrokken, verlopen of
    // fout token, een concept of een voorbeeldofferte: voor de buitenwereld allemaal hetzelfde.
    static async Task<(QuoteLink Link, Quote Quote)?> OpenLinkAsync(AppDb db, BusinessClock clock, string? token)
    {
        token = token?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > 100) return null;
        var hash = InvoiceMailer.Hash(token);
        var link = await db.QuoteLinks.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(l => l.TokenHash == hash);
        if (link is null || !link.Works(clock.UtcNow)) return null;
        var q = await db.Quotes.IgnoreQueryFilters().AsNoTracking().Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == link.QuoteId && x.WorkspaceId == link.WorkspaceId);
        if (q is null || q.Status == QuoteStatus.Draft || q.IsDemo || q.Number is null) return null;
        return (link, q);
    }

    // Het IP-adres van wie besluit, zoals de server het ziet (achter een proxy het doorgegeven adres).
    static string? Ip(HttpContext http) =>
        http.Connection.RemoteIpAddress is { } ip ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : null;

    static IResult Gone() => Results.NotFound(new { error = LinkGone });

    static void MapPublic(RouteGroupBuilder api)
    {
        // De openbare offertepagina: zonder inloggen, alleen met het token. Laat alleen deze ene offerte zien, met wat er op de
        // offerte zelf staat (plus logo en kleur), en of je klant nog kan besluiten.
        api.MapPost("/public/quote", async (AppDb db, BusinessClock clock, HttpResponse response, PublicQuoteRequest req) =>
        {
            InvoiceMailEndpoints.NoIndex(response);
            if (await OpenLinkAsync(db, clock, req.Token) is not { } found) return Gone();
            var (link, q) = found;
            var brand = await db.Settings.IgnoreQueryFilters().AsNoTracking().Where(s => s.WorkspaceId == link.WorkspaceId).OrderBy(s => s.Id)
                .Select(s => new { s.LogoDataUrl, s.BrandColor }).FirstOrDefaultAsync();
            // Is er een nieuwere versie verstuurd, dan zeggen we dat (zonder link: die krijgt je klant apart).
            var newer = await db.Quotes.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.PreviousVersionId == q.Id && x.WorkspaceId == link.WorkspaceId && x.Number != null && !x.IsDemo)
                .Select(x => new { x.Number, x.Version }).FirstOrDefaultAsync();
            await db.QuoteLinks.IgnoreQueryFilters().Where(l => l.Id == link.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(l => l.LastOpenedAt, clock.UtcNow).SetProperty(l => l.Opens, l => l.Opens + 1));
            var today = clock.Today;
            var b = q.Buyer;
            return Results.Ok(new
            {
                quote = new
                {
                    q.Number, q.Version, state = QuoteStatus.Of(q, today), q.IssueDate, q.ValidUntil, q.Subject, q.Intro, q.Terms, q.DepositPercent, q.VatRegime,
                    q.Language, q.Seller,
                    // Van de klant alleen wat op de offerte staat.
                    buyer = b is null ? null : new { b.Name, b.Contact, b.Address, b.City, b.Country, b.VatNumber },
                    lines = q.Lines.OrderBy(l => l.Position).ThenBy(l => l.Id).Select(l => new { l.Description, l.Quantity, l.Unit, l.UnitPrice, l.VatRate }),
                    q.Totals,
                    q.DecidedAt, q.DecidedName, q.RejectReason, q.WithdrawnAt,
                    newerNumber = newer?.Number, newerVersion = newer?.Version,
                },
                canDecide = DecisionProblem(q, today) is null,
                problem = DecisionProblem(q, today),
                brand = new { logoDataUrl = brand?.LogoDataUrl, brandColor = brand?.BrandColor ?? "#ff6d5a" },
            });
        }).AllowAnonymous().RequireRateLimiting("public");

        // Akkoord: met getypte naam en een vinkje. We bewaren naam, tijd en IP-adres.
        api.MapPost("/public/quote/accept", async (AppDb db, BusinessClock clock, HttpContext http, WorkflowEngine engine, QuoteNotices notices, PublicQuoteAccept req) =>
        {
            InvoiceMailEndpoints.NoIndex(http.Response);
            if (await OpenLinkAsync(db, clock, req.Token) is not { Link: var link, Quote: var quote }) return Gone();
            var name = EmailRules.OneLine(req.Name, 101);
            if (name.Length < 2 || name.Length > 100) return Results.BadRequest(new { error = ClientTexts.T(quote.Language, "quote.nameNeeded") });
            if (!req.Agree) return Results.BadRequest(new { error = ClientTexts.T(quote.Language, "quote.agreeNeeded") });
            return await DecideAsync(db, clock, http, engine, notices, link, accept: true, name, reason: null);
        }).AllowAnonymous().RequireRateLimiting("public");

        // Afwijzen, met een toelichting als je klant die wil geven.
        api.MapPost("/public/quote/reject", async (AppDb db, BusinessClock clock, HttpContext http, WorkflowEngine engine, QuoteNotices notices, PublicQuoteReject req) =>
        {
            InvoiceMailEndpoints.NoIndex(http.Response);
            if (await OpenLinkAsync(db, clock, req.Token) is not { Link: var link, Quote: var quote }) return Gone();
            var reason = Clean(req.Reason);
            if (reason?.Length > MaxReasonLength)
                return Results.BadRequest(new { error = ClientTexts.T(quote.Language, "quote.reasonTooLong", ("max", MaxReasonLength.ToString(CultureInfo.InvariantCulture))) });
            return await DecideAsync(db, clock, http, engine, notices, link, accept: false, name: null, reason);
        }).AllowAnonymous().RequireRateLimiting("public");
    }

    // Eén keer besluiten, vóór het einde van de geldigheid en niet na intrekken. Binnen een WriteLock, zodat twee tabbladen
    // tegelijk niet allebei kunnen accepteren. Daarna een mail aan de beheerders en, bij akkoord, de werkstroomtrigger.
    static async Task<IResult> DecideAsync(AppDb db, BusinessClock clock, HttpContext http, WorkflowEngine engine, QuoteNotices notices,
        QuoteLink link, bool accept, string? name, string? reason)
    {
        // Dit verzoek heeft geen ingelogde gebruiker: vanaf hier werken we in de werkruimte van de link.
        db.Tenant.WorkspaceId = link.WorkspaceId;
        Quote q;
        await using (var tx = await WriteLock.BeginAsync(db))
        {
            var fresh = await db.QuoteLinks.AsNoTracking().FirstOrDefaultAsync(l => l.Id == link.Id);
            var found = await db.Quotes.Include(x => x.Lines).Include(x => x.Customer).Include(x => x.Lead).FirstOrDefaultAsync(x => x.Id == link.QuoteId);
            if (fresh is null || !fresh.Works(clock.UtcNow) || found is null || found.IsDemo || found.Status == QuoteStatus.Draft) return Gone();
            q = found;
            if (DecisionProblem(q, clock.Today) is { } problem) return Results.Conflict(new { error = problem });
            q.Status = accept ? QuoteStatus.Accepted : QuoteStatus.Rejected;
            q.DecidedAt = clock.UtcNow;
            q.DecidedName = name;
            q.DecidedIp = Ip(http);
            q.RejectReason = reason;
            db.Log("offerte", accept
                ? $"Offerte {q.Number} geaccepteerd door {name}"
                : $"Offerte {q.Number} afgewezen door {Recipient(q)}{(reason is null ? "" : ": " + EmailRules.OneLine(reason, 120))}");
            await db.SaveChangesAsync();
            // Akkoord op een offerte voor een lead: de lead wordt klant (of was dat al), zodat je meteen een project of factuur kunt maken.
            if (accept && q.CustomerId is null) q.CustomerId = (await CustomerForAsync(db, q)).Id;
            if (accept && q.Lead is { Status: not "gewonnen" } lead)
            {
                db.Log("lead", $"Lead {lead.Name}: {lead.Status} → gewonnen");
                lead.Status = "gewonnen";
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        await notices.DecidedAsync(q, http.Request);
        if (accept)
        {
            var ctx = new Dictionary<string, string>();
            if (q.Lead is { } l) WorkflowContext.AddLead(ctx, l);
            if (q.CustomerId is { } customerId && await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId) is { } c) WorkflowContext.AddCustomer(ctx, c);
            WorkflowContext.AddQuote(ctx, q);
            await engine.TriggerAsync("trigger.quoteAccepted", ctx, $"Offerte {q.Number} geaccepteerd");
        }
        return Results.Ok(new { state = q.Status, q.DecidedAt, q.DecidedName });
    }

    // De klant bij een geaccepteerde offerte zonder klant: de lead omzetten, of (als die lead er niet meer is) een klant maken
    // met de gegevens die op de offerte stonden.
    static async Task<Customer> CustomerForAsync(AppDb db, Quote q)
    {
        if (q.Lead is { } lead) return await LeadEndpoints.ConvertAsync(db, lead);
        var b = q.Buyer ?? new InvoiceParty { Name = "Klant uit offerte " + q.Number };
        var customer = new Customer
        {
            Name = b.Contact ?? b.Name, Company = b.Contact is null ? null : b.Name, Email = b.Email, Phone = b.Phone, Address = b.Address, City = b.City,
            Language = Languages.Of(q.Language),
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        db.Log("klant", $"Klant {customer.Name} gemaakt uit geaccepteerde offerte {q.Number}");
        return customer;
    }
}

// Een mail aan de beheerders als een klant over een offerte heeft besloten, als er een mailserver is. Alleen naar bevestigde
// adressen. Gaat via de wachtrij, zodat je klant niet op de mailserver hoeft te wachten. Het dashboard toont het hoe dan ook.
public class QuoteNotices(AppDb db, IEmailSender sender, SystemMailQueue queue, AppUrls urls, IConfiguration config, ILogger<QuoteNotices> logger)
{
    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    string Product => config["Platform:Name"] is { Length: > 0 } name ? name : "Potel";

    public async Task DecidedAsync(Quote q, HttpRequest request)
    {
        try
        {
            if (!sender.Configured) return;
            var to = await db.Users.AsNoTracking().Where(u => u.Role == Roles.Admin && u.Active && u.EmailVerifiedAt != null).Select(u => u.Email).ToListAsync();
            if (to.Count == 0) return;
            var accepted = q.Status == QuoteStatus.Accepted;
            var at = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(q.DecidedAt ?? DateTime.UtcNow, DateTimeKind.Utc), BusinessClock.Zone);
            var who = q.Buyer?.Name ?? "Je klant";
            var subject = string.IsNullOrWhiteSpace(q.Subject) ? "" : $" ({q.Subject.Trim()})";
            var when = $"op {at.ToString("d MMMM yyyy", Dutch)} om {at:HH:mm}";
            var body = accepted
                ? $"Goed nieuws: {who} heeft offerte {q.Number}{subject} geaccepteerd {when}. Akkoord gegeven door: {q.DecidedName}."
                : $"{who} heeft offerte {q.Number}{subject} afgewezen {when}."
                    + (q.RejectReason is { } reason ? $"\n\nDe toelichting:\n{reason}" : " Er is geen toelichting gegeven.");
            if (urls.Base(request) is { } baseUrl)
                body += accepted
                    ? $"\n\nBekijk de offerte in {Product}. Daar maak je er een project of factuur van:\n{baseUrl}/offertes/{q.Id}"
                    : $"\n\nBekijk de offerte in {Product}:\n{baseUrl}/offertes/{q.Id}";
            var title = accepted ? $"Offerte {q.Number} is geaccepteerd" : $"Offerte {q.Number} is afgewezen";
            foreach (var address in to.Where(EmailRules.IsSingleAddress).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!queue.TryEnqueue(new OutgoingEmail(address, title, body, Product)))
                    logger.LogWarning("Melding over offerte {Quote} niet in de wachtrij gezet", q.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Melding over offerte {Quote} niet verstuurd", q.Id);
        }
    }
}
