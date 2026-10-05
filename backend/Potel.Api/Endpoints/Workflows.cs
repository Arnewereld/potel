using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public static class WorkflowEndpoints
{
    public static void MapWorkflows(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/workflows");

        g.MapGet("/", async (AppDb db) => await db.Workflows.OrderByDescending(w => w.UpdatedAt).ToListAsync());

        g.MapGet("/{id:int}", async (AppDb db, int id) =>
            await db.Workflows.FindAsync(id) is { } w ? Results.Ok(w) : Results.NotFound());

        g.MapPost("/", async (AppDb db, Workflow input) =>
        {
            var w = new Workflow { Name = string.IsNullOrWhiteSpace(input.Name) ? "Nieuwe werkstroom" : input.Name, GraphJson = input.GraphJson };
            db.Workflows.Add(w);
            db.Log("werkstroom", $"Werkstroom {w.Name} aangemaakt");
            await db.SaveChangesAsync();
            return Results.Created($"/api/workflows/{w.Id}", w);
        });

        g.MapPut("/{id:int}", async (AppDb db, int id, Workflow input) =>
        {
            var w = await db.Workflows.FindAsync(id);
            if (w is null) return Results.NotFound();
            w.Name = input.Name; w.Active = input.Active; w.GraphJson = input.GraphJson; w.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(w);
        });

        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var w = await db.Workflows.FindAsync(id);
            if (w is null) return Results.NotFound();
            db.Workflows.Remove(w);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
