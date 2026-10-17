using System.Globalization;
using System.Text;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

// Eén afspraak of deadline in de agenda. AllDay: alleen een datum (Start), anders een begin en einde in Nederlandse tijd.
public record IcsEvent(string Uid, string Summary, DateTime Start, DateTime? End, bool AllDay, string? Description = null, string? Location = null,
    string? Category = null);

// Schrijft een agenda volgens RFC 5545: regels eindigen op CRLF, lange regels worden na 75 bytes gevouwen (zonder een teken in
// tweeën te knippen), tekst wordt ge-escaped, tijden staan in UTC en een hele dag is een DATE met een einde dat niet meer meetelt.
public static class IcsWriter
{
    const int MaxOctets = 75;

    // Backslash, puntkomma, komma en nieuwe regels krijgen een backslash; andere stuurtekens laten we weg.
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case ';': sb.Append(@"\;"); break;
                case ',': sb.Append(@"\,"); break;
                case '\n': sb.Append(@"\n"); break;
                default:
                    if (!char.IsControl(ch) || ch == '\t') sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    // Een regel van hooguit 75 bytes; de rest op vervolgregels die met een spatie beginnen. Een teken (ook een emoji van twee
    // UTF-16-delen) blijft heel.
    public static string Fold(string line)
    {
        var sb = new StringBuilder();
        var octets = 0;
        var limit = MaxOctets;
        for (var i = 0; i < line.Length; i++)
        {
            var size = char.IsHighSurrogate(line[i]) && i + 1 < line.Length ? 2 : 1;
            var bytes = Encoding.UTF8.GetByteCount(line.AsSpan(i, size));
            if (octets + bytes > limit)
            {
                sb.Append("\r\n ");
                octets = 0;
                // De spatie aan het begin van een vervolgregel telt mee.
                limit = MaxOctets - 1;
            }
            sb.Append(line, i, size);
            octets += bytes;
            i += size - 1;
        }
        return sb.ToString();
    }

    static string Utc(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    static string Date(DateTime d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    // Een tijd in Nederlandse tijd naar UTC. Een tijd die door de zomertijd niet bestaat (02:30 op de laatste zondag van maart)
    // schuift een uur op.
    public static DateTime ToUtc(DateTime local)
    {
        var t = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (BusinessClock.Zone.IsInvalidTime(t)) t = t.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(t, BusinessClock.Zone);
    }

    public static string Write(string name, string description, IEnumerable<IcsEvent> events, DateTime utcNow)
    {
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(Fold(text)).Append("\r\n");
        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//Potel//Agenda//NL");
        Line("CALSCALE:GREGORIAN");
        Line("METHOD:PUBLISH");
        Line($"X-WR-CALNAME:{Escape(name)}");
        Line($"X-WR-CALDESC:{Escape(description)}");
        Line("X-WR-TIMEZONE:Europe/Amsterdam");
        // Een hint voor agenda's die zelf bepalen hoe vaak ze kijken; Google en Apple doen dat meestal een paar keer per dag.
        Line("REFRESH-INTERVAL;VALUE=DURATION:PT1H");
        Line("X-PUBLISHED-TTL:PT1H");
        var stamp = Utc(utcNow);
        foreach (var e in events)
        {
            Line("BEGIN:VEVENT");
            Line($"UID:{e.Uid}");
            Line($"DTSTAMP:{stamp}");
            if (e.AllDay)
            {
                Line($"DTSTART;VALUE=DATE:{Date(e.Start)}");
                Line($"DTEND;VALUE=DATE:{Date(e.Start.Date.AddDays(1))}");
                Line("TRANSP:TRANSPARENT");
            }
            else
            {
                var start = ToUtc(e.Start);
                var end = e.End is { } x ? ToUtc(x) : start;
                Line($"DTSTART:{Utc(start)}");
                Line($"DTEND:{Utc(end < start ? start : end)}");
            }
            Line($"SUMMARY:{Escape(e.Summary)}");
            if (!string.IsNullOrWhiteSpace(e.Description)) Line($"DESCRIPTION:{Escape(e.Description)}");
            if (!string.IsNullOrWhiteSpace(e.Location)) Line($"LOCATION:{Escape(e.Location)}");
            if (!string.IsNullOrWhiteSpace(e.Category)) Line($"CATEGORIES:{Escape(e.Category)}");
            Line("END:VEVENT");
        }
        Line("END:VCALENDAR");
        return sb.ToString();
    }
}
