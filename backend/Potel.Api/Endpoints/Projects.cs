using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public record InvoiceHoursRequest(int[]? EntryIds, bool Detailed);

public static class ProjectEndpoints
{
    public static decimal Hours(int minutes) => Math.Round(minutes / 60m, 2);

    // Een project met de optelsommen van zijn uren erbij.
    public record ProjectDto(
        int Id, string Name, int CustomerId, string? CustomerName, string Status, string Billing, decimal HourlyRate, decimal FixedPrice,
        decimal? BudgetHours, string Color, string? RepoUrl, string? Description, DateTime? Deadline, DateTime CreatedAt,
        int MinutesTotal, int MinutesUnbilled, decimal UnbilledValue, DateTime? LastEntry);

    public static ProjectDto ToDto(Project p, IEnumerable<TimeEntry> entries)
    {
        var mine = entries.Where(e => e.ProjectId == p.Id).ToList();
        var unbilled = p.Billing == Billing.Hourly ? mine.Where(e => e.Billable && e.InvoiceId == null).Sum(e => e.Minutes) : 0;
        return new ProjectDto(
            p.Id, p.Name, p.CustomerId, p.Customer?.Company ?? p.Customer?.Name, p.Status, p.Billing, p.HourlyRate, p.FixedPrice,
            p.BudgetHours, p.Color, p.RepoUrl, p.Description, p.Deadline, p.CreatedAt,
            mine.Sum(e => e.Minutes), unbilled, Math.Round(Hours(unbilled) * p.HourlyRate, 2),
            mine.Count == 0 ? null : mine.Max(e => e.Date));
    }

    public static async Task<List<ProjectDto>> ListAsync(AppDb db, Func<IQueryable<Project>, IQueryable<Project>>? filter = null)
    {
        var q = db.Projects.Include(p => p.Customer).AsNoTracking();
        var projects = await (filter is null ? q : filter(q)).ToListAsync();
        var ids = projects.Select(p => p.Id).ToList();
        var entries = await db.TimeEntries.Where(e => ids.Contains(e.ProjectId)).AsNoTracking().ToListAsync();
        return projects.Select(p => ToDto(p, entries)).ToList();
    }

    // Factuurregels voor uren: één regel met het totaal of één regel per boeking.
    public static List<InvoiceLine> HourLines(Project p, List<TimeEntry> entries, bool detailed) => detailed
        ? entries.Select(e => new InvoiceLine
        {
            Description = $"{e.Date:dd-MM-yyyy} {(string.IsNullOrWhiteSpace(e.Description) ? p.Name : e.Description)}",
            Quantity = Hours(e.Minutes), Unit = "uur", UnitPrice = p.HourlyRate,
        }).ToList()
        : [new InvoiceLine
        {
            Description = $"{p.Name}: werkzaamheden {entries[0].Date:dd-MM} t/m {entries[^1].Date:dd-MM-yyyy}",
            Quantity = Hours(entries.Sum(e => e.Minutes)), Unit = "uur", UnitPrice = p.HourlyRate,
        }];

    // Open, factureerbare uren van een project; optioneel alleen de gekozen boekingen.
    public static Task<List<TimeEntry>> OpenEntries(AppDb db, int projectId, int[]? ids = null)
    {
        var q = db.TimeEntries.Where(t => t.ProjectId == projectId && t.Billable && t.InvoiceId == null);
        if (ids is { Length: > 0 }) q = q.Where(t => ids.Contains(t.Id));
        return q.OrderBy(t => t.Date).ThenBy(t => t.Id).ToListAsync();
    }

    static string? Validate(AppDb db, Project input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) return "Naam is verplicht";
        if (!db.Customers.Any(c => c.Id == input.CustomerId)) return "Kies een klant";
        if (!ProjectStatus.All.Contains(input.Status)) return "Onbekende status";
        if (!Billing.All.Contains(input.Billing)) return "Kies per uur of vaste prijs";
        if (input.HourlyRate < 0 || input.FixedPrice < 0 || input.BudgetHours < 0) return "Bedragen en uren kunnen niet negatief zijn";
        return null;
    }

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
                RepoUrl = input.RepoUrl, Description = input.Description, Deadline = input.Deadline,
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
            if (Validate(db, input) is { } error) return Results.BadRequest(new { error });
            if (p.Status != input.Status) db.Log("project", $"Project {p.Name}: {p.Status} → {input.Status}");
            p.Name = input.Name.Trim(); p.CustomerId = input.CustomerId; p.Status = input.Status; p.Billing = input.Billing;
            p.HourlyRate = input.HourlyRate; p.FixedPrice = input.FixedPrice; p.BudgetHours = input.BudgetHours; p.Color = input.Color;
            p.RepoUrl = input.RepoUrl; p.Description = input.Description; p.Deadline = input.Deadline;
            await db.SaveChangesAsync();
            return Results.Ok(p);
        });

        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var p = await db.Projects.FindAsync(id);
            if (p is null) return Results.NotFound();
            if (await db.TimeEntries.AnyAsync(t => t.ProjectId == id && t.InvoiceId != null))
                return Results.Conflict(new { error = "Dit project heeft gefactureerde uren. Zet het op afgerond in plaats van het te verwijderen." });
            db.Projects.Remove(p);
            db.Log("project", $"Project {p.Name} verwijderd");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Zet de open uren van een project op een nieuwe conceptfactuur.
        g.MapPost("/{id:int}/invoice", async (AppDb db, int id, InvoiceHoursRequest? req) =>
        {
            var p = await db.Projects.FindAsync(id);
            if (p is null) return Results.NotFound();
            if (p.Billing != Billing.Hourly)
                return Results.BadRequest(new { error = "Dit project heeft een vaste prijs; maak de factuur zelf aan." });

            var entries = await OpenEntries(db, id, req?.EntryIds);
            if (entries.Count == 0) return Results.BadRequest(new { error = "Er zijn geen open uren om te factureren" });

            var settings = await SettingsEndpoints.GetAsync(db);
            var today = DateTime.Today;
            var lines = HourLines(p, entries, req?.Detailed == true);

            var inv = new Invoice
            {
                Number = await InvoiceEndpoints.NextNumber(db), CustomerId = p.CustomerId, IssueDate = today,
                DueDate = today.AddDays(settings.PaymentTermDays), Status = "concept", Notes = SettingsEndpoints.DefaultNote, Lines = lines,
            };
            db.Invoices.Add(inv);
            await db.SaveChangesAsync();
            foreach (var e in entries) e.InvoiceId = inv.Id;
            db.Log("factuur", $"Factuur {inv.Number} gemaakt van {Hours(entries.Sum(e => e.Minutes)):0.##} uur op {p.Name}");
            await db.SaveChangesAsync();
            return Results.Created($"/api/invoices/{inv.Id}", new { id = inv.Id, number = inv.Number });
        });
    }
}
