using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Importing;

namespace Potel.Api.Endpoints;

// Importeren uit een ander pakket: klanten, projecten, uren uit Toggl Track, Clockify of Harvest, en oude facturen. De wizard stuurt
// bij elke stap het bestand opnieuw mee; de server onthoudt niets tot je echt importeert. Alleen een beheerder.
public static class ImportEndpoints
{
    static IResult Problem(string error, int status = StatusCodes.Status400BadRequest) => Results.Json(new { error }, statusCode: status);

    // Ruim boven 5 MB, omdat het bestand als base64 in JSON meekomt.
    const long MaxRequest = 8 * 1024 * 1024;

    static object BatchDto(ImportBatch b, DateTime utcNow) => new
    {
        b.Id, b.Kind, b.FileName, b.Source, b.Created, b.Updated, b.Skipped, b.CustomersCreated, b.ProjectsCreated, b.ImportedBy, b.ImportedAt,
        b.UndoneAt, b.UndoneBy, b.Kept, canUndo = Importer.CanUndo(b, utcNow), undoUntil = b.ImportedAt.Add(Importer.UndoWindow),
    };

    public static void MapImports(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/imports").RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // De imports van de laatste tijd, de nieuwste eerst, met of je ze nog ongedaan kunt maken.
        g.MapGet("/", async (AppDb db, BusinessClock clock) =>
        {
            var batches = await db.ImportBatches.AsNoTracking().OrderByDescending(b => b.Id).Take(20).ToListAsync();
            return Results.Ok(batches.Select(b => BatchDto(b, clock.UtcNow)));
        });

        // Stap 2: het bestand lezen. Je krijgt de kolommen, de eerste regels en per veld de kolom die we herkennen.
        g.MapPost("/preview", (ImportFileRequest req) =>
        {
            var kind = req.Kind?.Trim() ?? "";
            if (!ImportKinds.All.Contains(kind)) return Problem(Importer.UnknownKind);
            var (table, error) = Importer.Read(kind, req.Data);
            if (table is null) return Problem(error!);
            return Results.Ok(new
            {
                kind, fileName = Importer.FileName(req.FileName), source = ImportFields.Source(kind, table.Headers), rows = table.Rows.Count,
                headerLine = table.HeaderLine, headers = table.Headers,
                sample = table.Rows.Take(5).Select(r => table.Headers.Select((_, i) => r.Cell(i)).ToList()),
                fields = ImportFields.ByKind[kind].Select(f => new { f.Key, f.Label, f.Required }),
                mapping = ImportFields.Guess(kind, table.Headers),
            });
        }).WithMetadata(new RequestSizeLimitAttribute(MaxRequest));

        // Stap 3 en 4: controleren zonder iets op te slaan. Fouten per regel, dubbele regels en de namen die een klant of project nodig hebben.
        g.MapPost("/check", async (AppDb db, BusinessClock clock, ImportFileRequest req) =>
        {
            var (check, error) = await Importer.CheckAsync(db, clock, req);
            return check is null ? Problem(error!) : Results.Ok(check);
        }).WithMetadata(new RequestSizeLimitAttribute(MaxRequest));

        // Stap 5: importeren. Alles of niets; lukt het, dan kun je het 24 uur lang ongedaan maken.
        g.MapPost("/", async (AppDb db, BusinessClock clock, ClaimsPrincipal user, ImportFileRequest req) =>
        {
            var me = user.UserId() is { } id ? await db.Users.FindAsync(id) : null;
            if (me is null) return Results.Unauthorized();
            var result = await Importer.RunAsync(db, clock, req, me);
            if (result.Error is { } error) return Problem(error);
            if (result.Refused is { } check)
            {
                var message = check.ErrorCount > 0
                    ? $"Er {(check.ErrorCount == 1 ? "staat 1 fout" : $"staan {check.ErrorCount} fouten")} in het bestand. Verbeter {(check.ErrorCount == 1 ? "die" : "ze")} en probeer het opnieuw; er is nog niets geïmporteerd."
                    : check.NeedsChoice
                        ? "Kies eerst bij elke naam uit het bestand een bestaande klant of project, of bevestig dat Potel een nieuwe aanmaakt."
                        : "Er valt niets te importeren: alles uit dit bestand staat al in Potel.";
                return Results.Json(new { error = message, check }, statusCode: StatusCodes.Status400BadRequest);
            }
            return Results.Ok(BatchDto(result.Batch!, clock.UtcNow));
        }).WithMetadata(new RequestSizeLimitAttribute(MaxRequest));

        // Een import ongedaan maken: tot 24 uur na het importeren, en alleen wat sindsdien niet gebruikt is.
        g.MapPost("/{id:int}/undo", async (AppDb db, BusinessClock clock, ClaimsPrincipal user, int id) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            var batch = await db.ImportBatches.FirstOrDefaultAsync(b => b.Id == id);
            if (batch is null) return Results.NotFound();
            if (batch.UndoneAt is not null) return Problem("Deze import is al ongedaan gemaakt.", StatusCodes.Status409Conflict);
            if (!Importer.CanUndo(batch, clock.UtcNow))
                return Problem("Een import kun je tot 24 uur na het importeren ongedaan maken. Deze is ouder; pas de gegevens zelf aan of verwijder ze.", StatusCodes.Status409Conflict);
            var name = user.FindFirstValue(ClaimTypes.Name) ?? "Iemand";
            var result = await Importer.UndoAsync(db, batch, clock.UtcNow, name);
            await tx.CommitAsync();
            return Results.Ok(new { result.Removed, result.Kept, keptReasons = result.KeptReasons, batch = BatchDto(batch, clock.UtcNow) });
        });

        // Facturen uit je vorige pakket: alleen bekijken. Ook voor een boekhouder, net als je gewone facturen.
        api.MapGet("/imported-invoices", async (AppDb db, int? year) =>
        {
            var q = db.ImportedInvoices.AsNoTracking().Include(i => i.Customer).AsQueryable();
            if (year is { } y) q = q.Where(i => i.IssueDate >= new DateTime(y, 1, 1) && i.IssueDate < new DateTime(y + 1, 1, 1));
            var list = await q.OrderByDescending(i => i.IssueDate).ThenByDescending(i => i.Id).ToListAsync();
            return Results.Ok(list.Select(i => new
            {
                i.Id, i.Number, i.IssueDate, i.CustomerId,
                customer = i.Customer is null ? null : string.IsNullOrWhiteSpace(i.Customer.Company) ? i.Customer.Name : i.Customer.Company,
                i.Subtotal, i.Vat, i.Total, i.PaidAt, i.Source,
            }));
        }).ForAccountant();
    }
}
