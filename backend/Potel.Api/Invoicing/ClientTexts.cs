using System.Globalization;
using System.Text.RegularExpressions;
using Potel.Api.Data;

namespace Potel.Api.Invoicing;

// Alles wat je klant van de server krijgt, in zijn eigen taal: de mails over facturen en offertes, meldingen op de openbare
// pagina's en de teksten die Potel zelf op een factuur zet (zoals de regel met uren). Per taal één woordenboek met dezelfde
// sleutels; ClientTextsTests controleert dat elke sleutel in beide talen staat. De factuur, de offerte en de openbare pagina's
// zelf vertaalt de frontend (frontend/src/lib/docTexts.ts). Woorden tussen accolades vult Potel in.
public static partial class ClientTexts
{
    public static readonly IReadOnlyDictionary<string, string> Dutch = new Dictionary<string, string>
    {
        // De mails over een factuur. De woorden tussen accolades zijn die van de eigen teksten bij Instellingen.
        ["mail.invoice.subject"] = "Factuur {nummer} van {bedrijf}",
        ["mail.invoice.body"] = """
            Beste {klant},

            Hierbij ontvang je factuur {nummer} van {factuurdatum} voor {bedrag}. Je bekijkt, downloadt en print de factuur via deze link:
            {link}

            Wil je het bedrag uiterlijk op {vervaldatum} overmaken? {betaalgegevens}

            Met vriendelijke groet,
            {bedrijf}
            """,
        ["mail.first.subject"] = "Herinnering: factuur {nummer}",
        ["mail.first.body"] = """
            Beste {klant},

            Misschien is het je ontschoten: factuur {nummer} van {factuurdatum} had uiterlijk op {vervaldatum} betaald moeten zijn. Er staat nog {bedrag} open.

            Wil je het bedrag zo snel mogelijk overmaken? {betaalgegevens}
            Je vindt de factuur hier: {link}

            Heb je al betaald? Dan kun je dit bericht als niet verzonden beschouwen.

            Met vriendelijke groet,
            {bedrijf}
            """,
        ["mail.second.subject"] = "Tweede herinnering: factuur {nummer}",
        ["mail.second.body"] = """
            Beste {klant},

            Op factuur {nummer} van {factuurdatum} staat nog {bedrag} open. De betaaltermijn is op {vervaldatum} verlopen en we hebben nog geen betaling ontvangen, ook niet na onze eerste herinnering.

            Wil je het bedrag binnen 7 dagen overmaken? {betaalgegevens}
            Je vindt de factuur hier: {link}

            Is er iets mis met de factuur, of lukt betalen nu niet? Laat het ons dan weten, dan zoeken we samen een oplossing.

            Met vriendelijke groet,
            {bedrijf}
            """,
        ["mail.final.subject"] = "Aanmaning: factuur {nummer}",
        ["mail.final.body"] = """
            Beste {klant},

            Ondanks onze herinneringen hebben we de betaling van factuur {nummer} van {factuurdatum} nog niet ontvangen. Er staat nog {bedrag} open. De betaaltermijn is op {vervaldatum} verlopen.

            {gevolgen}

            {betaalgegevens}
            Je vindt de factuur hier: {link}

            Heb je inmiddels betaald? Dan kun je deze aanmaning als niet verzonden beschouwen.

            Met vriendelijke groet,
            {bedrijf}
            """,
        ["mail.credit.subject"] = "Creditnota {nummer} van {bedrijf}",
        ["mail.credit.body"] = """
            Beste {klant},

            Hierbij ontvang je creditnota {nummer} van {factuurdatum}. Je bekijkt, downloadt en print de creditnota via deze link:
            {link}

            Het bedrag verrekenen we met je openstaande factuur of storten we terug.

            Met vriendelijke groet,
            {bedrijf}
            """,
        ["mail.pay.iban"] = "Maak het over naar {iban} ten name van {name}, onder vermelding van factuurnummer {number}.",
        ["mail.pay.reference"] = "Vermeld bij je betaling het factuurnummer {number}.",
        ["mail.ideal"] = "Betalen met iDEAL kan ook: open de factuur via de link en kies Betaal nu met iDEAL.",
        ["mail.viewInvoice"] = "Bekijk de factuur: {link}",
        ["mail.consequences.consumer"] = "We vragen je het bedrag binnen 14 dagen na ontvangst van deze aanmaning te betalen, dus uiterlijk op {payBy}. Betaal je niet op tijd, dan moeten we incassokosten in rekening brengen. Volgens de wet zijn dat bij dit bedrag {costs}. Daarna kunnen we verdere stappen nemen om het bedrag te innen.",
        ["mail.consequences.business"] = "We vragen je het bedrag uiterlijk op {payBy} te betalen. Omdat de betaaltermijn verstreken is, hebben we volgens de wet recht op de wettelijke handelsrente vanaf de dag na de vervaldatum en op een vergoeding voor incassokosten van {costs}. Betaal je niet op tijd, dan brengen we die in rekening en nemen we verdere stappen om het bedrag te innen.",

        // De mail bij een offerte. {link} blijft staan tot versturen.
        ["quote.mail.subject"] = "Offerte {number} van {seller}",
        ["quote.mail.hello"] = "Hallo",
        ["quote.mail.hi"] = "Hoi {name}",
        ["quote.mail.about"] = " voor {subject}",
        ["quote.mail.inclVat"] = "{amount} inclusief btw",
        ["quote.mail.body"] = "{greeting},\n\nHierbij stuur ik je offerte {number}{about}, voor in totaal {amount}. De offerte is geldig tot en met {validUntil}.\n\n"
                              + "Via deze link bekijk je de offerte. Daar kun je hem ook meteen accepteren of afwijzen:\n{link}\n\n"
                              + "Heb je nog vragen? Beantwoord dan gewoon deze mail.\n\nGroet,\n{owner}\n{seller}",

        // De openbare offertepagina.
        ["quote.accepted"] = "Deze offerte is al geaccepteerd.",
        ["quote.acceptedOn"] = "Deze offerte is al geaccepteerd op {date}.",
        ["quote.rejected"] = "Deze offerte is al afgewezen.",
        ["quote.rejectedOn"] = "Deze offerte is al afgewezen op {date}.",
        ["quote.withdrawn"] = "Deze offerte is ingetrokken. Neem contact op met {seller} voor een nieuwe offerte.",
        ["quote.expired"] = "Deze offerte was geldig tot en met {date}. Neem contact op met {seller} voor een nieuwe offerte.",
        ["quote.sender"] = "de afzender",
        ["quote.linkGone"] = "Deze link werkt niet (meer). Vraag de afzender van de offerte om een nieuwe link.",
        ["quote.nameNeeded"] = "Typ je volledige naam om akkoord te geven.",
        ["quote.agreeNeeded"] = "Vink aan dat je akkoord gaat met de offerte.",
        ["quote.reasonTooLong"] = "De toelichting mag hooguit {max} tekens zijn.",

        // Online betalen met iDEAL.
        ["ideal.failed"] = "Betalen met iDEAL lukt nu even niet. Probeer het later opnieuw of maak het bedrag over.",
        ["ideal.notOffered"] = "Online betalen kan bij deze factuur niet. Maak het bedrag over zoals op de factuur staat.",
        ["ideal.justPaid"] = "Deze factuur is net met iDEAL betaald. Dank je wel! Ververs de pagina om te zien wat er nog openstaat.",
        ["ideal.busy"] = "Er loopt al een betaling met iDEAL voor deze factuur. Wacht even tot die klaar is en ververs dan de pagina.",
        ["ideal.description"] = "Factuur {number} van {seller}",
        ["ideal.descriptionNoSeller"] = "Factuur {number}",

        // Wat Potel zelf op een factuur zet.
        ["note.default"] = "Bedankt voor de fijne samenwerking!",
        ["note.quote"] = "Volgens offerte {number}.",
        ["line.hours"] = "{project}: werkzaamheden {from} t/m {to}",
        ["line.rounding"] = "Afrondingsverschil",
        ["line.trips"] = "Reiskosten, {km} km à {rate}",
        ["line.deposit"] = "Aanbetaling {percent}% op offerte {number}",
        ["line.depositVat"] = " (btw {rate}%)",
        ["line.depositMinus"] = "Min aanbetaling (factuur {number})",
        ["line.depositMinusDraft"] = "Min aanbetaling (conceptfactuur)",
        ["line.services"] = "Diensten",

        // De urenspecificatie als CSV.
        ["csv.title"] = "Urenspecificatie bij factuur {number}",
        ["csv.draft"] = "(concept)",
        ["csv.project"] = "Project",
        ["csv.date"] = "Datum",
        ["csv.description"] = "Omschrijving",
        ["csv.who"] = "Wie",
        ["csv.hours"] = "Uren",
        ["csv.amount"] = "Bedrag zonder btw",
        ["csv.subtotal"] = "Subtotaal {project}",
        ["csv.total"] = "Totaal",
        ["csv.file"] = "urenspecificatie-{number}.csv",
        ["csv.fileDraft"] = "concept",

        // Perioden van een terugkerende factuur.
        ["period.through"] = "{from} t/m {to}",
    };

    public static readonly IReadOnlyDictionary<string, string> English = new Dictionary<string, string>
    {
        ["mail.invoice.subject"] = "Invoice {nummer} from {bedrijf}",
        ["mail.invoice.body"] = """
            Dear {klant},

            Here is invoice {nummer} of {factuurdatum} for {bedrag}. You can view, download and print the invoice using this link:
            {link}

            Could you please transfer the amount no later than {vervaldatum}? {betaalgegevens}

            Kind regards,
            {bedrijf}
            """,
        ["mail.first.subject"] = "Reminder: invoice {nummer}",
        ["mail.first.body"] = """
            Dear {klant},

            Perhaps it slipped your mind: invoice {nummer} of {factuurdatum} was due for payment no later than {vervaldatum}. There is still {bedrag} outstanding.

            Could you please transfer the amount as soon as possible? {betaalgegevens}
            You can find the invoice here: {link}

            Already paid? Then please disregard this message.

            Kind regards,
            {bedrijf}
            """,
        ["mail.second.subject"] = "Second reminder: invoice {nummer}",
        ["mail.second.body"] = """
            Dear {klant},

            There is still {bedrag} outstanding on invoice {nummer} of {factuurdatum}. The payment term expired on {vervaldatum} and we have not yet received your payment, not even after our first reminder.

            Could you please transfer the amount within 7 days? {betaalgegevens}
            You can find the invoice here: {link}

            Is something wrong with the invoice, or are you unable to pay right now? Please let us know, and we will find a solution together.

            Kind regards,
            {bedrijf}
            """,
        ["mail.final.subject"] = "Final notice: invoice {nummer}",
        ["mail.final.body"] = """
            Dear {klant},

            Despite our reminders, we have not yet received payment of invoice {nummer} of {factuurdatum}. There is still {bedrag} outstanding. The payment term expired on {vervaldatum}.

            {gevolgen}

            {betaalgegevens}
            You can find the invoice here: {link}

            Have you paid in the meantime? Then please disregard this final notice.

            Kind regards,
            {bedrijf}
            """,
        ["mail.credit.subject"] = "Credit note {nummer} from {bedrijf}",
        ["mail.credit.body"] = """
            Dear {klant},

            Here is credit note {nummer} of {factuurdatum}. You can view, download and print the credit note using this link:
            {link}

            We will offset the amount against your outstanding invoice or refund it to you.

            Kind regards,
            {bedrijf}
            """,
        ["mail.pay.iban"] = "Please transfer it to {iban} in the name of {name}, quoting invoice number {number}.",
        ["mail.pay.reference"] = "Please quote invoice number {number} with your payment.",
        ["mail.ideal"] = "You can also pay with iDEAL: open the invoice using the link and choose Pay now with iDEAL.",
        ["mail.viewInvoice"] = "View the invoice: {link}",
        ["mail.consequences.consumer"] = "We ask you to pay the amount within 14 days of receiving this final notice, so no later than {payBy}. If you do not pay on time, we will have to charge collection costs. Under Dutch law, for this amount these are {costs}. After that, we may take further steps to collect the amount.",
        ["mail.consequences.business"] = "We ask you to pay the amount no later than {payBy}. Because the payment term has expired, under Dutch law we are entitled to statutory commercial interest from the day after the due date and to compensation for collection costs of {costs}. If you do not pay on time, we will charge these and take further steps to collect the amount.",

        ["quote.mail.subject"] = "Quote {number} from {seller}",
        ["quote.mail.hello"] = "Hello",
        ["quote.mail.hi"] = "Hi {name}",
        ["quote.mail.about"] = " for {subject}",
        ["quote.mail.inclVat"] = "{amount} including VAT",
        ["quote.mail.body"] = "{greeting},\n\nHere is my quote {number}{about}, for a total of {amount}. The quote is valid until {validUntil}.\n\n"
                              + "You can view the quote using this link. There you can also accept or decline it right away:\n{link}\n\n"
                              + "Any questions? Just reply to this email.\n\nKind regards,\n{owner}\n{seller}",

        ["quote.accepted"] = "This quote has already been accepted.",
        ["quote.acceptedOn"] = "This quote was already accepted on {date}.",
        ["quote.rejected"] = "This quote has already been declined.",
        ["quote.rejectedOn"] = "This quote was already declined on {date}.",
        ["quote.withdrawn"] = "This quote has been withdrawn. Please contact {seller} for a new quote.",
        ["quote.expired"] = "This quote was valid until {date}. Please contact {seller} for a new quote.",
        ["quote.sender"] = "the sender",
        ["quote.linkGone"] = "This link no longer works. Please ask the sender of the quote for a new link.",
        ["quote.nameNeeded"] = "Type your full name to accept.",
        ["quote.agreeNeeded"] = "Tick the box to confirm that you accept the quote.",
        ["quote.reasonTooLong"] = "The explanation can be at most {max} characters.",

        ["ideal.failed"] = "Paying with iDEAL is not possible right now. Please try again later or transfer the amount.",
        ["ideal.notOffered"] = "Online payment is not available for this invoice. Please transfer the amount as stated on the invoice.",
        ["ideal.justPaid"] = "This invoice has just been paid with iDEAL. Thank you! Refresh the page to see what is still outstanding.",
        ["ideal.busy"] = "An iDEAL payment for this invoice is already in progress. Please wait until it is finished and then refresh the page.",
        ["ideal.description"] = "Invoice {number} from {seller}",
        ["ideal.descriptionNoSeller"] = "Invoice {number}",

        ["note.default"] = "Thank you for your business!",
        ["note.quote"] = "As per quote {number}.",
        ["line.hours"] = "{project}: work from {from} to {to}",
        ["line.rounding"] = "Rounding difference",
        ["line.trips"] = "Travel expenses, {km} km at {rate}",
        ["line.deposit"] = "Deposit {percent}% on quote {number}",
        ["line.depositVat"] = " (VAT {rate}%)",
        ["line.depositMinus"] = "Less deposit (invoice {number})",
        ["line.depositMinusDraft"] = "Less deposit (draft invoice)",
        ["line.services"] = "Services",

        ["csv.title"] = "Timesheet for invoice {number}",
        ["csv.draft"] = "(draft)",
        ["csv.project"] = "Project",
        ["csv.date"] = "Date",
        ["csv.description"] = "Description",
        ["csv.who"] = "Who",
        ["csv.hours"] = "Hours",
        ["csv.amount"] = "Amount excl. VAT",
        ["csv.subtotal"] = "Subtotal {project}",
        ["csv.total"] = "Total",
        ["csv.file"] = "timesheet-{number}.csv",
        ["csv.fileDraft"] = "draft",

        ["period.through"] = "{from} to {to}",
    };

    static readonly CultureInfo DutchCulture = CultureInfo.GetCultureInfo("nl-NL");

    // Engelse bedragen met de euro voorop en zonder spatie: €1,234.56 en -€1,234.56.
    static readonly NumberFormatInfo EnglishMoney = new()
    {
        CurrencySymbol = "€", CurrencyDecimalDigits = 2, CurrencyDecimalSeparator = ".", CurrencyGroupSeparator = ",",
        CurrencyPositivePattern = 0, CurrencyNegativePattern = 1, NegativeSign = "-",
    };

    public static bool IsEnglish(string? language) => Languages.Of(language) == Languages.English;

    public static IReadOnlyDictionary<string, string> For(string? language) => IsEnglish(language) ? English : Dutch;

    // De tekst bij een sleutel in de taal van de klant, met de woorden tussen accolades ingevuld. In één keer: wat er
    // ingevuld wordt (zoals een naam van de klant), wordt zelf niet nog eens doorzocht. Onbekende woorden blijven staan.
    public static string T(string? language, string key, params (string Name, string? Value)[] values)
    {
        var text = For(language)[key];
        if (values.Length == 0) return text;
        var map = values.ToDictionary(v => v.Name, v => v.Value ?? "");
        return Placeholder().Replace(text, m => map.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    // € 1.234,56 of €1,234.56.
    public static string Euro(string? language, decimal d) => IsEnglish(language) ? d.ToString("C", EnglishMoney) : d.ToString("C", DutchCulture);

    // 14 oktober 2026 of 14 October 2026.
    public static string Date(string? language, DateTime d) =>
        IsEnglish(language) ? d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture) : d.ToString("d MMMM yyyy", DutchCulture);

    // Getallen zoals 90,5 of 90.5, met duizendtallen.
    public static string Number(string? language, decimal d, string format = "#,0.##") =>
        d.ToString(format, IsEnglish(language) ? CultureInfo.InvariantCulture : DutchCulture);

    // De naam van een maand: oktober of October.
    public static string Month(string? language, int month) =>
        IsEnglish(language) ? CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month) : DutchCulture.DateTimeFormat.GetMonthName(month);
}
