using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Workflows;

namespace Potel.Api.Endpoints;

public static class CustomerEndpoints
{
    public static void MapCustomers(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/customers");

        g.MapGet("/", async (AppDb db) => await db.Customers.OrderBy(c => c.Name).ToListAsync());

        g.MapGet("/{id:int}", async (AppDb db, int id) =>
            await db.Customers.FindAsync(id) is { } c ? Results.Ok(c) : Results.NotFound());

        g.MapPost("/", async (AppDb db, WorkflowEngine engine, Customer input) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Naam is verplicht" });
            input.Id = 0;
            input.CreatedAt = DateTime.UtcNow;
            db.Customers.Add(input);
            db.Log("klant", $"Klant {input.Name} toegevoegd");
            await db.SaveChangesAsync();
            var ctx = new Dictionary<string, string>();
            WorkflowContext.AddCustomer(ctx, input);
            await engine.TriggerAsync("trigger.customer", ctx, $"Nieuwe klant: {input.Name}");
            return Results.Created($"/api/customers/{input.Id}", input);
        });

        g.MapPut("/{id:int}", async (AppDb db, int id, Customer input) =>
        {
            var c = await db.Customers.FindAsync(id);
            if (c is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Naam is verplicht" });
            c.Name = input.Name; c.Company = input.Company; c.Email = input.Email; c.Phone = input.Phone;
            c.Address = input.Address; c.City = input.City; c.Notes = input.Notes;
            db.Log("klant", $"Klant {c.Name} bijgewerkt");
            await db.SaveChangesAsync();
            return Results.Ok(c);
        });

        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var c = await db.Customers.FindAsync(id);
            if (c is null) return Results.NotFound();
            if (await db.Invoices.AnyAsync(i => i.CustomerId == id))
                return Results.Conflict(new { error = "Deze klant heeft facturen en kan niet verwijderd worden" });
            if (await db.Projects.AnyAsync(p => p.CustomerId == id))
                return Results.Conflict(new { error = "Deze klant heeft projecten. Verwijder die eerst." });
            db.Customers.Remove(c);
            db.Log("klant", $"Klant {c.Name} verwijderd");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
