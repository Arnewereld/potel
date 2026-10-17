using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Potel.Api.Banking;

namespace Potel.Api.Importing;

// Eén regel uit het bestand, met het regelnummer zoals je het in een teksteditor of spreadsheet ziet.
public record ImportRow(int Line, List<string> Cells)
{
    public string Cell(int index) => index >= 0 && index < Cells.Count ? Cells[index] : "";
}

public record ImportTable(List<string> Headers, List<ImportRow> Rows, int HeaderLine);

// Een CSV-bestand uit een ander pakket lezen: veilig, begrensd en zonder aannames over het scheidingsteken. Staan er boven de
// kopregel nog andere regels (een titel of een periode), dan zoeken we de kopregel zelf op aan de kolomnamen die we kennen.
public static partial class ImportFile
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxRows = 10_000;
    public const int MaxColumns = 100;
    const int MaxCell = 2_000;
    const int HeaderSearchLines = 20;

    public const string Empty = "Dit bestand is leeg. Kies het CSV-bestand dat je uit je vorige pakket hebt gedownload.";
    public const string TooBig = "Dit bestand is te groot. Een import mag hooguit 5 MB zijn; splits het bestand of kies een kortere periode.";
    public const string TooMany = "Dit bestand heeft meer dan 10.000 regels. Splits het in kleinere bestanden en importeer ze een voor een.";
    public const string NotCsv = "Dit lijkt geen CSV-bestand. Exporteer de gegevens in je vorige pakket als CSV en probeer het opnieuw. Een Excel-bestand (.xlsx) sla je in Excel op als CSV.";
    public const string TooWide = "Dit bestand heeft meer dan 100 kolommen. Dat is geen export die we kunnen lezen.";
    public const string NoRows = "In dit bestand staan onder de kopregel geen gegevens.";

    // Leest het bestand. score geeft per kandidaat-kopregel aan hoeveel kolomnamen we kennen; de regel met de meeste wint.
    public static (ImportTable? Table, string? Error) Read(byte[] bytes, Func<List<string>, int> score)
    {
        if (bytes.Length == 0) return (null, Empty);
        if (bytes.Length > MaxBytes) return (null, TooBig);
        var text = StatementParser.Decode(bytes);
        if (text is null) return (null, NotCsv);

        // De eerste regels apart, om de kopregel te vinden. Een regel als "sep=;" van Excel slaan we over.
        var lines = new List<(int Start, string Text)>();
        var pos = 0;
        while (pos < text.Length && lines.Count < HeaderSearchLines)
        {
            var end = text.IndexOf('\n', pos);
            var line = (end < 0 ? text[pos..] : text[pos..end]).TrimEnd('\r');
            lines.Add((pos, line));
            if (end < 0) break;
            pos = end + 1;
        }
        int best = -1, bestScore = 0;
        char delimiter = ';';
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Text;
            if (line.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(line)) continue;
            var d = Delimiter(line);
            var cells = Parse(line, d, 1).Rows.FirstOrDefault()?.Cells ?? [];
            var s = score(cells);
            // Een regel zonder scheidingsteken is alleen een kopregel als hij een kolomnaam is die we kennen (een bestand met één kolom).
            if (CountOutsideQuotes(line, d) == 0 && s == 0) continue;
            if (best < 0 || s > bestScore)
            {
                best = i;
                bestScore = s;
                delimiter = d;
            }
        }
        if (best < 0) return (null, NotCsv);

        var (rows, tooMany) = Parse(text[lines[best].Start..], delimiter, best + 1);
        if (tooMany) return (null, TooMany);
        if (rows.Count == 0) return (null, NotCsv);
        var headerCells = rows[0].Cells;
        if (headerCells.Count > MaxColumns) return (null, TooWide);
        var headers = new List<string>();
        for (var i = 0; i < headerCells.Count; i++)
        {
            var h = Collapse(headerCells[i]);
            if (h == "") h = $"Kolom {i + 1}";
            var name = h;
            for (var n = 2; headers.Contains(name, StringComparer.OrdinalIgnoreCase); n++) name = $"{h} ({n})";
            headers.Add(name);
        }
        var data = rows.Skip(1).Where(r => r.Cells.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (data.Count > MaxRows) return (null, TooMany);
        if (data.Count == 0) return (null, NoRows);
        return (new ImportTable(headers, data, best + 1), null);
    }

    // Het scheidingsteken dat het vaakst buiten aanhalingstekens voorkomt: puntkomma, komma of tab.
    static char Delimiter(string line) => new[] { ';', ',', '\t' }.OrderByDescending(d => CountOutsideQuotes(line, d)).First();

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

    // Een CSV-lezer volgens RFC 4180 die bijhoudt op welke regel elke rij begint. Velden tussen aanhalingstekens mogen het
    // scheidingsteken, regeleinden en dubbele aanhalingstekens bevatten. Te lange velden kappen we af.
    static (List<ImportRow> Rows, bool TooMany) Parse(string text, char delimiter, int firstLine)
    {
        var rows = new List<ImportRow>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var line = firstLine;
        var rowStart = firstLine;
        void AddField()
        {
            row.Add(field.Length > MaxCell ? field.ToString(0, MaxCell) : field.ToString());
            field.Clear();
        }
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
                else
                {
                    if (c == '\n') line++;
                    if (field.Length <= MaxCell) field.Append(c);
                }
                continue;
            }
            if (c == '"' && field.Length == 0) quoted = true;
            else if (c == delimiter)
            {
                AddField();
                if (row.Count > MaxColumns) return (rows, false);
            }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                AddField();
                rows.Add(new ImportRow(rowStart, row));
                row = [];
                line++;
                rowStart = line;
                if (rows.Count > MaxRows + 1) return (rows, true);
            }
            else if (field.Length <= MaxCell) field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            AddField();
            rows.Add(new ImportRow(rowStart, row));
        }
        return (rows, rows.Count > MaxRows + 1);
    }

    // Witruimte (ook tabs en regeleinden) wordt één spatie.
    static string Collapse(string? s) =>
        string.Join(' ', (s ?? "").Replace("﻿", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Een kolomnaam om mee te vergelijken: kleine letters, zonder valuta tussen haakjes (zoals "Billable Rate (EUR)").
    public static string Key(string header)
    {
        var s = Collapse(header).ToLowerInvariant().TrimEnd(':').Trim();
        return Currency().Replace(s, "").Trim();
    }

    [GeneratedRegex(@"\s*\((eur|usd|gbp|€|\$)\)$")]
    private static partial Regex Currency();

    // Tekst uit het bestand zoals we hem opslaan. Een spreadsheet ziet =, +, - en @ aan het begin als formule; die tekens
    // halen we weg, zodat een geïmporteerde naam nooit als formule terugkomt in een export.
    public static string? Text(string? s, int max)
    {
        var t = Collapse(s).TrimStart('=', '+', '-', '@', ' ');
        if (t == "") return null;
        return t.Length > max ? t[..max].TrimEnd() : t;
    }

    // Een telefoonnummer: +31 wordt 0031, zodat het nummer klopt zonder het plusteken aan het begin.
    public static string? Phone(string? s, int max)
    {
        var t = Collapse(s);
        if (t.Length > 1 && t[0] == '+' && char.IsAsciiDigit(t[1])) t = "00" + t[1..];
        return Text(t, max);
    }

    // Een datum als 1/2/2026: zijn alle datums in de kolom zo dat de eerste waarde hooguit 12 is en de tweede soms meer, dan staat
    // de maand voorop (zoals in Amerikaanse exports). Anders, en bij twijfel, de dag, zoals in Nederland.
    public static bool MonthFirst(IEnumerable<string> values)
    {
        bool dayFirst = false, monthFirst = false;
        foreach (var v in values)
        {
            var m = ShortDate().Match(v.Trim());
            if (!m.Success) continue;
            var a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var b = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (a > 12) dayFirst = true;
            if (b > 12) monthFirst = true;
        }
        return monthFirst && !dayFirst;
    }

    [GeneratedRegex(@"^(\d{1,2})[/.\-](\d{1,2})[/.\-](\d{2}|\d{4})(?:[ T].*)?$")]
    private static partial Regex ShortDate();

    public static DateTime? Date(string? s, bool monthFirst)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim();
        if (t.Length > 40) return null;
        // 1/2/26 en 01.02.2026 worden 01-02-26 en 01-02-2026, de vorm die de datumlezer van de bank kent.
        if (ShortDate().Match(t) is { Success: true } m)
        {
            var (day, month) = monthFirst ? (m.Groups[2].Value, m.Groups[1].Value) : (m.Groups[1].Value, m.Groups[2].Value);
            t = $"{day.PadLeft(2, '0')}-{month.PadLeft(2, '0')}-{m.Groups[3].Value}";
        }
        return StatementParser.ParseDate(t);
    }

    public static decimal? Amount(string? s) => string.IsNullOrWhiteSpace(s) ? null : StatementParser.ParseAmount(s);

    // Een duur zoals tijdregistraties hem schrijven: 1:30, 01:30:00, 26:15:00, 1,5 of 1.50 (uren), of 1h 30m. In minuten, afgerond.
    public static int? Minutes(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().ToLowerInvariant();
        if (t.Length > 20) return null;
        if (Clock().Match(t) is { Success: true } c)
        {
            var h = int.Parse(c.Groups[1].Value, CultureInfo.InvariantCulture);
            var m = int.Parse(c.Groups[2].Value, CultureInfo.InvariantCulture);
            var sec = c.Groups[3].Success ? int.Parse(c.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
            if (m > 59 || sec > 59) return null;
            return (int)Math.Round(h * 60 + m + sec / 60m, MidpointRounding.AwayFromZero);
        }
        if (Units().Match(t) is { Success: true } u && u.Length > 0 && (u.Groups[1].Success || u.Groups[2].Success || u.Groups[3].Success))
        {
            decimal Part(Group g) => g.Success ? int.Parse(g.Value, CultureInfo.InvariantCulture) : 0;
            return (int)Math.Round(Part(u.Groups[1]) * 60 + Part(u.Groups[2]) + Part(u.Groups[3]) / 60m, MidpointRounding.AwayFromZero);
        }
        if (Decimal().IsMatch(t))
        {
            int comma = t.LastIndexOf(','), dot = t.LastIndexOf('.');
            var normal = comma > dot ? t.Replace(".", "").Replace(',', '.') : t.Replace(",", "");
            if (decimal.TryParse(normal, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var hours))
                return (int)Math.Round(hours * 60, MidpointRounding.AwayFromZero);
        }
        return null;
    }

    [GeneratedRegex(@"^(\d{1,4}):(\d{1,2})(?::(\d{1,2}))?$")]
    private static partial Regex Clock();

    [GeneratedRegex(@"^(?:(\d{1,4})\s*(?:h|u|uur|hours?))?\s*(?:(\d{1,4})\s*(?:m|min|minuten|minutes?))?\s*(?:(\d{1,5})\s*(?:s|sec|seconden|seconds?))?$")]
    private static partial Regex Units();

    [GeneratedRegex(@"^\d{1,4}(?:[.,]\d{1,4})?$")]
    private static partial Regex Decimal();

    // Ja of nee, zoals exports dat schrijven. Leeg is null; iets onbekends geeft false bij ok.
    public static (bool Ok, bool? Value) Flag(string? s)
    {
        var t = (s ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "" => (true, null),
            "ja" or "j" or "yes" or "y" or "true" or "waar" or "1" or "x" or "billable" or "factureerbaar" or "declarabel" => (true, true),
            "nee" or "n" or "no" or "false" or "onwaar" or "0" or "non-billable" or "not billable" or "niet factureerbaar" or "niet declarabel" => (true, false),
            _ => (false, null),
        };
    }
}
