using Microsoft.EntityFrameworkCore;
using Potel.Api.Banking;
using Potel.Api.Data;
using Potel.Api.Workflows;

namespace Potel.Api.Endpoints;

public static class CustomerEndpoints
{
    public const string IbanError = "Dit rekeningnummer klopt niet. Vul een IBAN in, zoals NL91 ABNA 0417 1643 00, of laat het veld leeg.";

    // Het rekeningnummer zonder spaties en in hoofdletters; leeg mag, een IBAN dat niet klopt niet.
    static bool TryIban(string? input, out string? iban)
    {
        iban = Ibans.Normalize(input);
        if (string.IsNullOrWhiteSpace(input)) return true;
        return Ibans.IsValid(iban);
    }

    public static void MapCustomers(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/customers");

        g.MapGet("/", async (AppDb db) => await db.Customers.OrderBy(c => c.Name).ToListAsync());

        g.MapGet("/{id:int}", async (AppDb db, int id) =>
            await db.Customers.FindAsync(id) is { } c ? Results.Ok(c) : Results.NotFound());

        g.MapPost("/", async (AppDb db, WorkflowEngine engine, Customer input) =>
        {
            if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Naam is verplicht" });
            if (!TryIban(input.Iban, out var iban)) return Results.BadRequest(new { error = IbanError });
            if (TripEndpoints.RateProblem(input.KilometreRate) is { } rateProblem) return Results.BadRequest(new { error = rateProblem });
            input.Iban = iban;
            input.KilometreRate = TripEndpoints.CleanRate(input.KilometreRate);
            input.Id = 0;
            input.CreatedAt = DateTime.UtcNow;
            input.Country = Countries.Normalize(input.Country);
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
            if (!TryIban(input.Iban, out var iban)) return Results.BadRequest(new { error = IbanError });
            if (TripEndpoints.RateProblem(input.KilometreRate) is { } rateProblem) return Results.BadRequest(new { error = rateProblem });
            c.Iban = iban;
            c.KilometreRate = TripEndpoints.CleanRate(input.KilometreRate);
            c.Name = input.Name; c.Company = input.Company; c.Email = input.Email; c.Phone = input.Phone;
            c.Address = input.Address; c.City = input.City; c.Country = Countries.Normalize(input.Country); c.VatNumber = input.VatNumber; c.Notes = input.Notes;
            c.RemindersOff = input.RemindersOff;
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
            if (await db.RecurringInvoices.AnyAsync(r => r.CustomerId == id))
                return Results.Conflict(new { error = "Deze klant heeft terugkerende facturen. Verwijder die eerst." });
            if (await db.Quotes.AnyAsync(q => q.CustomerId == id))
                return Results.Conflict(new { error = "Deze klant heeft offertes. Een verstuurde offerte bewaar je, dus deze klant kan niet verwijderd worden." });
            // Afspraken en leads blijven bestaan, maar wijzen niet meer naar een klant die er niet is.
            await db.Appointments.Where(a => a.CustomerId == id).ExecuteUpdateAsync(x => x.SetProperty(a => a.CustomerId, (int?)null));
            await db.Leads.Where(l => l.CustomerId == id).ExecuteUpdateAsync(x => x.SetProperty(l => l.CustomerId, (int?)null));
            db.Customers.Remove(c);
            db.Log("klant", $"Klant {c.Name} verwijderd");
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
