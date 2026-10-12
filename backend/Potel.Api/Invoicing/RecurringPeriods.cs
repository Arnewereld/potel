using System.Globalization;
using System.Text.RegularExpressions;
using Potel.Api.Data;

namespace Potel.Api.Invoicing;

// Eén periode van een terugkerende factuur: van From tot en met To, met de dag waarop de factuur gemaakt wordt.
public record RecurringPeriod(int Index, DateTime From, DateTime To, DateTime RunDate);

// Het rekenwerk achter terugkerende facturen: perioden, de dag waarop ze aan de beurt zijn en de teksten {maand}, {jaar},
// {kwartaal} en {periode} in de regels. Alles zonder database, zodat het los te testen is.
public static partial class RecurringPeriods
{
    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    // Perioden tellen altijd vanaf de startdatum, niet vanaf de vorige periode. Zo wordt 31 januari daarna 28 of 29 februari
    // en dan weer 31 maart, in plaats van steeds verder af te zakken naar de 28e.
    public static DateTime Start(DateTime start, string interval, int index) => start.Date.AddMonths(index * RecurringIntervals.Months(interval));

    // Het volgnummer van de periode die op periodStart begint (0 is de eerste).
    public static int IndexOf(DateTime start, string interval, DateTime periodStart) =>
        ((periodStart.Year - start.Year) * 12 + periodStart.Month - start.Month) / RecurringIntervals.Months(interval);

    public static RecurringPeriod Period(DateTime start, string interval, string rule, int index)
    {
        var from = Start(start, interval, index);
        var next = Start(start, interval, index + 1);
        // Vooraf op de eerste dag van de periode, achteraf op de dag nadat hij voorbij is.
        return new RecurringPeriod(index, from, next.AddDays(-1), rule == RecurringPeriodRules.InArrears ? next : from);
    }

    public static RecurringPeriod Period(RecurringInvoice t, DateTime periodStart) =>
        Period(t.StartDate, t.Interval, t.PeriodRule, IndexOf(t.StartDate, t.Interval, periodStart));

    // Valt deze periode nog binnen de einddatum? Een periode die na de einddatum begint, komt er niet meer.
    public static bool WithinEnd(RecurringInvoice t, DateTime periodStart) => t.EndDate is not { } end || periodStart <= end.Date;

    // Zet de volgende periode en de dag waarop die aan de beurt is. Na de einddatum is er geen volgende dag meer.
    public static void Schedule(RecurringInvoice t, DateTime nextPeriodStart)
    {
        t.NextPeriodStart = nextPeriodStart.Date;
        t.NextRunDate = WithinEnd(t, t.NextPeriodStart) ? Period(t, t.NextPeriodStart).RunDate : null;
    }

    // De perioden vanaf de volgende, zolang ze binnen de einddatum vallen.
    public static IEnumerable<RecurringPeriod> Upcoming(RecurringInvoice t)
    {
        for (var i = IndexOf(t.StartDate, t.Interval, t.NextPeriodStart); ; i++)
        {
            var p = Period(t.StartDate, t.Interval, t.PeriodRule, i);
            if (!WithinEnd(t, p.From)) yield break;
            yield return p;
        }
    }

    // Hoeveel perioden op deze dag al aan de beurt zijn (vandaag telt mee). Stopt bij max + 1, want meer hoeven we niet te weten.
    public static int DueCount(RecurringInvoice t, DateTime today, int max) =>
        Upcoming(t).TakeWhile(p => p.RunDate <= today.Date).Take(max + 1).Count();

    static string Month(DateTime d) => Dutch.DateTimeFormat.GetMonthName(d.Month);
    static string Day(DateTime d) => $"{d.Day} {Month(d)}";

    // De periode in gewone woorden: "oktober 2026", "Q4 2026", "2026", "februari t/m april 2026" of "15 oktober t/m 14 november 2026".
    public static string Label(DateTime from, DateTime to)
    {
        var wholeMonths = from.Day == 1 && to.AddDays(1).Day == 1;
        if (wholeMonths)
        {
            var months = (to.Year - from.Year) * 12 + to.Month - from.Month + 1;
            if (months == 1) return $"{Month(from)} {from.Year}";
            if (months == 3 && from.Month % 3 == 1) return $"Q{(from.Month + 2) / 3} {from.Year}";
            if (months == 12 && from.Month == 1) return $"{from.Year}";
            return from.Year == to.Year ? $"{Month(from)} t/m {Month(to)} {to.Year}" : $"{Month(from)} {from.Year} t/m {Month(to)} {to.Year}";
        }
        if (from == to) return $"{Day(from)} {from.Year}";
        return from.Year == to.Year ? $"{Day(from)} t/m {Day(to)} {to.Year}" : $"{Day(from)} {from.Year} t/m {Day(to)} {to.Year}";
    }

    [GeneratedRegex(@"\{(maand|jaar|kwartaal|periode)\}", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();

    // Vult {maand}, {jaar}, {kwartaal} en {periode} in met de periode van de factuur. Met een hoofdletter ({Maand})
    // begint ook de tekst met een hoofdletter. Maand, jaar en kwartaal zijn die van de eerste dag van de periode.
    public static string Fill(string text, DateTime from, DateTime to) => Placeholder().Replace(text, m =>
    {
        var value = m.Groups[1].Value.ToLowerInvariant() switch
        {
            "maand" => Month(from),
            "jaar" => from.Year.ToString(CultureInfo.InvariantCulture),
            "kwartaal" => $"Q{(from.Month + 2) / 3}",
            _ => Label(from, to),
        };
        return char.IsUpper(m.Groups[1].Value[0]) && value.Length > 0 ? char.ToUpper(value[0], Dutch) + value[1..] : value;
    });
}
