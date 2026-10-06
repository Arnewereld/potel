using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public static partial class SettingsEndpoints
{
    public const int MaxLogoBytes = 300 * 1024;

    [GeneratedRegex(@"^data:image/(png|jpeg|webp);base64,([A-Za-z0-9+/]+={0,2})$")]
    private static partial Regex LogoDataUrl();

    [GeneratedRegex(@"^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    // Alleen PNG, JPG of WebP tot 300 KB, en de inhoud moet ook echt zo'n afbeelding zijn. Geen SVG: daar kan script in.
    static string? CheckLogo(string logo)
    {
        if (logo.Length > MaxLogoBytes * 4 / 3 + 64 || LogoDataUrl().Match(logo) is not { Success: true } m)
            return "Je logo moet een PNG-, JPG- of WebP-afbeelding zijn";
        byte[] bytes;
        try { bytes = Convert.FromBase64String(m.Groups[2].Value); }
        catch (FormatException) { return "Je logo moet een PNG-, JPG- of WebP-afbeelding zijn"; }
        if (bytes.Length > MaxLogoBytes) return "Je logo mag maximaal 300 KB zijn";
        var real = m.Groups[1].Value switch
        {
            "png" => bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            "jpeg" => bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }),
            _ => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
        };
        return real ? null : "Je logo moet een PNG-, JPG- of WebP-afbeelding zijn";
    }

    public static async Task<Settings> GetAsync(AppDb db)
    {
        var s = await db.Settings.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (s is not null) return s;
        s = new Settings();
        db.Settings.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    // De opmerking onder een nieuwe factuur. De betaalgegevens staan al in een eigen blok op de factuur.
    public const string DefaultNote = "Bedankt voor de fijne samenwerking!";

    public static void MapSettings(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/settings");

        g.MapGet("/", async (AppDb db) => await GetAsync(db));

        // Bedrijfsgegevens, IBAN en huisstijl komen op elke factuur; die past alleen een beheerder aan.
        g.MapPut("/", async (AppDb db, Settings input) =>
        {
            if (string.IsNullOrWhiteSpace(input.CompanyName)) return Results.BadRequest(new { error = "Bedrijfsnaam is verplicht" });
            if (input.DefaultHourlyRate < 0 || input.PaymentTermDays < 0 || input.WeeklyHoursTarget < 0 || input.YearlyHoursTarget < 0)
                return Results.BadRequest(new { error = "Getallen kunnen niet negatief zijn" });
            var logo = string.IsNullOrWhiteSpace(input.LogoDataUrl) ? null : input.LogoDataUrl.Trim();
            if (logo is not null && CheckLogo(logo) is { } logoError) return Results.BadRequest(new { error = logoError });
            var color = string.IsNullOrWhiteSpace(input.BrandColor) ? null : input.BrandColor.Trim();
            if (color is not null && !HexColor().IsMatch(color)) return Results.BadRequest(new { error = "Kies een accentkleur als #rrggbb, bijvoorbeeld #ff6d5a" });
            if (!VatRegimes.WorkspaceDefaults.Contains(input.VatRegime)) return Results.BadRequest(new { error = "Kies normaal of de kleineondernemersregeling (KOR) als standaard voor de btw" });
            var s = await GetAsync(db);
            s.CompanyName = input.CompanyName.Trim(); s.OwnerName = input.OwnerName; s.Address = input.Address; s.City = input.City;
            s.Email = input.Email; s.Phone = input.Phone; s.Website = input.Website; s.Kvk = input.Kvk; s.Btw = input.Btw; s.Iban = input.Iban;
            s.DefaultHourlyRate = input.DefaultHourlyRate; s.PaymentTermDays = input.PaymentTermDays; s.VatRegime = input.VatRegime;
            s.WeeklyHoursTarget = input.WeeklyHoursTarget; s.YearlyHoursTarget = input.YearlyHoursTarget;
            s.LogoDataUrl = logo;
            if (color is not null) s.BrandColor = color.ToLower();
            db.Log("systeem", "Bedrijfsgegevens bijgewerkt");
            await db.SaveChangesAsync();
            return Results.Ok(s);
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }
}
