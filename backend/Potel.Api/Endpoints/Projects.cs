using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Integrations;

namespace Potel.Api.Endpoints;

// Met Trips gaan ook de open ritten van het project mee die een kilometertarief hebben.
public record InvoiceHoursRequest(int[]? EntryIds, bool Detailed, bool Trips = false);

public static class ProjectEndpoints
{
    public static decimal Hours(int minutes) => Math.Round(minutes / 60m, 2, MidpointRounding.AwayFromZero);

    // Een project met de optelsommen van zijn uren erbij. InvoicedValue is wat er van dit project op facturen staat (excl. btw).
    public record ProjectDto(
        int Id, string Name, int CustomerId, string? CustomerName, string Status, string Billing, decimal HourlyRate, decimal FixedPrice,
        decimal? BudgetHours, string Color, string? RepoUrl, string? Description, DateTime? Deadline, DateTime CreatedAt,
        int MinutesTotal, int MinutesUnbilled, decimal UnbilledValue, decimal InvoicedValue, DateTime? LastEntry, decimal? KilometreRate);

    public static ProjectDto ToDto(Project p, IEnumerable<TimeEntry> entries, IReadOnlyDictionary<int, InvoiceLine> lines)
    {
        var mine = entries.Where(e => e.ProjectId == p.Id).ToList();
        var unbilled = p.Billing == Billing.Hourly ? mine.Where(e => e.Billable && e.InvoiceId == null).Sum(e => e.Minutes) : 0;
        // Alleen de eigen urenregels tellen, niet de hele factuur waar ze op staan. Uren van voor de koppeling per regel
        // tellen tegen het uurtarief.
        var invoiced = mine.Where(e => e.InvoiceId != null && e.InvoiceLineId is { } l && lines.ContainsKey(l))
                .Select(e => e.InvoiceLineId!.Value).Distinct().Sum(l => Money.LineAmount(lines[l]))
            + Money.Round(Hours(mine.Where(e => e.InvoiceId != null && (e.InvoiceLineId is not { } l || !lines.ContainsKey(l))).Sum(e => e.Minutes)) * p.HourlyRate);
        return new ProjectDto(
            p.Id, p.Name, p.CustomerId, p.Customer?.Company ?? p.Customer?.Name, p.Status, p.Billing, p.HourlyRate, p.FixedPrice,
            p.BudgetHours, p.Color, p.RepoUrl, p.Description, p.Deadline, p.CreatedAt,
            mine.Sum(e => e.Minutes), unbilled, Money.Round(Hours(unbilled) * p.HourlyRate), invoiced,
            mine.Count == 0 ? null : mine.Max(e => e.Date), p.KilometreRate);
    }

    public static async Task<List<ProjectDto>> ListAsync(AppDb db, Func<IQueryable<Project>, IQueryable<Project>>? filter = null)
    {
        var q = db.Projects.Include(p => p.Customer).AsNoTracking();
        var projects = await (filter is null ? q : filter(q)).ToListAsync();
        var ids = projects.Select(p => p.Id).ToList();
        var entries = await db.TimeEntries.Where(e => ids.Contains(e.ProjectId)).AsNoTracking().ToListAsync();
        // Factuurregels hebben zelf geen werkruimte; de ids komen van uren uit deze werkruimte.
        var lineIds = entries.Where(e => e.InvoiceLineId != null).Select(e => e.InvoiceLineId!.Value).Distinct().ToList();
        var lines = await db.InvoiceLines.Where(l => lineIds.Contains(l.Id)).AsNoTracking().ToDictionaryAsync(l => l.Id);
        return projects.Select(p => ToDto(p, entries, lines)).ToList();
    }

    // Factuurregels voor uren, elk met de boekingen die erbij horen: één regel met het totaal of één regel per boeking.
    // Per boeking ronden we zo af dat de regels samen precies even veel uren zijn als de totaalregel. Scheelt het afronden
    // per regel dan toch een cent (bij een uurtarief met centen), dan staat dat verschil er apart op.
    public static List<(InvoiceLine Line, List<TimeEntry> Entries)> HourLines(Project p, List<TimeEntry> entries, bool detailed, string? language = null)
    {
        var total = Hours(entries.Sum(e => e.Minutes));
        // In het Engels met de maand in letters: 01-10 leest een Amerikaan als 10 januari.
        var english = Invoicing.ClientTexts.IsEnglish(language);
        string Day(DateTime d, bool year = true) => english
            ? d.ToString(year ? "d MMM yyyy" : "d MMM", CultureInfo.InvariantCulture)
            : d.ToString(year ? "dd-MM-yyyy" : "dd-MM", CultureInfo.InvariantCulture);
        if (!detailed)
            return [(new InvoiceLine
            {
                Description = Invoicing.ClientTexts.T(language, "line.hours", ("project", p.Name), ("from", Day(entries[0].Date, year: false)), ("to", Day(entries[^1].Date))),
                Quantity = total, Unit = "uur", UnitPrice = p.HourlyRate,
            }, entries)];

        var parts = new List<(InvoiceLine Line, List<TimeEntry> Entries)>();
        var minutes = 0;
        var done = 0m;
        foreach (var e in entries)
        {
            minutes += e.Minutes;
            var upTo = Hours(minutes);
            parts.Add((new InvoiceLine
            {
                Description = $"{Day(e.Date)} {(string.IsNullOrWhiteSpace(e.Description) ? p.Name : e.Description)}",
                Quantity = upTo - done, Unit = "uur", UnitPrice = p.HourlyRate,
            }, [e]));
            done = upTo;
        }
        var difference = Money.Round(total * p.HourlyRate) - parts.Sum(x => Money.LineAmount(x.Line));
        if (difference != 0) parts.Add((new InvoiceLine { Description = Invoicing.ClientTexts.T(language, "line.rounding"), Quantity = 1, Unit = "stuk", UnitPrice = difference }, []));
        return parts;
    }

    // Koppelt de uren aan hun factuurregel, maar alleen als ze nog vrij zijn. Lukt dat niet voor allemaal (een ander verzoek
    // was net eerder), dan false: draai dan alles terug. Hoort binnen een WriteLock, na het opslaan van de regels.
    public static async Task<bool> ClaimAsync(AppDb db, int invoiceId, List<(InvoiceLine Line, List<TimeEntry> Entries)> parts)
    {
        foreach (var (line, entries) in parts.Where(x => x.Entries.Count > 0))
        {
            var ids = entries.Select(e => e.Id).ToList();
            var lineId = line.Id;
            var claimed = await db.TimeEntries.Where(t => ids.Contains(t.Id) && t.InvoiceId == null)
                .ExecuteUpdateAsync(x => x.SetProperty(t => t.InvoiceId, invoiceId).SetProperty(t => t.InvoiceLineId, lineId));
            if (claimed != ids.Count) return false;
        }
        return true;
    }

    // Open, factureerbare uren van een project; optioneel alleen de gekozen boekingen.
    public static Task<List<TimeEntry>> OpenEntries(AppDb db, int projectId, int[]? ids = null)
    {
        var q = db.TimeEntries.AsNoTracking().Where(t => t.ProjectId == projectId && t.Billable && t.InvoiceId == null);
        if (ids is { Length: > 0 }) q = q.Where(t => ids.Contains(t.Id));
        return q.OrderBy(t => t.Date).ThenBy(t => t.Id).ToListAsync();
    }

    // Bij wijzigen geeft de aanroeper de opgeslagen repository-link mee: een oude link die niet aan de regels voldoet,
    // houdt het opslaan van de rest van het project niet tegen zolang hij niet verandert.
    static string? Validate(AppDb db, Project input, string? storedRepoUrl = null)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return "Naam is verplicht";
        if (!db.Customers.Any(c => c.Id == input.CustomerId)) return "Kies een klant";
        if (!ProjectStatus.All.Contains(input.Status)) return "Onbekende status";
        if (!Billing.All.Contains(input.Billing)) return "Kies per uur of vaste prijs";
        if (input.HourlyRate < 0 || input.FixedPrice < 0 || input.BudgetHours < 0) return "Bedragen en uren kunnen niet negatief zijn";
        // Zo past een factuurregel van dit project altijd (zie Money.MaxAmount).
        if (input.HourlyRate > Money.MaxAmount || input.FixedPrice > Money.MaxAmount) return "Dat bedrag is te groot";
        if (TripEndpoints.RateProblem(input.KilometreRate) is { } rateProblem) return rateProblem;
        if (storedRepoUrl is not null && CleanRepoUrl(input.RepoUrl) == storedRepoUrl) return null;
        return RepoUrlProblem(input.RepoUrl);
    }

    // De repository-link: leeg, of een gewone https-link. Een link naar github.com moet precies naar één repository wijzen
    // (https://github.com/eigenaar/naam), want daar haalt Potel de commits voor de urenvoorstellen op.
    public static string? RepoUrlProblem(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (url.Length > 300 || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(u.UserInfo))
            return "Vul bij repository een gewone link in die begint met https://, of laat het veld leeg.";
        if (GitHubRepo.LooksLikeGitHub(url) && GitHubRepo.Parse(url) is null)
            return "Een GitHub-link moet naar één repository wijzen, zoals https://github.com/eigenaar/repository.";
        return null;
    }

    static string? CleanRepoUrl(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim();

    public static void MapProjects(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/projects");

        g.MapGet("/", async (AppDb db, int? customerId) =>
            (await ListAsync(db, q => customerId is { } c ? q.Where(p => p.CustomerId == c) : q))
                .OrderBy(p => Array.IndexOf(ProjectStatus.All, p.Status)).ThenByDescending(p => p.LastEntry ?? p.CreatedAt));

        g.MapGet("/{id:int}", async (AppDb db, int id) =>
            (await ListAsync(db, q => q.Where(p => p.Id == id))).FirstOrDefault() is { } p ? Results.Ok(p) : Results.NotFound());

        g.MapPost("/", async (AppDb db, Project input) =>
        {
            if (Validate(db, input) is { } error) return Results.BadRequest(new { error });
            var p = new Project
            {
                Name = input.Name.Trim(), CustomerId = input.CustomerId, Status = input.Status, Billing = input.Billing,
                HourlyRate = input.HourlyRate, FixedPrice = input.FixedPrice, BudgetHours = input.BudgetHours, Color = input.Color,
                RepoUrl = CleanRepoUrl(input.RepoUrl), Description = input.Description, Deadline = input.Deadline,
                KilometreRate = TripEndpoints.CleanRate(input.KilometreRate),
            };
            db.Projects.Add(p);
            db.Log("project", $"Project {p.Name} gestart");
            await db.SaveChangesAsync();
            return Results.Created($"/api/projects/{p.Id}", p);
        });

        g.MapPut("/{id:int}", async (AppDb db, int id, Project input) =>
        {
            var p = await db.Projects.FindAsync(id);
            if (p is null) return Results.NotFound();
            if (Validate(db, input, p.RepoUrl) is { } error) return Results.BadRequest(new { error });
            if (p.Status != input.Status) db.Log("project", $"Project {p.Name}: {p.Status} → {input.Status}");
            p.Name = input.Name.Trim(); p.CustomerId = input.CustomerId; p.Status = input.Status; p.Billing = input.Billing;
            p.HourlyRate = input.HourlyRate; p.FixedPrice = input.FixedPrice; p.BudgetHours = input.BudgetHours; p.Color = input.Color;
            p.RepoUrl = CleanRepoUrl(input.RepoUrl); p.Description = input.Description; p.Deadline = input.Deadline;
            p.KilometreRate = TripEndpoints.CleanRate(input.KilometreRate);
            await db.SaveChangesAsync();
            return Results.Ok(p);
        });

        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var p = await db.Projects.FindAsync(id);
            if (p is null) return Results.NotFound();
            if (await db.TimeEntries.AnyAsync(t => t.ProjectId == id && t.InvoiceId != null))
                return Results.Conflict(new { error = "Dit project heeft gefactureerde uren. Zet het op afgerond in plaats van het te verwijderen." });
            // Een lopende timer is werk dat nog geboekt moet worden; die gaat niet ongemerkt mee.
            if (await db.RunningTimers.AnyAsync(t => t.ProjectId == id))
                return Results.Conflict(new { error = "Op dit project loopt nog een timer. Stop die eerst, of vraag je collega dat te doen." });
            db.Projects.Remove(p);
            db.Log("project", $"Project {p.Name} verwijderd");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Zet de open uren van een project op een nieuwe conceptfactuur, en als je dat wilt ook de open ritten van het project.
        // Het nummer komt pas bij versturen.
        g.MapPost("/{id:int}/invoice", async (AppDb db, BusinessClock clock, int id, InvoiceHoursRequest? req) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            var p = await db.Projects.Include(x => x.Customer).FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound();
            if (p.Billing != Billing.Hourly)
                return Results.BadRequest(new { error = "Dit project heeft een vaste prijs; maak de factuur zelf aan." });

            var entries = await OpenEntries(db, id, req?.EntryIds);
            var trips = req?.Trips == true ? (await TripEndpoints.OpenTripsAsync(db, p.CustomerId, id)).Where(x => x.Rate is not null).ToList() : [];
            if (entries.Count == 0 && trips.Count == 0)
                return Results.BadRequest(new { error = req?.Trips == true ? "Er zijn geen open uren of ritten om te factureren" : "Er zijn geen open uren om te factureren" });

            var settings = await SettingsEndpoints.GetAsync(db);
            var today = clock.Today;
            var regime = VatRegimes.DefaultFor(settings, p.Customer);
            var lang = p.Customer?.Language ?? Languages.Dutch;
            var parts = entries.Count > 0 ? HourLines(p, entries, req?.Detailed == true, lang) : [];
            var tripParts = TripEndpoints.TripLines(trips, lang);
            foreach (var (line, _) in tripParts) line.VatRate = 21;
            var lines = parts.Select(x => x.Line).Concat(tripParts.Select(x => x.Line)).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                lines[i].Position = i;
                if (VatRegimes.ZeroVat(regime)) lines[i].VatRate = 0;
            }
            var dates = entries.Select(e => e.Date).Concat(trips.Select(x => x.Trip.Date)).ToList();

            var inv = new Invoice
            {
                CustomerId = p.CustomerId, IssueDate = today, DueDate = today.AddDays(settings.PaymentTermDays), Status = InvoiceStatus.Draft,
                VatRegime = regime, DeliveryFrom = dates.Min().Date, DeliveryTo = dates.Max().Date,
                Language = lang, Notes = SettingsEndpoints.Note(lang), Lines = lines,
            };
            db.Invoices.Add(inv);
            await db.SaveChangesAsync();
            if (!await ClaimAsync(db, inv.Id, parts)) return Results.Conflict(new { error = InvoiceEndpoints.HoursTaken });
            if (!await TripEndpoints.ClaimAsync(db, inv.Id, tripParts)) return Results.Conflict(new { error = TripEndpoints.TripsTaken });
            db.Log("factuur", entries.Count > 0
                ? $"Conceptfactuur gemaakt van {Hours(entries.Sum(e => e.Minutes)):0.##} uur op {p.Name}{(trips.Count > 0 ? $" en {TripEndpoints.Km(trips.Sum(x => x.Trip.Kilometres))} km aan ritten" : "")}"
                : $"Conceptfactuur gemaakt van {TripEndpoints.Km(trips.Sum(x => x.Trip.Kilometres))} km aan ritten op {p.Name}");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.Created($"/api/invoices/{inv.Id}", new { id = inv.Id, number = inv.Number });
        });
    }
}
