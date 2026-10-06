using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public static class TimeEndpoints
{
    public record TimeEntryDto(
        int Id, int ProjectId, string ProjectName, string ProjectColor, string? CustomerName, DateTime Date, int Minutes,
        string Description, bool Billable, int? InvoiceId, string? InvoiceNumber);

    static string? Validate(AppDb db, TimeEntry input)
    {
        if (!db.Projects.Any(p => p.Id == input.ProjectId)) return "Kies een project";
        if (input.Minutes <= 0) return "Vul de tijd in";
        if (input.Minutes > 24 * 60) return "Meer dan 24 uur op één dag kan niet";
        return null;
    }

    static string Locked(AppDb db, TimeEntry t) =>
        $"Deze uren staan al op factuur {db.Invoices.Where(i => i.Id == t.InvoiceId).Select(i => i.Number).FirstOrDefault()}. Verwijder die factuur om ze weer vrij te geven.";

    public static void MapTime(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/time");

        g.MapGet("/", async (AppDb db, DateTime? from, DateTime? to, int? projectId, bool? open) =>
        {
            var q = db.TimeEntries.AsNoTracking();
            if (from is { } f) q = q.Where(t => t.Date >= f.Date);
            if (to is { } e) q = q.Where(t => t.Date <= e.Date);
            if (projectId is { } p) q = q.Where(t => t.ProjectId == p);
            if (open == true) q = q.Where(t => t.Billable && t.InvoiceId == null);

            var entries = await q.OrderByDescending(t => t.Date).ThenByDescending(t => t.Id).ToListAsync();
            var projects = await db.Projects.Include(x => x.Customer).AsNoTracking().ToDictionaryAsync(x => x.Id);
            var invoiceIds = entries.Where(t => t.InvoiceId != null).Select(t => t.InvoiceId!.Value).Distinct().ToList();
            var numbers = await db.Invoices.Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Number);

            return entries.Select(t =>
            {
                var pr = projects[t.ProjectId];
                return new TimeEntryDto(t.Id, t.ProjectId, pr.Name, pr.Color, pr.Customer?.Company ?? pr.Customer?.Name, t.Date, t.Minutes,
                    t.Description, t.Billable, t.InvoiceId, t.InvoiceId is { } iid ? numbers.GetValueOrDefault(iid) : null);
            });
        });

        g.MapPost("/", async (AppDb db, TimeEntry input) =>
        {
            if (Validate(db, input) is { } error) return Results.BadRequest(new { error });
            var clientId = string.IsNullOrWhiteSpace(input.ClientId) ? null : input.ClientId.Trim();
            if (clientId is { Length: > 64 }) return Results.BadRequest(new { error = "Ongeldig kenmerk voor deze boeking" });
            // Dezelfde timer twee keer gestopt (dubbelklik of een tweede tabblad): de eerste boeking telt.
            if (clientId is not null && await db.TimeEntries.FirstOrDefaultAsync(x => x.ClientId == clientId) is { } booked)
                return Results.Ok(booked);
            var t = new TimeEntry
            {
                ProjectId = input.ProjectId, Date = input.Date.Date, Minutes = input.Minutes,
                Description = input.Description?.Trim() ?? "", Billable = input.Billable, ClientId = clientId,
            };
            db.TimeEntries.Add(t);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException) when (clientId is not null)
            {
                // Tegelijk binnengekomen: de unieke index hield de tweede tegen.
                db.Entry(t).State = EntityState.Detached;
                if (await db.TimeEntries.AsNoTracking().FirstOrDefaultAsync(x => x.ClientId == clientId) is { } first) return Results.Ok(first);
                throw;
            }
            return Results.Created($"/api/time/{t.Id}", t);
        });

        g.MapPut("/{id:int}", async (AppDb db, int id, TimeEntry input) =>
        {
            var t = await db.TimeEntries.FindAsync(id);
            if (t is null) return Results.NotFound();
            if (t.InvoiceId is not null) return Results.Conflict(new { error = Locked(db, t) });
            if (Validate(db, input) is { } error) return Results.BadRequest(new { error });
            t.ProjectId = input.ProjectId; t.Date = input.Date.Date; t.Minutes = input.Minutes;
            t.Description = input.Description?.Trim() ?? ""; t.Billable = input.Billable;
            await db.SaveChangesAsync();
            return Results.Ok(t);
        });

        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var t = await db.TimeEntries.FindAsync(id);
            if (t is null) return Results.NotFound();
            if (t.InvoiceId is not null) return Results.Conflict(new { error = Locked(db, t) });
            db.TimeEntries.Remove(t);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
