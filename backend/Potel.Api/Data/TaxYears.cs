namespace Potel.Api.Data;

// Bedragen voor de inkomstenbelasting per jaar, voor de indicatie op het jaaroverzicht. Alles op één plek: komt er een nieuw
// jaar bij of verandert er iets, pas het dan hier aan en zet de bron erbij. Null is: staat nog niet vast.
public record TaxYear(int Year, decimal SelfEmployedDeduction, decimal? StarterDeduction, decimal? SmeProfitExemption, string[] Sources, string? Note = null);

public static class TaxYears
{
    // Minstens zoveel uur per kalenderjaar aan je onderneming voor de zelfstandigenaftrek en startersaftrek.
    public const int HoursCriterion = 1225;
    public const string HoursSource =
        "https://www.belastingdienst.nl/wps/wcm/connect/bldcontentnl/belastingdienst/zakelijk/winst/inkomstenbelasting/inkomstenbelasting_voor_ondernemers/voorwaarden_urencriterium";
    // Bedrijfsmiddelen van € 450 of meer schrijf je af in plaats van ze in één keer af te trekken.
    public const decimal DepreciationThreshold = 450;
    public const string DepreciationSource =
        "https://www.belastingdienst.nl/wps/wcm/connect/bldcontentnl/belastingdienst/zakelijk/winst/inkomstenbelasting/inkomstenbelasting_voor_ondernemers/afschrijving/wat_is_afschrijven";

    const string Ib = "https://www.belastingdienst.nl/wps/wcm/connect/bldcontentnl/belastingdienst/zakelijk/winst/inkomstenbelasting";

    public static readonly TaxYear[] All =
    [
        new(2024, 3750, 2123, 13.31m,
        [
            $"{Ib}/verandering_inkomstenbelasting_vorige_jaren/veranderingen-inkomstenbelasting-2024/ondernemersaftrek-2024/zelfstandigenaftrek-2024",
            $"{Ib}/verandering_inkomstenbelasting_vorige_jaren/veranderingen-inkomstenbelasting-2024/mkb-winstvrijstelling-2024",
        ]),
        new(2025, 2470, 2123, 12.7m,
        [
            $"{Ib}/verandering_inkomstenbelasting_vorige_jaren/veranderingen-inkomstenbelasting-2025/ondernemersaftrek-2025/zelfstandigenaftrek-2025",
            $"{Ib}/verandering_inkomstenbelasting_vorige_jaren/veranderingen-inkomstenbelasting-2025/mkb-winstvrijstelling-2025",
        ]),
        new(2026, 1200, 2123, 12.7m,
        [
            $"{Ib}/veranderingen-inkomstenbelasting-2026/ondernemersaftrek-2026/zelfstandigenaftrek-2026",
            $"{Ib}/veranderingen-inkomstenbelasting-2026/mkb-winstvrijstelling-2026",
        ]),
        // De zelfstandigenaftrek voor 2027 staat in de wet; startersaftrek en MKB-winstvrijstelling nog niet.
        new(2027, 900, null, null,
            ["https://ondernemersplein.overheid.nl/wetswijzigingen/zelfstandigenaftrek-verder-omlaag-0/"],
            "De startersaftrek en de MKB-winstvrijstelling voor 2027 staan nog niet vast."),
    ];

    public static TaxYear? For(int year) => All.FirstOrDefault(y => y.Year == year);
}
