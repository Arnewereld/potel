using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Workflows;

namespace Potel.Api.Endpoints;

public record StatusRequest(string Status, DateTime? PaidAt);
public record AddHoursRequest(int ProjectId, bool Detailed);

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
        if (input.DueDate < input.IssueDate) return "De vervaldatum ligt voor de factuurdatum";
        if (input.ReverseCharge && string.IsNullOrWhiteSpace(db.Customers.Where(c => c.Id == input.CustomerId).Select(c => c.VatNumber).FirstOrDefault()))
            return "Voor btw verlegd moet het btw-nummer van de klant bekend zijn. Vul het in bij de klant.";
        return null;
    }

    // Bij btw verlegd rekent de afnemer de btw af; op de factuur zelf staat dan 0%.
    static List<InvoiceLine> CopyLines(IEnumerable<InvoiceLine> lines, bool reverseCharge = false, decimal sign = 1) =>
        lines.Select(l => new InvoiceLine
        {
            Description = l.Description, Quantity = sign * l.Quantity, Unit = string.IsNullOrWhiteSpace(l.Unit) ? "stuk" : l.Unit.Trim(),
            UnitPrice = l.UnitPrice, VatRate = reverseCharge ? 0 : l.VatRate,
        }).ToList();

    // Zet de status en houdt bij wanneer er betaald is. Geeft true terug als de factuur net betaald is.
    static bool SetStatus(AppDb db, Invoice inv, string status)
    {
        var becamePaid = inv.Status != "betaald" && status == "betaald";
        if (inv.Status != status && inv.Id != 0) db.Log("factuur", $"Factuur {inv.Number}: {inv.Status} → {status}");
        inv.Status = status;
        inv.PaidAt = status == "betaald" ? inv.PaidAt ?? DateTime.Today : null;
        return becamePaid;
    }

    static IQueryable<Invoice> Full(AppDb db) => db.Invoices.Include(i => i.Lines).Include(i => i.Customer);

    // Een nieuwe conceptfactuur op basis van een bestaande, met verse datums en nummer.
    static async Task<Invoice> CopyAsync(AppDb db, Invoice source, decimal sign)
    {
        var settings = await SettingsEndpoints.GetAsync(db);
        var today = DateTime.Today;
        return new Invoice
        {
            Number = await NextNumber(db), CustomerId = source.CustomerId, IssueDate = today, DueDate = today.AddDays(settings.PaymentTermDays),
            Status = "concept", Reference = source.Reference, ReverseCharge = source.ReverseCharge,
            Notes = sign < 0 ? $"Creditnota voor factuur {source.Number} van {source.IssueDate:dd-MM-yyyy}." : source.Notes,
            Lines = CopyLines(source.Lines, source.ReverseCharge, sign),
        };
    }

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
                CustomerId = input.CustomerId, IssueDate = input.IssueDate, DueDate = input.DueDate, Reference = input.Reference,
                ReverseCharge = input.ReverseCharge, Notes = input.Notes, Lines = CopyLines(input.Lines, input.ReverseCharge),
            };
            SetStatus(db, inv, input.Status);
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
            if (inv.CustomerId != input.CustomerId && await db.TimeEntries.AnyAsync(t => t.InvoiceId == id))
                return Results.BadRequest(new { error = "Op deze factuur staan uren van een project; de klant kan niet meer veranderen." });
            var becamePaid = SetStatus(db, inv, input.Status);
            if (input.Status == "betaald" && input.PaidAt is { } paidAt) inv.PaidAt = paidAt.Date;
            inv.Number = number; inv.CustomerId = input.CustomerId; inv.IssueDate = input.IssueDate; inv.DueDate = input.DueDate;
            inv.Reference = input.Reference; inv.ReverseCharge = input.ReverseCharge; inv.Notes = input.Notes;
            db.InvoiceLines.RemoveRange(inv.Lines);
            inv.Lines = CopyLines(input.Lines, input.ReverseCharge);
            await db.SaveChangesAsync();
            if (becamePaid) await TriggerPaid(db, engine, inv);
            return Results.Ok(inv);
        });

        // Alleen de status wijzigen, bijvoorbeeld "Markeer als betaald" vanuit de lijst.
        g.MapPost("/{id:int}/status", async (AppDb db, WorkflowEngine engine, int id, StatusRequest req) =>
        {
            var inv = await db.Invoices.FindAsync(id);
            if (inv is null) return Results.NotFound();
            if (!InvoiceStatus.All.Contains(req.Status)) return Results.BadRequest(new { error = "Onbekende status" });
            var becamePaid = SetStatus(db, inv, req.Status);
            if (req.Status == "betaald" && req.PaidAt is { } paidAt) inv.PaidAt = paidAt.Date;
            await db.SaveChangesAsync();
            if (becamePaid) await TriggerPaid(db, engine, inv);
            await MarkOverdue(db);
            return Results.Ok(await Full(db).FirstAsync(i => i.Id == id));
        });

        g.MapPost("/{id:int}/duplicate", async (AppDb db, int id) =>
        {
            var source = await Full(db).AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
            if (source is null) return Results.NotFound();
            var inv = await CopyAsync(db, source, 1);
            db.Invoices.Add(inv);
            db.Log("factuur", $"Factuur {inv.Number} gemaakt als kopie van {source.Number}");
            await db.SaveChangesAsync();
            return Results.Created($"/api/invoices/{inv.Id}", inv);
        });

        // Creditnota: dezelfde regels met negatieve aantallen.
        g.MapPost("/{id:int}/credit", async (AppDb db, int id) =>
        {
            var source = await Full(db).AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
            if (source is null) return Results.NotFound();
            if (source.Status == "concept") return Results.BadRequest(new { error = "Een concept kun je gewoon aanpassen of verwijderen." });
            var inv = await CopyAsync(db, source, -1);
            db.Invoices.Add(inv);
            db.Log("factuur", $"Creditnota {inv.Number} gemaakt voor factuur {source.Number}");
            await db.SaveChangesAsync();
            return Results.Created($"/api/invoices/{inv.Id}", inv);
        });

        // Zet de open uren van een project van dezelfde klant erbij op deze conceptfactuur.
        g.MapPost("/{id:int}/hours", async (AppDb db, int id, AddHoursRequest req) =>
        {
            var inv = await db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id);
            if (inv is null) return Results.NotFound();
            if (inv.Status != "concept") return Results.BadRequest(new { error = "Uren toevoegen kan alleen op een concept" });
            var project = await db.Projects.FindAsync(req.ProjectId);
            if (project is null || project.CustomerId != inv.CustomerId) return Results.BadRequest(new { error = "Dit project hoort niet bij de klant van deze factuur" });
            if (project.Billing != Billing.Hourly) return Results.BadRequest(new { error = "Dit project heeft een vaste prijs" });
            var entries = await ProjectEndpoints.OpenEntries(db, project.Id);
            if (entries.Count == 0) return Results.BadRequest(new { error = "Er zijn geen open uren op dit project" });
            // Een leeg beginregeltje van een nieuwe factuur hoeft niet te blijven staan.
            db.InvoiceLines.RemoveRange(inv.Lines.Where(l => string.IsNullOrWhiteSpace(l.Description) || (l.Description == "-" && l.UnitPrice == 0)));
            foreach (var line in CopyLines(ProjectEndpoints.HourLines(project, entries, req.Detailed), inv.ReverseCharge)) inv.Lines.Add(line);
            foreach (var e in entries) e.InvoiceId = inv.Id;
            db.Log("factuur", $"{ProjectEndpoints.Hours(entries.Sum(e => e.Minutes)):0.##} uur van {project.Name} op factuur {inv.Number} gezet");
            await db.SaveChangesAsync();
            return Results.Ok(await Full(db).FirstAsync(i => i.Id == id));
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
