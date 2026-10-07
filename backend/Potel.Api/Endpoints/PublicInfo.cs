using Potel.Api.Data;

namespace Potel.Api.Endpoints;

public class SubProcessor
{
    public string Name { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Location { get; set; } = "";
}

// Jouw eigen bedrijfsgegevens als eigenaar van het platform, uit de sectie "Platform" in appsettings.json.
// Ze staan onderaan de verkooppagina en in de voorwaarden, de privacyverklaring en de verwerkersovereenkomst.
public class PlatformInfo
{
    public string Name { get; set; } = "Potel";
    public string Company { get; set; } = "[Vul in: je bedrijfsnaam]";
    public string Address { get; set; } = "[Vul in: straat en huisnummer]";
    public string City { get; set; } = "[Vul in: postcode en plaats]";
    public string Email { get; set; } = "[Vul in: e-mailadres]";
    public string Phone { get; set; } = "";
    public string Kvk { get; set; } = "[Vul in: KvK-nummer]";
    public string VatId { get; set; } = "[Vul in: btw-id]";
    public List<SubProcessor> SubProcessors { get; set; } = [];

    static bool Placeholder(string? s) => string.IsNullOrWhiteSpace(s) || s.TrimStart().StartsWith('[');

    // Pas als alles is ingevuld, kun je echt verkopen.
    public bool Complete =>
        !new[] { Company, Address, City, Email, Kvk, VatId }.Any(Placeholder)
        && SubProcessors.Count > 0 && !SubProcessors.Any(p => Placeholder(p.Name) || Placeholder(p.Location));
}

public static class PublicInfoEndpoints
{
    public static void MapPublicInfo(this RouteGroupBuilder api)
    {
        // Voor de verkooppagina en de juridische pagina's, zonder in te loggen.
        api.MapGet("/public/platform", (IConfiguration config) =>
        {
            var p = config.GetSection("Platform").Get<PlatformInfo>() ?? new PlatformInfo();
            return Results.Ok(new
            {
                p.Name, p.Company, p.Address, p.City, p.Email, p.Phone, p.Kvk, p.VatId,
                subProcessors = p.SubProcessors, complete = p.Complete, termsVersion = Terms.Version,
            });
        }).AllowAnonymous();
    }
}
