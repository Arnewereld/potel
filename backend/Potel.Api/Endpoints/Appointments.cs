using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public static class AppointmentEndpoints
{
    // Een klant mag leeg blijven, maar als hij is gekozen moet hij in deze werkruimte bestaan.
    static async Task<string?> Validate(AppDb db, Appointment input)
    {
        if (string.IsNullOrWhiteSpace(input.Title)) return "Titel is verplicht";
        if (input.End < input.Start) return "Einde ligt voor het begin";
        if (input.CustomerId is { } cid && !await db.Customers.AnyAsync(c => c.Id == cid)) return "Deze klant bestaat niet. Kies een andere klant of laat het veld leeg.";
        return null;
    }

    public static void MapAppointments(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/appointments");

        g.MapGet("/", async (AppDb db, DateTime? from, DateTime? to) =>
        {
            var q = db.Appointments.AsQueryable();
            if (from is { } f) q = q.Where(a => a.End >= f);
            if (to is { } t) q = q.Where(a => a.Start <= t);
            return await q.OrderBy(a => a.Start).ToListAsync();
        });

        g.MapPost("/", async (AppDb db, Appointment input) =>
        {
            if (await Validate(db, input) is { } error) return Results.BadRequest(new { error });
            input.Id = 0;
            db.Appointments.Add(input);
            db.Log("planning", $"{input.Title} ingepland op {input.Start:dd-MM HH:mm}");
            await db.SaveChangesAsync();
            return Results.Created($"/api/appointments/{input.Id}", input);
        });

        g.MapPut("/{id:int}", async (AppDb db, int id, Appointment input) =>
        {
            var a = await db.Appointments.FindAsync(id);
            if (a is null) return Results.NotFound();
            if (await Validate(db, input) is { } error) return Results.BadRequest(new { error });
            if (!a.Done && input.Done) db.Log("planning", $"{a.Title} afgerond");
            a.Title = input.Title; a.Start = input.Start; a.End = input.End; a.Kind = input.Kind;
            a.CustomerId = input.CustomerId; a.Location = input.Location; a.Notes = input.Notes; a.Done = input.Done;
            await db.SaveChangesAsync();
            return Results.Ok(a);
        });

        g.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var a = await db.Appointments.FindAsync(id);
            if (a is null) return Results.NotFound();
            db.Appointments.Remove(a);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
