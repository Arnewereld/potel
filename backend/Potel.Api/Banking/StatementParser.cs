using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Potel.Api.Banking;

// Eén regel van een bankafschrift. Het bedrag is positief voor geld dat binnenkwam en negatief voor geld dat eraf ging.
public record ParsedTransaction(DateTime Date, decimal Amount, string? CounterpartyName, string? CounterpartyIban, string? Description,
    string? Reference, string? Account);

// Welke kolom van een eigen CSV waarvoor is, met de kolomnamen uit de kopregel. Zonder teken-kolom staat het teken in het bedrag.
public record CsvMapping(string Date, string Amount, string? Description, string? Name, string? Iban, string? Sign);

public record ParseResult(string Format, List<ParsedTransaction> Transactions, int SkippedOtherCurrency = 0, int SkippedNotBooked = 0)
{
    public string? Error { get; init; }
    // Een CSV die we niet herkennen: de kolomnamen en een paar regels, zodat je zelf kunt aangeven welke kolom wat is.
    public List<string>? Headers { get; init; }
    public List<List<string>>? Sample { get; init; }
    public bool NeedsMapping => Headers is not null && Error is null;

    public static ParseResult Fail(string error) => new("", []) { Error = error };
}

// Leest een bankafschrift: CAMT.053 (XML, de standaard van Europese banken) of CSV van ING of Rabobank, of een andere CSV met
// een kolomindeling die je zelf aangeeft. Alles wat binnenkomt is onbetrouwbaar: XML zonder DTD of externe entiteiten, een
// grens aan grootte en aantal regels, en een vriendelijke melding als het geen afschrift is.
//
// Eerlijk over wat we konden controleren: CAMT.053 volgt de ISO 20022-standaard (versies .001.02 tot en met .001.08 hebben
// dezelfde opbouw voor wat we lezen). De kopregels van ING en Rabobank komen uit hun eigen exports zoals die bij ons bekend
// zijn; we hadden geen echte bestanden om tegen te testen, dus banken kunnen kolommen toevoegen of hernoemen. Herkennen we
// een bestand niet, dan kun je de kolommen altijd zelf aanwijzen.
public static class StatementParser
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxTransactions = 20_000;
    public const string NotAStatement = "Dit lijkt geen bankafschrift. Download bij je bank een afschrift als CAMT.053 (XML) of als CSV en probeer het opnieuw.";
    public const string TooBig = "Dit bestand is te groot. Een afschrift mag hooguit 10 MB zijn; kies een kortere periode.";
    public const string TooMany = "Dit afschrift heeft meer dan 20.000 regels. Kies een kortere periode en lees het in delen in.";
    public const string Mt940 = "Dit is een MT940-bestand. Dat lezen we niet; download bij je bank het afschrift als CAMT.053 (XML) of als CSV.";
    public const string OtherCamt = "Dit is een XML-bestand van je bank, maar geen dagafschrift (CAMT.053). Kies bij het downloaden voor CAMT.053.";

    public static ParseResult Parse(byte[] bytes, CsvMapping? mapping = null)
    {
        if (bytes.Length == 0) return ParseResult.Fail(NotAStatement);
        if (bytes.Length > MaxBytes) return ParseResult.Fail(TooBig);
        var text = Decode(bytes);
        if (text is null) return ParseResult.Fail(NotAStatement);
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('<')) return ParseCamt(text);
        if (trimmed.StartsWith(":20:") || trimmed.StartsWith("{1:") || text.Contains("\n:61:")) return ParseResult.Fail(Mt940);
        return ParseCsv(text, mapping);
    }

    // UTF-8 als dat klopt, anders Windows-1252 (oudere exports uit Windows). Binaire bestanden (pdf, xlsx, zip) weigeren we.
    static string? Decode(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = (CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1).GetString(bytes); }
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        return text.Any(c => c < ' ' && c is not ('\r' or '\n' or '\t')) ? null : text;
    }

    // ---------- CAMT.053 ----------

    static ParseResult ParseCamt(string text)
    {
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 1024,
                MaxCharactersInDocument = MaxBytes * 2L, IgnoreComments = true, IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            return ParseResult.Fail(NotAStatement);
        }
        var root = doc.Root;
        if (root is null) return ParseResult.Fail(NotAStatement);
        var statements = root.Elements().Where(e => e.Name.LocalName == "BkToCstmrStmt").SelectMany(e => Children(e, "Stmt")).ToList();
        if (statements.Count == 0)
            return ParseResult.Fail(root.Elements().Any(e => e.Name.LocalName is "BkToCstmrAcctRpt" or "BkToCstmrDbtCdtNtfctn") ? OtherCamt : NotAStatement);

        var list = new List<ParsedTransaction>();
        int otherCurrency = 0, notBooked = 0;
        foreach (var stmt in statements)
        {
            var account = NormalizeIban(Path(stmt, "Acct", "Id", "IBAN")?.Value);
            foreach (var entry in Children(stmt, "Ntry"))
            {
                var status = Child(entry, "Sts") is { } sts ? (Child(sts, "Cd")?.Value ?? sts.Value).Trim() : "BOOK";
                if (!status.Equals("BOOK", StringComparison.OrdinalIgnoreCase)) { notBooked++; continue; }
                var amountEl = Child(entry, "Amt");
                if (amountEl is null) return ParseResult.Fail(NotAStatement);
                if (!string.Equals(amountEl.Attribute("Ccy")?.Value ?? "EUR", "EUR", StringComparison.OrdinalIgnoreCase)) { otherCurrency++; continue; }
                var credit = Child(entry, "CdtDbtInd")?.Value.Trim().ToUpperInvariant() switch { "CRDT" => true, "DBIT" => false, _ => (bool?)null };
                if (credit is null) return ParseResult.Fail(NotAStatement);
                // Een terugboeking (RvslInd) draait het teken om.
                if (string.Equals(Child(entry, "RvslInd")?.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)) credit = !credit;
                var date = CamtDate(Child(entry, "BookgDt")) ?? CamtDate(Child(entry, "ValDt"));
                if (date is null) return ParseResult.Fail(NotAStatement);
                var entryRef = Child(entry, "AcctSvcrRef")?.Value.Trim();
                var extra = Child(entry, "AddtlNtryInf")?.Value.Trim();

                if (!TryAmount(amountEl.Value, out var entryAmount)) return ParseResult.Fail(NotAStatement);

                // Een verzamelboeking splitsen we in de losse transacties, maar alleen als die samen precies het bedrag van de
                // boeking zijn (in euro's, met hetzelfde teken). Anders is het één regel met het bedrag van de boeking.
                var details = Children(entry, "NtryDtls").SelectMany(d => Children(d, "TxDtls")).ToList();
                var parts = details.Select(d => (Detail: d, Amount: TxAmount(d), Credit: TxCredit(d) ?? credit.Value)).ToList();
                var split = parts.Count > 1 && parts.All(p => p.Amount is { Currency: var c } && c.Equals("EUR", StringComparison.OrdinalIgnoreCase))
                    && parts.All(p => p.Credit == credit.Value) && parts.Sum(p => p.Amount!.Value.Amount) == entryAmount;
                if (split)
                {
                    for (var i = 0; i < parts.Count; i++)
                    {
                        var d = parts[i].Detail;
                        var r = Path(d, "Refs", "AcctSvcrRef")?.Value.Trim() is { Length: > 0 } own ? own : entryRef is null ? null : $"{entryRef}/{i + 1}";
                        if (Add(list, Transaction(d, credit.Value, parts[i].Amount!.Value.Amount, date.Value, r, account, extra)) is { } error) return ParseResult.Fail(error);
                    }
                }
                else
                {
                    // Bij meerdere transacties zonder kloppende bedragen weten we niet wie de tegenpartij is; dan alleen de boeking.
                    var d = details.Count == 1 ? details[0] : null;
                    var r = entryRef is { Length: > 0 } ? entryRef : d is null ? null : Path(d, "Refs", "AcctSvcrRef")?.Value.Trim();
                    if (Add(list, Transaction(d, credit.Value, entryAmount, date.Value, r, account, extra)) is { } error) return ParseResult.Fail(error);
                }
            }
        }
        return new ParseResult("CAMT.053", list, otherCurrency, notBooked);
    }

    static string? Add(List<ParsedTransaction> list, ParsedTransaction? t)
    {
        if (t is null) return NotAStatement;
        if (list.Count >= MaxTransactions) return TooMany;
        list.Add(t);
        return null;
    }

    static ParsedTransaction? Transaction(XElement? details, bool credit, decimal amount, DateTime date, string? reference, string? account, string? extra)
    {
        if (amount < 0 || amount > Data.Money.MaxAmount) return null;
        // Bij geld dat binnenkomt is de tegenpartij de betaler (Dbtr), bij geld dat eraf gaat de ontvanger (Cdtr).
        var party = credit ? "Dbtr" : "Cdtr";
        var parties = details is null ? null : Child(details, "RltdPties");
        var partyEl = parties is null ? null : Child(parties, party);
        var name = partyEl is null ? null : (Child(partyEl, "Nm") ?? Path(partyEl, "Pty", "Nm"))?.Value;
        var iban = parties is null ? null : Path(parties, party + "Acct", "Id", "IBAN")?.Value;
        var remittance = details is null ? null : Child(details, "RmtInf");
        var lines = new List<string>();
        if (remittance is not null)
        {
            lines.AddRange(Children(remittance, "Ustrd").Select(u => u.Value.Trim()).Where(u => u.Length > 0));
            lines.AddRange(remittance.Descendants().Where(e => e.Name.LocalName == "Ref" && e.Parent?.Name.LocalName == "CdtrRefInf").Select(e => e.Value.Trim()).Where(v => v.Length > 0));
        }
        if (lines.Count == 0 && !string.IsNullOrWhiteSpace(extra)) lines.Add(extra);
        return new ParsedTransaction(date, credit ? amount : -amount, Clean(name, 200), NormalizeIban(iban), Clean(string.Join(" ", lines), 1000),
            Clean(reference, 100), account);
    }

    static bool? TxCredit(XElement d) => Child(d, "CdtDbtInd")?.Value.Trim().ToUpperInvariant() switch { "CRDT" => true, "DBIT" => false, _ => null };

    static (decimal Amount, string Currency)? TxAmount(XElement d)
    {
        var el = Child(d, "Amt") ?? Path(d, "AmtDtls", "TxAmt", "Amt");
        return el is not null && TryAmount(el.Value, out var a) ? (a, el.Attribute("Ccy")?.Value ?? "EUR") : null;
    }

    // In XML staat een bedrag altijd met een punt, zonder duizendtallen.
    static bool TryAmount(string s, out decimal amount) =>
        decimal.TryParse(s.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount) && amount <= Data.Money.MaxAmount;

    static DateTime? CamtDate(XElement? el)
    {
        if (el is null) return null;
        var value = (Child(el, "Dt") ?? Child(el, "DtTm"))?.Value.Trim();
        if (value is null) return null;
        return ParseDate(value.Length >= 10 ? value[..10] : value);
    }

    static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name);
    static IEnumerable<XElement> Children(XElement e, string name) => e.Elements().Where(x => x.Name.LocalName == name);
    static XElement? Path(XElement e, params string[] names)
    {
        XElement? current = e;
        foreach (var n in names) { current = current is null ? null : Child(current, n); }
        return current;
    }

    // ---------- CSV ----------

    // De kopregels die we herkennen. ING (Nederlands en Engels) en Rabobank, zoals hun exports bij ons bekend zijn.
    static readonly string[] IngNl = ["datum", "naam / omschrijving", "rekening", "tegenrekening", "af bij", "bedrag (eur)", "mededelingen"];
    static readonly string[] IngEn = ["date", "name / description", "account", "counterparty", "debit/credit", "amount (eur)", "notifications"];
    static readonly string[] Rabo = ["iban/bban", "munt", "volgnr", "datum", "bedrag", "naam tegenpartij"];

    static ParseResult ParseCsv(string text, CsvMapping? mapping)
    {
        var rows = ReadCsv(text);
        if (rows is null) return ParseResult.Fail(NotAStatement);
        rows = rows.Where(r => r.Any(c => c.Trim().Length > 0)).ToList();
        if (rows.Count < 2 || rows[0].Count < 2) return ParseResult.Fail(NotAStatement);
        if (rows.Count - 1 > MaxTransactions) return ParseResult.Fail(TooMany);
        var headers = rows[0].Select(h => h.Trim().Trim('"').Trim()).ToList();
        var lower = headers.Select(h => h.ToLowerInvariant()).ToList();
        var data = rows.Skip(1).ToList();

        if (IngNl.All(lower.Contains)) return Ing(lower, data, "datum", "naam / omschrijving", "rekening", "tegenrekening", "af bij", "bedrag (eur)", "mededelingen");
        if (IngEn.All(lower.Contains)) return Ing(lower, data, "date", "name / description", "account", "counterparty", "debit/credit", "amount (eur)", "notifications");
        if (Rabo.All(lower.Contains)) return Rabobank(lower, data);
        if (mapping is not null) return Mapped(headers, data, mapping);
        return new ParseResult("CSV", [])
        {
            Headers = headers,
            Sample = data.Take(5).Select(r => r.Select(c => c.Length > 80 ? c[..80] : c).ToList()).ToList(),
        };
    }

    static string? Cell(List<string> row, int index) => index >= 0 && index < row.Count ? row[index].Trim() : null;

    static ParseResult Ing(List<string> h, List<List<string>> data, string date, string name, string account, string counter, string sign, string amount, string notes)
    {
        int iDate = h.IndexOf(date), iName = h.IndexOf(name), iAcc = h.IndexOf(account), iCounter = h.IndexOf(counter), iSign = h.IndexOf(sign),
            iAmount = h.IndexOf(amount), iNotes = h.IndexOf(notes);
        var list = new List<ParsedTransaction>();
        foreach (var row in data)
        {
            var d = ParseDate(Cell(row, iDate));
            var a = ParseAmount(Cell(row, iAmount));
            var s = SignOf(Cell(row, iSign));
            if (d is null || a is null || s is null) return ParseResult.Fail(RowError(list.Count + 2));
            list.Add(new ParsedTransaction(d.Value, Math.Abs(a.Value) * s.Value, Clean(Cell(row, iName), 200), NormalizeIban(Cell(row, iCounter)),
                Clean(Cell(row, iNotes), 1000), null, NormalizeIban(Cell(row, iAcc))));
        }
        return new ParseResult("ING", list);
    }

    static ParseResult Rabobank(List<string> h, List<List<string>> data)
    {
        int iAcc = h.IndexOf("iban/bban"), iCcy = h.IndexOf("munt"), iSeq = h.IndexOf("volgnr"), iDate = h.IndexOf("datum"), iAmount = h.IndexOf("bedrag"),
            iCounter = h.IndexOf("tegenrekening iban/bban"), iName = h.IndexOf("naam tegenpartij"), iRef = h.IndexOf("transactiereferentie"),
            iPayRef = h.IndexOf("betalingskenmerk");
        var descriptions = new[] { "omschrijving-1", "omschrijving-2", "omschrijving-3" }.Select(n => h.IndexOf(n)).Where(i => i >= 0).ToList();
        var list = new List<ParsedTransaction>();
        var other = 0;
        foreach (var row in data)
        {
            if (Cell(row, iCcy) is { Length: > 0 } ccy && !ccy.Equals("EUR", StringComparison.OrdinalIgnoreCase)) { other++; continue; }
            var d = ParseDate(Cell(row, iDate));
            var a = ParseAmount(Cell(row, iAmount));
            if (d is null || a is null) return ParseResult.Fail(RowError(list.Count + other + 2));
            var text = string.Join(" ", descriptions.Select(i => Cell(row, i)).Append(Cell(row, iPayRef)).Where(t => !string.IsNullOrWhiteSpace(t)));
            // Het volgnummer is per rekening uniek; anders de transactiereferentie.
            var reference = Cell(row, iSeq) is { Length: > 0 } seq ? $"volgnr:{seq}" : Cell(row, iRef) is { Length: > 0 } r ? r : null;
            list.Add(new ParsedTransaction(d.Value, a.Value, Clean(Cell(row, iName), 200), NormalizeIban(Cell(row, iCounter)), Clean(text, 1000),
                Clean(reference, 100), NormalizeIban(Cell(row, iAcc))));
        }
        return new ParseResult("Rabobank", list, other);
    }

    static ParseResult Mapped(List<string> headers, List<List<string>> data, CsvMapping m)
    {
        int Index(string? name) => name is null ? -1 : headers.FindIndex(h => h.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        int iDate = Index(m.Date), iAmount = Index(m.Amount), iDesc = Index(m.Description), iName = Index(m.Name), iIban = Index(m.Iban), iSign = Index(m.Sign);
        if (iDate < 0 || iAmount < 0) return ParseResult.Fail("Kies in welke kolom de datum en het bedrag staan.");
        if ((m.Description is not null && iDesc < 0) || (m.Name is not null && iName < 0) || (m.Iban is not null && iIban < 0) || (m.Sign is not null && iSign < 0))
            return ParseResult.Fail("Een van de gekozen kolommen staat niet in dit bestand.");
        var list = new List<ParsedTransaction>();
        foreach (var row in data)
        {
            var d = ParseDate(Cell(row, iDate));
            var a = ParseAmount(Cell(row, iAmount));
            int? s = iSign < 0 ? 1 : SignOf(Cell(row, iSign));
            if (d is null || a is null || s is null) return ParseResult.Fail(RowError(list.Count + 2));
            var amount = iSign < 0 ? a.Value : Math.Abs(a.Value) * s.Value;
            list.Add(new ParsedTransaction(d.Value, amount, Clean(Cell(row, iName), 200), NormalizeIban(Cell(row, iIban)), Clean(Cell(row, iDesc), 1000), null, null));
        }
        return new ParseResult("CSV", list);
    }

    static string RowError(int line) => $"Regel {line} van het bestand kunnen we niet lezen: de datum of het bedrag klopt niet. Controleer de kolommen of download het afschrift opnieuw.";

    // "Af"/"Bij", "Debit"/"Credit", "D"/"C" en dergelijke. Onbekend is null.
    public static int? SignOf(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "af" or "debit" or "debet" or "d" or "dbit" or "-" => -1,
        "bij" or "credit" or "c" or "crdt" or "+" => 1,
        _ => null,
    };

    // Een eenvoudige CSV-lezer volgens RFC 4180: velden tussen aanhalingstekens mogen het scheidingsteken, regeleinden en
    // dubbele aanhalingstekens bevatten. Het scheidingsteken leiden we af uit de kopregel (puntkomma, komma of tab).
    public static List<List<string>>? ReadCsv(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = new[] { ';', ',', '\t' }.OrderByDescending(d => CountOutsideQuotes(firstLine, d)).First();
        if (CountOutsideQuotes(firstLine, delimiter) == 0) return null;
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
                continue;
            }
            if (c == '"' && field.Length == 0) quoted = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = [];
                if (rows.Count > MaxTransactions + 1) return rows;
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    static int CountOutsideQuotes(string line, char d)
    {
        var count = 0;
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (c == d && !quoted) count++;
        }
        return count;
    }

    // ---------- Bedragen, datums, namen ----------

    // Een bedrag zoals banken het schrijven: "1.234,56", "-12,50", "+1500,00", "12.50", "€ 1 234,56", "(12,50)" of "12,50-".
    // Een komma is (zoals in Nederland) het decimaalteken; staan er punt en komma in, dan is de laatste het decimaalteken.
    public static decimal? ParseAmount(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim().Replace("\u20AC", "").Replace("EUR", "", StringComparison.OrdinalIgnoreCase).Replace("\u00A0", "").Replace(" ", "").Replace("'", "");
        var negative = false;
        if (s.StartsWith('(') && s.EndsWith(')')) { negative = true; s = s[1..^1]; }
        if (s.EndsWith('-')) { negative = !negative; s = s[..^1]; }
        else if (s.EndsWith('+')) s = s[..^1];
        if (s.StartsWith('-')) { negative = !negative; s = s[1..]; }
        else if (s.StartsWith('+')) s = s[1..];
        if (s.Length == 0 || s.Length > 20 || s.Any(c => !(char.IsAsciiDigit(c) || c is '.' or ','))) return null;
        int comma = s.LastIndexOf(','), dot = s.LastIndexOf('.');
        if (comma >= 0 && dot >= 0)
        {
            // De laatste is het decimaalteken, de andere scheidt duizendtallen.
            s = comma > dot ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        }
        else if (comma >= 0)
        {
            s = s.Count(c => c == ',') == 1 ? s.Replace(',', '.') : s.Replace(",", "");
        }
        else if (dot >= 0 && (s.Count(c => c == '.') > 1 || s.Length - dot - 1 == 3))
        {
            // "1.250" of "1.234.567": punten als duizendtallen, zoals in Nederland. "12.50" blijft een decimaalpunt.
            s = s.Replace(".", "");
        }
        if (s.StartsWith('.') || s.EndsWith('.')) return null;
        if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        if (value > Data.Money.MaxAmount) return null;
        value = Data.Money.Round(value);
        return negative ? -value : value;
    }

    static readonly string[] DateFormats =
    [
        "yyyyMMdd", "yyyy-MM-dd", "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy", "yyyy/MM/dd", "yyyy.MM.dd",
        "dd-MM-yy", "dd/MM/yy",
    ];

    // De gangbare notaties van Nederlandse banken. Altijd dag voor maand, zoals in Nederland (nooit de Amerikaanse volgorde).
    public static DateTime? ParseDate(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        if (s.Length > 10 && s[10] is 'T' or ' ') s = s[..10];
        return DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year is >= 2000 and <= 2100
            ? d.Date : null;
    }

    public static string? NormalizeIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return null;
        var s = new string(iban.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return s.Length is >= 8 and <= 34 ? s : null;
    }

    static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length > max ? t[..max] : t;
    }

    // De sleutel waarmee we een transactie herkennen als je hetzelfde afschrift nog eens inleest. Met een kenmerk van de bank
    // is dat het kenmerk; anders een hash van rekening, datum, bedrag, tegenpartij en omschrijving, met een volgnummer voor
    // transacties die precies gelijk zijn (twee keer dezelfde koffie op één dag).
    public static List<string> DedupeKeys(IReadOnlyList<ParsedTransaction> list)
    {
        var refCounts = list.Where(t => t.Reference is not null).GroupBy(t => (t.Account, t.Reference)).ToDictionary(g => g.Key, g => g.Count());
        var seen = new Dictionary<string, int>();
        var keys = new List<string>(list.Count);
        foreach (var t in list)
        {
            var amount = t.Amount.ToString("0.00", CultureInfo.InvariantCulture);
            var day = t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (t.Reference is not null && refCounts[(t.Account, t.Reference)] == 1)
            {
                keys.Add($"ref:{t.Account}|{t.Reference}|{day}|{amount}");
                continue;
            }
            var raw = string.Join('\u001F', t.Account ?? "", day, amount, t.CounterpartyIban ?? "", (t.CounterpartyName ?? "").ToLowerInvariant(), (t.Description ?? "").ToLowerInvariant());
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32];
            var n = seen[hash] = seen.GetValueOrDefault(hash) + 1;
            keys.Add($"hash:{hash}#{n}");
        }
        return keys;
    }
}
