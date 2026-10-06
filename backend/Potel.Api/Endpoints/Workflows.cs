using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Workflows;

namespace Potel.Api.Endpoints;

public record RunRequest(int? LeadId, int? CustomerId, int? InvoiceId);

public static class WorkflowEndpoints
{
    // Deze blokken sturen gegevens naar buiten. Een werkstroom met zo'n blok maakt of wijzigt alleen een beheerder.
    public static readonly string[] AdminOnlyBlocks = ["action.email", "action.webhook"];

    static bool NeedsAdmin(string? graphJson) => Graph.Parse(graphJson ?? "").Nodes.Any(n => AdminOnlyBlocks.Contains(n.Type));

    static IResult AdminOnly() =>
        Results.Json(new { error = "Werkstromen die e-mail sturen of een webhook aanroepen, kan alleen een beheerder maken of wijzigen." }, statusCode: 403);

    public static void MapWorkflows(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/workflows");

        g.MapGet("/", async (AppDb db) => await db.Workflows.OrderByDescending(w => w.UpdatedAt).ToListAsync());

        g.MapGet("/{id:int}", async (AppDb db, int id) =>
            await db.Workflows.FindAsync(id) is { } w ? Results.Ok(w) : Results.NotFound());

        g.MapPost("/", async (AppDb db, ClaimsPrincipal user, Workflow input) =>
        {
            if (!user.IsInRole(Roles.Admin) && NeedsAdmin(input.GraphJson)) return AdminOnly();
            var w = new Workflow { Name = string.IsNullOrWhiteSpace(input.Name) ? "Nieuwe werkstroom" : input.Name, GraphJson = input.GraphJson };
            db.Workflows.Add(w);
            db.Log("werkstroom", $"Werkstroom {w.Name} aangemaakt");
            await db.SaveChangesAsync();
            return Results.Created($"/api/workflows/{w.Id}", w);
        });

        g.MapPut("/{id:int}", async (AppDb db, ClaimsPrincipal user, int id, Workflow input) =>
        {
            var w = await db.Workflows.FindAsync(id);
            if (w is null) return Results.NotFound();
            if (!user.IsInRole(Roles.Admin) && (NeedsAdmin(w.GraphJson) || NeedsAdmin(input.GraphJson))) return AdminOnly();
            w.Name = input.Name; w.Active = input.Active; w.GraphJson = input.GraphJson; w.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(w);
        });

        // Handmatig uitvoeren, eventueel met een lead, klant of factuur als startgegevens.
        g.MapPost("/{id:int}/run", async (AppDb db, WorkflowEngine engine, int id, RunRequest req) =>
        {
            var w = await db.Workflows.FindAsync(id);
            if (w is null) return Results.NotFound();
            var starts = Graph.Parse(w.GraphJson).Nodes.Where(n => n.Type.StartsWith("trigger.")).Select(n => n.Id).ToList();
            if (starts.Count == 0) return Results.BadRequest(new { error = "Deze werkstroom heeft nog geen trigger" });

            // Alleen gegevens uit de eigen werkruimte; een onbekend nummer is een fout, geen lege run.
            var ctx = new Dictionary<string, string>();
            if (req.CustomerId is { } cid)
            {
                if (await db.Customers.FindAsync(cid) is not { } c) return Results.BadRequest(new { error = "Deze klant bestaat niet (meer)" });
                WorkflowContext.AddCustomer(ctx, c);
            }
            if (req.LeadId is { } lid)
            {
                if (await db.Leads.FindAsync(lid) is not { } l) return Results.BadRequest(new { error = "Deze lead bestaat niet (meer)" });
                WorkflowContext.AddLead(ctx, l);
            }
            if (req.InvoiceId is { } iid)
            {
                if (await db.Invoices.Include(i => i.Lines).Include(i => i.Customer).FirstOrDefaultAsync(i => i.Id == iid) is not { } inv)
                    return Results.BadRequest(new { error = "Deze factuur bestaat niet (meer)" });
                WorkflowContext.AddInvoice(ctx, inv);
            }

            var run = await engine.RunAsync(w, starts, ctx, "Handmatig gestart");
            return Results.Ok(run);
        });

        g.MapGet("/{id:int}/runs", async (AppDb db, int id) =>
            await db.WorkflowRuns.Where(r => r.WorkflowId == id).OrderByDescending(r => r.Id).Take(25).ToListAsync());

        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var w = await db.Workflows.FindAsync(id);
            if (w is null) return Results.NotFound();
            db.WorkflowRuns.RemoveRange(db.WorkflowRuns.Where(r => r.WorkflowId == id));
            db.Workflows.Remove(w);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
