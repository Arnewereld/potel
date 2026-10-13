namespace Potel.Api.Data;

// Bedragen voor de inkomstenbelasting per jaar, voor de indicatie op het jaaroverzicht. Alles op één plek: komt er een nieuw
// jaar bij of verandert er iets, pas het dan hier aan en zet de bron erbij. Null is: staat nog niet vast.
public record TaxYear(int Year, decimal SelfEmployedDeduction, decimal? StarterDeduction, decimal? SmeProfitExemption, string[] Sources, string? Note = null);

// Wat je per zakelijke kilometer met je eigen auto (of ander privévervoermiddel) van je winst aftrekt. Daarnaast mag je geen
// andere kosten van die auto aftrekken, zoals brandstof of parkeren. Null is: staat nog niet vast.
public record KilometreRate(int Year, decimal? Rate, string Source, string? Note = null);

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

    // Woon-werkverkeer met je eigen auto telt voor de inkomstenbelasting als zakelijke kilometers.
    public const string KilometreSource = $"{Ib}/inkomstenbelasting_voor_ondernemers/zakelijk-gebruik-privevervoermiddel";

    // Gecontroleerd op belastingdienst.nl op 13 oktober 2026. De pagina over 2025 noemt € 0,23 en zegt dat het in 2024 ook
    // € 0,23 was; de pagina over 2026 noemt € 0,25. Voor 2027 is er nog geen bedrag gepubliceerd.
    public static readonly KilometreRate[] KilometreRates =
    [
        new(2024, 0.23m, $"{Ib}/verandering_inkomstenbelasting_vorige_jaren/veranderingen-inkomstenbelasting-2025/zakelijk-gebruik-privevervoermiddel-2025"),
        new(2025, 0.23m, $"{Ib}/verandering_inkomstenbelasting_vorige_jaren/veranderingen-inkomstenbelasting-2025/zakelijk-gebruik-privevervoermiddel-2025"),
        new(2026, 0.25m, $"{Ib}/veranderingen-inkomstenbelasting-2026/zakelijk-gebruik-privevervoermiddel-2026"),
        new(2027, null, KilometreSource, "Het bedrag per kilometer voor 2027 staat nog niet vast. Daarom tellen de kilometers nog niet mee in je resultaat."),
    ];

    public static KilometreRate KilometreRateFor(int year) => KilometreRates.FirstOrDefault(k => k.Year == year)
        ?? new(year, null, KilometreSource, $"Voor {year} hebben we het bedrag per kilometer niet. Daarom tellen de kilometers niet mee in je resultaat.");
}
