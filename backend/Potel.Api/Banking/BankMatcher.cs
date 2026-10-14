using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Potel.Api.Data;

namespace Potel.Api.Banking;

// Wat de matcher van een factuur moet weten. Open is wat er nu nog openstaat (0 bij een betaalde factuur).
public record InvoiceCandidate(int Id, string Number, string Customer, string? CustomerIban, IReadOnlyList<string> CustomerNames, decimal Open,
    string Status, IReadOnlyList<PaymentCandidate> UnlinkedPayments);

// Een betaling die je zelf al had ingevuld en die nog niet aan een regel uit je bank hangt.
public record PaymentCandidate(int Id, decimal Amount, DateTime Date);

public record ExpenseCandidate(int Id, DateTime Date, string Supplier, decimal Amount, string Category, decimal? VatRate);

// Een voorstel om een regel te koppelen. Kind is "invoice" (nieuwe betaling op de factuur), "payment" (een betaling die er al
// stond) of "expense" (een uitgave die er al stond). Potel koppelt nooit vanzelf: je bevestigt elk voorstel zelf.
public record Proposal(string Kind, string Reason, int? InvoiceId = null, string? Number = null, string? Customer = null, decimal? Open = null,
    bool Partial = false, int? PaymentId = null, DateTime? PaymentDate = null, int? ExpenseId = null, string? Supplier = null, DateTime? ExpenseDate = null,
    string? RememberIban = null);

// Wat we alvast invullen als je van een afschrijving een nieuwe uitgave maakt. Bedrag en datum komen altijd uit de bank.
public record NewExpense(string? Supplier, DateTime Date, decimal Amount, string? Description, string? Category, decimal? VatRate, bool FromHistory);

// Zoekt bij een regel uit je bankafschrift de factuur of uitgave waar hij waarschijnlijk bij hoort.
//  - Geld dat binnenkomt: eerst op factuurnummer in de omschrijving, dan op precies het openstaande bedrag met de naam of het
//    rekeningnummer van de klant. Is het minder dan er openstaat, dan stellen we een deelbetaling voor. Had je de betaling zelf
//    al ingevuld, dan koppelen we aan die betaling in plaats van een tweede te maken.
//  - Geld dat eraf gaat: een uitgave met hetzelfde bedrag in de weken eromheen, het liefst bij dezelfde leverancier.
public partial class BankMatcher
{
    public const int MaxProposals = 3;
    // Een afschrijving mag tot 45 dagen na de uitgave komen (een rekening met betaaltermijn) en tot 7 dagen ervoor.
    public const int ExpenseDaysBefore = 45, ExpenseDaysAfter = 7;
    // Een betaling die je zelf invulde, mag tot 14 dagen van de datum in de bank afliggen.
    public const int PaymentDays = 14;

    readonly List<(InvoiceCandidate Invoice, Regex? Number)> invoices;
    readonly IReadOnlyList<ExpenseCandidate> expenses;

    public BankMatcher(IEnumerable<InvoiceCandidate> invoices, IEnumerable<ExpenseCandidate> expenses)
    {
        this.invoices = invoices.Select(i => (i, NumberPattern(i.Number))).ToList();
        this.expenses = expenses.ToList();
    }

    // Een factuurnummer is nooit zo lang; een langer nummer zoeken we niet.
    public const int MaxNumberLength = 64;
    // Een omschrijving uit de bank is hooguit 1000 tekens (StatementParser knipt hem af). Wat een matcher ook krijgt, we
    // zoeken niet verder dan dit.
    public const int MaxTextLength = 2000;

    [GeneratedRegex("[A-Za-z0-9]+")]
    private static partial Regex NumberPart();

    // Het factuurnummer als los woord, met of zonder streepje, spatie, punt of slash ertussen: 2026-0007 vindt ook "2026 0007"
    // en "20260007", maar niet "2026-00071". Korte nummers (minder dan 4 tekens) zijn te onzeker.
    // Zonder terugzoeken (NonBacktracking): de tijd groeit hooguit gelijk op met de lengte van de tekst, dus een time-out is
    // niet nodig en een match valt nooit stilletjes weg omdat de server even druk was. Daarom ook geen lookarounds (die kent
    // die engine niet): een teken ervoor en erna dat geen letter of cijfer is, of het begin of einde van de tekst. Voor IsMatch
    // maakt dat geen verschil.
    public static Regex? NumberPattern(string number)
    {
        if (number.Length > MaxNumberLength) return null;
        var parts = NumberPart().Matches(number).Select(m => Regex.Escape(m.Value)).ToList();
        if (parts.Sum(p => p.Length) < 4) return null;
        return new Regex($"(?:^|[^A-Za-z0-9]){string.Join("[-\\s./_]?", parts)}(?:[^A-Za-z0-9]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    static bool Mentions(Regex? number, string? text)
    {
        if (number is null || string.IsNullOrEmpty(text)) return false;
        return number.IsMatch(text.Length > MaxTextLength ? text.AsSpan(0, MaxTextLength) : text);
    }

    public List<Proposal> Propose(BankTransaction t)
    {
        var list = t.Amount > 0 ? Incoming(t) : Outgoing(t);
        return list.DistinctBy(p => (p.Kind, p.InvoiceId, p.PaymentId, p.ExpenseId)).Take(MaxProposals).ToList();
    }

    List<Proposal> Incoming(BankTransaction t)
    {
        var list = new List<Proposal>();

        // 1. Het factuurnummer staat in de omschrijving (niet in het kenmerk van de bank: dat is een eigen nummer van de bank).
        foreach (var (inv, _) in invoices.Where(x => Mentions(x.Number, t.Description)))
        {
            if (ExistingPayment(inv, t) is { } p) list.Add(PaymentProposal(inv, p, "factuurnummer"));
            else if (inv.Open > 0 && t.Amount <= inv.Open) list.Add(InvoiceProposal(inv, t, "factuurnummer"));
        }

        // 2. Een betaling die je zelf al invulde, met hetzelfde bedrag, van deze klant.
        foreach (var (inv, _) in invoices.Where(x => Identity(x.Invoice, t) is not null))
            if (ExistingPayment(inv, t) is { } p) list.Add(PaymentProposal(inv, p, Identity(inv, t)!));

        // 3. Precies het openstaande bedrag, met de naam of het rekeningnummer van de klant.
        foreach (var (inv, _) in invoices.Where(x => x.Invoice.Open > 0 && x.Invoice.Open == t.Amount))
            if (Identity(inv, t) is { } reason) list.Add(InvoiceProposal(inv, t, $"bedrag en {reason}"));

        // 4. Minder dan er openstaat, van een klant die we herkennen: een deelbetaling. Alleen als er maar één open factuur
        //    van die klant is, anders weten we niet welke.
        var partial = invoices.Where(x => x.Invoice.Open > t.Amount && Identity(x.Invoice, t) is not null).ToList();
        if (partial.Count == 1) list.Add(InvoiceProposal(partial[0].Invoice, t, Identity(partial[0].Invoice, t)!));
        return list;
    }

    static PaymentCandidate? ExistingPayment(InvoiceCandidate inv, BankTransaction t) =>
        inv.UnlinkedPayments.Where(p => p.Amount == t.Amount && Math.Abs((p.Date - t.Date).TotalDays) <= PaymentDays)
            .OrderBy(p => Math.Abs((p.Date - t.Date).TotalDays)).FirstOrDefault();

    // Waaraan we de klant herkennen: zijn rekeningnummer of zijn naam.
    static string? Identity(InvoiceCandidate inv, BankTransaction t)
    {
        if (inv.CustomerIban is not null && inv.CustomerIban == t.CounterpartyIban) return "rekeningnummer";
        if (t.CounterpartyName is { } name && inv.CustomerNames.Any(n => SameName(n, name))) return "naam";
        return null;
    }

    static Proposal InvoiceProposal(InvoiceCandidate inv, BankTransaction t, string reason) =>
        new("invoice", reason, inv.Id, inv.Number, inv.Customer, inv.Open, Partial: t.Amount < inv.Open, RememberIban: OfferIban(inv, t));

    static Proposal PaymentProposal(InvoiceCandidate inv, PaymentCandidate p, string reason) =>
        new("payment", reason, inv.Id, inv.Number, inv.Customer, inv.Open, PaymentId: p.Id, PaymentDate: p.Date);

    // Het rekeningnummer uit de bank als het nog niet bij de klant staat (of daar een ander staat).
    static string? OfferIban(InvoiceCandidate inv, BankTransaction t) =>
        t.CounterpartyIban is { } iban && iban != inv.CustomerIban && Ibans.IsValid(iban) ? iban : null;

    List<Proposal> Outgoing(BankTransaction t)
    {
        var amount = -t.Amount;
        return expenses
            .Where(e => e.Amount == amount && e.Date >= t.Date.AddDays(-ExpenseDaysBefore) && e.Date <= t.Date.AddDays(ExpenseDaysAfter))
            .Select(e => (Expense: e, Same: t.CounterpartyName is { } n && SameName(e.Supplier, n)))
            .OrderByDescending(x => x.Same).ThenBy(x => Math.Abs((x.Expense.Date - t.Date).TotalDays))
            .Select(x => new Proposal("expense", x.Same ? "bedrag en leverancier" : "bedrag en datum", ExpenseId: x.Expense.Id, Supplier: x.Expense.Supplier,
                ExpenseDate: x.Expense.Date))
            .ToList();
    }

    // Een nieuwe uitgave van een afschrijving: leverancier, datum, bedrag en omschrijving uit de bank. Categorie en btw-tarief
    // nemen we over van je laatste uitgave bij dezelfde leverancier; je kiest ze altijd zelf voor je opslaat.
    public static NewExpense Prefill(BankTransaction t, IEnumerable<ExpenseCandidate> history)
    {
        var name = t.CounterpartyName;
        var earlier = name is null ? null : history.Where(e => SameName(e.Supplier, name)).OrderByDescending(e => e.Date).FirstOrDefault();
        var supplier = earlier?.Supplier ?? Tidy(name);
        var description = t.Description is { Length: > 200 } d ? d[..200] : t.Description;
        return new NewExpense(supplier, t.Date, -t.Amount, description, earlier?.Category, earlier is null ? 21 : earlier.VatRate, earlier is not null);
    }

    // "ALBERT HEIJN 1403" wordt "Albert Heijn 1403": banken schrijven namen vaak in hoofdletters.
    static string? Tidy(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var t = name.Trim();
        if (t.Length > 200) t = t[..200];
        return t.Any(char.IsLower) ? t : CultureInfo.GetCultureInfo("nl-NL").TextInfo.ToTitleCase(t.ToLowerInvariant());
    }

    // Woorden die niets zeggen over wie het is: rechtsvormen, aanhef en tussenvoegsels.
    static readonly HashSet<string> Noise =
    [
        "bv", "nv", "vof", "cv", "eo", "ltd", "gmbh", "inc", "bvba", "holding", "de", "het", "van", "der", "den", "en", "the", "and",
        "hr", "dhr", "mw", "mevr", "mr", "mrs", "fam", "stichting", "via",
    ];

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotAWord();

    // Een naam van meer dan 200 tekens (zoals de bank hem hooguit geeft) vergelijken we op de eerste 200.
    const int MaxNameLength = 200;

    static List<string> Words(string s)
    {
        if (s.Length > MaxNameLength) s = s[..MaxNameLength];
        var plain = new string(s.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .ToLowerInvariant().Replace(".", "");
        return NotAWord().Split(plain).Where(w => w.Length >= 2 && !Noise.Contains(w)).Distinct().ToList();
    }

    // Lijken twee namen op dezelfde partij? Minstens de helft van de woorden van de kortste naam moet in de andere staan, en
    // minstens één daarvan moet echt iets zeggen (drie letters of meer). "BAKKERIJ JANSEN BV" is "Bakkerij Jansen B.V.",
    // en "HR J DE VRIES" is "Jan de Vries".
    public static bool SameName(string a, string b)
    {
        var x = Words(a);
        var y = Words(b);
        if (x.Count == 0 || y.Count == 0) return false;
        var common = x.Intersect(y).ToList();
        return common.Any(w => w.Length >= 3) && common.Count * 2 >= Math.Min(x.Count, y.Count);
    }
}

// Rekeningnummers (IBAN): zonder spaties, in hoofdletters, en met de controle van het getal 97.
public static class Ibans
{
    public static string? Normalize(string? s) => StatementParser.NormalizeIban(s);

    public static bool IsValid(string? iban)
    {
        if (iban is null || iban.Length is < 15 or > 34) return false;
        if (!char.IsAsciiLetterUpper(iban[0]) || !char.IsAsciiLetterUpper(iban[1]) || !char.IsAsciiDigit(iban[2]) || !char.IsAsciiDigit(iban[3])) return false;
        if (!iban.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c))) return false;
        var rearranged = iban[4..] + iban[..4];
        var rest = 0;
        foreach (var c in rearranged)
        {
            var value = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            rest = value >= 10 ? (rest * 100 + value) % 97 : (rest * 10 + value) % 97;
        }
        return rest == 1;
    }
}
