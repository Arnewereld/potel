using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Invoicing;

namespace Potel.Api.Endpoints;

// Wat je in je agenda wilt zien. Facturen, offertes en btw alleen voor een beheerder; CanSeeMoney zegt of dat voor jou geldt.
// Active: er is een werkende link. De link zelf zie je alleen als je hem maakt. Client: welke agenda hem het laatst ophaalde.
public record CalendarFeedDto(bool Active, DateTime? CreatedAt, bool Planning, bool Deadlines, bool Invoices, bool Quotes, bool Vat, bool ShowAmounts,
    DateTime? LastFetchedAt, int Fetches, string? Client, bool CanSeeMoney)
{
    public static CalendarFeedDto From(CalendarFeed? f, bool admin) => f is null
        ? new(false, null, true, true, admin, admin, admin, false, null, 0, null, admin)
        : new(f.TokenHash is not null, f.TokenHash is null ? null : f.CreatedAt, f.Planning, f.Deadlines, f.Invoices && admin, f.Quotes && admin, f.Vat && admin,
            f.ShowAmounts, f.TokenHash is null ? null : f.LastFetchedAt, f.TokenHash is null ? 0 : f.Fetches,
            f.TokenHash is null ? null : CalendarFeedEndpoints.ClientName(f.LastClient), admin);
}

public record CalendarFeedInput(bool Planning, bool Deadlines, bool Invoices, bool Quotes, bool Vat, bool ShowAmounts);

// Je planning in je eigen agenda (Apple, Google, Outlook) via een geheime link per gebruiker. In de database staat alleen de hash
// van het token; een verkeerde, ingetrokken of oude link geeft altijd hetzelfde lege 404. De agenda haalt de link op zonder cookie,
// dus bij elke keer kijken we opnieuw of de gebruiker nog bestaat en actief is, en welke rol hij heeft.
public static partial class CalendarFeedEndpoints
{
    public const string PathPrefix = "/api/public/agenda/";
    public const int PastDays = 90;
    public const int FutureDays = 400;
    public const int MaxClientLength = 200;
    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    public static string Path(string token) => $"{PathPrefix}{token}.ics";

    [GeneratedRegex(@"^/api/calendar-feed/revoke/?$", RegexOptions.IgnoreCase)]
    private static partial Regex RevokePath();

    // De link intrekken mag ook in een werkruimte die alleen-lezen is: het is een veiligheidsmaatregel.
    public static bool IsRevoke(HttpRequest r) => HttpMethods.IsPost(r.Method) && RevokePath().IsMatch(r.Path.Value ?? "");

    // Een herkenbare naam voor de agenda die de link ophaalde, uit de User-Agent die we bewaren.
    public static string? ClientName(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return null;
        bool Has(string s) => ua.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("Google")) return "Google Agenda";
        if (Has("Outlook") || Has("Microsoft") || Has("Exchange")) return "Outlook";
        if (ua.StartsWith("iOS/", StringComparison.OrdinalIgnoreCase) || Has("dataaccessd")) return "Apple Agenda op een iPhone of iPad";
        if (ua.StartsWith("macOS/", StringComparison.OrdinalIgnoreCase) || Has("CalendarAgent")) return "Apple Agenda op een Mac";
        if (Has("Thunderbird")) return "Thunderbird";
        if (ua.StartsWith("Mozilla/", StringComparison.OrdinalIgnoreCase)) return "een webbrowser";
        return "een andere agenda";
    }

    static string Euro(decimal d) => d.ToString("C", Dutch);
    static string Name(Customer? c) => c is null ? "" : string.IsNullOrWhiteSpace(c.Company) ? c.Name : c.Company;

    // Soort uit Planning zoals de agenda hem als categorie toont.
    static string KindLabel(string kind) => kind switch
    {
        "afspraak" => "Afspraak",
        "taak" => "Taak",
        "project" => "Project",
        "intern" => "Intern",
        _ => "Planning",
    };

    // De inhoud van de agenda voor deze gebruiker, binnen het venster rond vandaag. Alles expliciet op de werkruimte van de link;
    // voorbeelddata komt er nooit in. Bedragen en notities alleen met ShowAmounts, geld alleen voor een beheerder.
    public static async Task<List<IcsEvent>> EventsAsync(AppDb db, BusinessClock clock, CalendarFeed f, User user)
    {
        var ws = f.WorkspaceId;
        var today = clock.Today;
        var from = today.AddDays(-PastDays);
        var to = today.AddDays(FutureDays);
        var admin = user.Role == Roles.Admin;
        var amounts = f.ShowAmounts;
        string Uid(string kind, string id) => $"{kind}-{id}.ws{ws}@potel";
        string Id(int id) => id.ToString(CultureInfo.InvariantCulture);
        var customers = await db.Customers.IgnoreQueryFilters().AsNoTracking().Where(c => c.WorkspaceId == ws).ToDictionaryAsync(c => c.Id);
        Customer? CustomerOf(int? id) => id is { } x ? customers.GetValueOrDefault(x) : null;
        var events = new List<IcsEvent>();

        // Afspraken en taken uit Planning: wat voor jou is ingepland en wat voor iedereen is. Afgevinkte taken niet.
        if (f.Planning)
        {
            var list = await db.Appointments.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.WorkspaceId == ws && !a.IsDemo && (a.UserId == null || a.UserId == user.Id) && a.End >= from && a.Start <= to)
                .OrderBy(a => a.Start).ThenBy(a => a.Id).ToListAsync();
            foreach (var a in list.Where(a => !(a.Done && a.Kind == "taak")))
            {
                var lines = new List<string>();
                if (CustomerOf(a.CustomerId) is { } customer) lines.Add($"Klant: {Name(customer)}");
                if (amounts && !string.IsNullOrWhiteSpace(a.Notes)) lines.Add(a.Notes.Trim());
                events.Add(new IcsEvent(Uid("planning", Id(a.Id)), a.Title, a.Start, a.End, false,
                    lines.Count > 0 ? string.Join("\n", lines) : null, a.Location, KindLabel(a.Kind)));
            }
        }

        // Deadlines van projecten die nog lopen, als een hele dag.
        if (f.Deadlines)
        {
            var projects = await db.Projects.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.WorkspaceId == ws && !p.IsDemo && p.Deadline != null && p.Deadline >= from && p.Deadline <= to && p.Status != "afgerond")
                .OrderBy(p => p.Deadline).ThenBy(p => p.Id).ToListAsync();
            foreach (var p in projects)
                events.Add(new IcsEvent(Uid("deadline", Id(p.Id)), $"Deadline {p.Name}", p.Deadline!.Value.Date, null, true,
                    CustomerOf(p.CustomerId) is { } c ? $"Klant: {Name(c)}" : null, null, "Deadline"));
        }

        // Geld: alleen voor een beheerder. De rol telt van nu, dus wie geen beheerder meer is, ziet het bij de volgende keer kijken niet meer.
        if (admin && f.Invoices)
        {
            var open = await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .Include(i => i.Lines).Include(i => i.Credits).ThenInclude(c => c.Lines).Include(i => i.Payments)
                .Where(i => i.WorkspaceId == ws && !i.IsDemo && (i.Status == InvoiceStatus.Sent || i.Status == InvoiceStatus.Overdue) && i.CreditForInvoiceId == null
                            && i.DueDate >= from && i.DueDate <= to)
                .AsSplitQuery().ToListAsync();
            foreach (var i in open.Where(i => i.OpenAmount > 0).OrderBy(i => i.DueDate).ThenBy(i => i.Id))
            {
                var lines = new List<string> { $"Klant: {Name(CustomerOf(i.CustomerId))}" };
                if (amounts) lines.Add($"Nog open: {Euro(i.OpenAmount)}");
                events.Add(new IcsEvent(Uid("factuur", Id(i.Id)), $"Factuur {i.Number} vervalt", i.DueDate.Date, null, true,
                    string.Join("\n", lines), null, "Factuur"));
            }
        }

        if (admin && f.Quotes)
        {
            var quotes = await db.Quotes.IgnoreQueryFilters().AsNoTracking().Include(q => q.Lines)
                .Where(q => q.WorkspaceId == ws && !q.IsDemo && q.Status == QuoteStatus.Sent && q.ValidUntil >= from && q.ValidUntil <= to)
                .AsSplitQuery().ToListAsync();
            var leadIds = quotes.Where(q => q.LeadId != null).Select(q => q.LeadId!.Value).ToList();
            var leads = await db.Leads.IgnoreQueryFilters().AsNoTracking().Where(l => l.WorkspaceId == ws && leadIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id);
            foreach (var q in quotes.OrderBy(q => q.ValidUntil).ThenBy(q => q.Id))
            {
                var who = CustomerOf(q.CustomerId) is { } c ? Name(c)
                    : q.LeadId is { } lid && leads.GetValueOrDefault(lid) is { } l ? (string.IsNullOrWhiteSpace(l.Company) ? l.Name : l.Company) : null;
                var lines = new List<string>();
                if (!string.IsNullOrWhiteSpace(who)) lines.Add($"Voor: {who}");
                if (!string.IsNullOrWhiteSpace(q.Subject)) lines.Add(q.Subject.Trim());
                if (amounts) lines.Add($"Totaal: {Euro(q.Totals.Total)}");
                events.Add(new IcsEvent(Uid("offerte", Id(q.Id)), $"Offerte {q.Number} verloopt", q.ValidUntil.Date, null, true,
                    lines.Count > 0 ? string.Join("\n", lines) : null, null, "Offerte"));
            }
        }

        // De laatste dag voor elke btw-aangifte in het venster. Met de KOR doe je geen aangifte.
        if (admin && f.Vat)
        {
            var regime = await db.Settings.IgnoreQueryFilters().AsNoTracking().Where(s => s.WorkspaceId == ws).OrderBy(s => s.Id)
                .Select(s => s.VatRegime).FirstOrDefaultAsync();
            if (regime != VatRegimes.Kor)
            {
                var filed = (await db.VatFilings.IgnoreQueryFilters().AsNoTracking().Where(v => v.WorkspaceId == ws).Select(v => new { v.Year, v.Quarter }).ToListAsync())
                    .Select(v => (v.Year, v.Quarter)).ToHashSet();
                // De uiterste datum ligt een maand na het kwartaal; begin daarom een kwartaal voor het begin van het venster.
                var (year, quarter) = VatReturn.QuarterOf(from) == 1 ? (from.Year - 1, 4) : (from.Year, VatReturn.QuarterOf(from) - 1);
                for (; ; (year, quarter) = quarter == 4 ? (year + 1, 1) : (year, quarter + 1))
                {
                    var due = VatReturn.DueDate(year, quarter);
                    if (due > to) break;
                    if (due < from) continue;
                    events.Add(new IcsEvent(Uid("btw", $"{year}-q{quarter}"), $"Btw-aangifte {VatReturn.Label(year, quarter)}", due, null, true,
                        filed.Contains((year, quarter)) ? "Je hebt deze aangifte in Potel als gedaan gemarkeerd." : "De laatste dag om je aangifte te doen en de btw te betalen bij de Belastingdienst.",
                        null, "Btw"));
                }
            }
        }
        return events;
    }

    static async Task<User?> MeAsync(AppDb db, ClaimsPrincipal me) =>
        me.UserId() is { } id ? await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id) : null;

    public static void MapCalendarFeed(this RouteGroupBuilder api)
    {
        // Je eigen agendalink. Iedereen heeft er een voor zichzelf; niemand ziet of beheert die van een ander.
        var g = api.MapGroup("/calendar-feed");

        g.MapGet("/", async (AppDb db, ClaimsPrincipal me) =>
            await MeAsync(db, me) is { } user
                ? Results.Ok(CalendarFeedDto.From(await db.CalendarFeeds.AsNoTracking().FirstOrDefaultAsync(f => f.UserId == user.Id), user.Role == Roles.Admin))
                : Results.NotFound());

        g.MapPut("/", async (AppDb db, ClaimsPrincipal me, CalendarFeedInput input) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            if (await MeAsync(db, me) is not { } user) return Results.NotFound();
            var admin = user.Role == Roles.Admin;
            var f = await db.CalendarFeeds.FirstOrDefaultAsync(x => x.UserId == user.Id);
            if (f is null) db.CalendarFeeds.Add(f = new CalendarFeed { UserId = user.Id });
            f.Planning = input.Planning; f.Deadlines = input.Deadlines; f.ShowAmounts = input.ShowAmounts;
            // Facturen, offertes en btw zijn er alleen voor een beheerder.
            f.Invoices = admin && input.Invoices; f.Quotes = admin && input.Quotes; f.Vat = admin && input.Vat;
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Ok(CalendarFeedDto.From(f, admin));
        });

        // Een nieuwe link. Een oude link werkt daarna niet meer. Het token zie je alleen nu; wij bewaren alleen de hash.
        g.MapPost("/link", async (AppDb db, BusinessClock clock, AppUrls urls, HttpRequest request, ClaimsPrincipal me) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            if (await MeAsync(db, me) is not { } user) return Results.NotFound();
            var admin = user.Role == Roles.Admin;
            var f = await db.CalendarFeeds.FirstOrDefaultAsync(x => x.UserId == user.Id);
            if (f is null)
            {
                db.CalendarFeeds.Add(f = new CalendarFeed { UserId = user.Id });
                f.Invoices = admin; f.Quotes = admin; f.Vat = admin;
            }
            var renewed = f.TokenHash is not null;
            var token = InvoiceMailer.NewToken();
            f.TokenHash = InvoiceMailer.Hash(token);
            f.CreatedAt = clock.UtcNow;
            f.LastFetchedAt = null; f.Fetches = 0; f.LastClient = null;
            db.Log("planning", renewed ? $"Nieuwe agendalink voor {user.Name}; de oude werkt niet meer" : $"Agendalink voor {user.Name} gemaakt");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            var path = Path(token);
            return Results.Ok(new { path, url = urls.Base(request) is { } b ? $"{b}{path}" : null, feed = CalendarFeedDto.From(f, admin) });
        });

        g.MapPost("/revoke", async (AppDb db, ClaimsPrincipal me) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            if (await MeAsync(db, me) is not { } user) return Results.NotFound();
            var f = await db.CalendarFeeds.FirstOrDefaultAsync(x => x.UserId == user.Id);
            if (f is { TokenHash: not null })
            {
                f.TokenHash = null;
                db.Log("planning", $"Agendalink van {user.Name} ingetrokken");
                await db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            return Results.Ok(CalendarFeedDto.From(f, user.Role == Roles.Admin));
        });

        // De agenda zelf: zonder inloggen, alleen met het token in het adres (een agenda-app kan niet anders). Alles wordt expliciet
        // gefilterd op de werkruimte en de gebruiker van de link. Bij elke fout hetzelfde lege 404, zodat niemand kan afleiden of een
        // link ooit bestond.
        api.MapMethods("/public/agenda/{token}.ics", ["GET", "HEAD"], async (AppDb db, BusinessClock clock, HttpContext http, string token) =>
        {
            InvoiceMailEndpoints.NoIndex(http.Response);
            if (string.IsNullOrEmpty(token) || token.Length > 100) return Results.NotFound();
            var hash = InvoiceMailer.Hash(token);
            var f = await db.CalendarFeeds.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.TokenHash == hash);
            if (f is null) return Results.NotFound();
            var user = await db.Users.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(u => u.Id == f.UserId && u.WorkspaceId == f.WorkspaceId);
            if (user is not { Active: true }) return Results.NotFound();
            if (!await db.Workspaces.AsNoTracking().AnyAsync(w => w.Id == f.WorkspaceId)) return Results.NotFound();

            var events = await EventsAsync(db, clock, f, user);
            var company = await db.Settings.IgnoreQueryFilters().AsNoTracking().Where(s => s.WorkspaceId == f.WorkspaceId).OrderBy(s => s.Id)
                .Select(s => s.CompanyName).FirstOrDefaultAsync();
            var ics = IcsWriter.Write(string.IsNullOrWhiteSpace(company) ? "Potel" : $"Potel, {company.Trim()}", $"Je planning uit Potel voor {user.Name}", events, clock.UtcNow);
            // Wanneer en door welke agenda hij werd opgehaald, zodat je ziet of het werkt. Een HEAD telt niet mee.
            if (HttpMethods.IsGet(http.Request.Method))
            {
                var client = http.Request.Headers.UserAgent.ToString().Trim();
                var now = clock.UtcNow;
                await db.CalendarFeeds.IgnoreQueryFilters().Where(x => x.Id == f.Id && x.TokenHash == hash)
                    .ExecuteUpdateAsync(x => x.SetProperty(c => c.LastFetchedAt, now).SetProperty(c => c.Fetches, c => c.Fetches + 1)
                        .SetProperty(c => c.LastClient, client.Length == 0 ? null : client.Length > MaxClientLength ? client[..MaxClientLength] : client));
            }
            return Results.Text(ics, "text/calendar; charset=utf-8");
        }).AllowAnonymous().RequireRateLimiting("calendar");
    }
}
