using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public static class LeadEndpoints
{
    public static void MapLeads(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/leads");

        g.MapGet("/", async (AppDb db) => await db.Leads.OrderByDescending(l => l.CreatedAt).ToListAsync());

        g.MapPost("/", async (AppDb db, Lead input) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Naam is verplicht" });
            if (!LeadStatus.All.Contains(input.Status)) input.Status = "nieuw";
            input.Id = 0;
            input.CreatedAt = DateTime.UtcNow;
            db.Leads.Add(input);
            db.Log("lead", $"Lead {input.Name} toegevoegd");
            await db.SaveChangesAsync();
            return Results.Created($"/api/leads/{input.Id}", input);
        });

        g.MapPut("/{id:int}", async (AppDb db, int id, Lead input) =>
        {
            var l = await db.Leads.FindAsync(id);
            if (l is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Naam is verplicht" });
            if (!LeadStatus.All.Contains(input.Status)) return Results.BadRequest(new { error = "Onbekende status" });
            if (l.Status != input.Status) db.Log("lead", $"Lead {l.Name}: {l.Status} → {input.Status}");
            l.Name = input.Name; l.Company = input.Company; l.Email = input.Email; l.Phone = input.Phone;
            l.Value = input.Value; l.Status = input.Status; l.Source = input.Source; l.Notes = input.Notes;
            await db.SaveChangesAsync();
            return Results.Ok(l);
        });

        // Zet een lead om naar een klant en markeer hem als gewonnen.
        g.MapPost("/{id:int}/convert", async (AppDb db, int id) =>
        {
            var l = await db.Leads.FindAsync(id);
            if (l is null) return Results.NotFound();
            if (l.CustomerId is { } existing) return Results.Ok(await db.Customers.FindAsync(existing));
            var c = new Customer { Name = l.Name, Company = l.Company, Email = l.Email, Phone = l.Phone, Notes = l.Notes };
            db.Customers.Add(c);
            await db.SaveChangesAsync();
            l.CustomerId = c.Id;
            l.Status = "gewonnen";
            db.Log("lead", $"Lead {l.Name} omgezet naar klant");
            await db.SaveChangesAsync();
            return Results.Ok(c);
        });

        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var l = await db.Leads.FindAsync(id);
            if (l is null) return Results.NotFound();
            db.Leads.Remove(l);
            db.Log("lead", $"Lead {l.Name} verwijderd");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
