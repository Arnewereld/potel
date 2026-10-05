using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public static class SettingsEndpoints
{
    public static async Task<Settings> GetAsync(AppDb db)
    {
        var s = await db.Settings.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (s is not null) return s;
        s = new Settings();
        db.Settings.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    // De tekst onderaan een factuur, met betaaltermijn en rekeningnummer.
    public static string PaymentNote(Settings s) =>
        $"Graag binnen {s.PaymentTermDays} dagen overmaken"
        + (string.IsNullOrWhiteSpace(s.Iban) ? "" : $" op {s.Iban} t.n.v. {s.CompanyName}")
        + " onder vermelding van het factuurnummer.";

    public static void MapSettings(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/settings");

        g.MapGet("/", async (AppDb db) => await GetAsync(db));

        g.MapPut("/", async (AppDb db, Settings input) =>
        {
            if (string.IsNullOrWhiteSpace(input.CompanyName)) return Results.BadRequest(new { error = "Bedrijfsnaam is verplicht" });
            if (input.DefaultHourlyRate < 0 || input.PaymentTermDays < 0 || input.WeeklyHoursTarget < 0 || input.YearlyHoursTarget < 0)
                return Results.BadRequest(new { error = "Getallen kunnen niet negatief zijn" });
            var s = await GetAsync(db);
            s.CompanyName = input.CompanyName.Trim(); s.OwnerName = input.OwnerName; s.Address = input.Address; s.City = input.City;
            s.Email = input.Email; s.Phone = input.Phone; s.Website = input.Website; s.Kvk = input.Kvk; s.Btw = input.Btw; s.Iban = input.Iban;
            s.DefaultHourlyRate = input.DefaultHourlyRate; s.PaymentTermDays = input.PaymentTermDays;
            s.WeeklyHoursTarget = input.WeeklyHoursTarget; s.YearlyHoursTarget = input.YearlyHoursTarget;
            db.Log("systeem", "Bedrijfsgegevens bijgewerkt");
            await db.SaveChangesAsync();
            return Results.Ok(s);
        });
    }
}
