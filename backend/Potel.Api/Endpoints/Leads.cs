using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Workflows;

namespace Potel.Api.Endpoints;

public static class LeadEndpoints
{
    // Zet een lead om naar een klant en markeer hem als gewonnen.
    public static async Task<Customer> ConvertAsync(AppDb db, Lead l)
    {
        if (l.CustomerId is { } existing && await db.Customers.FindAsync(existing) is { } found) return found;
        var c = new Customer { Name = l.Name, Company = l.Company, Email = l.Email, Phone = l.Phone, Notes = l.Notes };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        l.CustomerId = c.Id;
        l.Status = "gewonnen";
        db.Log("lead", $"Lead {l.Name} omgezet naar klant");
        await db.SaveChangesAsync();
        return c;
    }

    public static void MapLeads(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/leads");

        g.MapGet("/", async (AppDb db) => await db.Leads.OrderByDescending(l => l.CreatedAt).ToListAsync());

        g.MapPost("/", async (AppDb db, WorkflowEngine engine, Lead input) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Naam is verplicht" });
            if (input.CustomerId is { } cid && !await db.Customers.AnyAsync(c => c.Id == cid))
                return Results.BadRequest(new { error = "Deze klant bestaat niet. Kies een andere klant of laat het veld leeg." });
            if (!LeadStatus.All.Contains(input.Status)) input.Status = "nieuw";
            input.Id = 0;
            input.CreatedAt = DateTime.UtcNow;
            db.Leads.Add(input);
            db.Log("lead", $"Lead {input.Name} toegevoegd");
            await db.SaveChangesAsync();
            var ctx = new Dictionary<string, string>();
            WorkflowContext.AddLead(ctx, input);
            await engine.TriggerAsync("trigger.lead", ctx, $"Nieuwe lead: {input.Name}");
            await db.Entry(input).ReloadAsync();
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

        g.MapPost("/{id:int}/convert", async (AppDb db, int id) =>
            await db.Leads.FindAsync(id) is { } l ? Results.Ok(await ConvertAsync(db, l)) : Results.NotFound());

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
