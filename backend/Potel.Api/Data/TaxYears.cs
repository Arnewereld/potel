namespace Potel.Api.Data;

// Bedragen voor de inkomstenbelasting per jaar, voor de indicatie op het jaaroverzicht. Alles op één plek: komt er een nieuw
// jaar bij of verandert er iets, pas het dan hier aan en zet de bron erbij. Null is: staat nog niet vast.
public record TaxYear(int Year, decimal SelfEmployedDeduction, decimal? StarterDeduction, decimal? SmeProfitExemption, string[] Sources, string? Note = null);

// De kleinschaligheidsinvesteringsaftrek (KIA) van een jaar, uit het totaal dat je dat jaar investeert in bedrijfsmiddelen die
// meetellen. Tot en met Minimum niets; daarboven tot en met FullUpTo een percentage van dat totaal; dan tot en met FixedUpTo een
// vast bedrag; dan tot en met PhaseOutUpTo dat vaste bedrag min een percentage van het deel boven FixedUpTo; daarboven niets.
public record KiaYear(int Year, decimal Minimum, decimal FullUpTo, decimal Percentage, decimal FixedUpTo, decimal FixedAmount,
    decimal PhaseOutUpTo, decimal PhaseOutPercentage, string Source);

// Wat je per zakelijke kilometer met je eigen auto (of ander privévervoermiddel) van je winst aftrekt. Daarnaast mag je geen
// andere kosten van die auto aftrekken, zoals brandstof of parkeren. Null is: staat nog niet vast.
public record KilometreRate(int Year, decimal? Rate, string Source, string? Note = null);

// Een schijf in box 1: tot en met UpTo (null is: alles daarboven) betaal je Rate procent.
public record TaxBracket(decimal? UpTo, decimal Rate);

// Een stuk van de arbeidskorting: tot en met UpTo (null is: alles daarboven) is de korting Base plus Rate procent van het
// arbeidsinkomen boven From. Een negatief Rate bouwt de korting af.
public record LabourCreditStep(decimal? UpTo, decimal Base, decimal Rate, decimal From);

// Box 1, de algemene heffingskorting, de arbeidskorting, de tariefsaanpassing en de inkomensafhankelijke bijdrage Zvw van één
// jaar, voor wie dat jaar nog niet de AOW-leeftijd heeft. Voor de belastingpot. De algemene heffingskorting is GeneralCredit
// tot en met PhaseOutFrom, daarboven min PhaseOutRate procent van het deel erboven, nooit onder nul. Ligt je inkomen voor
// aftrekposten boven RateAdjustmentFrom, dan gaat er RateAdjustment procent bij over je ondernemersaftrek en MKB-winstvrijstelling
// (hooguit over het deel van je inkomen boven die grens). De bijdrage Zvw is ZvwRate procent tot ZvwMaximum.
public record IncomeTaxYear(int Year,
    TaxBracket[] Brackets, string BracketsSource,
    decimal GeneralCredit, decimal GeneralCreditPhaseOutFrom, decimal GeneralCreditPhaseOutRate, string GeneralCreditSource,
    LabourCreditStep[] LabourCredit, string LabourCreditSource,
    decimal RateAdjustment, decimal RateAdjustmentFrom, string RateAdjustmentSource,
    decimal ZvwRate, decimal ZvwMaximum, string ZvwSource);

public static class TaxYears
{
    // Minstens zoveel uur per kalenderjaar aan je onderneming voor de zelfstandigenaftrek en startersaftrek.
    public const int HoursCriterion = 1225;
    public const string HoursSource =
        "https://www.belastingdienst.nl/wps/wcm/connect/bldcontentnl/belastingdienst/zakelijk/winst/inkomstenbelasting/inkomstenbelasting_voor_ondernemers/voorwaarden_urencriterium";
    // Bedrijfsmiddelen van € 450 of meer schrijf je af in plaats van ze in één keer af te trekken. Het bedrag is zonder btw als je
    // de btw aftrekt, en met btw als je dat niet kunt (KOR of vrijgestelde omzet). Hetzelfde geldt voor de KIA.
    public const decimal DepreciationThreshold = 450;
    public const string DepreciationSource =
        "https://www.belastingdienst.nl/wps/wcm/connect/bldcontentnl/belastingdienst/zakelijk/winst/inkomstenbelasting/inkomstenbelasting_voor_ondernemers/afschrijving/wat_is_afschrijven";
    // Per jaar hooguit 20% van de aanschafkosten, naar rato van de maanden in het eerste jaar, nooit onder de restwaarde.
    // Verkoop je het, dan is het verschil tussen verkoopprijs en boekwaarde winst of verlies.
    public const decimal MaxDepreciationPercentage = 20;
    public const string DepreciationCalculationSource =
        "https://www.belastingdienst.nl/wps/wcm/connect/bldcontentnl/belastingdienst/zakelijk/winst/inkomstenbelasting/inkomstenbelasting_voor_ondernemers/afschrijving/hoe_berekent_u_het_bedrag_van_de_afschrijving";
    // Wat niet meetelt voor de KIA, zoals bedrijfsmiddelen onder € 450 en personenauto's die niet bedoeld zijn voor beroepsvervoer.
    public const string KiaExclusionsSource =
        "https://www.belastingdienst.nl/wps/wcm/connect/bldcontentnl/belastingdienst/zakelijk/winst/inkomstenbelasting/inkomstenbelasting_voor_ondernemers/investeringsaftrek_en_desinvesteringsbijtelling/geen_recht_op_investeringsaftrek";
    public const string KiaSource =
        "https://www.belastingdienst.nl/wps/wcm/connect/bldcontentnl/belastingdienst/zakelijk/winst/inkomstenbelasting/inkomstenbelasting_voor_ondernemers/investeringsaftrek_en_desinvesteringsbijtelling/kleinschaligheidsinvesteringsaftrek_kia";

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

    // Gecontroleerd op belastingdienst.nl op 13 oktober 2026. De KIA is gelijk voor de inkomstenbelasting en de
    // vennootschapsbelasting; de tabellen voor 2024 en 2025 staan op de pagina's over de vennootschapsbelasting van die jaren.
    // Voor 2027 is er nog geen tabel gepubliceerd.
    public static readonly KiaYear[] Kia =
    [
        new(2024, 2800, 69765, 28, 129194, 19535, 387580, 7.56m,
            "https://www.belastingdienst.nl/wps/wcm/connect/bldcontentnl/belastingdienst/zakelijk/winst/vennootschapsbelasting/veranderingen-vennootschapsbelasting-vorige-jaren/veranderingen-vennootschapsbelasting-2024"),
        new(2025, 2900, 70602, 28, 130744, 19769, 392230, 7.56m,
            "https://www.belastingdienst.nl/wps/wcm/connect/bldcontentnl/belastingdienst/zakelijk/winst/vennootschapsbelasting/veranderingen-vennootschapsbelasting-vorige-jaren/veranderingen-vennootschapsbelasting-2025"),
        new(2026, 2900, 71683, 28, 132746, 20072, 398236, 7.56m,
            $"{Ib}/veranderingen-inkomstenbelasting-2026/investeringsaftrek-2026/kleinschaligheidsinvesteringsaftrek-2026"),
    ];

    public static KiaYear? KiaFor(int year) => Kia.FirstOrDefault(k => k.Year == year);

    // De KIA over het totaal dat je in een jaar investeert, op centen.
    public static decimal KiaAmount(KiaYear k, decimal invested)
    {
        if (invested <= k.Minimum) return 0;
        if (invested <= k.FullUpTo) return Money.Round(invested * k.Percentage / 100);
        if (invested <= k.FixedUpTo) return k.FixedAmount;
        if (invested <= k.PhaseOutUpTo) return Math.Max(0, Money.Round(k.FixedAmount - (invested - k.FixedUpTo) * k.PhaseOutPercentage / 100));
        return 0;
    }

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

    const string Prive = "https://www.belastingdienst.nl/wps/wcm/connect/bldcontentnl/belastingdienst/prive";
    const string Kortingen = $"{Prive}/inkomstenbelasting/heffingskortingen_boxen_tarieven/heffingskortingen";

    // De regels achter de belastingpot, met hun bron. Arbeidsinkomen is je winst vóór ondernemersaftrek en MKB-winstvrijstelling
    // plus je loon. Heffingskortingen gaan van je belasting af, maar worden zonder fiscale partner niet uitbetaald: ze zijn dus
    // nooit hoger dan de belasting. De bijdrage Zvw betaal je over je winst uit onderneming; je werkgever betaalt over je loon,
    // en dat loon telt mee voor het maximum. Gecontroleerd op belastingdienst.nl op 13 oktober 2026.
    public const string LabourIncomeSource = $"{Kortingen}/arbeidskorting/inkomen_uit_werk";
    public const string CreditsLimitSource = "https://www.belastingdienst.nl/wps/wcm/connect/fisin/fisin2026/heffingskortingen";
    public const string ZvwProfitSource =
        "https://www.belastingdienst.nl/wps/wcm/connect/bldcontentnl/belastingdienst/zakelijk/ondernemen/onderneming_starten/wat_u_verder_wilt_weten/zorgverzekering/zorgverzekering";
    public const string ZvwWageSource = "https://www.belastingdienst.nl/wps/wcm/connect/fisin/fisin2026/inkomensafhankelijke_bijdrage_zorgverzekeringswet";

    const string Box1 = $"{Prive}/inkomstenbelasting/heffingskortingen_boxen_tarieven/boxen_en_tarieven/box_1/box_1";
    const string RateAdjustmentPage = "https://www.belastingdienst.nl/wps/wcm/connect/nl/aftrek-en-kortingen/content/afbouw-tarief-aftrekposten-bij-hoog-inkomen";
    const string ZvwPage = $"{Prive}/werk_en_inkomen/zorgverzekeringswet/veranderingen-bijdrage-zvw/percentages-zvw";

    // Gecontroleerd op belastingdienst.nl op 13 oktober 2026, voor wie dat jaar nog niet de AOW-leeftijd heeft. De pagina's met
    // de schijven, de tariefsaanpassing en de bijdrage Zvw noemen 2025 en 2026 samen. Voor 2027 staat er nog niets: geen schijven,
    // geen tabellen met heffingskortingen en geen percentage voor de bijdrage Zvw.
    public static readonly IncomeTaxYear[] IncomeTax =
    [
        new(2025,
            [new(38441, 35.82m), new(76817, 37.48m), new(null, 49.50m)], Box1,
            3068, 28406, 6.337m, $"{Kortingen}/algemene_heffingskorting/tabel-algemene-heffingskorting-2025",
            [new(12169, 0, 8.053m, 0), new(26288, 980, 30.030m, 12169), new(43071, 5220, 2.258m, 26288), new(129078, 5599, -6.510m, 43071), new(null, 0, 0, 0)],
            $"{Kortingen}/arbeidskorting/tabel-arbeidskorting-2025",
            12.02m, 76817, RateAdjustmentPage,
            5.26m, 75864, ZvwPage),
        new(2026,
            [new(38883, 35.75m), new(78426, 37.56m), new(null, 49.50m)], Box1,
            3115, 29736, 6.398m, $"{Kortingen}/algemene_heffingskorting/tabel-algemene-heffingskorting-2026",
            [new(11965, 0, 8.324m, 0), new(25845, 996, 31.009m, 11965), new(45592, 5300, 1.950m, 25845), new(132920, 5685, -6.510m, 45592), new(null, 0, 0, 0)],
            $"{Kortingen}/arbeidskorting/tabel-arbeidskorting-2026",
            11.94m, 78426, RateAdjustmentPage,
            4.85m, 79409, ZvwPage),
    ];

    public static IncomeTaxYear? IncomeTaxFor(int year) => IncomeTax.FirstOrDefault(t => t.Year == year);
}
