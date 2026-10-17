using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Endpoints;

namespace Potel.Api.Invoicing;

// Eén boeking op de urenspecificatie. Who staat er alleen in een team; Amount alleen als bedragen per regel aan staan.
public record HourSpecEntry(DateTime Date, string Description, string? Who, decimal Hours, decimal? Amount);

// De boekingen van één project, met het subtotaal.
public record HourSpecProject(string Name, List<HourSpecEntry> Entries, decimal Hours, decimal? Amount);

// De urenspecificatie van een factuur: per project de boekingen, met subtotalen en een totaal dat gelijk is aan de urenregels.
public record HourSpec(List<HourSpecProject> Projects, decimal Hours, decimal? Amount, bool ShowWho, bool ShowAmounts);

// De urenspecificatie bij een factuur. Zolang de factuur een concept is, komt hij uit de boekingen die op de factuur staan;
// bij versturen leggen we hem vast (Invoice.HoursSnapshot), net als de gegevens van beide partijen. Niet-factureerbare uren
// staan er nooit op. Per factuurregel tellen de uren van de boekingen precies op tot het aantal op die regel: we verdelen
// het aantal over de boekingen en ronden zo af dat de som klopt, zoals ook bij een regel per boeking (ProjectEndpoints.HourLines).
public static class HourSpecs
{
    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static decimal Round2(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);

    static string Number(decimal d) => d.ToString("0.##", Dutch);

    // Bouwt de specificatie. Geeft ook terug wat er niet klopt: een urenregel waarvan het aantal is aangepast, zodat de
    // boekingen er niet meer op uitkomen. Zo'n factuur kan niet met specificatie verstuurd worden.
    public static (HourSpec? Spec, string? Problem) Build(IEnumerable<InvoiceLine> lines, IEnumerable<TimeEntry> entries,
        IReadOnlyDictionary<int, string> projects, IReadOnlyDictionary<int, string> users, bool team, bool showAmounts)
    {
        var billable = entries.Where(e => e.Billable && e.Minutes > 0).ToList();
        if (billable.Count == 0) return (null, null);
        var byId = lines.Where(l => l.Id != 0).ToDictionary(l => l.Id);
        var rows = new List<(TimeEntry Entry, decimal Hours, decimal? Amount)>();
        string? problem = null;

        foreach (var group in billable.GroupBy(e => e.InvoiceLineId is { } id && byId.ContainsKey(id) ? id : 0).OrderBy(g => g.Key))
        {
            var ordered = group.OrderBy(e => e.Date).ThenBy(e => e.Id).ToList();
            var minutes = ordered.Sum(e => e.Minutes);
            var line = group.Key == 0 ? null : byId[group.Key];
            // Per regel afgerond op 2 decimalen scheelt het hooguit 0,01 uur met de werkelijke tijd; meer betekent een aangepast aantal.
            var fits = line is not null && Math.Abs(line.Quantity - minutes / 60m) < 0.01m;
            if (line is not null && !fits)
                problem ??= $"Op de regel \"{line.Description.Trim()}\" staat {Number(line.Quantity)} {line.Unit}, maar de uren die erbij horen zijn samen {Number(ProjectEndpoints.Hours(minutes))} uur. "
                            + "Pas het aantal aan of zet de urenspecificatie uit.";
            // Klopt de regel, dan verdelen we zijn aantal; anders (of zonder eigen regel, uit een oudere versie) de echte tijd.
            var total = fits ? line!.Quantity : ProjectEndpoints.Hours(minutes);
            var amounts = showAmounts && fits && line!.Unit == "uur";
            int cumulative = 0;
            decimal doneHours = 0, doneAmount = 0;
            foreach (var e in ordered)
            {
                cumulative += e.Minutes;
                var upTo = Round2(total * cumulative / minutes);
                decimal? amount = null;
                if (amounts)
                {
                    var upToAmount = Money.Round(upTo * line!.UnitPrice);
                    amount = upToAmount - doneAmount;
                    doneAmount = upToAmount;
                }
                rows.Add((e, upTo - doneHours, amount));
                doneHours = upTo;
            }
        }

        string? Who(TimeEntry e) => team && e.UserId is { } u && users.TryGetValue(u, out var name) ? name : null;
        static decimal? Sum(IEnumerable<decimal?> amounts) => amounts.Any(a => a is not null) ? amounts.Sum(a => a ?? 0) : null;
        var list = rows.GroupBy(r => r.Entry.ProjectId)
            .Select(g =>
            {
                var items = g.OrderBy(r => r.Entry.Date).ThenBy(r => r.Entry.Id)
                    .Select(r => new HourSpecEntry(r.Entry.Date.Date, r.Entry.Description.Trim(), Who(r.Entry), r.Hours, r.Amount)).ToList();
                return new HourSpecProject(projects.GetValueOrDefault(g.Key) ?? "Project", items, items.Sum(i => i.Hours), Sum(items.Select(i => i.Amount)));
            })
            .OrderBy(p => p.Name, StringComparer.Create(Dutch, true)).ToList();
        var spec = new HourSpec(list, list.Sum(p => p.Hours), Sum(list.Select(p => p.Amount)),
            list.Any(p => p.Entries.Any(e => e.Who is not null)), list.Any(p => p.Amount is not null));
        return (spec, problem);
    }

    // De specificatie van een concept, uit de boekingen die er nu op staan. De factuur moet met zijn regels geladen zijn.
    // Alles via de filters van de werkruimte: uren, projecten en namen van een andere werkruimte komen er nooit in.
    public static async Task<(HourSpec? Spec, string? Problem)> LiveAsync(AppDb db, Invoice inv)
    {
        var entries = await db.TimeEntries.AsNoTracking().Where(t => t.InvoiceId == inv.Id).ToListAsync();
        if (entries.Count == 0) return (null, null);
        var projectIds = entries.Select(e => e.ProjectId).Distinct().ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name);
        var userIds = entries.Select(e => e.UserId).OfType<int>().Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Name);
        // Een team: meer dan één gebruiker in de werkruimte. Alleen dan staat erbij wie de uren boekte.
        var team = await db.Users.CountAsync() > 1;
        return Build(inv.Lines, entries, projects, users, team, inv.HourAmounts);
    }

    public static string Serialize(HourSpec spec) => JsonSerializer.Serialize(spec, Json);

    public static HourSpec? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<HourSpec>(json, Json); }
        catch (JsonException) { return null; }
    }

    // Wat de factuur laat zien: bij een concept de stand van nu, daarna de vastgelegde specificatie.
    public static async Task<(HourSpec? Spec, string? Problem)> ForAsync(AppDb db, Invoice inv) =>
        inv.Status == InvoiceStatus.Draft ? await LiveAsync(db, inv) : (Parse(inv.HoursSnapshot), null);

    // Wat de klant te zien krijgt: alleen als de specificatie mee moest bij versturen.
    public static HourSpec? Sent(Invoice inv) =>
        inv.Status != InvoiceStatus.Draft && inv.HourSpecification ? Parse(inv.HoursSnapshot) : null;

    // De specificatie als CSV voor de klant, net als de export voor de boekhouder: UTF-8 met BOM, puntkomma's en Nederlandse getallen.
    // In het Engels zoals een Engelse spreadsheet hem verwacht: komma's, getallen met een punt en datums als 2026-10-14.
    public static byte[] Csv(HourSpec spec, string? invoiceNumber, string? seller, string? language = null)
    {
        var english = ClientTexts.IsEnglish(language);
        var separator = english ? ',' : ';';
        string Text(string? s) => AnnualOverview.CsvText(s, separator);
        string Num(decimal d) => english ? Money.Round(d).ToString("0.00", CultureInfo.InvariantCulture) : AnnualOverview.CsvNumber(d);
        string Day(DateTime d) => english ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : AnnualOverview.CsvDate(d);
        string T(string key, params (string, string?)[] values) => Text(ClientTexts.T(language, key, values));
        var sb = new StringBuilder();
        void Line(params string[] cells) => sb.Append(string.Join(separator, cells)).Append("\r\n");
        Line(T("csv.title", ("number", invoiceNumber ?? ClientTexts.T(language, "csv.draft"))), Text(seller));
        Line();
        var head = new List<string> { T("csv.project"), T("csv.date"), T("csv.description") };
        if (spec.ShowWho) head.Add(T("csv.who"));
        head.Add(T("csv.hours"));
        if (spec.ShowAmounts) head.Add(T("csv.amount"));
        Line([.. head]);
        foreach (var p in spec.Projects)
            foreach (var e in p.Entries)
            {
                var cells = new List<string> { Text(p.Name), Day(e.Date), Text(e.Description) };
                if (spec.ShowWho) cells.Add(Text(e.Who));
                cells.Add(Num(e.Hours));
                if (spec.ShowAmounts) cells.Add(e.Amount is { } a ? Num(a) : "");
                Line([.. cells]);
            }
        Line();
        foreach (var p in spec.Projects) Total(T("csv.subtotal", ("project", p.Name)), p.Hours, p.Amount);
        Total(T("csv.total"), spec.Hours, spec.Amount);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];

        void Total(string label, decimal hours, decimal? amount)
        {
            var cells = new List<string> { label, "", "" };
            if (spec.ShowWho) cells.Add("");
            cells.Add(Num(hours));
            if (spec.ShowAmounts) cells.Add(amount is { } a ? Num(a) : "");
            Line([.. cells]);
        }
    }

    public static string CsvFileName(string? invoiceNumber, string? language = null) =>
        ClientTexts.T(language, "csv.file", ("number", invoiceNumber is null ? ClientTexts.T(language, "csv.fileDraft")
            : string.Concat(invoiceNumber.Where(c => char.IsLetterOrDigit(c) || c == '-'))));
}
