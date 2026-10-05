using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Workflows;

namespace Potel.Api.Endpoints;

public static class InvoiceEndpoints
{
    public static decimal Total(Invoice i) => i.Lines.Sum(l => Math.Round(l.Quantity * l.UnitPrice * (1 + l.VatRate / 100), 2));

    public static async Task MarkOverdue(AppDb db)
    {
        var today = DateTime.UtcNow.Date;
        var overdue = await db.Invoices.Where(i => i.Status == "verzonden" && i.DueDate < today).ToListAsync();
        if (overdue.Count == 0) return;
        foreach (var i in overdue) { i.Status = "verlopen"; db.Log("factuur", $"Factuur {i.Number} is verlopen"); }
        await db.SaveChangesAsync();
    }

    public static async Task<string> NextNumber(AppDb db)
    {
        var prefix = $"{DateTime.UtcNow.Year}-";
        var numbers = await db.Invoices.Where(i => i.Number.StartsWith(prefix)).Select(i => i.Number).ToListAsync();
        var max = numbers.Select(n => int.TryParse(n[prefix.Length..], out var x) ? x : 0).DefaultIfEmpty(0).Max();
        return $"{prefix}{max + 1:0000}";
    }

    static string? Validate(AppDb db, Invoice input)
    {
        if (!db.Customers.Any(c => c.Id == input.CustomerId)) return "Kies een klant";
        if (!InvoiceStatus.All.Contains(input.Status)) return "Onbekende status";
        if (input.Lines.Count == 0) return "Voeg minstens één regel toe";
        if (input.Lines.Any(l => string.IsNullOrWhiteSpace(l.Description))) return "Elke regel heeft een omschrijving nodig";
        return null;
    }

    static List<InvoiceLine> CopyLines(IEnumerable<InvoiceLine> lines) =>
        lines.Select(l => new InvoiceLine { Description = l.Description, Quantity = l.Quantity, UnitPrice = l.UnitPrice, VatRate = l.VatRate }).ToList();

    // Start werkstromen met de trigger "Factuur betaald".
    static async Task TriggerPaid(AppDb db, WorkflowEngine engine, Invoice inv)
    {
        var full = await db.Invoices.Include(i => i.Lines).Include(i => i.Customer).FirstAsync(i => i.Id == inv.Id);
        var ctx = new Dictionary<string, string>();
        WorkflowContext.AddInvoice(ctx, full);
        await engine.TriggerAsync("trigger.paid", ctx, $"Factuur {full.Number} betaald");
    }

    public static void MapInvoices(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/invoices");

        g.MapGet("/", async (AppDb db) =>
        {
            await MarkOverdue(db);
            return await db.Invoices.Include(i => i.Lines).Include(i => i.Customer).OrderByDescending(i => i.Number).ToListAsync();
        });

        g.MapGet("/next-number", async (AppDb db) => new { number = await NextNumber(db) });

        g.MapGet("/{id:int}", async (AppDb db, int id) =>
            await db.Invoices.Include(i => i.Lines).Include(i => i.Customer).FirstOrDefaultAsync(i => i.Id == id) is { } inv
                ? Results.Ok(inv) : Results.NotFound());

        g.MapPost("/", async (AppDb db, WorkflowEngine engine, Invoice input) =>
        {
            if (Validate(db, input) is { } error) return Results.BadRequest(new { error });
            var inv = new Invoice
            {
                Number = string.IsNullOrWhiteSpace(input.Number) ? await NextNumber(db) : input.Number.Trim(),
                CustomerId = input.CustomerId, IssueDate = input.IssueDate, DueDate = input.DueDate,
                Status = input.Status, Notes = input.Notes, Lines = CopyLines(input.Lines),
            };
            if (await db.Invoices.AnyAsync(i => i.Number == inv.Number)) return Results.Conflict(new { error = "Dit factuurnummer bestaat al" });
            db.Invoices.Add(inv);
            db.Log("factuur", $"Factuur {inv.Number} aangemaakt");
            await db.SaveChangesAsync();
            if (inv.Status == "betaald") await TriggerPaid(db, engine, inv);
            return Results.Created($"/api/invoices/{inv.Id}", inv);
        });

        g.MapPut("/{id:int}", async (AppDb db, WorkflowEngine engine, int id, Invoice input) =>
        {
            var inv = await db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id);
            if (inv is null) return Results.NotFound();
            if (Validate(db, input) is { } error) return Results.BadRequest(new { error });
            var number = string.IsNullOrWhiteSpace(input.Number) ? inv.Number : input.Number.Trim();
            if (await db.Invoices.AnyAsync(i => i.Number == number && i.Id != id)) return Results.Conflict(new { error = "Dit factuurnummer bestaat al" });
            var becamePaid = inv.Status != "betaald" && input.Status == "betaald";
            if (inv.Status != input.Status) db.Log("factuur", $"Factuur {inv.Number}: {inv.Status} → {input.Status}");
            inv.Number = number; inv.CustomerId = input.CustomerId; inv.IssueDate = input.IssueDate; inv.DueDate = input.DueDate;
            inv.Status = input.Status; inv.Notes = input.Notes;
            db.InvoiceLines.RemoveRange(inv.Lines);
            inv.Lines = CopyLines(input.Lines);
            await db.SaveChangesAsync();
            if (becamePaid) await TriggerPaid(db, engine, inv);
            return Results.Ok(inv);
        });

        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var inv = await db.Invoices.FindAsync(id);
            if (inv is null) return Results.NotFound();
            db.Invoices.Remove(inv);
            db.Log("factuur", $"Factuur {inv.Number} verwijderd");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
