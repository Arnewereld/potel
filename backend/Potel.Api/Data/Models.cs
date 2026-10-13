using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace Potel.Api.Data;

// Elke klant van het portaal heeft een eigen werkruimte. Alle gegevens hangen aan een werkruimte
// en zijn via een filter in AppDb alleen zichtbaar binnen de eigen werkruimte.
public interface IWorkspaceOwned
{
    int WorkspaceId { get; set; }
}

public static class Plans
{
    public const string Trial = "proef";
    public const string Solo = "zzp";
    public const string Team = "team";
    public static readonly string[] All = [Trial, Solo, Team];

    // Hoeveel gebruikers (ook uitgeschakelde) een werkruimte per abonnement mag hebben.
    // De verkooppagina toont dit vanuit frontend/src/lib/plans.ts; PlanTests controleert dat die gelijk blijft.
    static readonly Dictionary<string, int> UserLimits = new() { [Trial] = 5, [Solo] = 1, [Team] = 5 };
    public static int MaxUsers(string plan) => UserLimits.GetValueOrDefault(plan, 1);

    // De prijs per maand in euro, exclusief btw. Ook dit staat in frontend/src/lib/plans.ts (monthly); PlanTests controleert dat.
    static readonly Dictionary<string, decimal> MonthlyPrices = new() { [Trial] = 0, [Solo] = 12, [Team] = 29 };
    public static decimal MonthlyPrice(string plan) => MonthlyPrices.GetValueOrDefault(plan);
    public static readonly string[] Paid = [Solo, Team];
    public static string Name(string plan) => plan switch { Solo => "ZZP", Team => "Team", _ => "Proef" };

    public const string TrialEndedError = "Je proefperiode is afgelopen. Kies een abonnement onder Instellingen om weer te kunnen werken.";
    public const string SubscriptionEndedError = "Je abonnement is afgelopen. Kies opnieuw een abonnement onder Instellingen om weer te kunnen werken.";
    public const string PaymentMissingError = "We hebben de laatste betaling van je abonnement niet ontvangen. Betaal opnieuw onder Instellingen, Abonnement om weer te kunnen werken.";
    // Zo lang wachten we op een automatische incasso voordat de werkruimte alleen-lezen wordt. Een incasso duurt een paar werkdagen.
    public const int PaymentGraceDays = 10;
}

// De versie van de algemene voorwaarden en de verwerkersovereenkomst (de teksten staan in frontend/src/pages/public/Legal.tsx).
// Pas je de tekst aan, verhoog dan deze versie: bij elke werkruimte staat welke versie bij het aanmelden is geaccepteerd.
public static class Terms
{
    public const string Version = "2026-10-concept";
}

public class Workspace
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Plan { get; set; } = Plans.Trial;
    public DateTime? TrialEndsAt { get; set; }
    // Leeg zolang de welkomstwizard nog niet is afgerond.
    public DateTime? OnboardedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    // Hoeveel e-mails werkstromen op EmailDay (UTC) verstuurden, voor de daglimiet per werkruimte.
    public DateTime? EmailDay { get; set; }
    public int EmailsSent { get; set; }
    // Hetzelfde voor webhooks.
    public DateTime? WebhookDay { get; set; }
    public int WebhooksSent { get; set; }
    // Gezet zolang er voorbeelddata in de werkruimte staat, zodat je die in één keer kunt weghalen.
    public DateTime? DemoDataAt { get; set; }
    // Het KvK-nummer uit het aanmelden: Potel is alleen voor bedrijven.
    public string? Kvk { get; set; }
    // Welke versie van de voorwaarden en verwerkersovereenkomst is geaccepteerd, wanneer en door wie.
    public string? TermsVersion { get; set; }
    public DateTime? TermsAcceptedAt { get; set; }
    public int? TermsAcceptedByUserId { get; set; }
    public string? TermsAcceptedByEmail { get; set; }
    // Online betalen via Mollie: de klant en het lopende abonnement daar, en tot wanneer er betaald is.
    // PaidUntil is leeg als jij het abonnement zelf omzet op de pagina Platform; dan loopt het door tot je het weer omzet.
    public string? MollieCustomerId { get; set; }
    public string? MollieSubscriptionId { get; set; }
    public DateTime? PaidUntil { get; set; }
    public DateTime? SubscriptionCanceledAt { get; set; }
    // De klant die bij deze werkruimte hoort in de werkruimte van de eigenaar, voor de facturen van het abonnement.
    public int? BillingCustomerId { get; set; }

    // Na de proef zonder abonnement is een werkruimte alleen-lezen: niets wijzigen en geen werkstromen meer.
    public bool TrialExpired(DateTime utcNow) => Plan == Plans.Trial && TrialEndsAt is { } end && end < utcNow;

    // Waarom deze werkruimte nu alleen-lezen is, of null als alles kan. Na een opgezegd abonnement gaat dat in zodra de
    // betaalde periode voorbij is; loopt het abonnement nog, dan wachten we eerst een paar dagen op de incasso.
    public string? ReadOnlyReason(DateTime utcNow)
    {
        if (TrialExpired(utcNow)) return Plans.TrialEndedError;
        if (Plan == Plans.Trial || PaidUntil is not { } until) return null;
        if (MollieSubscriptionId is null) return until < utcNow ? Plans.SubscriptionEndedError : null;
        return until.AddDays(Plans.PaymentGraceDays) < utcNow ? Plans.PaymentMissingError : null;
    }

    public bool ReadOnly(DateTime utcNow) => ReadOnlyReason(utcNow) is not null;
}

// Een betaling bij Mollie voor het abonnement van een werkruimte: de eerste met iDEAL, daarna de maandelijkse incasso's.
// Het id is dat van Mollie (tr_...). De status nemen we altijd over van Mollie zelf, nooit uit de webhook.
public class MolliePayment : IWorkspaceOwned
{
    public string Id { get; set; } = "";
    [JsonIgnore] public int WorkspaceId { get; set; }
    public string Plan { get; set; } = "";
    // "first" voor de betaling met iDEAL, "recurring" voor een incasso van het abonnement.
    public string SequenceType { get; set; } = "first";
    public string Status { get; set; } = "open";
    // Het bedrag inclusief btw, en het btw-percentage.
    public decimal Amount { get; set; }
    public decimal VatRate { get; set; }
    public string? SubscriptionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PaidAt { get; set; }
    // De maand waarvoor deze betaling is.
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    // Gezet zodra de betaling is verwerkt, zodat een herhaalde webhook niets dubbel doet.
    public DateTime? AppliedAt { get; set; }
    // De factuur voor deze betaling in de werkruimte van de eigenaar van het platform.
    public int? InvoiceId { get; set; }
}

// Tellers over het hele platform, los van werkruimtes. Bijvoorbeeld hoeveel e-mails alle proefwerkruimtes samen vandaag stuurden.
public class PlatformCounter
{
    public const string TrialEmails = "proef-emails";

    public string Key { get; set; } = "";
    public DateTime? Day { get; set; }
    public int Count { get; set; }
}

// Voorbeelddata uit de welkomstwizard. Die staat apart, zodat je hem in één keer kunt weghalen.
public interface IDemoData
{
    bool IsDemo { get; set; }
}

public class Customer : IWorkspaceOwned, IDemoData
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    [JsonIgnore] public bool IsDemo { get; set; }
    public string Name { get; set; } = "";
    public string? Company { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    // Bepaalt welke btw-regeling mag: btw verlegd alleen voor een ander EU-land, "buiten de EU" alleen daarbuiten.
    public string Country { get; set; } = Countries.Netherlands;
    public string? VatNumber { get; set; }
    public string? Notes { get; set; }
    // Geen automatische herinneringen aan deze klant.
    public bool RemindersOff { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Landen zoals we ze op de factuur zetten, met de EU-landen apart om de btw-regeling te kunnen controleren.
public static class Countries
{
    public const string Netherlands = "Nederland";

    public static readonly string[] Eu =
    [
        "België", "Bulgarije", "Cyprus", "Denemarken", "Duitsland", "Estland", "Finland", "Frankrijk", "Griekenland", "Hongarije",
        "Ierland", "Italië", "Kroatië", "Letland", "Litouwen", "Luxemburg", "Malta", "Nederland", "Oostenrijk", "Polen",
        "Portugal", "Roemenië", "Slovenië", "Slowakije", "Spanje", "Tsjechië", "Zweden",
    ];

    // Andere schrijfwijzen (Engels, zonder trema of als landcode) van de EU-landen, zodat "Germany" of "DE" ook als EU telt.
    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NL"] = "Nederland", ["Netherlands"] = "Nederland", ["The Netherlands"] = "Nederland", ["Holland"] = "Nederland",
        ["BE"] = "België", ["Belgie"] = "België", ["Belgium"] = "België", ["BG"] = "Bulgarije", ["Bulgaria"] = "Bulgarije",
        ["CY"] = "Cyprus", ["DK"] = "Denemarken", ["Denmark"] = "Denemarken", ["DE"] = "Duitsland", ["Germany"] = "Duitsland",
        ["Deutschland"] = "Duitsland", ["EE"] = "Estland", ["Estonia"] = "Estland", ["FI"] = "Finland", ["FR"] = "Frankrijk",
        ["France"] = "Frankrijk", ["GR"] = "Griekenland", ["EL"] = "Griekenland", ["Greece"] = "Griekenland", ["HU"] = "Hongarije",
        ["Hungary"] = "Hongarije", ["IE"] = "Ierland", ["Ireland"] = "Ierland", ["IT"] = "Italië", ["Italie"] = "Italië",
        ["Italy"] = "Italië", ["HR"] = "Kroatië", ["Kroatie"] = "Kroatië", ["Croatia"] = "Kroatië", ["LV"] = "Letland",
        ["Latvia"] = "Letland", ["LT"] = "Litouwen", ["Lithuania"] = "Litouwen", ["LU"] = "Luxemburg", ["Luxembourg"] = "Luxemburg",
        ["MT"] = "Malta", ["AT"] = "Oostenrijk", ["Austria"] = "Oostenrijk", ["PL"] = "Polen", ["Poland"] = "Polen",
        ["PT"] = "Portugal", ["RO"] = "Roemenië", ["Roemenie"] = "Roemenië", ["Romania"] = "Roemenië", ["SI"] = "Slovenië",
        ["Slovenie"] = "Slovenië", ["Slovenia"] = "Slovenië", ["SK"] = "Slowakije", ["Slovakia"] = "Slowakije", ["ES"] = "Spanje",
        ["Spain"] = "Spanje", ["CZ"] = "Tsjechië", ["Tsjechie"] = "Tsjechië", ["Czechia"] = "Tsjechië", ["Czech Republic"] = "Tsjechië",
        ["SE"] = "Zweden", ["Sweden"] = "Zweden",
    };

    // Leeg wordt Nederland; een bekende andere schrijfwijze wordt de Nederlandse naam.
    public static string Normalize(string? country)
    {
        var c = (country ?? "").Trim();
        if (c == "") return Netherlands;
        if (Aliases.TryGetValue(c, out var name)) return name;
        return Eu.FirstOrDefault(e => string.Equals(e, c, StringComparison.OrdinalIgnoreCase)) ?? c;
    }

    public static bool IsNetherlands(string? country) => Normalize(country) == Netherlands;
    public static bool InEu(string? country) => Eu.Contains(Normalize(country));
}

public static class LeadStatus
{
    public static readonly string[] All = ["nieuw", "contact", "offerte", "gewonnen", "verloren"];
}

public class Lead : IWorkspaceOwned, IDemoData
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    [JsonIgnore] public bool IsDemo { get; set; }
    public string Name { get; set; } = "";
    public string? Company { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public decimal Value { get; set; }
    public string Status { get; set; } = "nieuw";
    public string? Source { get; set; }
    public string? Notes { get; set; }
    public int? CustomerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class InvoiceStatus
{
    public const string Draft = "concept";
    public const string Sent = "verzonden";
    public const string Paid = "betaald";
    public const string Overdue = "verlopen";
    public static readonly string[] All = [Draft, Sent, Paid, Overdue];

    // Een verstuurde factuur ligt vast: de status mag daarna alleen nog vooruit (verzonden, verlopen, betaald).
    public static bool CanMove(string from, string to) => from == to || (from, to) switch
    {
        (Draft, Sent or Paid) => true,
        (Sent, Overdue or Paid) => true,
        (Overdue, Paid) => true,
        _ => false,
    };
}

// De btw-regeling van een factuur. Normaal: gewoon btw. Verlegd: de klant in een ander EU-land draagt de btw af.
// KOR: de kleineondernemersregeling, zonder btw. Vrijgesteld: diensten die vrijgesteld zijn van btw (art. 11 Wet OB),
// zoals CRKBO-onderwijs. Buiten de EU: geen Nederlandse btw voor een klant buiten de EU.
public static class VatRegimes
{
    public const string Normal = "normaal";
    public const string ReverseCharge = "verlegd";
    public const string Kor = "kor";
    public const string Exempt = "vrijgesteld";
    public const string OutsideEu = "buiten-eu";
    public static readonly string[] All = [Normal, ReverseCharge, Kor, Exempt, OutsideEu];
    // Wat een werkruimte standaard gebruikt; verleggen en buiten de EU hangen van de klant af.
    public static readonly string[] WorkspaceDefaults = [Normal, Kor, Exempt];

    // Alleen bij normaal staat er btw op de regels.
    public static bool ZeroVat(string regime) => regime != Normal;

    // De regeling voor een nieuwe factuur: verlegd voor een zakelijke klant in een ander EU-land, buiten de EU daarbuiten,
    // en anders wat de werkruimte standaard gebruikt (normaal, KOR of vrijgesteld).
    public static string DefaultFor(Settings settings, Customer? customer)
    {
        if (customer is not null && !Countries.IsNetherlands(customer.Country))
        {
            if (!Countries.InEu(customer.Country)) return OutsideEu;
            if (!string.IsNullOrWhiteSpace(customer.VatNumber)) return ReverseCharge;
        }
        return WorkspaceDefaults.Contains(settings.VatRegime) ? settings.VatRegime : Normal;
    }
}

public class Invoice : IWorkspaceOwned, IDemoData
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    [JsonIgnore] public bool IsDemo { get; set; }
    // Het doorlopende nummer komt pas bij versturen; een concept heeft nog geen nummer.
    public string? Number { get; set; }
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public DateTime IssueDate { get; set; } = DateTime.UtcNow.Date;
    public DateTime DueDate { get; set; } = DateTime.UtcNow.Date.AddDays(14);
    public string Status { get; set; } = InvoiceStatus.Draft;
    // Referentie of inkoopnummer van de klant.
    public string? Reference { get; set; }
    public string VatRegime { get; set; } = VatRegimes.Normal;
    // Leverdatum, of de periode waarin het werk is gedaan als DeliveryTo later ligt.
    public DateTime? DeliveryFrom { get; set; }
    public DateTime? DeliveryTo { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? Notes { get; set; }
    // Bij een creditnota: de factuur die hij corrigeert.
    public int? CreditForInvoiceId { get; set; }
    [JsonIgnore] public Invoice? CreditFor { get; set; }
    // Gemaakt door een terugkerende factuur, voor de periode die op RecurringPeriodStart begint. Per terugkerende factuur
    // bestaat elke periode hooguit één keer (unieke index), ook als de planner twee keer tegelijk draait. Bewust zonder
    // databasekoppeling: die zou de hele tabel met facturen laten herbouwen. Verwijderen van de terugkerende factuur maakt dit leeg.
    public int? RecurringInvoiceId { get; set; }
    public DateTime? RecurringPeriodStart { get; set; }
    // Geen automatische herinneringen voor deze factuur. Mag ook na versturen nog aan of uit: het staat niet op de factuur.
    public bool RemindersOff { get; set; }
    // Urenspecificatie meesturen: een extra pagina met de boekingen op deze factuur. Telt alleen als er uren aan hangen.
    public bool HourSpecification { get; set; } = true;
    // Op de urenspecificatie ook het bedrag per boeking, voor boekingen op een regel per uur.
    public bool HourAmounts { get; set; }
    // De urenspecificatie zoals hij bij versturen was (JSON, zie Invoicing/HourSpecs.cs). Een verstuurde factuur toont deze,
    // nooit de boekingen van nu, zodat een latere wijziging aan uren of projecten hem niet verandert.
    [JsonIgnore] public string? HoursSnapshot { get; set; }
    // De gegevens van jou en je klant zoals ze bij versturen waren. Een verstuurde factuur toont deze, niet de huidige.
    public InvoiceParty? Seller { get; set; }
    public InvoiceParty? Buyer { get; set; }
    public List<InvoiceLine> Lines { get; set; } = [];

    // De creditnota's die deze factuur corrigeren (ook concepten).
    [JsonIgnore] public List<Invoice> Credits { get; set; } = [];

    [NotMapped] public string? CreditForNumber => CreditFor?.Number;
    [NotMapped] public DateTime? CreditForIssueDate => CreditFor?.IssueDate;
    [NotMapped] public bool IsCredit => CreditForInvoiceId != null || Totals.Total < 0;
    [NotMapped] public InvoiceTotals Totals => Money.Totals(Lines);
    // Voorbeeldfactuur uit de welkomstwizard: eigen nummerreeks en altijd te verwijderen.
    [NotMapped] public bool Demo => IsDemo;

    // Verstuurde creditnota's op deze factuur (alleen bekend als Credits met hun regels geladen zijn).
    [NotMapped] public List<CreditRef> CreditNotes =>
        Credits.Where(c => c.Status != InvoiceStatus.Draft).OrderBy(c => c.Id).Select(c => new CreditRef(c.Id, c.Number, c.IssueDate, c.Totals.Total)).ToList();
    // Wat er met creditnota's van deze factuur af is gegaan (0 of negatief).
    [NotMapped] public decimal CreditedTotal => CreditNotes.Sum(c => c.Total);
    // Wat de klant nog moet betalen: het totaal min wat er gecrediteerd is. Alleen voor een verstuurde, onbetaalde factuur.
    [NotMapped] public decimal OpenAmount =>
        IsCredit || Status is not (InvoiceStatus.Sent or InvoiceStatus.Overdue) ? 0 : Math.Max(0, Totals.Total + CreditedTotal);
    // Helemaal tegengeboekt met creditnota's: niemand hoeft nog iets te betalen.
    [NotMapped] public bool FullyCredited => !IsCredit && CreditNotes.Count > 0 && Totals.Total + CreditedTotal <= 0;
}

public record CreditRef(int Id, string? Number, DateTime IssueDate, decimal Total);

// Naam, adres en nummers van een partij op de factuur, vastgelegd bij versturen.
public class InvoiceParty
{
    public string Name { get; set; } = "";
    // De eigenaar bij jou, of "t.a.v." bij je klant.
    public string? Contact { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Website { get; set; }
    public string? Kvk { get; set; }
    public string? VatNumber { get; set; }
    public string? Iban { get; set; }

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Een btw-id zoals hij op de factuur hoort: hoofdletters, zonder spaties of punten (NL001234567B01).
    public static string? NormalizeVatId(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Replace(" ", "").Replace(".", "").Trim().ToUpperInvariant();

    public static InvoiceParty Seller(Settings s) => new()
    {
        Name = s.CompanyName.Trim(), Contact = Clean(s.OwnerName), Address = Clean(s.Address), City = Clean(s.City), Country = Countries.Netherlands,
        Email = Clean(s.Email), Phone = Clean(s.Phone), Website = Clean(s.Website), Kvk = Clean(s.Kvk), VatNumber = NormalizeVatId(s.Btw), Iban = Clean(s.Iban),
    };

    public InvoiceParty Copy() => (InvoiceParty)MemberwiseClone();

    public static InvoiceParty Buyer(Customer c) => new()
    {
        Name = Clean(c.Company) ?? c.Name.Trim(), Contact = Clean(c.Company) is null ? null : Clean(c.Name), Address = Clean(c.Address),
        City = Clean(c.City), Country = Countries.Normalize(c.Country), Email = Clean(c.Email), Phone = Clean(c.Phone), VatNumber = Clean(c.VatNumber),
    };
}

public record VatGroup(decimal Rate, decimal Base, decimal Vat);
public record InvoiceTotals(decimal Subtotal, decimal Vat, decimal Total, List<VatGroup> VatGroups);

// Eén manier van afronden voor de hele app, op de server en in de frontend (frontend/src/lib/format.ts):
// elk regelbedrag op centen, de btw per tarief over het totaal van dat tarief, en altijd half van nul af (2,345 → 2,35 en -2,345 → -2,35).
public static class Money
{
    // Grenzen voor één regel, zodat aantal × prijs en de btw daarover altijd in een decimal passen.
    public const decimal MaxQuantity = 1_000_000;
    public const decimal MaxAmount = 1_000_000_000;
    public const string TooLargeError = "Een regel heeft een te groot aantal of bedrag. Een aantal kan tot 1.000.000 en een prijs tot 1.000.000.000.";

    public static bool TooLarge(InvoiceLine l) => Math.Abs(l.Quantity) > MaxQuantity || Math.Abs(l.UnitPrice) > MaxAmount;

    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static decimal LineAmount(InvoiceLine l) => Round(l.Quantity * l.UnitPrice);

    public static InvoiceTotals Totals(IEnumerable<InvoiceLine> lines)
    {
        var groups = lines.GroupBy(l => l.VatRate).OrderByDescending(g => g.Key)
            .Select(g => { var b = g.Sum(LineAmount); return new VatGroup(g.Key, b, Round(b * g.Key / 100)); }).ToList();
        var subtotal = groups.Sum(g => g.Base);
        var vat = groups.Sum(g => g.Vat);
        return new InvoiceTotals(subtotal, vat, subtotal + vat, groups);
    }
}

public class InvoiceLine
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    // De volgorde op de factuur; bij gelijke positie telt het id.
    [JsonIgnore] public int Position { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public string Unit { get; set; } = "stuk";
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; } = 21;
}

// Hoe vaak een terugkerende factuur komt, welke periode hij dekt en wat er bij elke keer gebeurt.
public static class RecurringIntervals
{
    public const string Monthly = "maand";
    public const string Quarterly = "kwartaal";
    public const string Yearly = "jaar";
    public static readonly string[] All = [Monthly, Quarterly, Yearly];

    public static int Months(string interval) => interval switch { Quarterly => 3, Yearly => 12, _ => 1 };
}

public static class RecurringPeriodRules
{
    // Vooraf: de factuur komt aan het begin van de periode die hij dekt (hosting, een abonnement).
    public const string InAdvance = "vooraf";
    // Achteraf: de factuur komt de dag nadat de periode voorbij is (een retainer, onderhoud per maand).
    public const string InArrears = "achteraf";
    public static readonly string[] All = [InAdvance, InArrears];
}

public static class RecurringActions
{
    public const string Draft = "concept";
    public const string Send = "versturen";
    public static readonly string[] All = [Draft, Send];
}

// Een factuur die elke maand, elk kwartaal of elk jaar terugkomt voor dezelfde klant, zoals onderhoud, hosting of een retainer.
// De planner maakt per periode precies één factuur (zie RecurringInvoiceService). NextPeriodStart is de eerste periode die
// nog geen factuur heeft; NextRunDate is de dag waarop die aan de beurt is, of leeg als de einddatum voorbij is.
public class RecurringInvoice : IWorkspaceOwned, IDemoData
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    [JsonIgnore] public bool IsDemo { get; set; }
    public string Name { get; set; } = "";
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string VatRegime { get; set; } = VatRegimes.Normal;
    public string Interval { get; set; } = RecurringIntervals.Monthly;
    public string PeriodRule { get; set; } = RecurringPeriodRules.InAdvance;
    public string Action { get; set; } = RecurringActions.Draft;
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime NextPeriodStart { get; set; }
    public DateTime? NextRunDate { get; set; }
    public bool Active { get; set; } = true;
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<RecurringInvoiceLine> Lines { get; set; } = [];
    [NotMapped] public bool Demo => IsDemo;
}

// Een regel van een terugkerende factuur, net als een factuurregel. In de omschrijving mogen {maand}, {jaar}, {kwartaal} en {periode}.
public class RecurringInvoiceLine
{
    public int Id { get; set; }
    public int RecurringInvoiceId { get; set; }
    [JsonIgnore] public int Position { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public string Unit { get; set; } = "maand";
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; } = 21;
}

// Wat een mail over een factuur is: de factuur zelf (0) of een van de drie herinneringen bij een te late factuur.
public static class MailSteps
{
    public const int Invoice = 0;
    public const int First = 1;
    public const int Second = 2;
    public const int Final = 3;

    public static string Label(int step) => step switch
    {
        First => "Herinnering",
        Second => "Tweede herinnering",
        Final => "Aanmaning",
        _ => "Factuur",
    };
}

public static class MailStatus
{
    public const string Sending = "bezig";
    public const string Sent = "verstuurd";
    public const string Failed = "mislukt";
    public const string Skipped = "overgeslagen";
}

// Elke mail over een factuur: wanneer, welke stap en aan wie. Ook wat niet weg kon, met de reden.
// Een automatische herinnering staat er per stap hooguit één keer als "bezig" of "verstuurd" (unieke index), zodat
// twee rondes tegelijk dezelfde herinnering nooit twee keer sturen.
public class InvoiceMail : IWorkspaceOwned
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    public int InvoiceId { get; set; }
    public int Step { get; set; }
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Status { get; set; } = MailStatus.Sending;
    public string? Note { get; set; }
    public bool Automatic { get; set; }
    // Wie hem handmatig verstuurde.
    public string? SentBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
}

// Een openbare link naar één verstuurde factuur, voor in de mail. Het token zelf staat alleen in de mail; hier bewaren we
// alleen de SHA-256-hash. Intrekken maakt de link meteen onbruikbaar.
public class InvoiceLink : IWorkspaceOwned
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    public int InvoiceId { get; set; }
    [JsonIgnore] public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastOpenedAt { get; set; }
    public int Opens { get; set; }
    // Gemaakt vanuit het klantportaal: werkt alleen zolang die portaallink werkt en facturen daar zichtbaar zijn.
    // Het token is afgeleid van het portaaltoken (zie CustomerPortalEndpoints.InvoiceToken), dus ook hier alleen de hash.
    public int? PortalLinkId { get; set; }
}

// Wat een klant in zijn portaal ziet. Hooguit één rij per klant; zonder rij gelden de standaardwaarden hieronder.
public class CustomerPortal : IWorkspaceOwned
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    public int CustomerId { get; set; }
    public bool ShowHours { get; set; } = true;
    public bool ShowBudget { get; set; } = true;
    public bool ShowInvoices { get; set; } = true;
    // Uurtarieven en de waarde van de uren. Facturen tonen altijd hun eigen bedrag.
    public bool ShowAmounts { get; set; }
    // Een kort bericht van de freelancer bovenaan het portaal.
    public string? Note { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// Een geheime link naar het portaal van één klant. Net als bij de factuurlink staat alleen de SHA-256-hash van het token
// hier; het token zelf ziet de beheerder één keer. Per klant werkt hooguit één link tegelijk.
public class PortalLink : IWorkspaceOwned
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    public int CustomerId { get; set; }
    [JsonIgnore] public string TokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime? LastOpenedAt { get; set; }
    public int Opens { get; set; }

    public bool Works(DateTime utcNow) => RevokedAt is null && (ExpiresAt is not { } end || end > utcNow);
}

// De koppeling met GitHub van een werkruimte. Het persoonlijke toegangstoken staat versleuteld met ASP.NET Data Protection
// in de database en gaat nooit terug naar de browser; daar zie je alleen dat het is ingesteld en de laatste 4 tekens.
public class GitHubSettings : IWorkspaceOwned
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    [JsonIgnore] public string ProtectedToken { get; set; } = "";
    public string TokenLast4 { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string? UpdatedBy { get; set; }
}

// Met welke e-mailadressen een gebruiker commit. Zonder rij gebruiken we het e-mailadres van zijn account.
public class GitHubAuthor : IWorkspaceOwned
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    public int UserId { get; set; }
    // Eén adres per regel.
    public string Emails { get; set; } = "";
}

// Een commit die als uren geboekt of overgeslagen is. Zo komt dezelfde commit nooit twee keer als voorstel.
public class GitHubCommit : IWorkspaceOwned
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    public string Sha { get; set; } = "";
    public int ProjectId { get; set; }
    // De boeking met deze commit; leeg als hij is overgeslagen of als de boeking later is verwijderd.
    public int? TimeEntryId { get; set; }
    public bool Skipped { get; set; }
    public int? UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Automatische herinneringen bij te late facturen: aan of uit, hoeveel dagen na de vervaldatum elke stap komt, en de teksten.
// Een lege tekst betekent de standaardtekst (Invoicing/MailTemplates.cs), zodat verbeteringen daarin vanzelf meekomen.
public class ReminderSettings : IWorkspaceOwned
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    public bool Enabled { get; set; }
    public int FirstDays { get; set; } = 7;
    public int SecondDays { get; set; } = 14;
    public int FinalDays { get; set; } = 28;
    public string? InvoiceSubject { get; set; }
    public string? InvoiceBody { get; set; }
    public string? FirstSubject { get; set; }
    public string? FirstBody { get; set; }
    public string? SecondSubject { get; set; }
    public string? SecondBody { get; set; }
    public string? FinalSubject { get; set; }
    public string? FinalBody { get; set; }
    // De laatste melding in het logboek dat herinneringen niet weg konden (dag en reden), zodat die hooguit één keer per dag komt.
    [JsonIgnore] public string? LastNotice { get; set; }
}

public static class ProjectStatus
{
    public static readonly string[] All = ["actief", "gepauzeerd", "afgerond"];
}

public static class Billing
{
    // Per uur: uren worden gefactureerd tegen het uurtarief. Vast: één afgesproken prijs voor het hele project.
    public const string Hourly = "uur";
    public const string Fixed = "vast";
    public static readonly string[] All = [Hourly, Fixed];
}

public class Project : IWorkspaceOwned, IDemoData
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    [JsonIgnore] public bool IsDemo { get; set; }
    public string Name { get; set; } = "";
    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string Status { get; set; } = "actief";
    public string Billing { get; set; } = Data.Billing.Hourly;
    public decimal HourlyRate { get; set; }
    public decimal FixedPrice { get; set; }
    public decimal? BudgetHours { get; set; }
    public string Color { get; set; } = "#ff6d5a";
    public string? RepoUrl { get; set; }
    public string? Description { get; set; }
    public DateTime? Deadline { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class TimeEntry : IWorkspaceOwned, IDemoData
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    [JsonIgnore] public bool IsDemo { get; set; }
    public int ProjectId { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow.Date;
    public int Minutes { get; set; }
    public string Description { get; set; } = "";
    public bool Billable { get; set; } = true;
    // Gevuld zodra de uren op een factuur staan; daarna liggen ze vast.
    public int? InvoiceId { get; set; }
    // De factuurregel met deze uren. Gaat die regel van een concept af, dan komen de uren weer vrij.
    public int? InvoiceLineId { get; set; }
    // Wie de uren boekte, voor de urenspecificatie in een team. Leeg bij boekingen van voor deze kolom. Bewust zonder
    // databasekoppeling: die zou de hele tabel met uren laten herbouwen. De naam zoeken we op binnen de eigen werkruimte.
    public int? UserId { get; set; }
    // Kenmerk dat de timer meestuurt, zodat dubbel stoppen maar één boeking oplevert.
    public string? ClientId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Eén rij met de gegevens van de freelancer zelf: voor op de factuur en voor de doelen op het dashboard.
public class Settings : IWorkspaceOwned
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    public string CompanyName { get; set; } = "Jouw Naam Development";
    public string? OwnerName { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Website { get; set; }
    public string? Kvk { get; set; }
    public string? Btw { get; set; }
    public string? Iban { get; set; }
    public decimal DefaultHourlyRate { get; set; } = 95;
    public int PaymentTermDays { get; set; } = 14;
    // Standaard btw-regeling voor nieuwe facturen: normaal, KOR (kleineondernemersregeling) of vrijgesteld.
    public string VatRegime { get; set; } = VatRegimes.Normal;
    public int WeeklyHoursTarget { get; set; } = 32;
    // Het urencriterium voor de zelfstandigenaftrek.
    public int YearlyHoursTarget { get; set; } = 1225;
    // Huisstijl op de factuur: een logo als data-URL en een accentkleur.
    public string? LogoDataUrl { get; set; }
    public string BrandColor { get; set; } = "#ff6d5a";
}

// Soorten zakelijke kosten, met de naam zoals we hem tonen (ook in de export voor je boekhouder).
public static class ExpenseCategories
{
    public const string Hardware = "hardware";
    public const string Software = "software";
    public const string Hosting = "hosting";
    public const string Training = "opleiding";
    public const string Travel = "reiskosten";
    public const string Phone = "telefoon";
    public const string Office = "kantoor";
    public const string Other = "overig";
    public static readonly string[] All = [Hardware, Software, Hosting, Training, Travel, Phone, Office, Other];

    public static string Label(string category) => category switch
    {
        Hardware => "Hardware",
        Software => "Software en abonnementen",
        Hosting => "Hosting en domeinen",
        Training => "Opleiding",
        Travel => "Reiskosten",
        Phone => "Telefoon en internet",
        Office => "Kantoor",
        _ => "Overig",
    };
}

// Waar een leverancier zit als de btw naar jou is verlegd: binnen de EU (rubriek 4b) of daarbuiten (rubriek 4a).
public static class SupplierRegions
{
    public const string Eu = "eu";
    public const string OutsideEu = "buiten-eu";
    public static readonly string[] All = [Eu, OutsideEu];
}

// Een zakelijke uitgave met de btw erop. Amount is wat je betaalde. Bij btw verlegd staat er geen btw op de rekening:
// dan is Amount het bedrag zonder btw en is VatAmount de Nederlandse btw die je zelf aangeeft en weer aftrekt.
public class Expense : IWorkspaceOwned, IDemoData
{
    // Btw-tarieven voor een uitgave; null is "geen btw".
    public static readonly decimal[] VatRates = [21, 9, 0];

    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    [JsonIgnore] public bool IsDemo { get; set; }
    public DateTime Date { get; set; } = DateTime.UtcNow.Date;
    public string Supplier { get; set; } = "";
    public string? Description { get; set; }
    public string Category { get; set; } = ExpenseCategories.Other;
    public decimal Amount { get; set; }
    public decimal? VatRate { get; set; } = 21;
    public decimal VatAmount { get; set; }
    // "Btw verlegd naar mij": een buitenlandse leverancier rekende geen btw. Dan staat hier waar hij zit.
    public bool ReverseCharge { get; set; }
    public string? SupplierRegion { get; set; }
    public int? ProjectId { get; set; }
    public int? CustomerId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Het bedrag zonder btw. Bij btw verlegd betaalde je al geen btw.
    [NotMapped] public decimal AmountExclVat => ReverseCharge ? Amount : Amount - VatAmount;
    [NotMapped] public bool Demo => IsDemo;

    // Wat de uitgave van je winst afhaalt. Trek je geen btw af (KOR of vrijgestelde omzet), dan hoort de btw bij de kosten,
    // ook de verlegde btw die je zelf afdraagt. Dezelfde regel staat in frontend/src/lib/expenses.ts.
    public decimal CostForProfit(bool deduction) => deduction ? AmountExclVat : Amount + (ReverseCharge ? VatAmount : 0);

    // De btw zoals een kassabon hem uitrekent: uit het bedrag inclusief btw, of bij btw verlegd erbovenop. Op centen, half van nul af.
    public static decimal VatFor(decimal amount, decimal? rate, bool reverseCharge) => rate is not > 0
        ? 0
        : Money.Round(reverseCharge ? amount * rate.Value / 100 : amount * rate.Value / (100 + rate.Value));
}

// Het bonnetje bij een uitgave (PNG, JPEG, WebP of PDF), in de database van de werkruimte. Los van de uitgave,
// zodat een lijst met kosten de bestanden niet meeleest.
public class ExpenseReceipt : IWorkspaceOwned
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    public int ExpenseId { get; set; }
    public string FileName { get; set; } = "";
    // Bepaald aan de inhoud van het bestand, nooit aan wat de browser zegt.
    public string ContentType { get; set; } = "";
    public int Size { get; set; }
    [JsonIgnore] public byte[] Data { get; set; } = [];
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}

public class Appointment : IWorkspaceOwned, IDemoData
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    [JsonIgnore] public bool IsDemo { get; set; }
    public string Title { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Kind { get; set; } = "afspraak";
    public int? CustomerId { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }
    public bool Done { get; set; }
}

// Eigen modules: de gebruiker bepaalt zelf de velden, records worden als JSON opgeslagen.
public class CustomModule : IWorkspaceOwned, IDemoData
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    [JsonIgnore] public bool IsDemo { get; set; }
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "box";
    public string Color { get; set; } = "#ff6d5a";
    public string FieldsJson { get; set; } = "[]";
}

public class CustomRecord : IWorkspaceOwned, IDemoData
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    [JsonIgnore] public bool IsDemo { get; set; }
    public int ModuleId { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Workflow : IWorkspaceOwned, IDemoData
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    [JsonIgnore] public bool IsDemo { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; }
    public string GraphJson { get; set; } = "{\"nodes\":[],\"edges\":[]}";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastScheduledAt { get; set; }
}

public class Activity : IWorkspaceOwned
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
}

public static class Roles
{
    public const string Admin = "beheerder";
    public const string Employee = "medewerker";
    public static readonly string[] All = [Admin, Employee];
}

public class User : IWorkspaceOwned
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Employee;
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    // Verandert bij een nieuw wachtwoord, e-mailadres, andere rol of uitschakelen; oudere inlogcookies werken dan niet meer.
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    // Eigenaar van het platform. Wordt alleen bij het opstarten uit de instellingen gezet of door een andere platformbeheerder.
    public bool IsPlatformAdmin { get; set; }
    // Wanneer de gebruiker zijn e-mailadres bevestigde via de link in de mail. Leeg na een nieuw adres.
    public DateTime? EmailVerifiedAt { get; set; }

    public void NewSecurityStamp() => SecurityStamp = Guid.NewGuid().ToString("N");
}

public static class TokenPurposes
{
    public const string PasswordReset = "wachtwoord";
    public const string VerifyEmail = "e-mail";
}

// Een link uit een mail om je wachtwoord te herstellen of je e-mailadres te bevestigen. Werkt één keer en kort.
public class AccountToken : IWorkspaceOwned
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    public int UserId { get; set; }
    public string Purpose { get; set; } = "";
    // Alleen de SHA-256 van het token; het token zelf staat alleen in de mail.
    public string TokenHash { get; set; } = "";
    // Het adres waar de mail heen ging. Heeft de gebruiker intussen een ander adres, dan werkt de link niet meer.
    public string Email { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

public class WorkflowRun : IWorkspaceOwned
{
    public int Id { get; set; }
    [JsonIgnore] public int WorkspaceId { get; set; }
    public int WorkflowId { get; set; }
    public string Trigger { get; set; } = "";
    // bezig, wachtend, klaar, fout
    public string Status { get; set; } = "bezig";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    // Vroegste moment waarop een wachtend blok verder mag.
    public DateTime? ResumeAt { get; set; }
    public string ContextJson { get; set; } = "{}";
    public string PendingJson { get; set; } = "[]";
    public string LogJson { get; set; } = "[]";
    // Tellers over het hele leven van de run, ook na wachten: stappen, keren gewacht en verstuurde e-mails of webhooks.
    public int Steps { get; set; }
    public int Waits { get; set; }
    public int Calls { get; set; }
}
