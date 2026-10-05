using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public static class ModuleEndpoints
{
    public static void MapModules(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/modules");

        g.MapGet("/", async (AppDb db) => await db.CustomModules.OrderBy(m => m.Name).ToListAsync());

        g.MapPost("/", async (AppDb db, CustomModule input) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Naam is verplicht" });
            input.Id = 0;
            db.CustomModules.Add(input);
            db.Log("module", $"Module {input.Name} aangemaakt");
            await db.SaveChangesAsync();
            return Results.Created($"/api/modules/{input.Id}", input);
        });

        g.MapPut("/{id:int}", async (AppDb db, int id, CustomModule input) =>
        {
            var m = await db.CustomModules.FindAsync(id);
            if (m is null) return Results.NotFound();
            m.Name = input.Name; m.Icon = input.Icon; m.Color = input.Color; m.FieldsJson = input.FieldsJson;
            await db.SaveChangesAsync();
            return Results.Ok(m);
        });

        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var m = await db.CustomModules.FindAsync(id);
            if (m is null) return Results.NotFound();
            db.CustomRecords.RemoveRange(db.CustomRecords.Where(r => r.ModuleId == id));
            db.CustomModules.Remove(m);
            db.Log("module", $"Module {m.Name} verwijderd");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/{id:int}/records", async (AppDb db, int id) =>
            await db.CustomRecords.Where(r => r.ModuleId == id).OrderByDescending(r => r.Id).ToListAsync());

        g.MapPost("/{id:int}/records", async (AppDb db, int id, CustomRecord input) =>
        {
            if (!await db.CustomModules.AnyAsync(m => m.Id == id)) return Results.NotFound();
            var r = new CustomRecord { ModuleId = id, DataJson = input.DataJson };
            db.CustomRecords.Add(r);
            await db.SaveChangesAsync();
            return Results.Created($"/api/modules/{id}/records/{r.Id}", r);
        });

        g.MapPut("/{id:int}/records/{recordId:int}", async (AppDb db, int id, int recordId, CustomRecord input) =>
        {
            var r = await db.CustomRecords.FirstOrDefaultAsync(x => x.Id == recordId && x.ModuleId == id);
            if (r is null) return Results.NotFound();
            r.DataJson = input.DataJson;
            await db.SaveChangesAsync();
            return Results.Ok(r);
        });

        g.MapDelete("/{id:int}/records/{recordId:int}", async (AppDb db, int id, int recordId) =>
        {
            var r = await db.CustomRecords.FirstOrDefaultAsync(x => x.Id == recordId && x.ModuleId == id);
            if (r is null) return Results.NotFound();
            db.CustomRecords.Remove(r);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
