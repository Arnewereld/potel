using System.Text;
using System.Text.RegularExpressions;

namespace Potel.Api.Data;

// Btw-nummers van klanten in de EU. Potel controleert alleen de vorm per land, zoals de Europese Commissie die voor VIES
// beschrijft. Of het nummer echt bestaat, kan alleen VIES zelf zeggen; daar vraagt Potel niets aan. De frontend gebruikt
// dezelfde patronen (frontend/src/lib/vat.ts); VatNumberTests controleert dat ze gelijk blijven.
public static class VatNumbers
{
    // Langer is nooit een btw-nummer; zo blijft elke controle kort, wat iemand ook intypt.
    public const int MaxLength = 30;

    // Per land de landcode, het patroon na de landcode en een voorbeeld. Griekenland gebruikt EL, niet GR.
    public static readonly IReadOnlyDictionary<string, (string Code, string Pattern, string Example)> Formats = new Dictionary<string, (string, string, string)>
    {
        ["België"] = ("BE", @"[01]\d{9}", "BE0123456789"),
        ["Bulgarije"] = ("BG", @"\d{9,10}", "BG123456789"),
        ["Cyprus"] = ("CY", @"\d{8}[A-Z]", "CY12345678L"),
        ["Denemarken"] = ("DK", @"\d{8}", "DK12345678"),
        ["Duitsland"] = ("DE", @"\d{9}", "DE123456789"),
        ["Estland"] = ("EE", @"\d{9}", "EE123456789"),
        ["Finland"] = ("FI", @"\d{8}", "FI12345678"),
        ["Frankrijk"] = ("FR", @"[0-9A-HJ-NP-Z]{2}\d{9}", "FR12345678901"),
        ["Griekenland"] = ("EL", @"\d{9}", "EL123456789"),
        ["Hongarije"] = ("HU", @"\d{8}", "HU12345678"),
        ["Ierland"] = ("IE", @"\d{7}[A-W][A-IW]?|\d[A-Z+*]\d{5}[A-W]", "IE1234567T"),
        ["Italië"] = ("IT", @"\d{11}", "IT12345678901"),
        ["Kroatië"] = ("HR", @"\d{11}", "HR12345678901"),
        ["Letland"] = ("LV", @"\d{11}", "LV12345678901"),
        ["Litouwen"] = ("LT", @"\d{9}|\d{12}", "LT123456789"),
        ["Luxemburg"] = ("LU", @"\d{8}", "LU12345678"),
        ["Malta"] = ("MT", @"\d{8}", "MT12345678"),
        ["Nederland"] = ("NL", @"\d{9}B\d{2}", "NL123456789B01"),
        ["Oostenrijk"] = ("AT", @"U\d{8}", "ATU12345678"),
        ["Polen"] = ("PL", @"\d{10}", "PL1234567890"),
        ["Portugal"] = ("PT", @"\d{9}", "PT123456789"),
        ["Roemenië"] = ("RO", @"[1-9]\d{1,9}", "RO1234567"),
        ["Slovenië"] = ("SI", @"\d{8}", "SI12345678"),
        ["Slowakije"] = ("SK", @"\d{10}", "SK1234567890"),
        ["Spanje"] = ("ES", @"[A-Z]\d{7}[A-Z0-9]|\d{8}[A-Z]", "ESB12345678"),
        ["Tsjechië"] = ("CZ", @"\d{8,10}", "CZ12345678"),
        ["Zweden"] = ("SE", @"\d{10}01", "SE123456789001"),
    };

    // Vaste patronen zonder geneste herhalingen, op hooguit MaxLength tekens: geen kans op traag terugzoeken.
    static readonly Dictionary<string, Regex> Patterns = Formats.Values.ToDictionary(
        f => f.Code, f => new Regex($"^{f.Code}(?:{f.Pattern})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking));

    static readonly Dictionary<string, string> CountryOfCode = Formats.ToDictionary(f => f.Value.Code, f => f.Key);

    // De landcode voor een land in de EU, of null.
    public static string? CodeFor(string? country) => Formats.TryGetValue(Countries.Normalize(country), out var f) ? f.Code : null;

    // Zoals het op de factuur hoort: hoofdletters, zonder spaties, punten, streepjes of schuine strepen. Begint het nummer van
    // een klant in de EU niet met een landcode uit de EU, dan zet Potel die van de klant ervoor; een Grieks nummer met GR wordt EL.
    public static string? Clean(string? vatNumber, string? country)
    {
        if (string.IsNullOrWhiteSpace(vatNumber)) return null;
        var sb = new StringBuilder(Math.Min(vatNumber.Length, MaxLength + 1));
        foreach (var ch in vatNumber)
        {
            if (char.IsWhiteSpace(ch) || ch is '.' or '-' or '/' or '_') continue;
            sb.Append(char.ToUpperInvariant(ch));
            if (sb.Length > MaxLength) break;
        }
        var clean = sb.ToString();
        if (clean.Length == 0) return null;
        if (CodeFor(country) is not { } code) return clean;
        if (code == "EL" && clean.StartsWith("GR", StringComparison.Ordinal)) return "EL" + clean[2..];
        return clean.Length >= 2 && CountryOfCode.ContainsKey(clean[..2]) ? clean : code + clean;
    }

    // Heeft het nummer de vorm van een btw-nummer uit het land van de klant? Alleen voor klanten in de EU.
    public static bool LooksValid(string? vatNumber, string? country) =>
        CodeFor(country) is { } code && Clean(vatNumber, country) is { Length: <= MaxLength } clean && Patterns[code].IsMatch(clean);

    // Wat er niet klopt aan het btw-nummer van een klant, in gewone woorden, of null. Leeg mag, en buiten de EU controleert
    // Potel de vorm niet: daar heeft elk land zijn eigen nummers.
    public static string? Problem(string? vatNumber, string? country)
    {
        var name = Countries.Normalize(country);
        if (!Formats.TryGetValue(name, out var f) || Clean(vatNumber, country) is not { } clean) return null;
        if (clean.Length <= MaxLength && Patterns[f.Code].IsMatch(clean)) return null;
        var prefix = clean.Length >= 2 ? clean[..2] : clean;
        if (prefix != f.Code && CountryOfCode.TryGetValue(prefix, out var other))
            return $"Dit btw-nummer begint met {prefix} en hoort dus bij {other}, maar de klant zit in {name}. Een btw-nummer uit {name} begint met {f.Code}, zoals {f.Example}. Klopt het land van de klant niet? Pas dat dan aan.";
        return $"Dit btw-nummer heeft niet de vorm van een btw-nummer uit {name}. Daar ziet een btw-nummer eruit als {f.Example}. Kijk het nummer na op de factuur of website van je klant.";
    }
}
