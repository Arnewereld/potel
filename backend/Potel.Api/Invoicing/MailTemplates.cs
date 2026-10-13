using System.Globalization;
using System.Text.RegularExpressions;
using Potel.Api.Data;

namespace Potel.Api.Invoicing;

public record MailText(string Subject, string Body);

// Wat de wet zegt over herinneringen, aanmaningen en incassokosten. Bronnen:
// - https://ondernemersplein.overheid.nl/wetten-en-regels/betalingstermijn-incassokosten-en-wettelijke-rente/
// - https://ondernemersplein.overheid.nl/geldzaken-en-belastingen/cashflow/stappenplan-klant-betaalt-factuur-niet/
// - https://www.rijksoverheid.nl/onderwerpen/schulden/vraag-en-antwoord/hoogte-incassokosten
// - artikel 6:96 BW: https://wetten.overheid.nl/jci1.3:c:BWBR0005289&boek=6&titeldeel=1&afdeling=10&artikel=96
public static class ReminderRules
{
    // Een particulier: geen bedrijfsnaam en geen btw-nummer. Op een verstuurde factuur telt de klant zoals hij toen was.
    public static bool IsConsumer(Invoice inv, Customer c) => inv.Buyer is { } b
        ? b.Contact is null && string.IsNullOrWhiteSpace(b.VatNumber)
        : string.IsNullOrWhiteSpace(c.Company) && string.IsNullOrWhiteSpace(c.VatNumber);

    // Een particulier krijgt in de aanmaning 14 dagen, te rekenen vanaf de dag na ontvangst (art. 6:96 lid 6 BW).
    // We noemen de dag erna, zodat een mail die pas de volgende dag gelezen wordt ook genoeg tijd geeft.
    public const int ConsumerTermDays = 14;
    public static DateTime PayBy(DateTime today) => today.AddDays(ConsumerTermDays + 1);

    // De wettelijke vergoeding voor incassokosten (staffel van het Besluit vergoeding voor buitengerechtelijke incassokosten):
    // 15% over de eerste € 2.500, 10% over de volgende € 2.500, 5% over de volgende € 5.000, 1% over de volgende € 190.000
    // en 0,5% over de rest, met minimaal € 40 en maximaal € 6.775.
    public static decimal CollectionCosts(decimal amount)
    {
        if (amount <= 0) return 0;
        (decimal Upto, decimal Rate)[] bands = [(2_500, 0.15m), (5_000, 0.10m), (10_000, 0.05m), (200_000, 0.01m), (decimal.MaxValue, 0.005m)];
        decimal costs = 0, from = 0;
        foreach (var (upto, rate) in bands)
        {
            if (amount <= from) break;
            costs += (Math.Min(amount, upto) - from) * rate;
            from = upto;
        }
        return Math.Clamp(Money.Round(costs), 40m, 6_775m);
    }
}

// De teksten van de mails over facturen: de factuur zelf en de drie herinneringen. Ze zijn per werkruimte aan te passen
// bij Instellingen; leeg betekent de standaardtekst hieronder. Woorden tussen accolades vult Potel per factuur in.
public static partial class MailTemplates
{
    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    public static readonly string[] Placeholders =
        ["{klant}", "{nummer}", "{bedrag}", "{factuurdatum}", "{vervaldatum}", "{dagen}", "{betaalgegevens}", "{link}", "{bedrijf}", "{gevolgen}"];

    public const string LinkPlaceholder = "{link}";
    // Alleen in de aanmaning: de wettelijke tekst voor een particulier of een bedrijf, met het bedrag aan incassokosten.
    public const string ConsequencesPlaceholder = "{gevolgen}";

    public static readonly MailText Invoice = new("Factuur {nummer} van {bedrijf}",
        """
        Beste {klant},

        Hierbij ontvang je factuur {nummer} van {factuurdatum} voor {bedrag}. Je bekijkt, downloadt en print de factuur via deze link:
        {link}

        Wil je het bedrag uiterlijk op {vervaldatum} overmaken? {betaalgegevens}

        Met vriendelijke groet,
        {bedrijf}
        """);

    public static readonly MailText First = new("Herinnering: factuur {nummer}",
        """
        Beste {klant},

        Misschien is het je ontschoten: factuur {nummer} van {factuurdatum} had uiterlijk op {vervaldatum} betaald moeten zijn. Er staat nog {bedrag} open.

        Wil je het bedrag zo snel mogelijk overmaken? {betaalgegevens}
        Je vindt de factuur hier: {link}

        Heb je al betaald? Dan kun je dit bericht als niet verzonden beschouwen.

        Met vriendelijke groet,
        {bedrijf}
        """);

    public static readonly MailText Second = new("Tweede herinnering: factuur {nummer}",
        """
        Beste {klant},

        Op factuur {nummer} van {factuurdatum} staat nog {bedrag} open. De betaaltermijn is op {vervaldatum} verlopen en we hebben nog geen betaling ontvangen, ook niet na onze eerste herinnering.

        Wil je het bedrag binnen 7 dagen overmaken? {betaalgegevens}
        Je vindt de factuur hier: {link}

        Is er iets mis met de factuur, of lukt betalen nu niet? Laat het ons dan weten, dan zoeken we samen een oplossing.

        Met vriendelijke groet,
        {bedrijf}
        """);

    public static readonly MailText Final = new("Aanmaning: factuur {nummer}",
        """
        Beste {klant},

        Ondanks onze herinneringen hebben we de betaling van factuur {nummer} van {factuurdatum} nog niet ontvangen. Er staat nog {bedrag} open. De betaaltermijn is op {vervaldatum} verlopen.

        {gevolgen}

        {betaalgegevens}
        Je vindt de factuur hier: {link}

        Heb je inmiddels betaald? Dan kun je deze aanmaning als niet verzonden beschouwen.

        Met vriendelijke groet,
        {bedrijf}
        """);

    // Een creditnota mailen: geen betaalverzoek maar een verrekening.
    public static readonly MailText CreditNote = new("Creditnota {nummer} van {bedrijf}",
        """
        Beste {klant},

        Hierbij ontvang je creditnota {nummer} van {factuurdatum}. Je bekijkt, downloadt en print de creditnota via deze link:
        {link}

        Het bedrag verrekenen we met je openstaande factuur of storten we terug.

        Met vriendelijke groet,
        {bedrijf}
        """);

    public static MailText Default(int step) => step switch
    {
        MailSteps.First => First,
        MailSteps.Second => Second,
        MailSteps.Final => Final,
        _ => Invoice,
    };

    // De tekst van deze werkruimte voor een stap: eigen tekst als die er is, anders de standaardtekst.
    public static MailText For(ReminderSettings? s, int step, bool credit = false)
    {
        if (credit && step == MailSteps.Invoice) return CreditNote;
        var d = Default(step);
        var (subject, body) = step switch
        {
            MailSteps.First => (s?.FirstSubject, s?.FirstBody),
            MailSteps.Second => (s?.SecondSubject, s?.SecondBody),
            MailSteps.Final => (s?.FinalSubject, s?.FinalBody),
            _ => (s?.InvoiceSubject, s?.InvoiceBody),
        };
        return new(string.IsNullOrWhiteSpace(subject) ? d.Subject : subject, string.IsNullOrWhiteSpace(body) ? d.Body : body);
    }

    // Wat er mis is met een eigen tekst, of null. Incassokosten en rente horen alleen in de aanmaning; de aanmaning moet de
    // wettelijke tekst houden, want voor een particulier moeten de termijn van 14 dagen en de kosten erin staan.
    public static string? Problem(int step, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(body)) return $"Vul bij \"{MailSteps.Label(step)}\" een onderwerp en een tekst in.";
        if (subject.Length > 200) return $"Het onderwerp van \"{MailSteps.Label(step)}\" is te lang.";
        if (body.Length > 5000) return $"De tekst van \"{MailSteps.Label(step)}\" is te lang.";
        var hasConsequences = body.Contains(ConsequencesPlaceholder, StringComparison.OrdinalIgnoreCase);
        if (step == MailSteps.Final && !hasConsequences)
            return "Laat {gevolgen} in de aanmaning staan. Daar zet Potel de wettelijke tekst over de termijn en de incassokosten neer, voor een particulier anders dan voor een bedrijf.";
        if (step != MailSteps.Final && (hasConsequences || CostWords().IsMatch(body)))
            return $"Incassokosten en rente horen alleen in de aanmaning, niet in \"{MailSteps.Label(step)}\".";
        return null;
    }

    [GeneratedRegex(@"incassokosten|wettelijke (handels)?rente", RegexOptions.IgnoreCase)]
    private static partial Regex CostWords();

    static string Euro(decimal d) => d.ToString("C", Dutch);
    static string Date(DateTime d) => d.ToString("d MMMM yyyy", Dutch);

    // De betaalgegevens in één zin, met het IBAN van de factuur zelf (zoals hij verstuurd is).
    public static string PaymentDetails(Invoice inv, Settings s)
    {
        var seller = inv.Seller;
        var iban = seller?.Iban ?? s.Iban;
        var name = seller?.Name ?? s.CompanyName;
        return string.IsNullOrWhiteSpace(iban)
            ? $"Vermeld bij je betaling het factuurnummer {inv.Number}."
            : $"Maak het over naar {iban.Trim()} ten name van {name}, onder vermelding van factuurnummer {inv.Number}.";
    }

    // De wettelijke alinea voor de aanmaning. Een particulier krijgt 14 dagen na ontvangst en hoort welke incassokosten
    // er komen als hij dan niet betaalt (art. 6:96 lid 6 BW). Bij een bedrijf mag de vergoeding (minimaal € 40) zonder
    // aanmaning en geldt de wettelijke handelsrente; die noemen we alleen hier, in de laatste stap.
    public static string Consequences(bool consumer, decimal open, DateTime today)
    {
        var costs = Euro(ReminderRules.CollectionCosts(open));
        var payBy = Date(ReminderRules.PayBy(today));
        return consumer
            ? $"We vragen je het bedrag binnen 14 dagen na ontvangst van deze aanmaning te betalen, dus uiterlijk op {payBy}. Betaal je niet op tijd, dan moeten we incassokosten in rekening brengen. Volgens de wet zijn dat bij dit bedrag {costs}. Daarna kunnen we verdere stappen nemen om het bedrag te innen."
            : $"We vragen je het bedrag uiterlijk op {payBy} te betalen. Omdat de betaaltermijn verstreken is, hebben we volgens de wet recht op de wettelijke handelsrente vanaf de dag na de vervaldatum en op een vergoeding voor incassokosten van {costs}. Betaal je niet op tijd, dan brengen we die in rekening en nemen we verdere stappen om het bedrag te innen.";
    }

    // Kan de klant online betalen, dan zegt {betaalgegevens} dat erbij; de knop staat op de factuurpagina achter de link.
    public const string IdealSentence = "Betalen met iDEAL kan ook: open de factuur via de link en kies Betaal nu met iDEAL.";

    // Vult de woorden tussen accolades in. Zonder link blijft {link} staan (in een voorbeeld), en een tekst zonder {link}
    // krijgt de link er onderaan bij: de klant moet de factuur altijd kunnen openen.
    public static MailText Fill(MailText text, Invoice inv, Customer c, Settings s, DateTime today, string? link, bool ideal = false)
    {
        var open = inv.IsCredit ? Math.Abs(Data.Money.Totals(inv.Lines).Total)
            : inv.Status is InvoiceStatus.Sent or InvoiceStatus.Overdue ? inv.OpenAmount : Data.Money.Totals(inv.Lines).Total;
        var late = Math.Max(0, (today - inv.DueDate.Date).Days);
        var consumer = ReminderRules.IsConsumer(inv, c);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["klant"] = c.Name.Trim(),
            ["nummer"] = inv.Number ?? "",
            ["bedrag"] = Euro(open),
            ["factuurdatum"] = Date(inv.IssueDate),
            ["vervaldatum"] = Date(inv.DueDate),
            ["dagen"] = late.ToString(Dutch),
            ["betaalgegevens"] = PaymentDetails(inv, s) + (ideal && !inv.IsCredit && open > 0 ? $" {IdealSentence}" : ""),
            ["bedrijf"] = (inv.Seller?.Name ?? s.CompanyName).Trim(),
            ["gevolgen"] = Consequences(consumer, open, today),
        };
        if (link is not null) values["link"] = link;
        string Replace(string t) => Placeholder().Replace(t, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        var body = Replace(text.Body);
        if (link is not null && !body.Contains(link)) body = $"{body.TrimEnd()}\n\nBekijk de factuur: {link}";
        return new(Workflows.EmailRules.OneLine(Replace(text.Subject)), body.Replace("\r\n", "\n").Trim());
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();
}
